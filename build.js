const { execSync } = require('child_process');
const fs = require('fs');

const csc = 'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe';

if (!fs.existsSync(csc)) {
  console.error('[Error] C# compiler not found at: ' + csc);
  process.exit(1);
}

const args = [
  '/nologo',
  '/target:winexe',
  '/optimize+',
  '/platform:anycpu',
  '/win32icon:app.ico',
  '/out:TongYan.exe',
  '/r:System.dll',
  '/r:System.Drawing.dll',
  '/r:System.Windows.Forms.dll',
  '/r:System.Web.Extensions.dll',
  '/r:System.Core.dll',
  'src\\Logger.cs',
  'src\\Config.cs',
  'src\\MemoryManager.cs',
  'src\\StaWorker.cs',
  'src\\ClipboardHelper.cs',
  'src\\HotKey.cs',
  'src\\AiClient.cs',
  'src\\ResultForm.cs',
  'src\\SettingsForm.cs',
  'src\\Program.cs'
];

console.log('[1/2] 正在编译 TongYan.exe...');
try {
  execSync('taskkill /F /IM TongYan.exe', { stdio: 'ignore' });
} catch (e) {}
const cmd = `"${csc}" ${args.join(' ')}`;

try {
  const stdout = execSync(cmd, { encoding: 'utf8' });
  if (stdout && stdout.trim()) {
    console.log(stdout);
  }
  if (fs.existsSync('TongYan.exe')) {
    const stat = fs.statSync('TongYan.exe');
    console.log(`[2/2] 编译成功！生成文件: TongYan.exe (大小: ${(stat.size / 1024).toFixed(1)} KB)`);
  }
} catch (err) {
  console.error('[Build Error]');
  if (err.stdout) console.error(err.stdout);
  if (err.stderr) console.error(err.stderr);
  process.exit(1);
}
