// Asks the home router (UPnP / IGD) to forward a TCP port to this PC, and reads the public IP address.
// Used by the "Internet" mode of Folder Sync PC. C# 5, no external libraries.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace PhoneSync
{
    class Upnp
    {
        string controlUrl, serviceType;
        public string ExternalIp = "";
        public string Error = "";

        public bool Found { get { return controlUrl != null; } }

        /// <summary>Looks for the router on the network that owns localIp.</summary>
        public bool Discover(string localIp)
        {
            try
            {
                string[] targets = {
                    "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                    "urn:schemas-upnp-org:service:WANIPConnection:1",
                    "upnp:rootdevice" };
                var locations = new List<string>();
                using (var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localIp), 0)))
                {
                    udp.Client.ReceiveTimeout = 1500;
                    foreach (var st in targets)
                    {
                        string msg = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n";
                        byte[] b = Encoding.ASCII.GetBytes(msg);
                        udp.Send(b, b.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
                    }
                    var end = DateTime.Now.AddSeconds(4);
                    while (DateTime.Now < end)
                    {
                        try
                        {
                            IPEndPoint from = null;
                            byte[] data = udp.Receive(ref from);
                            foreach (var line in Encoding.ASCII.GetString(data).Split('\n'))
                                if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                                {
                                    string loc = line.Substring(9).Trim();
                                    if (!locations.Contains(loc)) locations.Add(loc);
                                }
                        }
                        catch (SocketException) { if (locations.Count > 0) break; }
                    }
                }
                foreach (var loc in locations)
                    if (TryDescription(loc)) return true;
                Error = locations.Count == 0 ? "No router answered (UPnP may be turned off)." : "The router doesn't offer port forwarding.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return false;
        }

        bool TryDescription(string location)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(location);
                req.Timeout = 4000;
                var doc = new XmlDocument();
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream()) doc.Load(s);
                string urlBase = null;
                var ub = doc.SelectSingleNode("//*[local-name()='URLBase']");
                if (ub != null && ub.InnerText.Trim() != "") urlBase = ub.InnerText.Trim();
                foreach (XmlNode svc in doc.SelectNodes("//*[local-name()='service']"))
                {
                    var t = svc.SelectSingleNode("*[local-name()='serviceType']");
                    var c = svc.SelectSingleNode("*[local-name()='controlURL']");
                    if (t == null || c == null) continue;
                    string type = t.InnerText.Trim();
                    if (type.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Uri baseUri = new Uri(urlBase ?? location);
                    controlUrl = new Uri(baseUri, c.InnerText.Trim()).ToString();
                    serviceType = type;
                    return true;
                }
            }
            catch { }
            return false;
        }

        string Soap(string action, string args)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                          "<s:Body><u:" + action + " xmlns:u=\"" + serviceType + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
            var req = (HttpWebRequest)WebRequest.Create(controlUrl);
            req.Method = "POST";
            req.Timeout = 6000;
            req.ContentType = "text/xml; charset=\"utf-8\"";
            req.Headers.Add("SOAPACTION", "\"" + serviceType + "#" + action + "\"");
            byte[] b = Encoding.UTF8.GetBytes(body);
            req.ContentLength = b.Length;
            using (var rs = req.GetRequestStream()) rs.Write(b, 0, b.Length);
            try
            {
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                string detail = "";
                try { if (wex.Response != null) using (var sr = new StreamReader(wex.Response.GetResponseStream())) detail = sr.ReadToEnd(); } catch { }
                var m = System.Text.RegularExpressions.Regex.Match(detail, "<errorDescription>(.*?)</errorDescription>");
                throw new Exception(m.Success ? m.Groups[1].Value : wex.Message);
            }
        }

        /// <summary>Forward externalPort (TCP) on the router to internalIp:internalPort.</summary>
        public bool AddMapping(int externalPort, string internalIp, int internalPort, string description)
        {
            try
            {
                Soap("AddPortMapping",
                    "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + externalPort + "</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
                    "<NewInternalPort>" + internalPort + "</NewInternalPort><NewInternalClient>" + internalIp + "</NewInternalClient>" +
                    "<NewEnabled>1</NewEnabled><NewPortMappingDescription>" + description + "</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>");
                try
                {
                    string r = Soap("GetExternalIPAddress", "");
                    var m = System.Text.RegularExpressions.Regex.Match(r, "<NewExternalIPAddress>(.*?)</NewExternalIPAddress>");
                    if (m.Success) ExternalIp = m.Groups[1].Value.Trim();
                }
                catch { }
                return true;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }

        public void RemoveMapping(int externalPort)
        {
            try { Soap("DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + externalPort + "</NewExternalPort><NewProtocol>TCP</NewProtocol>"); }
            catch { }
        }

        /// <summary>True for addresses that are not reachable from the internet (private or carrier-grade NAT).</summary>
        public static bool IsPrivate(string ip)
        {
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a)) return true;
            byte[] b = a.GetAddressBytes();
            if (b.Length != 4) return true;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 127 || (b[0] == 169 && b[1] == 254);
        }

        /// <summary>Public address as seen from outside (used when the router can't tell us).</summary>
        public static string LookupPublicIp()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                req.Timeout = 6000;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd().Trim();
            }
            catch { return ""; }
        }
    }
}
