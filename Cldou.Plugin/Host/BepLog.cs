using BepInEx.Logging;
using Cldou.Runtime;

namespace Cldou.Plugin.Host
{
    /// <summary>IDiag → BepInEx 日志（ManualLogSource）。</summary>
    internal sealed class BepLog : IDiag
    {
        private readonly ManualLogSource _log;

        public BepLog(ManualLogSource log)
        {
            _log = log;
        }

        public void Info(string message) { _log.LogInfo(message); }
        public void Warn(string message) { _log.LogWarning(message); }
        public void Error(string message) { _log.LogError(message); }
        public void Debug(string message) { _log.LogDebug(message); }
    }
}
