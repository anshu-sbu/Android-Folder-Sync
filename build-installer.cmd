@echo off
rem Builds Folder Sync PC, then its installer (shows LICENSE.txt, checks for .NET, installs per user).
cd /d "%~dp0"
call build.cmd || exit /b 1
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe /win32icon:WifiSync.ico /out:"Folder Sync PC Setup.exe" /resource:"Folder Sync PC.exe",App.exe /resource:LICENSE.txt,EULA.txt %REFS% Installer.cs
if errorlevel 1 (echo INSTALLER BUILD FAILED & exit /b 1)
echo Built "Folder Sync PC Setup.exe" in %~dp0
