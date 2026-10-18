using System;

namespace Cldou.Runtime
{
    /// <summary>宿主日志接口：由各宿主实现（BepInEx ManualLogSource / 独立模式文件日志）。</summary>
    public interface IDiag
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Debug(string message);
    }

    /// <summary>Verbose 过滤包装：运行时内部统一经由本类输出。</summary>
    internal sealed class Diag : IDiag
    {
        private readonly IDiag _inner;
        private readonly Func<bool> _verbose;

        public Diag(IDiag inner, Func<bool> verbose)
        {
            _inner = inner;
            _verbose = verbose;
        }

        public void Info(string message)
        {
            if (_inner != null)
            {
                _inner.Info(message);
            }
        }

        public void Warn(string message)
        {
            if (_inner != null)
            {
                _inner.Warn(message);
            }
        }

        public void Error(string message)
        {
            if (_inner != null)
            {
                _inner.Error(message);
            }
        }

        public void Debug(string message)
        {
            if (_inner != null && _verbose != null && _verbose())
            {
                _inner.Debug(message);
            }
        }
    }
}
