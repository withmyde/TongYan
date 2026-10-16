using System;
using System.Collections.Generic;
using System.Threading;

namespace TongYan
{
    /// <summary>
    /// 常驻单例 STA 后台工作线程，池化复用
    /// 彻底消除反复 new Thread(1MB 栈 + TEB + COM 初始化) 造成的内存剧烈颠簸与膨胀
    /// </summary>
    internal static class StaWorker
    {
        private static readonly Queue<Action> _queue = new Queue<Action>();
        private static readonly object _syncLock = new object();
        private static Thread _thread;
        private static volatile bool _isRunning = false;

        public static void EnsureStarted()
        {
            if (_thread != null && _thread.IsAlive) return;

            lock (_syncLock)
            {
                if (_thread != null && _thread.IsAlive) return;

                _isRunning = true;
                _thread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "TongYan_StaWorker"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        public static void Post(Action action, bool clearPending = false)
        {
            if (action == null) return;
            EnsureStarted();
            lock (_syncLock)
            {
                if (clearPending)
                {
                    _queue.Clear();
                }
                _queue.Enqueue(action);
                Monitor.Pulse(_syncLock);
            }
        }

        private static void WorkerLoop()
        {
            while (_isRunning)
            {
                Action action = null;
                lock (_syncLock)
                {
                    while (_queue.Count == 0 && _isRunning)
                    {
                        Monitor.Wait(_syncLock);
                    }
                    if (!_isRunning) break;
                    action = _queue.Dequeue();
                }

                if (action != null)
                {
                    try
                    {
                        action();
                    }
                    catch (ThreadAbortException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("StaWorker 执行任务异常", ex);
                    }
                }
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                _isRunning = false;
                _queue.Clear();
                Monitor.PulseAll(_syncLock);
            }
        }
    }
}
