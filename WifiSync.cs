// Folder Sync PC - serves a PC folder to the "Folder Sync Android" app over the local network or the internet.
// No developer options needed: the phone scans a QR code, pairs with a secret token and pins this PC's
// certificate, then pulls the files over TLS.
// Built with the C# compiler that ships with Windows (.NET Framework 4.x), so this is C# 5.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyDescription("Shares a PC folder with an Android phone over Wi-Fi or the internet")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace PhoneSync
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            string shot = null;
            int si = Array.IndexOf(args, "--shot");
            if (si >= 0 && si + 1 < args.Length) { shot = args[si + 1]; WifiSettings.NoSave = true; }
            bool first;
            using (var mutex = new Mutex(true, Variant.MutexName, out first))
            {
                if (!first && shot == null)
                {
                    MessageBox.Show(Variant.Name + " is already running. Look for its icon in the system tray.", Variant.Name);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.Scale = g.DpiX / 96f;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WifiForm(Array.IndexOf(args, "--tray") >= 0, shot));
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Settings: registry, HKCU\Software\PhoneSyncWifi (value "Settings")
    // ------------------------------------------------------------------------------------
    public class WifiSettings
    {
        public string LocalFolder { get; set; }
        public int Port { get; set; }              // HTTP (download page) port; TLS (sync) port is Port + 1
        public string Token { get; set; }
        public string Address { get; set; }        // "" = automatic, "internet", or a chosen local IP
        public string CertPass { get; set; }
        public bool AllowTwoWay { get; set; }      // may the phone add / change / delete files in the shared folder?

        public static bool NoSave;
        const string RegKey = @"Software\" + Variant.Key;

        public WifiSettings()
        {
            LocalFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Variant.FolderPrefix);
            Port = 8765;
            Token = NewToken();
            Address = "";
            CertPass = NewToken();
        }

        public static string NewToken()
        {
            var b = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static WifiSettings Load()
        {
            WifiSettings s = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    string json = k == null ? null : k.GetValue("Settings") as string;
                    if (!string.IsNullOrEmpty(json)) s = new JavaScriptSerializer().Deserialize<WifiSettings>(json);
                }
            }
            catch { }
            if (s == null) { s = new WifiSettings(); s.Save(); }
            if (string.IsNullOrEmpty(s.LocalFolder)) s.LocalFolder = new WifiSettings().LocalFolder;
            if (s.Port < 1024 || s.Port > 65000) s.Port = 8765;
            if (string.IsNullOrEmpty(s.Token)) s.Token = NewToken();
            if (s.Address == null) s.Address = "";
            if (string.IsNullOrEmpty(s.CertPass)) { s.CertPass = NewToken(); s.Save(); }
            return s;
        }

        public void Save()
        {
            if (NoSave) return;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
                    k.SetValue("Settings", new JavaScriptSerializer().Serialize(this), RegistryValueKind.String);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------------------------
    // Tiny servers (no admin rights needed):
    //   HTTP  (Port)     download page for the phone app - public, no secrets
    //   HTTPS (Port + 1) manifest + file downloads, guarded by the pairing token, TLS with a self-signed certificate
    // ------------------------------------------------------------------------------------
    class WifiServer
    {
        public string Folder;
        public string Token;
        public int Port;
        public string CertPass;
        public volatile bool AllowUploads;       // two-way sync switch (PUT / DELETE on /api/file)
        public string Fingerprint = "";           // first 128 bits of the certificate's SHA-256, hex
        public event Action<string> Log;

        readonly object gate = new object();
        readonly Dictionary<string, DateTime> phones = new Dictionary<string, DateTime>();
        TcpListener httpListener, tlsListener;
        X509Certificate2 cert;
        volatile bool running;
        string cacheJson = "{\"version\":\"\",\"files\":[]}";
        string cacheVersion = "";
        DateTime cacheAt = DateTime.MinValue;
        public volatile string LastError = "";
        public long BytesSent;

        public bool Running { get { return running; } }
        public int TlsPort { get { return Port + 1; } }

        public int ConnectedPhones
        {
            get
            {
                lock (gate)
                {
                    int n = 0;
                    foreach (var kv in phones) if ((DateTime.Now - kv.Value).TotalSeconds < 20) n++;
                    return n;
                }
            }
        }

        public string PhoneNames
        {
            get
            {
                lock (gate)
                {
                    var l = new List<string>();
                    foreach (var kv in phones) if ((DateTime.Now - kv.Value).TotalSeconds < 20) l.Add(kv.Key);
                    return string.Join(", ", l.ToArray());
                }
            }
        }

        public void Start()
        {
            Stop();
            Directory.CreateDirectory(Folder);
            try { cert = LoadOrCreateCert(); }
            catch (Exception ex) { LastError = "Couldn't prepare the secure connection: " + ex.Message; return; }
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(cert.RawData);
                var sb = new StringBuilder();
                for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
                Fingerprint = sb.ToString();
            }
            for (int i = 0; i < 10; i++)
            {
                int p = Port + i * 2;
                try
                {
                    httpListener = new TcpListener(IPAddress.Any, p);
                    tlsListener = new TcpListener(IPAddress.Any, p + 1);
                    httpListener.Start();
                    tlsListener.Start();
                    Port = p;
                    break;
                }
                catch (SocketException)
                {
                    try { if (httpListener != null) httpListener.Stop(); } catch { }
                    httpListener = null; tlsListener = null;
                }
            }
            if (httpListener == null) { LastError = "Couldn't open a network port (is another copy running?)."; return; }
            running = true;
            LastError = "";
            StartLoop(httpListener, false);
            StartLoop(tlsListener, true);
            Say("Sharing " + Folder + " (ports " + Port + " and " + TlsPort + ")");
        }

        void StartLoop(TcpListener l, bool tls)
        {
            var t = new Thread(() => AcceptLoop(l, tls));
            t.IsBackground = true;
            t.Name = tls ? "https" : "http";
            t.Start();
        }

        public void Stop()
        {
            running = false;
            try { if (httpListener != null) httpListener.Stop(); } catch { }
            try { if (tlsListener != null) tlsListener.Stop(); } catch { }
            httpListener = null; tlsListener = null;
        }

        void Say(string s) { var h = Log; if (h != null) h(s); }

        // ---------------------------------------------------------------- certificate
        static string CertPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderSyncPC", "server.pfx"); }
        }

        /// <summary>Self-signed certificate, created once and kept in %APPDATA%\FolderSyncPC (needs .NET Framework 4.7.2+).</summary>
        X509Certificate2 LoadOrCreateCert()
        {
            string path = CertPath;
            if (File.Exists(path))
            {
                try { return new X509Certificate2(path, CertPass, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet); }
                catch { try { File.Delete(path); } catch { } }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Say("Creating the secure certificate (first start only)…");
            // 2048-bit RSA key, self-signed for 20 years, built in-process (no helper programs are started)
            using (var rsa = RSA.Create(2048))
            {
                var req = new CertificateRequest("CN=Folder Sync PC", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // server authentication
                using (var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20)))
                    File.WriteAllBytes(path, self.Export(X509ContentType.Pfx, CertPass));
            }
            return new X509Certificate2(path, CertPass, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        // ---------------------------------------------------------------- accept / one request
        void AcceptLoop(TcpListener l, bool tls)
        {
            while (running)
            {
                try
                {
                    var c = l.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(o => Handle((TcpClient)o, tls), c);
                }
                catch { if (!running) break; }
            }
        }

        void Handle(TcpClient c, bool tls)
        {
            try
            {
                c.ReceiveTimeout = 15000;
                c.SendTimeout = 60000;
                using (c)
                using (var raw = c.GetStream())
                {
                    Stream s = raw;
                    if (tls)
                    {
                        var ssl = new SslStream(raw, false);
                        ssl.AuthenticateAsServer(cert, false, (System.Security.Authentication.SslProtocols)3072, false);   // TLS 1.2
                        s = ssl;
                    }
                    byte[] leftover;
                    string head = ReadHead(s, out leftover);
                    if (head == null) return;
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines[0].Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0], target = parts[1];
                    bool headOnly = method == "HEAD";
                    string host = "";
                    long contentLength = -1;
                    foreach (var ln in lines)
                    {
                        if (ln.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = ln.Substring(5).Trim();
                        else if (ln.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) long.TryParse(ln.Substring(15).Trim(), out contentLength);
                    }
                    bool isPut = method == "PUT", isDelete = method == "DELETE";
                    if (method != "GET" && !headOnly && !(tls && (isPut || isDelete))) { Reply(s, 405, "text/plain", "Method not allowed", headOnly); return; }

                    string path = target, qs = "";
                    int q = target.IndexOf('?');
                    if (q >= 0) { path = target.Substring(0, q); qs = target.Substring(q + 1); }
                    var query = ParseQuery(qs);
                    string remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();

                    if (!tls)
                    {
                        // plain HTTP: only the public download page and the app file - no token, no folder data
                        if (path == "/") { Reply(s, 200, "text/html; charset=utf-8", DownloadPage(), headOnly); return; }
                        if (path == "/app.apk") { SendApk(s, headOnly); return; }
                        Reply(s, 404, "text/plain", "Not found", headOnly);
                        return;
                    }

                    if (path == "/") { Reply(s, 200, "text/plain; charset=utf-8", Variant.Name + " is running.\n", headOnly); return; }
                    string tok;
                    if (!query.TryGetValue("t", out tok) || !SameToken(tok, Token))
                    {
                        if (isPut) Reject(s, 401, "Scan the QR code shown on the PC.", contentLength, leftover);
                        else Reply(s, 401, "text/plain", "Scan the QR code shown on the PC.", headOnly);
                        return;
                    }
                    string dev;
                    if (!query.TryGetValue("d", out dev) || dev.Trim() == "") dev = remote;
                    dev = Clean(dev);

                    if (path == "/api/hello")
                    {
                        Touch(dev, remote);
                        var o = new Dictionary<string, object>();
                        o["pc"] = Environment.MachineName;
                        o["folder"] = Path.GetFileName(Folder.TrimEnd('\\'));
                        o["twoWay"] = AllowUploads;
                        Reply(s, 200, "application/json", new JavaScriptSerializer().Serialize(o), headOnly);
                        return;
                    }
                    if (path != "/api/file" && (isPut || isDelete)) { Reply(s, 404, "text/plain", "Not found", false); return; }
                    if (path == "/api/version") { Touch(dev, remote); Manifest(); Reply(s, 200, "text/plain", cacheVersion, headOnly); return; }
                    if (path == "/api/manifest") { Touch(dev, remote); Reply(s, 200, "application/json; charset=utf-8", Manifest(), headOnly); return; }
                    if (path == "/api/file")
                    {
                        string rel;
                        if (!query.TryGetValue("p", out rel)) { Reply(s, 400, "text/plain", "Missing path", false); return; }
                        Touch(dev, remote);
                        if (isPut) ReceiveFile(s, query, rel, dev, contentLength, leftover);
                        else if (isDelete) DeleteFile(s, query, rel, dev);
                        else SendFile(s, rel, dev, headOnly);
                        return;
                    }
                    Reply(s, 404, "text/plain", "Not found", headOnly);
                }
            }
            catch { }
        }

        void Touch(string device, string remote)
        {
            bool isNew;
            lock (gate)
            {
                DateTime last;
                isNew = !phones.TryGetValue(device, out last) || (DateTime.Now - last).TotalMinutes > 2;
                phones[device] = DateTime.Now;
            }
            if (isNew) Say("Phone connected: " + device + " (" + remote + ")");
        }

        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s) if (!char.IsControl(ch) && ch != '<' && ch != '>' && ch != '&' && ch != '"') sb.Append(ch);
            string r = sb.ToString().Trim();
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        static bool SameToken(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static string ReadHead(Stream s, out byte[] leftover)
        {
            leftover = new byte[0];
            var buf = new byte[4096];
            var all = new MemoryStream();
            while (all.Length < 16384)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) return null;
                all.Write(buf, 0, n);
                byte[] data = all.ToArray();
                for (int i = 3; i < data.Length; i++)
                {
                    if (data[i] == '\n' && data[i - 1] == '\r' && data[i - 2] == '\n' && data[i - 3] == '\r')
                    {
                        leftover = new byte[data.Length - (i + 1)];
                        Array.Copy(data, i + 1, leftover, 0, leftover.Length);
                        return Encoding.ASCII.GetString(data, 0, i - 3);
                    }
                }
            }
            return null;
        }

        static Dictionary<string, string> ParseQuery(string qs)
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in qs.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = kv.IndexOf('=');
                string k = i < 0 ? kv : kv.Substring(0, i);
                string v = i < 0 ? "" : kv.Substring(i + 1);
                try { d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
            }
            return d;
        }

        static void Reply(Stream s, int code, string type, string body, bool headOnly)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            WriteHead(s, code, type, b.Length);
            if (!headOnly) s.Write(b, 0, b.Length);
            s.Flush();
        }

        static void WriteHead(Stream s, int code, string type, long length)
        {
            string text = code == 200 ? "OK" : code == 400 ? "Bad Request" : code == 401 ? "Unauthorized" : code == 404 ? "Not Found" : "Error";
            string h = "HTTP/1.1 " + code + " " + text + "\r\nContent-Type: " + type + "\r\nContent-Length: " + length +
                       "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(h);
            s.Write(hb, 0, hb.Length);
        }

        // ---------------------------------------------------------------- content
        string Manifest()
        {
            lock (gate)
            {
                if ((DateTime.Now - cacheAt).TotalSeconds < 2) return cacheJson;
                var files = new List<Dictionary<string, object>>();
                var sig = new StringBuilder();
                try
                {
                    string root = Path.GetFullPath(Folder).TrimEnd('\\') + "\\";
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileName(f).ToLowerInvariant();
                        if (name == "desktop.ini" || name == "thumbs.db" || name.StartsWith("~$") || name.EndsWith("~") ||
                            name.EndsWith(".tmp") || name.EndsWith(".part") || name.EndsWith(".crdownload")) continue;
                        try
                        {
                            var fi = new FileInfo(f);
                            string rel = f.Substring(root.Length).Replace('\\', '/');
                            long m = (long)(fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                            var o = new Dictionary<string, object>();
                            o["p"] = rel; o["s"] = fi.Length; o["m"] = m;
                            files.Add(o);
                            sig.Append(rel).Append('|').Append(fi.Length).Append('|').Append(m).Append('\n');
                        }
                        catch { }
                    }
                }
                catch { }
                using (var sha = SHA1.Create())
                {
                    var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sig.ToString()));
                    var sb = new StringBuilder();
                    for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                    cacheVersion = sb.ToString();
                }
                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                var doc = new Dictionary<string, object>();
                doc["version"] = cacheVersion;
                doc["files"] = files;
                cacheJson = ser.Serialize(doc);
                cacheAt = DateTime.Now;
                return cacheJson;
            }
        }

        void SendFile(Stream s, string rel, string dev, bool headOnly)
        {
            string root = Path.GetFullPath(Folder).TrimEnd('\\') + "\\";
            string full;
            try
            {
                if (rel.Contains("..") || rel.StartsWith("/") || rel.Contains(":")) throw new Exception();
                full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception();
            }
            catch { Reply(s, 400, "text/plain", "Bad path", false); return; }
            if (!File.Exists(full)) { Reply(s, 404, "text/plain", "No such file", headOnly); return; }
            try
            {
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    WriteHead(s, 200, "application/octet-stream", fs.Length);
                    if (headOnly) return;
                    var buf = new byte[64 * 1024];
                    long sent = 0;
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) { s.Write(buf, 0, n); sent += n; }
                    s.Flush();
                    Interlocked.Add(ref BytesSent, sent);
                    Say("Sent " + rel + "  (" + Human(sent) + ")  to " + dev);
                }
            }
            catch (Exception) { /* phone went away mid-transfer; it will retry */ }
        }

        // ---------------------------------------------------------------- two-way: the phone may write (only when switched on)
        static readonly string[] BlockedExt = { ".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".msi", ".msp", ".lnk", ".hta", ".cpl", ".reg", ".inf", ".pif", ".jar", ".appx", ".msix" };
        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        const long MaxUpload = 8L * 1024 * 1024 * 1024;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Turns a relative path from the phone into a full path inside the shared folder, or null if it is not acceptable.</summary>
        string ResolvePath(string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Length > 600 || rel.StartsWith("/") || rel.Contains("\\") || rel.Contains(":")) return null;
            foreach (var seg in rel.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == ".." || seg.EndsWith(".") || seg.EndsWith(" ") || seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                string stem = seg.Split('.')[0].ToUpperInvariant();
                if (Array.IndexOf(Reserved, stem) >= 0) return null;
            }
            string root = Path.GetFullPath(Folder).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>Does the PC file still look the way the phone last saw it? ("-" = the phone expects it not to exist)</summary>
        static bool Matches(string full, Dictionary<string, string> q)
        {
            string ps, pm;
            if (!q.TryGetValue("ps", out ps)) ps = "-";
            if (!q.TryGetValue("pm", out pm)) pm = "-";
            bool exists = File.Exists(full);
            if (ps == "-") return !exists;
            if (!exists) return false;
            var fi = new FileInfo(full);
            long m = (long)(fi.LastWriteTimeUtc - Epoch).TotalSeconds;
            return ps == fi.Length.ToString() && pm == m.ToString();
        }

        /// <summary>Answers a refused upload. The upload body is read and thrown away first (up to 256 MB), otherwise the
        /// connection reset would hide our answer from the phone.</summary>
        static void Reject(Stream s, int code, string msg, long len, byte[] leftover)
        {
            long remaining = len - leftover.Length;
            if (remaining > 0 && remaining <= 256L * 1024 * 1024)
            {
                var buf = new byte[64 * 1024];
                try
                {
                    while (remaining > 0)
                    {
                        int n = s.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                        if (n <= 0) break;
                        remaining -= n;
                    }
                }
                catch { }
            }
            Reply(s, code, "text/plain", msg, false);
        }

        void ReceiveFile(Stream s, Dictionary<string, string> q, string rel, string dev, long len, byte[] leftover)
        {
            if (!AllowUploads) { Reject(s, 403, "Two-way sync is turned off on the PC.", len, leftover); return; }
            string full = ResolvePath(rel);
            if (full == null) { Reject(s, 400, "Bad path", len, leftover); return; }
            if (Array.IndexOf(BlockedExt, Path.GetExtension(full).ToLowerInvariant()) >= 0) { Reject(s, 415, "This file type can't be sent to the PC.", len, leftover); return; }
            if (len < 0 || len > MaxUpload) { Reject(s, 400, "Bad size", len, leftover); return; }
            if (Directory.Exists(full)) { Reject(s, 409, "A folder has that name", len, leftover); return; }
            if (!Matches(full, q)) { Reject(s, 409, "The PC file changed meanwhile", len, leftover); return; }
            string ms; long mtime;
            if (!q.TryGetValue("m", out ms) || !long.TryParse(ms, out mtime)) mtime = (long)(DateTime.UtcNow - Epoch).TotalSeconds;
            string tmp = full + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
            long got = 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int take = (int)Math.Min(leftover.Length, len);
                    if (take > 0) { fs.Write(leftover, 0, take); got += take; }
                    var buf = new byte[64 * 1024];
                    while (got < len)
                    {
                        int n = s.Read(buf, 0, (int)Math.Min(buf.Length, len - got));
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                        got += n;
                    }
                }
                if (got != len) { try { File.Delete(tmp); } catch { } return; }     // the phone went away: nothing is kept
                File.SetLastWriteTimeUtc(tmp, Epoch.AddSeconds(mtime));
                if (File.Exists(full)) File.Replace(tmp, full, null); else File.Move(tmp, full);
                File.SetLastWriteTimeUtc(full, Epoch.AddSeconds(mtime));
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                Reply(s, 500, "text/plain", "Couldn't save the file: " + ex.Message, false);
                return;
            }
            lock (gate) cacheAt = DateTime.MinValue;
            var fi = new FileInfo(full);
            var o = new Dictionary<string, object>();
            o["s"] = fi.Length;
            o["m"] = (long)(fi.LastWriteTimeUtc - Epoch).TotalSeconds;
            Say("Received " + rel + "  (" + Human(len) + ")  from " + dev);
            Reply(s, 200, "application/json", new JavaScriptSerializer().Serialize(o), false);
        }

        void DeleteFile(Stream s, Dictionary<string, string> q, string rel, string dev)
        {
            if (!AllowUploads) { Reply(s, 403, "text/plain", "Two-way sync is turned off on the PC.", false); return; }
            string full = ResolvePath(rel);
            if (full == null) { Reply(s, 400, "text/plain", "Bad path", false); return; }
            if (!File.Exists(full)) { Reply(s, 200, "application/json", "{\"deleted\":false}", false); return; }
            if (!Matches(full, q)) { Reply(s, 409, "text/plain", "The PC file changed meanwhile", false); return; }
            try
            {
                // never a hard delete: the file goes to the Recycle Bin
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                string root = Path.GetFullPath(Folder).TrimEnd('\\');
                string dir = Path.GetDirectoryName(full);
                while (dir != null && dir.Length > root.Length && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex) { Reply(s, 500, "text/plain", "Couldn't delete: " + ex.Message, false); return; }
            lock (gate) cacheAt = DateTime.MinValue;
            Say("Deleted " + rel + " (moved to the Recycle Bin) at the request of " + dev);
            Reply(s, 200, "application/json", "{\"deleted\":true}", false);
        }

        public static string Human(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }

        void SendApk(Stream s, bool headOnly)
        {
            using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("FolderSync.apk"))
            {
                if (rs == null) { Reply(s, 404, "text/plain", "The app file is not bundled with this build.", headOnly); return; }
                WriteHead(s, 200, "application/vnd.android.package-archive", rs.Length);
                if (headOnly) return;
                var buf = new byte[64 * 1024];
                int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0) s.Write(buf, 0, n);
                s.Flush();
                Say("A phone downloaded the Folder Sync Android app.");
            }
        }

        static string DownloadPage()
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>Folder Sync Android</title><style>");
            sb.Append("body{margin:0;padding:24px;background:#110c08;color:#faf2e8;font:18px system-ui,sans-serif}");
            sb.Append("h1{font-size:26px;margin:0 0 8px}p{color:#a69480;line-height:1.5}");
            sb.Append("a.b{display:block;text-align:center;padding:18px;margin:18px 0;border-radius:16px;font-weight:700;text-decoration:none;");
            sb.Append("background:#ff9933;color:#1c1104}</style></head><body>");
            sb.Append("<h1>Folder Sync Android</h1><p>The phone app for <b>" + Environment.MachineName + "</b>'s Folder Sync PC.</p>");
            sb.Append("<a class=\"b\" href=\"/app.apk\">Download the app</a>");
            sb.Append("<p>Open the downloaded file to install it. If Android asks, allow \"install unknown apps\" for this browser. ");
            sb.Append("Then open Folder Sync Android, tap Scan QR code, and scan the pairing code on the PC.</p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------------------------
    // QR image helper
    // ------------------------------------------------------------------------------------
    static class QrImage
    {
        public static Bitmap Make(string text)
        {
            var m = Qr.Encode(text);
            int n = m.GetLength(0), quiet = 4, cell = 8;
            int dim = (n + quiet * 2) * cell;
            var bmp = new Bitmap(dim, dim);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (m[y, x]) g.FillRectangle(Brushes.Black, (x + quiet) * cell, (y + quiet) * cell, cell, cell);
            }
            return bmp;
        }
    }

    /// <summary>Second QR code: the page that downloads the phone app (needed only once per phone).</summary>
    class InstallDialog : Form
    {
        public InstallDialog(string url)
        {
            Text = "Get the phone app";
            Font = Ui.F(11f);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            ClientSize = new Size(Ui.Px(560), Ui.Px(700));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Padding = new Padding(Ui.Px(24));
            t.BackColor = Ui.Bg;
            Controls.Add(t);

            var title = new Label();
            title.Text = "Install Folder Sync Android";
            title.Font = Ui.FB(18f);
            title.AutoSize = true;
            title.Margin = new Padding(0, 0, 0, Ui.Px(8));
            t.Controls.Add(title);

            var info = new Label();
            info.Text = "Scan this code with the phone's camera (phone on the same Wi-Fi as this PC), open the page, and download the app. " +
                        "Install it - allow \"install unknown apps\" for the browser if Android asks. You only need to do this once.";
            info.AutoSize = true;
            info.MaximumSize = new Size(Ui.Px(500), 0);
            info.ForeColor = Ui.Muted;
            info.Margin = new Padding(0, 0, 0, Ui.Px(16));
            t.Controls.Add(info);

            var pic = new PictureBox();
            pic.Size = new Size(Ui.Px(320), Ui.Px(320));
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.White;
            pic.Margin = new Padding(0, 0, 0, Ui.Px(12));
            try { pic.Image = QrImage.Make(url); } catch { }
            t.Controls.Add(pic);

            var addr = new Label();
            addr.Text = url;
            addr.AutoSize = true;
            addr.ForeColor = Ui.Muted;
            addr.Font = Ui.F(10f);
            addr.Margin = new Padding(0, 0, 0, Ui.Px(16));
            t.Controls.Add(addr);

            var close = new FlatButton();
            close.Text = "Close";
            close.Margin = new Padding(0);
            close.Click += (o, e) => Close();
            t.Controls.Add(close);
        }
    }

    // ------------------------------------------------------------------------------------
    // Window + tray
    // ------------------------------------------------------------------------------------
    class WifiForm : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly WifiSettings settings;
        readonly WifiServer server = new WifiServer();
        readonly NotifyIcon tray;
        readonly LogList log;
        readonly Label status, activity, netNote;
        readonly Picker addressBox;
        readonly Field folderBox;
        readonly PictureBox qrBox;
        readonly ToggleSwitch startupBox, twoWayBox;
        bool revertingTwoWay;
        readonly bool startHidden;
        readonly string shotPath;
        bool reallyExit, toldAboutTray, started, filling;
        string lastLog = "";
        string internetHost = "";               // public IP once Internet mode is ready
        Upnp upnp;                              // holds the router mapping so we can remove it again
        int mappedPort;
        int internetJob;                        // ignore results of outdated setups

        class Item
        {
            public string Key, Text;
            public override string ToString() { return Text; }
        }

        public WifiForm(bool startHidden, string shotPath)
        {
            this.startHidden = startHidden;
            this.shotPath = shotPath;
            settings = WifiSettings.Load();

            Text = Variant.Name;
            Font = Ui.F(11f);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            ClientSize = new Size(Ui.Px(980), Ui.Px(shotPath != null ? 1180 : 860));
            MinimumSize = new Size(Ui.Px(640), Ui.Px(560));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Ui.MakeIcon();

            var scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Ui.Bg;
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Padding = new Padding(Ui.Px(28), Ui.Px(24), Ui.Px(28), Ui.Px(24));
            root.BackColor = Ui.Bg;
            Controls.Add(scroll);
            scroll.Controls.Add(root);
            int gap = Ui.Px(18);

            // ---- header
            var head = new TableLayoutPanel();
            head.AutoSize = true;
            head.ColumnCount = 2;
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            head.Margin = new Padding(0, 0, 0, gap);
            head.BackColor = Ui.Bg;
            var logo = new PictureBox();
            logo.Image = Icon.ToBitmap();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Size = new Size(Ui.Px(60), Ui.Px(60));
            logo.Margin = new Padding(0, 0, Ui.Px(16), 0);
            logo.BackColor = Ui.Bg;
            head.Controls.Add(logo, 0, 0);
            var titles = new TableLayoutPanel();
            titles.AutoSize = true;
            titles.ColumnCount = 1;
            titles.Anchor = AnchorStyles.Left;
            titles.BackColor = Ui.Bg;
            var title = new Label();
            title.Text = Variant.Name;
            title.Font = Ui.FB(22f);
            title.AutoSize = true;
            title.Margin = new Padding(0);
            var sub = new Label();
            sub.Text = Variant.Tagline;
            sub.Font = Ui.F(11f);
            sub.ForeColor = Ui.Muted;
            sub.AutoSize = true;
            sub.Margin = new Padding(0);
            titles.Controls.Add(title);
            titles.Controls.Add(sub);
            head.Controls.Add(titles, 1, 0);
            root.Controls.Add(head);

            // ---- status card
            var statusCard = NewCard(gap);
            var st = new TableLayoutPanel();
            st.AutoSize = true;
            st.Dock = DockStyle.Fill;
            st.ColumnCount = 1;
            st.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            st.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Px(38)));
            st.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Px(30)));
            st.BackColor = Ui.Card;
            status = new Label();
            status.AutoSize = false;
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Font = Ui.FB(15f);
            status.Margin = new Padding(0);
            status.BackColor = Ui.Card;
            activity = new Label();
            activity.AutoSize = false;
            activity.AutoEllipsis = true;
            activity.Dock = DockStyle.Fill;
            activity.TextAlign = ContentAlignment.MiddleLeft;
            activity.Font = Ui.F(11f);
            activity.ForeColor = Ui.Muted;
            activity.Margin = new Padding(0);
            activity.BackColor = Ui.Card;
            st.Controls.Add(status, 0, 0);
            st.Controls.Add(activity, 0, 1);
            statusCard.Controls.Add(st);

            // ---- pairing card: QR on the left, steps on the right
            var pairCard = NewCard(gap);
            var pt = new TableLayoutPanel();
            pt.AutoSize = true;
            pt.Dock = DockStyle.Fill;
            pt.ColumnCount = 2;
            pt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pt.BackColor = Ui.Card;
            qrBox = new PictureBox();
            qrBox.Size = new Size(Ui.Px(300), Ui.Px(300));
            qrBox.SizeMode = PictureBoxSizeMode.Zoom;
            qrBox.BackColor = Color.White;
            qrBox.Margin = new Padding(0, 0, Ui.Px(24), 0);
            pt.Controls.Add(qrBox, 0, 0);

            var right = new TableLayoutPanel();
            right.AutoSize = true;
            right.Dock = DockStyle.Fill;
            right.ColumnCount = 1;
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.BackColor = Ui.Card;
            right.Controls.Add(Ui.Caption("Pair a phone"));
            var steps = new Label();
            steps.Text = "1  First time only: click Get the phone app and install Folder Sync Android.\n" +
                         "2  Open Folder Sync Android on the phone and tap Scan QR code.\n" +
                         "3  Scan this code, choose a phone folder, and allow access when Android asks.";
            steps.AutoSize = true;
            steps.MaximumSize = new Size(Ui.Px(520), 0);
            steps.Font = Ui.F(11f);
            steps.Margin = new Padding(0, 0, 0, Ui.Px(12));
            steps.BackColor = Ui.Card;
            right.Controls.Add(steps);
            right.Controls.Add(Ui.Caption("Network address"));
            addressBox = new Picker();
            addressBox.Dock = DockStyle.Fill;
            addressBox.Margin = new Padding(0, 0, 0, Ui.Px(8));
            addressBox.SelectedIndexChanged += (o, e) => { if (!filling) AddressChanged(); };
            right.Controls.Add(addressBox);
            netNote = new Label();
            netNote.AutoSize = true;
            netNote.MaximumSize = new Size(Ui.Px(520), 0);
            netNote.Font = Ui.F(10f);
            netNote.ForeColor = Ui.Muted;
            netNote.Margin = new Padding(0, 0, 0, Ui.Px(10));
            netNote.BackColor = Ui.Card;
            right.Controls.Add(netNote);
            var btns = new FlowLayoutPanel();
            btns.AutoSize = true;
            btns.WrapContents = true;
            btns.Margin = new Padding(0);
            btns.BackColor = Ui.Card;
            var getApp = Btn("Get the phone app", (o, e) => ShowInstall());
            getApp.Margin = new Padding(0, 0, Ui.Px(10), 0);
            var newCode = Btn("New code", (o, e) => NewCode());
            newCode.Margin = new Padding(0);
            btns.Controls.Add(getApp);
            btns.Controls.Add(newCode);
            right.Controls.Add(btns);
            pt.Controls.Add(right, 1, 0);
            pairCard.Controls.Add(pt);

            // ---- folder card
            var folderCard = NewCard(gap);
            var ft = new TableLayoutPanel();
            ft.AutoSize = true;
            ft.Dock = DockStyle.Fill;
            ft.ColumnCount = 3;
            ft.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ft.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ft.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ft.BackColor = Ui.Card;
            var fcap = Ui.Caption("PC folder shared with the phone");
            ft.Controls.Add(fcap, 0, 0);
            ft.SetColumnSpan(fcap, 3);
            folderBox = new Field(true);
            folderBox.Dock = DockStyle.Fill;
            folderBox.Margin = new Padding(0);
            ft.Controls.Add(folderBox, 0, 1);
            ft.Controls.Add(Btn("Open", (o, e) => OpenFolder()), 1, 1);
            ft.Controls.Add(Btn("Change…", (o, e) => ChangeFolder()), 2, 1);
            twoWayBox = new ToggleSwitch();
            twoWayBox.Text = "Two-way sync: let the phone add, change and delete files in this folder";
            twoWayBox.Checked = settings.AllowTwoWay;
            twoWayBox.Margin = new Padding(0, Ui.Px(14), 0, 0);
            twoWayBox.CheckedChanged += (o, e) => TwoWayChanged();
            ft.Controls.Add(twoWayBox, 0, 2);
            ft.SetColumnSpan(twoWayBox, 3);
            folderCard.Controls.Add(ft);

            // ---- activity card
            var logCard = NewCard(gap);
            var lt = new TableLayoutPanel();
            lt.AutoSize = true;
            lt.Dock = DockStyle.Fill;
            lt.ColumnCount = 1;
            lt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lt.BackColor = Ui.Card;
            lt.Controls.Add(Ui.Caption("Activity"));
            log = new LogList();
            log.Dock = DockStyle.Fill;
            log.Margin = new Padding(0);
            log.Height = Ui.Px(200);
            lt.Controls.Add(log, 0, 1);
            logCard.Controls.Add(lt);

            // ---- startup + note
            startupBox = new ToggleSwitch();
            startupBox.Text = "Start " + Variant.Name + " with Windows";
            startupBox.Checked = IsStartupEnabled();
            startupBox.CheckedChanged += (o, e) => SetStartup(startupBox.Checked);
            startupBox.Margin = new Padding(Ui.Px(4), 0, 0, Ui.Px(10));
            var note = new Label();
            note.Text = "Same network: the phone and PC on one Wi-Fi, including this PC joined to the phone's own hotspot (pick that address). " +
                        "Internet: pick \"Internet\" under Network address to reach this PC from anywhere over mobile data or any Wi-Fi; " +
                        "that needs your router to allow it, and many mobile and home connections share one public address and won't work. " +
                        "Over the internet or on public Wi-Fi, transfers can be slow and may use mobile data that you are charged for. " +
                        "Files are encrypted in transit and the phone only trusts this PC's certificate from the QR code. " +
                        "Windows may ask to allow this app through the firewall: tick Private and Public networks. \"New code\" locks out old pairings.";
            note.AutoSize = true;
            note.MaximumSize = new Size(Ui.Px(880), 0);
            note.Font = Ui.F(10f);
            note.ForeColor = Ui.Muted;
            note.Margin = new Padding(Ui.Px(4), 0, 0, 0);

            root.Controls.Add(statusCard);
            root.Controls.Add(pairCard);
            root.Controls.Add(folderCard);
            root.Controls.Add(logCard);
            root.Controls.Add(startupBox);
            root.Controls.Add(note);

            // ---- tray
            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = Variant.Name;
            tray.Visible = shotPath == null;
            var menu = new ContextMenuStrip();
            menu.Font = Ui.F(11f);
            menu.ShowImageMargin = false;
            menu.Items.Add("Open " + Variant.Name, null, (o, e) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (o, e) => { reallyExit = true; Close(); });
            foreach (ToolStripItem it in menu.Items) if (!(it is ToolStripSeparator)) it.Padding = new Padding(0, Ui.Px(8), 0, Ui.Px(8));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (o, e) => ShowWindow();

            server.Log += msg => { try { BeginInvoke((Action)(() => AddLog(msg))); } catch { } };

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += (o, e) => RefreshStatus();
            timer.Start();

            Load += (o, e) =>
            {
                if (started) return;
                started = true;
                folderBox.Box.Text = settings.LocalFolder;
                FillAddresses();
                if (shotPath != null) { Demo(); return; }
                // certificate creation can take a few seconds the first time: do it off the UI thread
                server.Folder = settings.LocalFolder;
                server.Token = settings.Token;
                server.Port = settings.Port;
                server.CertPass = settings.CertPass;
                server.AllowUploads = settings.AllowTwoWay;
                ThreadPool.QueueUserWorkItem(x =>
                {
                    try { server.Start(); } catch (Exception ex) { server.LastError = ex.Message; }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (server.Running && server.Port != settings.Port) { settings.Port = server.Port; settings.Save(); }
                            AddressChanged();
                            RefreshStatus();
                        }));
                    }
                    catch { }
                });
                RefreshStatus();
            };
        }

        // ---------------------------------------------------------------- addresses / pairing code
        bool IsInternet { get { var it = addressBox.SelectedItem as Item; return it != null && it.Key == "internet"; } }

        /// <summary>The PC's address on the local network (the selected one, or the best guess).</summary>
        string LanIp()
        {
            var it = addressBox.SelectedItem as Item;
            if (it != null && it.Key != "internet") return it.Key;
            foreach (var x in addressBox.Items) { var i = x as Item; if (i != null && i.Key != "internet" && i.Key != "127.0.0.1") return i.Key; }
            return "127.0.0.1";
        }

        void FillAddresses()
        {
            filling = true;
            try
            {
                addressBox.Items.Clear();
                Item pick = null, first = null;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    bool hasGateway = false;
                    foreach (var g in props.GatewayAddresses) if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) hasGateway = true;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254.")) continue;
                        var it = new Item();
                        it.Key = ip;
                        it.Text = ip + "   (" + ni.Name + ")";
                        addressBox.Items.Add(it);
                        if (first == null) first = it;
                        if (ip == settings.Address) pick = it;
                        else if (pick == null && settings.Address != "internet" && hasGateway && Upnp.IsPrivate(ip)) pick = it;
                    }
                }
                if (addressBox.Items.Count == 0)
                {
                    var none = new Item(); none.Key = "127.0.0.1"; none.Text = "No network found";
                    addressBox.Items.Add(none); first = none;
                }
                var net = new Item();
                net.Key = "internet";
                net.Text = "Internet (public IP)";
                addressBox.Items.Add(net);
                if (settings.Address == "internet") pick = net;
                addressBox.SelectedItem = pick ?? first;
            }
            finally { filling = false; }
        }

        void AddressChanged()
        {
            var it = addressBox.SelectedItem as Item;
            if (it != null) { settings.Address = it.Key; settings.Save(); }
            if (IsInternet) SetupInternet();
            else
            {
                CloseMapping();
                internetJob++;
                netNote.Text = "Same network. If the phone is sharing its hotspot, join it from this PC and pick that address (usually 192.168.x.x).";
                RebuildQr();
            }
        }

        void RebuildQr()
        {
            string host = IsInternet ? internetHost : LanIp();
            var old = qrBox.Image;
            qrBox.Image = null;
            if (old != null) old.Dispose();
            if (host == "" || !server.Running || server.Fingerprint == "")
            {
                if (shotPath == null) return;      // nothing to show yet (server starting / internet not ready)
            }
            try
            {
                qrBox.Image = QrImage.Make("afs://" + host + ":" + server.TlsPort + "/" + settings.Token + "?f=" + server.Fingerprint);
            }
            catch (Exception ex) { AddLog("Couldn't draw the QR code: " + ex.Message); }
        }

        void ShowInstall()
        {
            using (var dlg = new InstallDialog("http://" + LanIp() + ":" + server.Port + "/")) dlg.ShowDialog(this);
        }

        // ---------------------------------------------------------------- internet mode (UPnP port forward + public IP)
        void SetupInternet()
        {
            int job = ++internetJob;
            internetHost = "";
            netNote.Text = "Setting up internet access…";
            RebuildQr();
            if (!server.Running) return;           // called again when the server is up
            string lan = LanIp();
            int tlsPort = server.TlsPort;
            CloseMapping();
            ThreadPool.QueueUserWorkItem(o =>
            {
                string host = "", note;
                var u = new Upnp();
                bool mapped = false;
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    mapped = u.Discover(lan) && u.AddMapping(tlsPort, lan, tlsPort, Variant.Name);
                }
                catch { }
                string ip = mapped ? u.ExternalIp : "";
                if (mapped && ip != "" && !Upnp.IsPrivate(ip))
                {
                    host = ip;
                    note = "Internet address " + ip + ":" + tlsPort + " - the port was opened on your router automatically.";
                }
                else
                {
                    string pub = Upnp.LookupPublicIp();
                    if (mapped)
                    {
                        host = pub;
                        note = ip != "" && Upnp.IsPrivate(ip)
                            ? "Your router's address is " + ip + ", which the internet can't reach (your provider shares one address). Internet mode won't work here. ASK YOUR ISP TO OPEN TCP PORT " + tlsPort + " ON YOUR ROUTER FOR TRANSFERRING THROUGH THE INTERNET, or SWITCH TO THE SAME NETWORK ON BOTH YOUR DEVICES."
                            : "The port was opened on your router" + (pub != "" ? ", reachable at " + pub + ":" + tlsPort + "." : ", but the public address couldn't be found.");
                    }
                    else
                    {
                        host = pub;
                        note = pub != ""
                            ? "Couldn't open the port automatically (" + u.Error + "). Switch on UPnP in your router, or forward TCP port " + tlsPort + " on your router to this PC (" + lan + "); then it works at " + pub + ":" + tlsPort + ". " +
                              "ASK YOUR ISP TO OPEN TCP PORT " + tlsPort + " ON YOUR ROUTER FOR TRANSFERRING THROUGH THE INTERNET, or SWITCH TO THE SAME NETWORK ON BOTH YOUR DEVICES."
                            : "No internet address available (" + u.Error + "). ASK YOUR ISP TO OPEN TCP PORT " + tlsPort + " ON YOUR ROUTER FOR TRANSFERRING THROUGH THE INTERNET, or SWITCH TO THE SAME NETWORK ON BOTH YOUR DEVICES.";
                    }
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (job != internetJob) { if (mapped) u.RemoveMapping(tlsPort); return; }
                        upnp = mapped ? u : null;
                        mappedPort = mapped ? tlsPort : 0;
                        internetHost = host;
                        netNote.Text = note;
                        RebuildQr();
                        AddLog(note);
                    }));
                }
                catch { }
            });
        }

        void CloseMapping()
        {
            if (upnp != null && mappedPort != 0)
            {
                var u = upnp; int p = mappedPort;
                ThreadPool.QueueUserWorkItem(o => u.RemoveMapping(p));
            }
            upnp = null; mappedPort = 0;
        }

        void TwoWayChanged()
        {
            if (revertingTwoWay) return;
            if (twoWayBox.Checked)
            {
                var a = MessageBox.Show(this,
                    "Let the phone add, change and delete files in\n\n    " + settings.LocalFolder + "\n\nDeleted files go to the Recycle Bin. Only turn this on for a folder you are happy to have changed from the phone, " +
                    "and keep the pairing code private (anyone with it could then change this folder).",
                    Variant.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (a != DialogResult.Yes) { revertingTwoWay = true; twoWayBox.Checked = false; revertingTwoWay = false; return; }
            }
            settings.AllowTwoWay = twoWayBox.Checked;
            settings.Save();
            server.AllowUploads = settings.AllowTwoWay;
            AddLog(settings.AllowTwoWay ? "Two-way sync is ON: the phone may change files in the shared folder." : "Two-way sync is OFF: the phone can only read.");
        }

        void NewCode()
        {
            var a = MessageBox.Show(this, "Make a new pairing code?\n\nPhones paired with the old code stop syncing until they scan the new one.",
                Variant.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (a != DialogResult.Yes) return;
            settings.Token = WifiSettings.NewToken();
            settings.Save();
            server.Token = settings.Token;
            RebuildQr();
            AddLog("New pairing code created. Old pairings are disconnected.");
        }

        // ---------------------------------------------------------------- folder
        void OpenFolder()
        {
            Directory.CreateDirectory(settings.LocalFolder);
            Process.Start("explorer.exe", "\"" + settings.LocalFolder + "\"");
        }

        void ChangeFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Pick the PC folder to share with the phone";
                dlg.SelectedPath = settings.LocalFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                settings.LocalFolder = dlg.SelectedPath;
                settings.Save();
                folderBox.Box.Text = settings.LocalFolder;
                server.Folder = settings.LocalFolder;
                AddLog("Now sharing " + settings.LocalFolder);
            }
        }

        // ---------------------------------------------------------------- status / log
        void RefreshStatus()
        {
            if (shotPath != null) return;
            if (!server.Running)
            {
                status.Text = server.LastError != "" ? "○ Not running" : "○ Starting…";
                status.ForeColor = Ui.Warn;
                activity.Text = server.LastError != "" ? server.LastError : "Preparing the secure connection (first start can take a few seconds)";
            }
            else
            {
                int n = server.ConnectedPhones;
                status.Text = (n > 0 ? "● " : "○ ") + (n == 0 ? "Ready - waiting for a phone" : n == 1 ? "1 phone connected: " + server.PhoneNames : n + " phones connected: " + server.PhoneNames);
                status.ForeColor = n > 0 ? Ui.Good : Ui.Accent;
                activity.Text = lastLog != "" ? lastLog : "Sharing " + settings.LocalFolder;
            }
            string tip = Variant.Name + " - " + (server.ConnectedPhones == 0 ? "no phone connected" : server.ConnectedPhones + " connected");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void AddLog(string msg)
        {
            lastLog = msg;
            log.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "   " + msg);
            while (log.Items.Count > 500) log.Items.RemoveAt(0);
            log.TopIndex = Math.Max(0, log.Items.Count - 1);
        }

        /// <summary>--shot file.png: sample data, saves a screenshot of the window, exits. Touches no settings.</summary>
        void Demo()
        {
            TopMost = true;
            Activate();
            server.Port = 8765;
            server.Fingerprint = "0123456789abcdef0123456789abcdef";
            settings.LocalFolder = @"C:\Users\you\Desktop\WiFi Sync";
            folderBox.Box.Text = settings.LocalFolder;
            settings.Token = "0123456789abcdef01234567";
            netNote.Text = "Same network. If the phone is sharing its hotspot, join it from this PC and pick that address (usually 192.168.x.x).";
            RebuildQr();
            status.Text = "● 1 phone connected: OnePlus CPH2805";
            status.ForeColor = Ui.Good;
            activity.Text = "Sent Trip\\IMG_20260310_1842.jpg  (3.2 MB)  to OnePlus CPH2805";
            AddLog("Sharing C:\\Users\\you\\Desktop\\WiFi Sync (ports 8765 and 8766)");
            AddLog("Phone connected: OnePlus CPH2805 (192.168.1.23)");
            AddLog("Sent Trip\\IMG_20260310_1841.jpg  (2.8 MB)  to OnePlus CPH2805");
            AddLog("Sent Trip\\VID_20260310_1843.mp4  (41 MB)  to OnePlus CPH2805");
            var tm = new System.Windows.Forms.Timer();
            tm.Interval = 1500;
            tm.Tick += (o, e) =>
            {
                tm.Stop();
                try
                {
                    using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(PointToScreen(Point.Empty), Point.Empty, ClientSize);
                        bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch { }
                reallyExit = true; Close();
            };
            tm.Start();
        }

        // ---------------------------------------------------------------- window / tray
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated) { CreateHandle(); OnLoad(EventArgs.Empty); value = false; }
            base.SetVisibleCore(value);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!toldAboutTray)
                {
                    toldAboutTray = true;
                    tray.ShowBalloonTip(4000, Variant.Name + " is still running",
                        "It keeps sharing from the system tray. Right-click the icon -> Exit to quit.", ToolTipIcon.Info);
                }
                return;
            }
            if (upnp != null && mappedPort != 0) { try { upnp.RemoveMapping(mappedPort); } catch { } }   // close the router port again
            server.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        static bool IsStartupEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Variant.RunValue) != null; }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(Variant.RunValue, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(Variant.RunValue, false);
                }
                AddLog(on ? "Will start automatically when you sign in to Windows." : "Won't start with Windows.");
            }
            catch (Exception ex) { AddLog("Couldn't change startup setting: " + ex.Message); }
        }

        // ---------------------------------------------------------------- layout helpers
        static Card NewCard(int gap)
        {
            var c = new Card();
            c.AutoSize = true;
            c.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            c.Padding = new Padding(Ui.Px(22));
            c.Margin = new Padding(0, 0, 0, gap);
            return c;
        }

        static FlatButton Btn(string text, EventHandler click)
        {
            var b = new FlatButton();
            b.Text = text;
            b.Click += click;
            return b;
        }
    }
}
