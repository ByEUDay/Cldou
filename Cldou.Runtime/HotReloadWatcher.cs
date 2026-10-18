using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Cldou.Runtime
{
    /// <summary>
    /// 文件变更侦听（Cldou/mods 与 Cldou/staging）。
    ///
    /// FileSystemWatcher 的事件在后台线程到达，这里只做最廉价的操作
    /// （入队 + 记录时间戳）；实际处理由主线程 Tick 在去抖窗口静默后执行。
    /// </summary>
    internal sealed class HotReloadWatcher : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private long _lastEventTicks;
        private bool _disposed;

        public bool HasPending
        {
            get { return !_pending.IsEmpty; }
        }

        public DateTime LastEventUtc
        {
            get { return new DateTime(Interlocked.Read(ref _lastEventTicks), DateTimeKind.Utc); }
        }

        public void Start(IEnumerable<string> directories, IDiag diag)
        {
            foreach (string dir in directories)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    var watcher = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 32 * 1024
                    };

                    FileSystemEventHandler handler = (s, e) => Enqueue(e.FullPath);
                    RenamedEventHandler renameHandler = (s, e) =>
                    {
                        Enqueue(e.FullPath);
                        Enqueue(e.OldFullPath);
                    };

                    watcher.Changed += handler;
                    watcher.Created += handler;
                    watcher.Deleted += handler;
                    watcher.Renamed += renameHandler;
                    watcher.Error += (s, e) =>
                    {
                        try { diag.Warn("文件侦听错误（" + dir + "）：" + e.GetException().Message); }
                        catch { }
                    };
                    watcher.EnableRaisingEvents = true;

                    _watchers.Add(watcher);
                    diag.Debug("文件侦听已启动：" + dir);
                }
                catch (Exception ex)
                {
                    diag.Warn("无法启动文件侦听 " + dir + "：" + ex.Message);
                }
            }
        }

        private void Enqueue(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            _pending.Enqueue(path);
            Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// 若距离最后一次事件已超过去抖窗口，取出并返回本批变更路径（已去重）。
        /// quiet 为 true 表示本次是“静默后”的有效批次。
        /// </summary>
        public List<string> DrainIfQuiet(TimeSpan debounce, out bool quiet)
        {
            quiet = false;
            var result = new List<string>();
            if (_pending.IsEmpty)
            {
                return result;
            }

            if (DateTime.UtcNow - LastEventUtc < debounce)
            {
                return result;
            }

            quiet = true;
            string path;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_pending.TryDequeue(out path))
            {
                if (seen.Add(path))
                {
                    result.Add(path);
                }
            }
            return result;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            foreach (FileSystemWatcher watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // 容忍
                }
            }
            _watchers.Clear();
        }
    }
}
