// Android Folder Sync installer (per-user, no admin needed).
//  - makes sure .NET Framework 4.5+ is present (downloads Microsoft's installer if not)
//  - shows the licence (EULA) and requires acceptance
//  - copies the app, adds Start Menu / Desktop shortcuts and an "Apps & features" uninstall entry
// Run with /uninstall (or from Apps & features) to remove it. Settings in HKCU\Software\PhoneSync are kept.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Android Folder Sync Setup")]
[assembly: AssemblyProduct("Android Folder Sync")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace PhoneSyncSetup
{
    static class Setup
    {
        const string AppName = "Folder Sync PC";
        const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AndroidFolderSyncWifi";
        const string DotNetUrl = "https://go.microsoft.com/fwlink/?linkid=2085155"; // .NET Framework 4.8 web installer

        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\" + AppName); } }
        static string StartMenuLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        static Form form; static Label status; static ProgressBar bar;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool uninstall = Array.IndexOf(args, "/uninstall") >= 0;
            if (uninstall && MessageBox.Show("Remove " + AppName + "?\r\nYour settings in the registry are kept.", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!uninstall && !AcceptEula()) return;

            form = new Form { Text = AppName + " Setup", Width = 440, Height = 130, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ControlBox = false };
            status = new Label { Left = 14, Top = 14, Width = 400, Height = 20, Text = "Starting…" };
            bar = new ProgressBar { Left = 14, Top = 44, Width = 400, Height = 20, Style = ProgressBarStyle.Marquee };
            form.Controls.Add(status); form.Controls.Add(bar);
            string error = null;
            form.Shown += (o, e) =>
            {
                var t = new Thread(() =>
                {
                    try { if (uninstall) Uninstall(); else Install(); }
                    catch (Exception ex) { error = ex.Message; }
                    form.BeginInvoke((Action)(() => form.Close()));
                });
                t.IsBackground = true; t.Start();
            };
            Application.Run(form);

            if (error != null) { MessageBox.Show("Setup failed:\r\n" + error, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (uninstall) { MessageBox.Show(AppName + " was removed.", AppName); return; }
            if (MessageBox.Show(AppName + " is installed.\r\nStart it now?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(Path.Combine(InstallDir, AppName + ".exe"));
        }

        static void Say(string s) { form.BeginInvoke((Action)(() => status.Text = s)); }

        // ---------------------------------------------------------------- install
        static void Install()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls; // TLS 1.2 / 1.1 / 1.0
            EnsureDotNet();

            Say("Stopping any running copy…");
            foreach (var p in Process.GetProcessesByName(AppName)) { try { p.Kill(); p.WaitForExit(3000); } catch { } }

            Say("Copying " + AppName + "…");
            Directory.CreateDirectory(InstallDir);
            string exe = Path.Combine(InstallDir, AppName + ".exe");
            using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("App.exe"))
            using (var fs = File.Create(exe)) rs.CopyTo(fs);
            string un = Path.Combine(InstallDir, "Uninstall.exe");
            try { File.Copy(Application.ExecutablePath, un, true); } catch { }

            try { File.WriteAllText(Path.Combine(InstallDir, "LICENSE.txt"), EulaText(), System.Text.Encoding.UTF8); } catch { }

            Say("Creating shortcuts…");
            MakeShortcut(StartMenuLnk, exe);
            MakeShortcut(DesktopLnk, exe);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", "1.0.0");
                k.SetValue("Publisher", AppName);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        static void EnsureDotNet()
        {
            Say("Checking .NET Framework…");
            int rel = 0;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                if (k != null && k.GetValue("Release") is int) rel = (int)k.GetValue("Release");
            if (rel >= 378389) return; // 4.5 or newer — included with Windows 10/11

            string tmp = Path.Combine(Path.GetTempPath(), "ndp48-web.exe");
            Download(DotNetUrl, tmp, "Downloading .NET Framework 4.8…");
            Say("Installing .NET Framework (this can take a few minutes)…");
            var pr = Process.Start(new ProcessStartInfo(tmp, "/q /norestart") { Verb = "runas", UseShellExecute = true });
            pr.WaitForExit();
            if (pr.ExitCode != 0 && pr.ExitCode != 3010 && pr.ExitCode != 1641)
                throw new Exception(".NET Framework installer exited with code " + pr.ExitCode);
        }

        static string EulaText()
        {
            using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("EULA.txt"))
            using (var sr = new StreamReader(rs)) return sr.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>Shows the licence; true only if the user ticks "I agree" and clicks Install.</summary>
        static bool AcceptEula()
        {
            using (var f = new Form())
            {
                f.Text = AppName + " Setup - License Agreement";
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(700, 560);
                f.MinimumSize = new Size(520, 420);
                f.Font = new Font("Segoe UI", 10f);
                var head = new Label { Text = "Please read the license agreement", Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI Semibold", 13f), Padding = new Padding(12, 10, 0, 0) };
                var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = EulaText(), BackColor = Color.White, Font = new Font("Consolas", 9.5f) };
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 110 };
                var agree = new CheckBox { Text = "I have read and agree to the terms of this license agreement", AutoSize = true, Left = 14, Top = 14 };
                var ok = new Button { Text = "Install", Width = 130, Height = 40, Enabled = false, Top = 52, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Width = 130, Height = 40, Top = 52, DialogResult = DialogResult.Cancel };
                bottom.Controls.Add(agree); bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
                EventHandler place = (o, e) => { cancel.Left = bottom.Width - cancel.Width - 14; ok.Left = cancel.Left - ok.Width - 10; };
                bottom.Resize += place;
                agree.CheckedChanged += (o, e) => ok.Enabled = agree.Checked;
                f.Controls.Add(box); f.Controls.Add(bottom); f.Controls.Add(head);
                f.CancelButton = cancel;
                f.Shown += (o, e) => { place(null, null); box.Select(0, 0); };
                return f.ShowDialog() == DialogResult.OK;
            }
        }

        static void Download(string url, string dest, string message)
        {
            Say(message);
            form.BeginInvoke((Action)(() => { bar.Style = ProgressBarStyle.Continuous; bar.Value = 0; }));
            using (var wc = new WebClient())
            {
                var done = new ManualResetEvent(false); Exception err = null;
                wc.DownloadProgressChanged += (o, e) => form.BeginInvoke((Action)(() => bar.Value = Math.Min(100, e.ProgressPercentage)));
                wc.DownloadFileCompleted += (o, e) => { err = e.Error; done.Set(); };
                wc.DownloadFileAsync(new Uri(url), dest);
                done.WaitOne();
                if (err != null) throw new Exception("Download failed (" + url + "): " + err.Message);
            }
            form.BeginInvoke((Action)(() => bar.Style = ProgressBarStyle.Marquee));
        }

        static void MakeShortcut(string lnk, string target)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { AppName });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            catch { }
        }

        // ---------------------------------------------------------------- uninstall
        static void Uninstall()
        {
            Say("Stopping " + AppName + "…");
            foreach (var p in Process.GetProcessesByName(AppName)) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            Say("Removing files…");
            try { File.Delete(StartMenuLnk); } catch { }
            try { File.Delete(DesktopLnk); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            // this running Uninstall.exe can't delete itself: remove everything else, then schedule the folder removal
            try
            {
                foreach (var f in Directory.GetFiles(InstallDir)) { if (!f.EndsWith("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { } }
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }
    }
}
