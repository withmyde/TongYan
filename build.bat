@echo off
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /win32icon:app.ico /out:TongYan.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Core.dll src\Logger.cs src\Config.cs src\MemoryManager.cs src\StaWorker.cs src\ClipboardHelper.cs src\HotKey.cs src\AiClient.cs src\ResultForm.cs src\SettingsForm.cs src\Program.cs
if errorlevel 1 goto error
echo [Build Success] TongYan.exe generated successfully!
goto end
:error
echo [Build Error] Failed to compile!
pause
:end
