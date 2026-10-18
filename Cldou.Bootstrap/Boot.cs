using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Cldou.Runtime;

namespace Cldou.Bootstrap
{
    /// <summary>
    /// 独立模式引导：初始化 → 等待游戏程序集自然加载 → 按策略接管帧回调 → 交给运行时。
    ///   - 绝不强制提前加载任何游戏程序集（等待 Unity 自己加载）；
    ///   - 默认零补丁（unity 场景事件 + 自建 Ticker）；Harmony 为可选/兜底。
    /// </summary>
    internal static class Boot
    {
        public const string LoaderName = "Cldou.Standalone";
        public const string LoaderVersion = "0.1.1";

        internal static FileLog Log;
        internal static CldouPaths Paths;
        internal static StandaloneSettings Settings;
        internal static string BootstrapDir;
        internal static int MainThreadId;

        public static void Run()
        {
            try
            {
                MainThreadId = Thread.CurrentThread.ManagedThreadId;
                BootstrapDir = Path.GetDirectoryName(typeof(Boot).Assembly.Location) ?? ".";

                string gameRoot = Environment.GetEnvironmentVariable("CLDOU_GAME_ROOT");
                if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot))
                {
                    gameRoot = CldouPaths.FindGameRoot(BootstrapDir);
                }
                if (string.IsNullOrEmpty(gameRoot))
                {
                    gameRoot = Directory.GetCurrentDirectory();
                }

                Paths = new CldouPaths(gameRoot, Path.Combine(gameRoot, "Cldou", "config"), BootstrapDir);
                Paths.EnsureCreated();
                Log = new FileLog(Path.Combine(Paths.Root, "logs"));
                Settings = StandaloneSettings.Load(Paths);

                AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

                Log.Info("Cldou 独立模式引导启动（Doorstop → " + LoaderName + " v" + LoaderVersion + "）");
                Log.Info("游戏根目录：" + gameRoot);
                Log.Info("引导目录：" + BootstrapDir);

                AssemblyWatcher.WaitForGameAssembly(OnGameAssemblyLoaded);
            }
            catch (Exception ex)
            {
                SafeConsole("[Cldou] 引导初始化失败：" + ex);
            }
        }

        private static void OnGameAssemblyLoaded(Assembly gameAssembly)
        {
            Log.Info("Assembly-CSharp 已加载（线程：" + DescribeThread() + "）");

            string strategy = Settings.TickHook ?? "unity";
            bool installed;

            switch (strategy)
            {
                case "none":
                    Log.Info("帧回调策略：none（诊断模式，不接管运行时）。");
                    return;

                case "harmony":
                    TickDriver.StrategyName = "harmony";
                    installed = HarmonyStrategy.Install(gameAssembly);
                    break;

                case "monomod":
                    Log.Warn("帧回调策略：monomod（实验性！此前 macOS 上的 SIGBUS 闪退即来自该路径，如非排查请勿使用）。");
                    TickDriver.StrategyName = "monomod";
                    installed = MonoModStrategy.Install(gameAssembly);
                    break;

                case "unity":
                default:
                    TickDriver.StrategyName = "unity";
                    installed = UnityEventsStrategy.Subscribe(gameAssembly);
                    break;
            }

            if (installed)
            {
                StrategyWatchdog.Start(gameAssembly);
                return;
            }

            Log.Warn("首选策略未能安装，退避到 unity（场景事件）策略…");
            TickDriver.StrategyName = "unity(fallback)";
            if (UnityEventsStrategy.Subscribe(gameAssembly))
            {
                StrategyWatchdog.Start(gameAssembly);
            }
        }

        private static string DescribeThread()
        {
            try
            {
                int id = Thread.CurrentThread.ManagedThreadId;
                return (id == MainThreadId ? "主线程" : "其他线程") + " #" + id;
            }
            catch
            {
                return "?";
            }
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                Assembly loaded = FindLoaded(name);
                if (loaded != null)
                {
                    return loaded;
                }

                string candidate = Path.Combine(BootstrapDir ?? ".", name + ".dll");
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }
            catch
            {
                // 容忍
            }
            return null;
        }

        internal static Assembly FindLoaded(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
                catch
                {
                    // 容忍
                }
            }
            return null;
        }

        internal static void SafeConsole(string message)
        {
            try { Console.WriteLine(message); } catch { }
        }
    }
}
