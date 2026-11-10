# Folder Sync

Drop files into a folder on your PC and they appear on your Android phone, over Wi-Fi or the internet.
Two apps work together:

- **Folder Sync PC** - a portable Windows app (one `.exe`, no installer) that shares one PC folder and shows a pairing QR code.
- **Folder Sync Android** - the phone app. It scans the QR code, asks you to pick a phone folder, and keeps that folder
  in step with the PC folder in the background.

No developer options or cables are needed.

## Get it

Download from the [Releases page](https://github.com/anshu-sbu/Android-Folder-Sync/releases):

- `Folder-Sync-PC.exe` - run it from anywhere. Needs .NET Framework 4.7.2 or newer (built into current Windows 10/11).
  It needs no admin rights and installs nothing; settings are kept in the registry under `HKCU\Software\PhoneSyncWifi`.
  Use its **Start with Windows** switch if you want it to run at sign-in.
- `Folder-Sync-Android.apk` - the phone app (or let Folder Sync PC hand it to the phone, see below).
- `SHA256SUMS.txt` - checksums to verify the downloads.

The Windows file is **not code-signed**, so SmartScreen or an antivirus may warn about a new unknown program (a
"false positive"). Verify the download against `SHA256SUMS.txt`, or build everything yourself from this source.

## Use it

1. Run **Folder Sync PC**. It shows a pairing QR code (the first start takes a few seconds while it creates its
   security certificate).
2. First time only: click **Get the phone app**, scan that second QR code with the phone's camera, download
   **Folder Sync Android**, and install it (allow "install unknown apps" for the browser when asked).
3. Open **Folder Sync Android**, tap **Scan QR code**, and scan the pairing code on the PC.
4. Tap **Choose folder**, pick a phone folder and allow access when Android asks.
5. Files in the shared PC folder (default `Desktop\WiFi Sync`) are copied into the phone folder and updated when they
   change. The phone app keeps syncing in the background (a notification shows its status).

Pairing is QR only - there is no link to copy or paste. By default sync goes from the PC to the phone; see
[Two-way sync](#two-way-sync-optional) to send phone changes back.

### Where the phone can be

Chosen under **Network address** in Folder Sync PC:

- **Same network** - the phone and PC on one Wi-Fi, or the PC joined to the phone's own hotspot (pick that address,
  usually `192.168.x.x`; this uses no mobile data because the traffic stays between the two devices).
- **Internet** - reach the PC from anywhere over mobile data or any Wi-Fi, using the PC's public IP. The app asks your
  router to open the port automatically (UPnP). Many routers, ISPs and mobile connections don't allow that (they share one
  public address); then switch on UPnP, forward the port yourself, ask your ISP to open it, or use the same network.

**Over the internet or on public Wi-Fi, transfers can be slow and may use mobile data that you are charged for.**

### Two-way sync (optional)

Off by default. To keep the two folders identical in both directions, turn on **both**:

- in Folder Sync PC: **Two-way sync: let the phone add, change and delete files in this folder** (it asks first);
- in Folder Sync Android: **Two-way sync**, and optionally **Sync deletions too**.

How it decides: each side is compared with how it looked at the last sync, never with the other device's clock.

| What happened | Result |
|---|---|
| Changed on the PC only | downloaded to the phone |
| Changed on the phone only | sent to the PC |
| Changed on **both** | both versions kept: the phone's becomes `name (phone copy yyyyMMdd-HHmm).ext` on both sides |
| Deleted on one side | deleted on the other **only if "Sync deletions too" is on** (otherwise kept and not sent back) |
| Deleted on one side, changed on the other | the changed file wins and is restored |

Safety: files deleted on the PC at the phone's request go to the **Recycle Bin** (deletions on the phone are permanent, as
Android has no recycle bin for app folders). The PC refuses changes if its file changed since the phone last saw it, never
accepts files outside the shared folder, and blocks program and script types (`.exe`, `.bat`, `.ps1`, `.js`, `.lnk`, ...).
The phone rescans its folder every 20 seconds, because Android can't announce changes in a chosen folder. Empty folders
are not synced, and the same names that differ only by letter case on the phone can collide on Windows.

### Security

Files are encrypted in transit (TLS). The PC creates its own self-signed certificate; the QR code carries the certificate's
fingerprint and a secret token, and the phone only talks to a server with exactly that certificate. In Internet mode the
shared folder is reachable by anyone who has the pairing code while the app is running (and, with two-way sync on, they could
add, change and delete files in it), so keep the code private, share
only folders you are happy to expose, and use **New code** to revoke old pairings. Windows may ask to allow the app
through the firewall - allow Private and Public networks.

## Build from source

Needs the C# compiler that ships with Windows, and (for the phone app) the Android SDK build-tools and the JDK
bundled with Android Studio.

```
bash FolderSyncApp/build.sh   # builds FolderSyncApp/FolderSync.apk (Git Bash)
build.cmd                     # builds Folder Sync PC.exe (embeds the apk)
```

Source layout: `WifiSync.cs` (Folder Sync PC and its servers), `Qr.cs` (QR code generator), `Upnp.cs` (router port
forwarding), `Ui.cs` (look and controls), and `FolderSyncApp/` (Folder Sync Android, Java, no Gradle; uses ZXing for QR
decoding).

## Disclaimer and license

This is a **personal project**, shared as-is for reference. It is for personal, non-commercial use
only, with no support or warranty. See [LICENSE.txt](LICENSE.txt). Not affiliated with Google.
