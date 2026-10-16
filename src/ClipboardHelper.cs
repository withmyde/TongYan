using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TongYan
{
    public static class ClipboardHelper
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private const byte VK_SHIFT = 0x10;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12; // Alt
        private const byte VK_LWIN = 0x5B;
        private const byte VK_RWIN = 0x5C;
        private const byte VK_C = 0x43;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        public static string GetSelectedText()
        {
            // 等待真正与 Ctrl+C 冲突的物理修饰键（Alt 与 Win）释放
            bool hadModifiers = false;
            for (int i = 0; i < 15; i++)
            {
                bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool winDown = ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0) || ((GetAsyncKeyState(VK_RWIN) & 0x8000) != 0);
                if (!altDown && !winDown)
                    break;
                hadModifiers = true;
                Thread.Sleep(15);
            }

            // 若有修饰键按下，确保系统状态释放 Alt 与 Shift 键
            if (hadModifiers)
            {
                keybd_event(VK_MENU, 0x38, KEYEVENTF_KEYUP, 0);
                keybd_event(VK_SHIFT, 0x2A, KEYEVENTF_KEYUP, 0);
                Thread.Sleep(10);
            }

            // 仅只读备份原剪贴板文本，不在 Ctrl+C 前清空剪贴板，避免提前触发系统剪贴板监听服务抢锁
            string originalText = TryGetClipboardText(3, 5);
            uint initialSeq = GetClipboardSequenceNumber();
            uint lastSeq = initialSeq;

            // 模拟发送 Ctrl + C
            keybd_event(VK_CONTROL, 0x1D, 0, 0);
            keybd_event(VK_C, 0x2E, 0, 0);
            Thread.Sleep(10);
            keybd_event(VK_C, 0x2E, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_CONTROL, 0x1D, KEYEVENTF_KEYUP, 0);

            // 基于系统剪贴板序列号无锁轮询（最多等待 350ms），目标程序写入期间绝不调用 OpenClipboard 抢锁
            string selected = null;
            for (int i = 0; i < 35; i++)
            {
                Thread.Sleep(10);
                uint currentSeq = GetClipboardSequenceNumber();
                if (currentSeq != lastSeq)
                {
                    lastSeq = currentSeq;
                    selected = TryGetClipboardText(6, 5);
                    if (!string.IsNullOrEmpty(selected))
                        break;
                }
            }

            // 若剪贴板在 Ctrl+C 后发生了变化，恢复原剪贴板内容
            if (GetClipboardSequenceNumber() != initialSeq)
            {
                if (!string.IsNullOrEmpty(originalText))
                {
                    TrySetClipboardText(originalText, 5, 5);
                }
                else
                {
                    TryClearClipboard(3, 5);
                }
            }

            return selected != null ? selected.Trim() : null;
        }

        private static string TryGetClipboardText(int maxRetries, int retryDelayMs)
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                return null;

            for (int i = 0; i < maxRetries; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                        if (hData == IntPtr.Zero)
                            return null;

                        IntPtr ptr = GlobalLock(hData);
                        if (ptr == IntPtr.Zero)
                            return null;

                        try
                        {
                            return Marshal.PtrToStringUni(ptr);
                        }
                        finally
                        {
                            GlobalUnlock(hData);
                        }
                    }
                    catch
                    {
                        return null;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }

                if (i + 1 < maxRetries)
                    Thread.Sleep(retryDelayMs);
            }

            return null;
        }

        private static void TrySetClipboardText(string text, int maxRetries, int retryDelayMs)
        {
            if (text == null)
                return;

            for (int i = 0; i < maxRetries; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        int chars = text.Length;
                        UIntPtr bytes = new UIntPtr((uint)((chars + 1) * 2));
                        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, bytes);
                        if (hGlobal == IntPtr.Zero)
                            return;

                        IntPtr ptr = GlobalLock(hGlobal);
                        if (ptr == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                            return;
                        }

                        try
                        {
                            Marshal.Copy(text.ToCharArray(), 0, ptr, chars);
                            Marshal.WriteInt16(ptr, chars * 2, 0);
                        }
                        finally
                        {
                            GlobalUnlock(hGlobal);
                        }

                        if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                        }
                        return;
                    }
                    catch
                    {
                        return;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }

                if (i + 1 < maxRetries)
                    Thread.Sleep(retryDelayMs);
            }
        }

        private static void TryClearClipboard(int maxRetries, int retryDelayMs)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        return;
                    }
                    catch
                    {
                        return;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }

                if (i + 1 < maxRetries)
                    Thread.Sleep(retryDelayMs);
            }
        }
    }
}
