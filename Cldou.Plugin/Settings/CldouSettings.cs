using BepInEx.Configuration;
using Cldou.Runtime;
using UnityEngine;

namespace Cldou.Plugin.Settings
{
    /// <summary>BepInEx 配置适配（BepInEx/config/cldou.loader.cfg），实现宿主无关的 ICldouSettings。</summary>
    internal sealed class CldouSettings : ICldouSettings
    {
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<string> _profile;
        private readonly ConfigEntry<bool> _hotReload;
        private readonly ConfigEntry<int> _errorBudget;
        private readonly ConfigEntry<IntegrityMode> _integrity;
        private readonly ConfigEntry<bool> _indexModdingApi;
        private readonly ConfigEntry<bool> _indexBepInExPlugins;
        private readonly ConfigEntry<KeyCode> _snapshotKey;
        private readonly ConfigEntry<bool> _verbose;

        public CldouSettings(ConfigFile config)
        {
            _enabled = config.Bind(
                "General", "Enabled", true,
                "是否启用 Cldou 运行时加载。");

            _profile = config.Bind(
                "General", "Profile", "default",
                "加载剖面名称（对应 Cldou/profiles/<name>.json，可选）。");

            _hotReload = config.Bind(
                "Loader", "HotReload", true,
                "检测到 Cldou/mods 中文件变化时是否尝试热重载。未实现 IReloadable 的 mod 将标记为“下次启动生效”。");

            _errorBudget = config.Bind(
                "Loader", "ErrorBudget", 3,
                "单个 mod 连续失败达到该次数后熔断停用该 mod（不影响其它 mod）。");

            _integrity = config.Bind(
                "Loader", "Integrity", IntegrityMode.Warn,
                "清单 hash 校验策略：Off=关闭；Warn=哈希不符仅警告；Strict=哈希不符拒绝加载。");

            _indexModdingApi = config.Bind(
                "Bridge", "IndexModdingApi", true,
                "只读探测/索引 HK Modding API 已加载的 mod（绝不干预其加载流程）。");

            _indexBepInExPlugins = config.Bind(
                "Bridge", "IndexBepInExPlugins", true,
                "只读列出 BepInEx 已加载的插件（不触碰它们的生命周期）。");

            _snapshotKey = config.Bind(
                "Diagnostics", "SnapshotKey", KeyCode.F10,
                "按下后把运行状态快照写入 Cldou/state/registry.json。");

            _verbose = config.Bind(
                "Diagnostics", "Verbose", false,
                "输出详细诊断日志。");
        }

        public bool Enabled { get { return _enabled.Value; } }
        public string Profile { get { return _profile.Value; } }
        public bool HotReload { get { return _hotReload.Value; } }
        public int ErrorBudget { get { return _errorBudget.Value; } }
        public IntegrityMode Integrity { get { return _integrity.Value; } }
        public bool IndexModdingApi { get { return _indexModdingApi.Value; } }
        public bool IndexBepInExPlugins { get { return _indexBepInExPlugins.Value; } }
        public KeyCode SnapshotKey { get { return _snapshotKey.Value; } }
        public bool Verbose { get { return _verbose.Value; } }
    }
}
