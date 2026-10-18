using System.Collections.Generic;

namespace Cldou.Core.Diagnostics
{
    /// <summary>
    /// 错误预算（熔断器）：为每个 mod 记录“连续失败次数”。
    /// 达到阈值即判定熔断（IsTripped），由宿主停用该 mod；
    /// 任意一次成功调用将计数清零。
    /// </summary>
    public sealed class ErrorBudget
    {
        private readonly int _threshold;
        private readonly Dictionary<string, int> _streaks = new Dictionary<string, int>();

        public ErrorBudget(int failureThreshold)
        {
            _threshold = failureThreshold < 1 ? 1 : failureThreshold;
        }

        public int FailureThreshold
        {
            get { return _threshold; }
        }

        public int ReportFailure(string id)
        {
            int current;
            _streaks.TryGetValue(id, out current);
            current += 1;
            _streaks[id] = current;
            return current;
        }

        public void ReportSuccess(string id)
        {
            _streaks[id] = 0;
        }

        public int FailureCount(string id)
        {
            int current;
            _streaks.TryGetValue(id, out current);
            return current;
        }

        public bool IsTripped(string id)
        {
            return FailureCount(id) >= _threshold;
        }

        public void Reset(string id)
        {
            _streaks.Remove(id);
        }
    }
}
