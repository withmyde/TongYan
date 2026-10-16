using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace TongYan
{
    /// <summary>
    /// 物理内存治理中心：运用 Windows 底层 Virtual Memory / Working Set 管理与 CLR 垃圾回收机制
    /// 确保托盘静默与划词窗口关闭后，物理工作集与私有字节被强制冲刷回极低水位 (数 MB)
    /// </summary>
    internal static class MemoryManager
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        public static Func<bool> IsActiveWindowPredicate = null;
        private static int _compactSeq = 0;

        private static bool HasActiveWindow()
        {
            try
            {
                if (IsActiveWindowPredicate != null) return IsActiveWindowPredicate();
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 同步执行彻底垃圾回收并冲刷进程物理工作集 (Working Set) 回极低水位
        /// </summary>
        public static void FlushMemory()
        {
            try
            {
                // 1. 若无活动窗口在绘制，释放 WinForms 双缓冲图形缓冲区可能残留的离屏表面
                if (!HasActiveWindow())
                {
                    try
                    {
                        BufferedGraphicsManager.Current.Invalidate();
                    }
                    catch { }
                }

                // 2. 强制回收托管堆全部代际并等待析构器
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);

                // 3. 调用 Win32 API 冲刷物理内存工作集
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    IntPtr hProc = Process.GetCurrentProcess().Handle;
                    SetProcessWorkingSetSize(hProc, (IntPtr)(-1), (IntPtr)(-1));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("FlushMemory 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 异步延迟冲刷内存（UI 关闭后留出少许缓冲期让句柄与消息彻底消化）
        /// 支持防抖调度与活动窗口安全拦截，杜绝用户交互中途强行冲刷工作集
        /// </summary>
        public static void CompactMemoryAsync(int delayMs = 250)
        {
            int seq = Interlocked.Increment(ref _compactSeq);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (delayMs > 0)
                    {
                        Thread.Sleep(delayMs);
                    }
                    // 防抖：若期间已调度更新的紧凑请求，直接放弃当前过时请求
                    if (Thread.VolatileRead(ref _compactSeq) != seq)
                    {
                        return;
                    }
                    // 保护：若当前仍有窗口处于活动状态，绝不在交互中途强刷工作集
                    if (HasActiveWindow())
                    {
                        return;
                    }
                    FlushMemory();
                }
                catch { }
            });
        }
    }
}
