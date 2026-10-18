using System;
using System.IO;
using System.Text;
using Cldou.Runtime;

namespace Cldou.Bootstrap
{
    /// <summary>独立模式日志：Cldou/logs/cldou.log（追加写 + 简单轮转 + 控制台镜像）。</summary>
    internal sealed class FileLog : IDiag
    {
        private const long MaxBytes = 8 * 1024 * 1024;

        private readonly string _file;
        private readonly object _lock = new object();

        public FileLog(string logDir)
        {
            Directory.CreateDirectory(logDir);
            _file = Path.Combine(logDir, "cldou.log");
            Rotate();
        }

        public void Info(string message) { Write("INFO ", message, false); }
        public void Warn(string message) { Write("WARN ", message, true); }
        public void Error(string message) { Write("ERROR", message, true); }
        public void Debug(string message) { Write("DEBUG", message, false); }

        private void Write(string level, string message, bool important)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                          + " [" + level + "] [Cldou] " + message;

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 容忍
                }
            }

            try
            {
                if (important)
                {
                    Console.Error.WriteLine(line);
                }
                else
                {
                    Console.WriteLine(line);
                }
            }
            catch
            {
                // 无控制台环境时忽略
            }
        }

        private void Rotate()
        {
            try
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string backup = _file + ".1";
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                    File.Move(_file, backup);
                }
            }
            catch
            {
                // 容忍
            }
        }
    }
}
