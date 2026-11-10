// Folder Sync PC installer (per-user, no admin needed).
//  - shows the licence (EULA) and requires acceptance
//  - checks that .NET Framework 4.7.2 or newer is present (Windows 10/11 include it)
//  - asks you to close a running copy, then copies the app
//  - adds Start Menu / Desktop shortcuts and an "Apps & features" uninstall entry
// Run Uninstall.exe (or use Apps & features) to remove it. Settings in HKCU\Software\PhoneSyncWifi are kept.
// Deliberately does none of the things installers are often flagged for: it never kills processes, downloads and
// runs programs, or starts command shells.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Folder Sync PC Setup")]
[assembly: AssemblyProduct("Folder Sync PC")]
[assembly: AssemblyCompany("Prabhanshu Mishra")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Prabhanshu Mishra")]
[assembly: AssemblyDescription("Installer for Folder Sync PC")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PhoneSyncSetup
{
    static class Setup
    {
        const string AppName = "Folder Sync PC";
        const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AndroidFolderSyncWifi";
        const int NetFx472 = 461808;     // "Release" registry value of .NET Framework 4.7.2
        const string DotNetPage = "https://dotnet.microsoft.com/download/dotnet-framework";

        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\" + AppName); } }
        static string StartMenuLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        static Form form; static Label status; static ProgressBar bar;

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool uninstall = Array.IndexOf(args, "/uninstall") >= 0;
            bool fromTemp = Array.IndexOf(args, "/confirmed") >= 0;

            if (uninstall && !fromTemp)
            {
                // a running program can't delete itself: run a copy from the temp folder instead
                if (MessageBox.Show("Remove " + AppName + "?\r\nYour settings in the registry are kept.", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "FolderSyncPC-uninstall.exe");
                    File.Copy(Application.ExecutablePath, tmp, true);
                    Process.Start(tmp, "/uninstall /confirmed");
                }
                catch (Exception ex) { MessageBox.Show("Couldn't start the uninstaller: " + ex.Message, AppName); }
                return;
            }
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

        /// <summary>If the app is running, asks the user to close it (we never force-close it) and waits for them.</summary>
        static void EnsureAppClosed()
        {
            while (Process.GetProcessesByName(AppName).Length > 0)
            {
                DialogResult r = DialogResult.Cancel;
                form.Invoke((Action)(() => r = MessageBox.Show(form,
                    AppName + " is running.\r\n\r\nClose it from its tray icon (right-click the icon, then Exit), then click Retry.",
                    AppName + " Setup", MessageBoxButtons.RetryCancel, MessageBoxIcon.Information)));
                if (r != DialogResult.Retry) throw new Exception("Cancelled: " + AppName + " is still running.");
            }
            Thread.Sleep(700);   // let Windows release the file
        }

        /// <summary>Writes an embedded file, retrying for a few seconds while it is still locked.</summary>
        static void WriteResource(string resource, string dest)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                    using (var fs = File.Create(dest)) rs.CopyTo(fs);
                    return;
                }
                catch (IOException) { if (i >= 20) throw new Exception(Path.GetFileName(dest) + " is in use. Close " + AppName + " and run setup again."); Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { if (i >= 20) throw new Exception("Couldn't write " + Path.GetFileName(dest) + " (access denied)."); Thread.Sleep(500); }
            }
        }

        // ---------------------------------------------------------------- install
        static void Install()
        {
            CheckDotNet();

            Say("Checking that " + AppName + " is closed…");
            EnsureAppClosed();

            Say("Copying " + AppName + "…");
            Directory.CreateDirectory(InstallDir);
            string exe = Path.Combine(InstallDir, AppName + ".exe");
            WriteResource("App.exe", exe);
            string un = Path.Combine(InstallDir, "Uninstall.exe");
            try { File.Copy(Application.ExecutablePath, un, true); } catch { }
            try { File.WriteAllText(Path.Combine(InstallDir, "LICENSE.txt"), EulaText(), Encoding.UTF8); } catch { }

            Say("Creating shortcuts…");
            MakeShortcut(StartMenuLnk, exe);
            MakeShortcut(DesktopLnk, exe);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", "1.0.0");
                k.SetValue("Publisher", "Prabhanshu Mishra");
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        /// <summary>Folder Sync PC needs .NET Framework 4.7.2+ (built into current Windows 10 and 11). We don't download anything.</summary>
        static void CheckDotNet()
        {
            Say("Checking .NET Framework…");
            int rel = 0;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                if (k != null && k.GetValue("Release") is int) rel = (int)k.GetValue("Release");
            if (rel >= NetFx472) return;
            DialogResult r = DialogResult.No;
            form.Invoke((Action)(() => r = MessageBox.Show(form,
                AppName + " needs .NET Framework 4.7.2 or newer, which this PC doesn't have yet.\r\n\r\nOpen Microsoft's download page now? Install .NET Framework 4.8, then run setup again.",
                AppName + " Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)));
            if (r == DialogResult.Yes) { try { Process.Start(DotNetPage); } catch { } }
            throw new Exception(".NET Framework 4.7.2 or newer is required.");
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

        // ---------------------------------------------------------------- shortcuts (standard Windows shell link)
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
        interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        static void MakeShortcut(string lnk, string target)
        {
            try
            {
                var link = (IShellLinkW)new ShellLink();
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetDescription(AppName);
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(lnk, true);
            }
            catch { }
        }

        // ---------------------------------------------------------------- uninstall (runs from a temp copy of this file)
        static void Uninstall()
        {
            Say("Checking that " + AppName + " is closed…");
            EnsureAppClosed();
            Say("Removing files…");
            try { File.Delete(StartMenuLnk); } catch { }
            try { File.Delete(DesktopLnk); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            for (int i = 0; i < 10 && Directory.Exists(InstallDir); i++)
            {
                try { Directory.Delete(InstallDir, true); }
                catch { Thread.Sleep(500); }
            }
        }
    }
}
