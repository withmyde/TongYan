using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TongYan
{
    public class GlobalHotKey : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 9001;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private class HotKeyForm : Form
        {
            private readonly Action _callback;

            public HotKeyForm(Action callback)
            {
                _callback = callback;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
                Opacity = 0;
                Size = Size.Empty;
                // 确保创建句柄
                IntPtr h = Handle;
            }

            protected override void SetVisibleCore(bool value)
            {
                base.SetVisibleCore(false);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    if (_callback != null)
                    {
                        _callback();
                    }
                }
                base.WndProc(ref m);
            }
        }

        private HotKeyForm _form;
        private bool _isRegistered = false;
        public event Action HotKeyPressed;

        public GlobalHotKey()
        {
            _form = new HotKeyForm(() =>
            {
                if (HotKeyPressed != null)
                {
                    HotKeyPressed();
                }
            });
        }

        public bool Register(string hotkeyStr)
        {
            Unregister();

            if (string.IsNullOrEmpty(hotkeyStr))
                return false;

            uint modifiers = MOD_NOREPEAT;
            Keys key = Keys.None;

            string[] parts = hotkeyStr.Split(new char[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string part = p.Trim().ToLowerInvariant();
                if (part == "alt") modifiers |= MOD_ALT;
                else if (part == "ctrl" || part == "control") modifiers |= MOD_CONTROL;
                else if (part == "shift") modifiers |= MOD_SHIFT;
                else if (part == "win" || part == "windows") modifiers |= MOD_WIN;
                else
                {
                    try
                    {
                        key = (Keys)Enum.Parse(typeof(Keys), p.Trim(), true);
                    }
                    catch
                    {
                        key = Keys.None;
                    }
                }
            }

            if (key == Keys.None)
                return false;

            _isRegistered = RegisterHotKey(_form.Handle, HOTKEY_ID, modifiers, (uint)key);
            return _isRegistered;
        }

        public void Unregister()
        {
            if (_isRegistered && _form != null && !_form.IsDisposed)
            {
                UnregisterHotKey(_form.Handle, HOTKEY_ID);
                _isRegistered = false;
            }
        }

        public void Dispose()
        {
            Unregister();
            if (_form != null)
            {
                _form.Dispose();
                _form = null;
            }
        }
    }
}
