using System;

namespace Cldou.Api
{
    /// <summary>mod 日志接口（宿主注入，带 [modId] 前缀）。</summary>
    public interface IModLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Debug(string message);
    }

    /// <summary>
    /// 进程内事件总线（面向 mod 的窄接口）。
    /// 所有回调在游戏主线程执行；订阅随 mod 停用自动清理。
    /// </summary>
    public interface ICldouBus
    {
        /// <summary>订阅主题。返回可释放句柄；亦可依赖宿主在 OnDisable 时自动退订。</summary>
        IDisposable Subscribe(string topic, Action<object> handler, int priority = 0);

        /// <summary>发布事件（入队，由宿主统一泵送）。</summary>
        void Publish(string topic, object payload = null);
    }

    /// <summary>
    /// mod 配置接口（持久化到 BepInEx/config/cldou/&lt;id&gt;.cfg）。
    /// 读取在首次访问时绑定；写入后调用 Save() 落盘。
    /// </summary>
    public interface IModConfig
    {
        string GetString(string key, string fallback = null);
        bool GetBool(string key, bool fallback = false);
        int GetInt(string key, int fallback = 0);
        float GetFloat(string key, float fallback = 0f);

        void SetString(string key, string value);
        void SetBool(string key, bool value);
        void SetInt(string key, int value);
        void SetFloat(string key, float value);

        void Save();
    }

    /// <summary>mod 上下文：由 Cldou 在 OnLoad 时注入的所有服务入口。</summary>
    public sealed class ModContext
    {
        public ModInfo Info { get; private set; }
        public IModLogger Logger { get; private set; }
        public ICldouBus Bus { get; private set; }
        public IModConfig Config { get; private set; }

        /// <summary>mod 根目录（程序集与清单所在目录）。</summary>
        public string ModRoot { get; private set; }

        /// <summary>该 mod 的持久数据目录（Cldou/data/&lt;id&gt;/，已创建）。</summary>
        public string DataPath { get; private set; }

        /// <summary>当前 Cldou API 版本串。</summary>
        public string ApiVersion { get; private set; }

        public ModContext(ModInfo info, IModLogger logger, ICldouBus bus, IModConfig config,
            string modRoot, string dataPath, string apiVersion)
        {
            Info = info;
            Logger = logger;
            Bus = bus;
            Config = config;
            ModRoot = modRoot;
            DataPath = dataPath;
            ApiVersion = apiVersion;
        }
    }
}
