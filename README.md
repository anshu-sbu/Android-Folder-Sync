# Folder Sync

Drop files into a folder on your PC and they appear on your Android phone, over Wi-Fi or the internet.
Two apps work together:

- **Folder Sync PC** - a Windows app that shares one PC folder and shows a pairing QR code.
- **Folder Sync Android** - the phone app. It scans the QR code, asks you to pick a phone folder, and keeps that folder
  in step with the PC folder in the background.

No developer options or cables are needed.

## Use it

1. Run **Folder Sync PC**. It shows a pairing QR code (the first start takes a few seconds while it creates its
   security certificate).
2. First time only: click **Get the phone app**, scan that second QR code with the phone's camera, download
   **Folder Sync Android**, and install it (allow "install unknown apps" for the browser when asked).
3. Open **Folder Sync Android**, tap **Scan QR code**, and scan the pairing code on the PC.
4. Tap **Choose folder**, pick a phone folder and allow access when Android asks.
5. Files in the shared PC folder (default `Desktop\WiFi Sync`) are copied into the phone folder and updated when they
   change. The phone app keeps syncing in the background (a notification shows its status).

Pairing is QR only - there is no link to copy or paste. Sync goes from the PC to the phone.

### Where the phone can be

Chosen under **Network address** in Folder Sync PC:

- **Same network** - the phone and PC on one Wi-Fi, or the PC joined to the phone's own hotspot (pick that address,
  usually `192.168.x.x`; this uses no mobile data because the traffic stays between the two devices).
- **Internet** - reach the PC from anywhere over mobile data or any Wi-Fi, using the PC's public IP. The app asks your
  router to open the port automatically (UPnP). Many routers, ISPs and mobile connections don't allow that (they share one
  public address); then switch on UPnP, forward the port yourself, ask your ISP to open it, or use the same network.

**Over the internet or on public Wi-Fi, transfers can be slow and may use mobile data that you are charged for.**

### Security

Files are encrypted in transit (TLS). The PC creates its own self-signed certificate; the QR code carries the certificate's
fingerprint and a secret token, and the phone only talks to a server with exactly that certificate. In Internet mode the
shared folder is reachable by anyone who has the pairing code while the app is running, so keep the code private, share
only folders you are happy to expose, and use **New code** to revoke old pairings. Windows may ask to allow the app
through the firewall - allow Private and Public networks.

## Install

Run `Folder Sync PC Setup.exe`. It shows the license, checks for .NET Framework, and installs per user (no admin).
Settings live in the registry under `HKCU\Software\PhoneSyncWifi`.

## Build from source

Needs the C# compiler that ships with Windows, and (for the phone app) the Android SDK build-tools and the JDK
bundled with Android Studio.

```
bash FolderSyncApp/build.sh   # builds FolderSyncApp/FolderSync.apk (Git Bash)
build.cmd                     # builds Folder Sync PC (embeds the apk)
build-installer.cmd           # builds the app and its installer
```

Source layout: `WifiSync.cs` (Folder Sync PC and its servers), `Qr.cs` (QR code generator), `Upnp.cs` (router port
forwarding), `Ui.cs` (look and controls), `Installer.cs`, and `FolderSyncApp/` (Folder Sync Android, Java, no Gradle;
uses ZXing for QR decoding).

## Disclaimer and license

This is a **personal project**, shared as-is for reference. It is for personal, non-commercial use
only, with no support or warranty. See [LICENSE.txt](LICENSE.txt). Not affiliated with Google.
