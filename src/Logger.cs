using System;
using System.IO;

namespace TongYan
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string LogPath
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            string msg = message;
            if (ex != null)
            {
                msg += "\n" + ex.ToString();
            }
            Write("ERROR", msg);
        }

        private static void Write(string level, string message)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}", DateTime.Now, level, message);
                lock (_lock)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
