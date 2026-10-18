using System;
using System.IO;
using System.Reflection;
using Cldou.Api;
using Cldou.Core.Bus;

namespace Cldou.Runtime
{
    /// <summary>IModLogger → 宿主日志（带 [modId] 前缀）。</summary>
    internal sealed class ModLogAdapter : IModLogger
    {
        private readonly IDiag _log;
        private readonly string _prefix;

        public ModLogAdapter(IDiag log, string modId)
        {
            _log = log;
            _prefix = "[" + modId + "] ";
        }

        public void Info(string message) { _log.Info(_prefix + message); }
        public void Warn(string message) { _log.Warn(_prefix + message); }
        public void Error(string message) { _log.Error(_prefix + message); }
        public void Debug(string message) { _log.Debug(_prefix + message); }
    }

    /// <summary>兜底配置（宿主配置创建失败时使用：读默认值、写丢弃）。</summary>
    internal sealed class NullModConfig : IModConfig
    {
        public static readonly NullModConfig Instance = new NullModConfig();

        private NullModConfig() { }

        public string GetString(string key, string fallback = null) { return fallback; }
        public bool GetBool(string key, bool fallback = false) { return fallback; }
        public int GetInt(string key, int fallback = 0) { return fallback; }
        public float GetFloat(string key, float fallback = 0f) { return fallback; }
        public void SetString(string key, string value) { }
        public void SetBool(string key, bool value) { }
        public void SetInt(string key, int value) { }
        public void SetFloat(string key, float value) { }
        public void Save() { }
    }

    /// <summary>ICldouBus → 事件总线适配（带 owner，随 mod 停用自动退订）。</summary>
    internal sealed class BusAdapter : ICldouBus
    {
        private readonly EventBus _bus;
        private readonly object _owner;

        public BusAdapter(EventBus bus, object owner)
        {
            _bus = bus;
            _owner = owner;
        }

        public IDisposable Subscribe(string topic, Action<object> handler, int priority = 0)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            return _bus.Subscribe(topic, e => handler(e.Payload), priority, _owner);
        }

        public void Publish(string topic, object payload = null)
        {
            _bus.Publish(topic, payload, _owner);
        }
    }

    /// <summary>
    /// 程序集解析钩子（宿主无关）：
    ///   1) 已加载表优先 —— 避免同标识程序集重复加载；
    ///   2) Cldou.* / 宿主目录（BepInEx 插件目录 / 独立模式 Bootstrap 目录）中的依赖；
    ///   3) Cldou/mods 树下的 mod 私有依赖（含 0Harmony、MonoMod.* 等）。
    /// 仅在默认解析失败时触发；返回 null 则让官方解析链继续处理。
    /// </summary>
    internal static class AssemblyResolveHook
    {
        public static void Install(CldouPaths paths, IDiag diag)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    string name = new AssemblyName(args.Name).Name;
                    if (string.IsNullOrEmpty(name))
                    {
                        return null;
                    }

                    // 1) 已加载优先
                    foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                return loaded;
                            }
                        }
                        catch
                        {
                            // 某些动态程序集可能抛异常
                        }
                    }

                    // 2) 宿主目录
                    if (name.StartsWith("Cldou.", StringComparison.Ordinal))
                    {
                        string candidate = Path.Combine(paths.PluginDir ?? "", name + ".dll");
                        if (File.Exists(candidate))
                        {
                            return Assembly.LoadFrom(candidate);
                        }
                    }

                    if (!string.IsNullOrEmpty(paths.PluginDir))
                    {
                        string candidate = Path.Combine(paths.PluginDir, name + ".dll");
                        if (File.Exists(candidate))
                        {
                            return Assembly.Load(File.ReadAllBytes(candidate));
                        }
                    }

                    // 3) mods 树
                    if (Directory.Exists(paths.ModsDir))
                    {
                        foreach (string file in Directory.GetFiles(paths.ModsDir, name + ".dll", SearchOption.AllDirectories))
                        {
                            return Assembly.Load(File.ReadAllBytes(file));
                        }
                    }
                }
                catch (Exception ex)
                {
                    diag.Debug("AssemblyResolve 处理失败：" + ex.Message);
                }
                return null;
            };
        }
    }
}
