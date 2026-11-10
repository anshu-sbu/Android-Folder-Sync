@echo off
rem Builds Folder Sync PC with the C# compiler that ships with Windows (.NET Framework 4.x).
rem It embeds FolderSyncApp\FolderSync.apk (the phone app) - build that first with: bash FolderSyncApp/build.sh
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll
if not exist FolderSyncApp\FolderSync.apk (echo FolderSyncApp\FolderSync.apk is missing - build it with FolderSyncApp\build.sh first & exit /b 1)
"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe /win32icon:WifiSync.ico /out:"Folder Sync PC.exe" /resource:FolderSyncApp\FolderSync.apk,FolderSync.apk %REFS% Ui.cs Qr.cs Upnp.cs WifiSync.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built "Folder Sync PC.exe" in %~dp0
