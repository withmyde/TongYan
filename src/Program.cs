using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("通言 TongYan")]
[assembly: AssemblyDescription("Windows 桌面划词 AI 助手")]
[assembly: AssemblyProduct("通言 TongYan")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace TongYan
{
    static class Program
    {
        private static Mutex _mutex;
        private static NotifyIcon _trayIcon;
        private static GlobalHotKey _hotKey;
        private static AppConfig _config;
        private static ResultForm _activeResultForm;
        private static bool _isSettingsOpen = false;
        private static long _requestIdCounter = 0;

        private static MenuItem _menuHotkeyTip;
        private static MenuItem _menuPrompts;
        private static MenuItem _menuDisableThinking;
        private static MenuItem _menuAutoStart;

        public static Icon AppIcon { get; private set; }

        public static bool HasActiveWindow
        {
            get
            {
                if (_isSettingsOpen) return true;
                return _activeResultForm != null && !_activeResultForm.IsDisposed;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Logger.Error("UI 线程未捕获异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Logger.Error("AppDomain 未捕获异常", e.ExceptionObject as Exception);
            };

            bool createdNew = false;
            try
            {
                _mutex = new Mutex(true, "Local\\TongYan_SingleInstance_Mutex_98765", out createdNew);
                if (!createdNew)
                {
                    try
                    {
                        if (_mutex.WaitOne(0, false))
                        {
                            createdNew = true;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        createdNew = true;
                    }
                }
            }
            catch
            {
                createdNew = true;
            }

            if (!createdNew)
            {
                MessageBox.Show("通言已在后台运行中", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppIcon = LoadAppIcon();

            Logger.Info("=== 通言 v1.0.0 启动 ===");

            _config = AppConfig.Load();

            if (_config.auto_start)
            {
                SetAutoStart(true);
            }

            InitTray();

            MemoryManager.IsActiveWindowPredicate = () => HasActiveWindow;

            StaWorker.EnsureStarted();

            _hotKey = new GlobalHotKey();
            _hotKey.HotKeyPressed += OnHotKeyPressed;
            bool registered = _hotKey.Register(_config.hotkey);

            if (registered)
            {
                Logger.Info("全局快捷键注册成功: " + _config.hotkey);
                _trayIcon.ShowBalloonTip(1500, "通言已启动", "快捷键已就绪: " + _config.hotkey + "，选中文本后按下即可触发。", ToolTipIcon.Info);
            }
            else
            {
                Logger.Warn("快捷键注册失败: " + _config.hotkey + "，可能被其他软件占用");
                MessageBox.Show("快捷键 " + _config.hotkey + " 已被占用，请在设置中更换。", "快捷键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (string.IsNullOrEmpty(_config.base_url) || string.IsNullOrEmpty(_config.api_key) || string.IsNullOrEmpty(_config.model))
            {
                ShowSettingsDialog();
            }

            MemoryManager.FlushMemory();

            Application.Run();

            Logger.Info("程序退出清理");
            StaWorker.Stop();
            if (_activeResultForm != null)
            {
                try { _activeResultForm.Dispose(); } catch { }
                _activeResultForm = null;
            }
            if (_hotKey != null) _hotKey.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
        }

        private static void InitTray()
        {
            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "通言: " + _config.hotkey;
            _trayIcon.Icon = AppIcon ?? CreateFallbackIcon();
            _trayIcon.Visible = true;

            ContextMenu menu = new ContextMenu();
            _menuHotkeyTip = new MenuItem("快捷键: " + _config.hotkey)
            {
                Enabled = false
            };
            menu.MenuItems.Add(_menuHotkeyTip);

            menu.MenuItems.Add("设置", (s, e) => ShowSettingsDialog());

            _menuPrompts = new MenuItem("切换提示词");
            RefreshPromptMenuItems();
            menu.MenuItems.Add(_menuPrompts);

            _menuDisableThinking = new MenuItem("关闭思考模式", (s, e) =>
            {
                bool nextState = !_menuDisableThinking.Checked;
                _menuDisableThinking.Checked = nextState;
                _config.disable_thinking = nextState;
                _config.Save();
                _trayIcon.ShowBalloonTip(1500, "通言设置", nextState ? "已关闭思考模式" : "已开启思考模式", ToolTipIcon.Info);
            });
            _menuDisableThinking.Checked = _config.disable_thinking;
            menu.MenuItems.Add(_menuDisableThinking);

            _menuAutoStart = new MenuItem("开机自启动", (s, e) =>
            {
                bool nextState = !_menuAutoStart.Checked;
                SetAutoStart(nextState);
                _menuAutoStart.Checked = nextState;
                _config.auto_start = nextState;
                _config.Save();
                _trayIcon.ShowBalloonTip(1500, "通言设置", nextState ? "已开启自启动" : "已关闭自启动", ToolTipIcon.Info);
            });
            _menuAutoStart.Checked = IsAutoStartEnabled();
            menu.MenuItems.Add(_menuAutoStart);

            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", (s, e) =>
            {
                Application.Exit();
            });

            _trayIcon.ContextMenu = menu;
            _trayIcon.DoubleClick += (s, e) => ShowSettingsDialog();
        }

        private static void RefreshPromptMenuItems()
        {
            if (_menuPrompts == null) return;
            _menuPrompts.MenuItems.Clear();

            _config.EnsureValid();
            for (int i = 0; i < _config.prompts.Count; i++)
            {
                PromptPreset preset = _config.prompts[i];
                if (preset == null) continue;

                string pid = preset.id;
                string pname = preset.name;
                MenuItem item = new MenuItem(pname, (s, e) =>
                {
                    _config.active_prompt_id = pid;
                    _config.Save();
                    RefreshPromptMenuItems();
                    _trayIcon.ShowBalloonTip(1500, "提示词已切换", "当前提示词: " + pname, ToolTipIcon.Info);
                });
                item.Checked = pid == _config.active_prompt_id;
                _menuPrompts.MenuItems.Add(item);
            }
        }

        private static void OnHotKeyPressed()
        {
            Point cursor = Cursor.Position;
            Logger.Info(string.Format("快捷键触发，光标位置: X={0}, Y={1}", cursor.X, cursor.Y));

            long requestId = Interlocked.Increment(ref _requestIdCounter);
            AiClient.AbortCurrent();

            if (_activeResultForm != null)
            {
                try
                {
                    if (!_activeResultForm.IsDisposed)
                    {
                        _activeResultForm.Close();
                        _activeResultForm.Dispose();
                    }
                }
                catch { }
                _activeResultForm = null;
            }

            ResultForm form = new ResultForm(cursor);
            _activeResultForm = form;

            form.FormClosed += (s, e) =>
            {
                if (_activeResultForm == form)
                {
                    _activeResultForm = null;
                }
                if (Interlocked.Read(ref _requestIdCounter) == requestId)
                {
                    AiClient.AbortCurrent();
                }
                MemoryManager.CompactMemoryAsync(250);
            };

            form.Show();

            StaWorker.Post(() =>
            {
                if (Interlocked.Read(ref _requestIdCounter) != requestId) return;

                string selectedText = null;
                try
                {
                    selectedText = ClipboardHelper.GetSelectedText();
                }
                catch (Exception ex)
                {
                    Logger.Error("异步提取选中文本异常", ex);
                }

                if (Interlocked.Read(ref _requestIdCounter) != requestId) return;

                try
                {
                    if (form.IsDisposed) return;

                    form.BeginInvoke(new Action(() =>
                    {
                        if (form.IsDisposed) return;
                        if (Interlocked.Read(ref _requestIdCounter) != requestId)
                        {
                            Logger.Info("丢弃过时的划词请求 ReqID=" + requestId);
                            return;
                        }

                        int textLen = string.IsNullOrEmpty(selectedText) ? 0 : selectedText.Length;
                        Logger.Info(string.Format("划词文本获取完毕 ReqID={0}，字符数: {1}", requestId, textLen));

                        if (string.IsNullOrEmpty(selectedText))
                        {
                            form.Activate();
                            form.ShowNotice("未检测到选中文本");
                            return;
                        }

                        form.UpdateTargetSize(textLen);
                        form.SetStatusText("正在请求...");
                        form.Activate();

                        AiClient.StreamChatAsync(
                            requestId,
                            selectedText,
                            _config,
                            () =>
                            {
                                if (Interlocked.Read(ref _requestIdCounter) != requestId) return;
                                if (form != null && !form.IsDisposed)
                                {
                                    form.SetStatusText("正在思考...");
                                }
                            },
                            chunk =>
                            {
                                if (Interlocked.Read(ref _requestIdCounter) != requestId) return;
                                if (form != null && !form.IsDisposed)
                                {
                                    form.AppendChunk(chunk);
                                }
                            },
                            () =>
                            {
                                if (Interlocked.Read(ref _requestIdCounter) != requestId) return;
                                if (form != null && !form.IsDisposed)
                                {
                                    form.Complete();
                                }
                            },
                            error =>
                            {
                                if (Interlocked.Read(ref _requestIdCounter) != requestId) return;
                                if (form != null && !form.IsDisposed)
                                {
                                    form.SetError(error);
                                }
                            }
                        );
                    }));
                }
                catch { }
            }, clearPending: true);
        }

        private static void ShowSettingsDialog()
        {
            if (_isSettingsOpen) return;
            _isSettingsOpen = true;
            string oldHotkey = _config.hotkey;

            try
            {
                using (SettingsForm form = new SettingsForm(_config, () =>
                {
                    SetAutoStart(_config.auto_start);
                    if (_menuAutoStart != null)
                    {
                        _menuAutoStart.Checked = _config.auto_start;
                    }
                    if (_menuDisableThinking != null)
                    {
                        _menuDisableThinking.Checked = _config.disable_thinking;
                    }
                    RefreshPromptMenuItems();

                    bool hotkeyChanged = !string.Equals(oldHotkey, _config.hotkey, StringComparison.OrdinalIgnoreCase);
                    if (hotkeyChanged)
                    {
                        bool ok = _hotKey.Register(_config.hotkey);
                        _trayIcon.Text = "通言: " + _config.hotkey;
                        if (_menuHotkeyTip != null)
                        {
                            _menuHotkeyTip.Text = "快捷键: " + _config.hotkey;
                        }

                        if (ok)
                        {
                            Logger.Info("快捷键重新注册成功: " + _config.hotkey);
                            _trayIcon.ShowBalloonTip(2000, "设置已保存", "快捷键已更新为: " + _config.hotkey, ToolTipIcon.Info);
                        }
                        else
                        {
                            Logger.Warn("快捷键重新注册失败: " + _config.hotkey);
                            MessageBox.Show("快捷键 " + _config.hotkey + " 已被占用，请在设置中更换。", "快捷键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    else
                    {
                        _trayIcon.ShowBalloonTip(1500, "设置已保存", "通言配置已更新", ToolTipIcon.Info);
                    }
                }))
                {
                    form.ShowDialog();
                }
            }
            finally
            {
                _isSettingsOpen = false;
            }
            MemoryManager.CompactMemoryAsync(250);
        }

        private const string REG_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_REG_NAME = "TongYan";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(APP_REG_NAME);
                        return val != null;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string exePath = "\"" + Application.ExecutablePath + "\"";
                            key.SetValue(APP_REG_NAME, exePath);
                            Logger.Info("开机自启动注册表已写入: " + exePath);
                        }
                        else
                        {
                            key.DeleteValue(APP_REG_NAME, false);
                            Logger.Info("开机自启动注册表已移除");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("操作开机自启动注册表异常", ex);
            }
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icoPath))
                {
                    return new Icon(icoPath, 32, 32);
                }

                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null) return extracted;
            }
            catch { }

            return CreateFallbackIcon();
        }

        private static Icon CreateFallbackIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(14, 110, 245)))
                    {
                        g.FillEllipse(b, 1, 1, 30, 30);
                    }
                    using (Font font = new Font("Microsoft YaHei", 12f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Brush brush = new SolidBrush(Color.White))
                    {
                        g.DrawString("通", font, brush, 8, 8);
                    }
                    IntPtr hIcon = bmp.GetHicon();
                    return Icon.FromHandle(hIcon);
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }
    }
}
