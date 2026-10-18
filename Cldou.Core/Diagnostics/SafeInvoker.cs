using System;

namespace Cldou.Core.Diagnostics
{
    /// <summary>
    /// 安全调用包装：mod 的任何回调都可能抛异常，
    /// 宿主必须保证“单个 mod 的异常不会波及加载器与其它 mod”。
    /// </summary>
    public static class SafeInvoker
    {
        public static bool Try(string label, Action action, Action<Exception, string> onError)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                if (onError != null)
                {
                    onError(ex, label);
                }
                return false;
            }
        }

        public static bool TryFunc<T>(string label, Func<T> func, out T result, Action<Exception, string> onError)
        {
            try
            {
                result = func();
                return true;
            }
            catch (Exception ex)
            {
                result = default(T);
                if (onError != null)
                {
                    onError(ex, label);
                }
                return false;
            }
        }
    }
}
