@echo off
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /target:winexe /optimize /out:"%~dp0XboxBatteryTray.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%WM%\Windows.Gaming.winmd" /r:"%WM%\Windows.Devices.winmd" /r:"%WM%\Windows.System.winmd" /r:"%WM%\Windows.Foundation.winmd" /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /resource:"%~dp0xbox-controller.png",FullIcon.png "%~dp0XboxBatteryTray.cs"
