using System.IO;
using BepInEx;
using Cldou.Plugin.Host;
using Cldou.Plugin.Settings;
using Cldou.Runtime;
using UnityEngine;

namespace Cldou.Plugin
{
    /// <summary>
    /// Cldou 运行时 Mod 加载器 — BepInEx 宿主入口（薄壳：只负责把运行时接进 BepInEx 生命周期）。
    ///
    /// 独立模式（不依赖 BepInEx）由 Cldou.Bootstrap 承载，二者共享同一 Cldou.Runtime 内核：
    ///   · 本壳：BepInEx 插件形态（有 BepInEx 的环境）
    ///   · Cldou.Bootstrap：Doorstop 形态（无 BepInEx 的环境）
    /// 两壳共享 .NET 静态状态与同一 Cldou/ 数据目录，互斥启动（谁在场用谁）。
    /// </summary>
    [BepInPlugin(Guid, PluginName, PluginVersion)]
    public sealed class CldouPlugin : BaseUnityPlugin
    {
        public const string Guid = "cldou.loader";
        public const string PluginName = "Cldou";
        public const string PluginVersion = "0.1.0";

        internal static CldouPlugin Instance { get; private set; }
        internal CldouRuntime Runtime { get; private set; }
        internal CldouSettings Settings { get; private set; }

        private void Awake()
        {
            Instance = this;
            Settings = new CldouSettings(Config);

            string pluginDir = Path.GetDirectoryName(typeof(CldouPlugin).Assembly.Location) ?? "";

            string gameRoot = null;
            try
            {
                if (!string.IsNullOrEmpty(Paths.PluginPath))
                {
                    gameRoot = CldouPaths.FindGameRoot(Path.GetFullPath(Path.Combine(Paths.PluginPath, "..", "..")));
                }
            }
            catch
            {
                // 走下面的回退
            }

            if (string.IsNullOrEmpty(gameRoot))
            {
                gameRoot = Paths.GameRootPath;
            }
            if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot))
            {
                gameRoot = CldouPaths.FindGameRoot(pluginDir) ?? Directory.GetCurrentDirectory();
            }

            var paths = new CldouPaths(gameRoot, Path.Combine(Paths.ConfigPath, "cldou"), pluginDir);

            Runtime = new CldouRuntime(
                new BepLog(Logger),
                Settings,
                paths,
                PluginName + " (BepInEx)",
                PluginVersion,
                id => new BepInExModConfig(Path.Combine(paths.ConfigDir, id + ".cfg")));
            Runtime.Boot();
        }

        private void Start()
        {
            // 等到所有插件 Awake 结束（含 HK Modding API 完成其 mod 加载）后再做
            // 外部索引与 Cldou mods 注入，保证顺序稳定。
            Runtime.StartRuntime();
        }

        private void Update()
        {
            Runtime.Tick(Time.unscaledDeltaTime);
        }

        private void OnApplicationQuit()
        {
            Runtime.Shutdown("游戏退出");
        }

        private void OnDestroy()
        {
            Runtime.Shutdown("插件销毁");
        }
    }
}
