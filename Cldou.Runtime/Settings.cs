using UnityEngine;

namespace Cldou.Runtime
{
    /// <summary>清单 hash 校验策略。</summary>
    public enum IntegrityMode
    {
        Off,
        Warn,
        Strict
    }

    /// <summary>
    /// 加载器配置契约（宿主无关）。
    /// BepInEx 宿主实现 = BepInEx/config/cldou.loader.cfg；
    /// 独立模式实现 = Cldou/config/loader.json。
    /// </summary>
    public interface ICldouSettings
    {
        /// <summary>总开关。</summary>
        bool Enabled { get; }

        /// <summary>加载剖面名（Cldou/profiles/&lt;name&gt;.json）。</summary>
        string Profile { get; }

        /// <summary>是否启用热重载侦听。</summary>
        bool HotReload { get; }

        /// <summary>单个 mod 连续失败熔断阈值。</summary>
        int ErrorBudget { get; }

        /// <summary>清单 hash 校验策略。</summary>
        IntegrityMode Integrity { get; }

        /// <summary>只读索引 HK Modding API 已加载 mod。</summary>
        bool IndexModdingApi { get; }

        /// <summary>只读索引 BepInEx 已加载插件。</summary>
        bool IndexBepInExPlugins { get; }

        /// <summary>状态快照快捷键。</summary>
        KeyCode SnapshotKey { get; }

        /// <summary>详细日志。</summary>
        bool Verbose { get; }
    }
}
