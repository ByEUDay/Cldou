using System;
using System.Reflection;
using System.Threading;
using Cldou.Runtime;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cldou.Bootstrap
{
    /// <summary>
    /// 帧驱动策略集合。核心原则：能不碰原生补丁就不碰。
    ///
    ///   unity   ——（默认，零补丁）订阅场景事件，在首个场景加载时创建自有 Ticker
    ///              （MonoBehaviour），之后由 Unity 原生驱动每帧回调；
    ///   harmony ——HarmonyX postfix 到 GameManager.Update；
    ///   monomod ——MonoMod Detour（实验性；macOS 上已验证会 SIGBUS，仅研究用）；
    ///   none    ——不接管（诊断对照）。
    /// </summary>
    internal static class TickDriver
    {
        internal static volatile bool FirstFrameSeen;
        internal static int FrameCount;
        internal static string StrategyName = "?";

        private static CldouRuntime _runtime;
        private static bool _booted;
        private static bool _bootFailed;
        private static bool _shutDown;
        private static int _frameErrors;

        internal static void OnFrame()
        {
            if (_shutDown || _bootFailed)
            {
                return;
            }

            FrameCount++;
            if (FrameCount == 1)
            {
                FirstFrameSeen = true;
                Boot.Log.Info("首帧回调进入（策略：" + StrategyName + "）");
            }

            try
            {
                if (!_booted)
                {
                    _booted = true;
                    try
                    {
                        BootRuntime();
                    }
                    catch (Exception bootEx)
                    {
                        _bootFailed = true;
                        Boot.Log.Error("运行时启动失败：" + bootEx);
                        return;
                    }
                }

                if (_runtime != null)
                {
                    _runtime.Tick(Time.unscaledDeltaTime);
                }
            }
            catch (Exception ex)
            {
                _frameErrors++;
                if (_frameErrors == 1 || _frameErrors % 100 == 0)
                {
                    Boot.Log.Error("帧处理异常（第 " + _frameErrors + " 次）：" + ex);
                }
            }
        }

        private static void BootRuntime()
        {
            Boot.Log.Info("主线程已接管，启动 Cldou 运行时…");

            StandaloneSettings settings = Boot.Settings;
            if (!settings.Enabled)
            {
                Boot.Log.Warn("独立模式配置中已禁用（enabled=false），跳过加载。");
                return;
            }

            _runtime = new CldouRuntime(
                Boot.Log,
                settings,
                Boot.Paths,
                Boot.LoaderName,
                Boot.LoaderVersion,
                id => new JsonModConfig(System.IO.Path.Combine(Boot.Paths.ConfigDir, "mods", id + ".json")));

            _runtime.Boot();
            _runtime.StartRuntime();
            Boot.Log.Info("运行时启动完成。");
        }

        internal static void Shutdown(string reason)
        {
            if (_shutDown)
            {
                return;
            }
            _shutDown = true;
            try
            {
                if (_runtime != null)
                {
                    _runtime.Shutdown(reason);
                }
            }
            catch (Exception ex)
            {
                if (Boot.Log != null)
                {
                    Boot.Log.Error("关闭时异常：" + ex.Message);
                }
            }
        }
    }

    /// <summary>自建 Ticker：挂在引导器创建的 GameObject 上，由 Unity 原生每帧驱动。</summary>
    public sealed class CldouTicker : MonoBehaviour
    {
        private void Update()
        {
            TickDriver.OnFrame();
        }
    }

    /// <summary>等待 Assembly-CSharp 自然加载（不强制、不干预加载过程）。</summary>
    internal static class AssemblyWatcher
    {
        private static Action<Assembly> _callback;
        private static volatile int _fired;

        internal static void WaitForGameAssembly(Action<Assembly> callback)
        {
            _callback = callback;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            // 万一已经加载（罕见）：立即触发
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsGameAssembly(asm))
                {
                    Fire(asm);
                    return;
                }
            }

            // 安全网：轻量轮询（只读已加载列表，绝不触发新加载）
            var poller = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "Cldou.AssemblyWatch"
            };
            poller.Start();
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (IsGameAssembly(args.LoadedAssembly))
            {
                Fire(args.LoadedAssembly);
            }
        }

        private static bool IsGameAssembly(Assembly asm)
        {
            try
            {
                return string.Equals(asm.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void Fire(Assembly asm)
        {
            Action<Assembly> callback = _callback;
            if (callback == null || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
            {
                return;
            }

            try { AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad; } catch { }

            try
            {
                callback(asm);
            }
            catch (Exception ex)
            {
                Boot.Log.Error("游戏程序集加载回调异常：" + ex);
            }
        }

        private static void PollLoop()
        {
            for (int i = 0; i < 600 && _fired == 0; i++) // 最多约 5 分钟
            {
                Thread.Sleep(500);
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (IsGameAssembly(asm))
                    {
                        Fire(asm);
                        return;
                    }
                }
            }

            if (_fired == 0)
            {
                Boot.Log.Error("超过时限仍未检测到 Assembly-CSharp 加载。");
            }
        }
    }

    /// <summary>
    /// 策略：unity 事件 + 自建 Ticker（零补丁，默认）。
    /// 注意：此处全部走“运行时反射订阅”，不做编译期成员绑定
    ///（本机 Assembly-CSharp 引用组合下 csc 对 SceneManager 事件绑定存在解析异常；
    ///  反射方式对不同 Unity 版本也更稳健）。
    /// </summary>
    internal static class UnityEventsStrategy
    {
        private static GameObject _tickerObject;

        internal static bool Subscribe(Assembly gameAssembly)
        {
            bool any = false;
            try
            {
                Application.quitting += OnQuitting;
            }
            catch
            {
                // 忽略：退出时将走 ProcessExit 兜底
            }

            Type sceneManager = FindTypeAmongLoaded("UnityEngine.SceneManagement.SceneManager");
            if (sceneManager != null)
            {
                any |= TrySubscribeEvent(sceneManager, "sceneLoaded", "OnSceneLoaded", "（sceneLoaded）");
                any |= TrySubscribeEvent(sceneManager, "activeSceneChanged", "OnActiveSceneChanged", "（activeSceneChanged）");
            }
            else
            {
                Boot.Log.Warn("unity 策略：未在已加载程序集中找到 SceneManager 类型");
            }

            Type canvas = FindTypeAmongLoaded("UnityEngine.Canvas");
            if (canvas != null)
            {
                any |= TrySubscribeEvent(canvas, "willRenderCanvases", "OnWillRenderCanvases", "（Canvas.willRenderCanvases）");
            }

            if (any)
            {
                Boot.Log.Info("unity 策略：事件订阅完成，等待首个回调…");
            }
            else
            {
                Boot.Log.Error("unity 策略：所有事件订阅均失败");
            }
            return any;
        }

        private static bool TrySubscribeEvent(Type type, string eventName, string handlerName, string label)
        {
            try
            {
                EventInfo evt = type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
                if (evt == null)
                {
                    Boot.Log.Warn("unity 策略：未找到事件 " + eventName + " " + label);
                    return false;
                }

                MethodInfo handler = typeof(UnityEventsStrategy).GetMethod(handlerName, BindingFlags.Static | BindingFlags.NonPublic);
                Delegate del = Delegate.CreateDelegate(evt.EventHandlerType, handler);
                evt.AddEventHandler(null, del);
                Boot.Log.Info("unity 策略：已订阅 " + type.Name + "." + eventName + " " + label);
                return true;
            }
            catch (Exception ex)
            {
                Boot.Log.Warn("unity 策略：订阅 " + eventName + " 失败：" + ex.Message);
                return false;
            }
        }

        private static Type FindTypeAmongLoaded(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null)
                    {
                        return t;
                    }
                }
                catch
                {
                    // 容忍
                }
            }
            return null;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureTicker(scene.name);
        }

        private static void OnActiveSceneChanged(Scene previous, Scene current)
        {
            EnsureTicker(current.name);
        }

        private static void OnWillRenderCanvases()
        {
            EnsureTicker(null);
        }

        private static void EnsureTicker(string sceneName)
        {
            try
            {
                if (_tickerObject != null)
                {
                    return;
                }

                _tickerObject = new GameObject("Cldou.Ticker");
                UnityEngine.Object.DontDestroyOnLoad(_tickerObject);
                _tickerObject.AddComponent<CldouTicker>();
                Boot.Log.Info("已创建 Ticker GameObject" + (sceneName != null ? "（场景：" + sceneName + "）" : "") + "，等待首帧…");
            }
            catch (Exception ex)
            {
                Boot.Log.Error("创建 Ticker 失败：" + ex);
            }
        }

        private static void OnQuitting()
        {
            TickDriver.Shutdown("游戏退出");
        }
    }

    /// <summary>策略：HarmonyX postfix（使用随包分发的 0Harmony）。</summary>
    internal static class HarmonyStrategy
    {
        internal static bool Install(Assembly gameAssembly)
        {
            try
            {
                Type gameManager = gameAssembly.GetType("GameManager", false);
                if (gameManager == null)
                {
                    Boot.Log.Error("harmony：未找到 GameManager 类型");
                    return false;
                }

                MethodInfo update = gameManager.GetMethod(
                    "Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?? gameManager.GetMethod(
                        "Update",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (update == null)
                {
                    Boot.Log.Error("harmony：未找到 GameManager.Update");
                    return false;
                }

                MethodInfo postfix = typeof(HarmonyStrategy).GetMethod(
                    "UpdatePostfix",
                    BindingFlags.Static | BindingFlags.NonPublic);

                var harmony = new Harmony("cldou.standalone.tick");
                harmony.Patch(update, postfix: new HarmonyMethod(postfix));
                Boot.Log.Info("harmony 策略：补丁已应用（GameManager.Update postfix）");
                return true;
            }
            catch (Exception ex)
            {
                Boot.Log.Error("harmony 策略安装失败：" + ex);
                return false;
            }
        }

        private static void UpdatePostfix()
        {
            TickDriver.OnFrame();
        }
    }

    /// <summary>策略：MonoMod Detour（实验性；macOS 上已验证会闪退，默认不启用）。</summary>
    internal static class MonoModStrategy
    {
        private static Detour _detour;

        internal static bool Install(Assembly gameAssembly)
        {
            try
            {
                Type gameManager = gameAssembly.GetType("GameManager", false);
                if (gameManager == null)
                {
                    Boot.Log.Error("monomod：未找到 GameManager 类型");
                    return false;
                }

                MethodInfo update = gameManager.GetMethod(
                    "Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?? gameManager.GetMethod(
                        "Update",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (update == null)
                {
                    Boot.Log.Error("monomod：未找到 GameManager.Update");
                    return false;
                }

                MethodInfo hook = typeof(MonoModStrategy).GetMethod(
                    "GameManagerUpdate",
                    BindingFlags.Static | BindingFlags.NonPublic);

                _detour = new Detour(update, hook);
                Boot.Log.Info("monomod 策略：Detour 已安装（实验性！）");
                return true;
            }
            catch (Exception ex)
            {
                Boot.Log.Error("monomod 策略安装失败：" + ex);
                return false;
            }
        }

        private static void GameManagerUpdate(Action<GameManager> orig, GameManager self)
        {
            if (orig != null)
            {
                orig(self);
            }
            TickDriver.OnFrame();
        }
    }

    /// <summary>看门狗：策略安装后若迟迟收不到帧，自动尝试备用策略并给出明确指引。</summary>
    internal static class StrategyWatchdog
    {
        internal static void Start(Assembly gameAssembly)
        {
            var thread = new Thread(() => Loop(gameAssembly))
            {
                IsBackground = true,
                Name = "Cldou.Watchdog"
            };
            thread.Start();
        }

        private static void Loop(Assembly gameAssembly)
        {
            try
            {
                for (int i = 0; i < 5 && !TickDriver.FirstFrameSeen; i++)
                {
                    Thread.Sleep(5000);
                }

                if (TickDriver.FirstFrameSeen)
                {
                    return;
                }

                Boot.Log.Warn("看门狗：25 秒未收到帧回调（当前策略：" + TickDriver.StrategyName + "）。");

                if (TickDriver.StrategyName == "unity" || TickDriver.StrategyName == "unity(fallback)")
                {
                    Boot.Log.Warn("看门狗：尝试 harmony 备用策略…");
                    if (HarmonyStrategy.Install(gameAssembly))
                    {
                        Boot.Log.Info("看门狗：harmony 备用策略已安装。");
                    }
                }
                else if (TickDriver.StrategyName == "harmony")
                {
                    Boot.Log.Warn("看门狗：尝试 unity（场景事件）备用策略…");
                    if (UnityEventsStrategy.Subscribe(gameAssembly))
                    {
                        Boot.Log.Info("看门狗：unity 备用策略已订阅。");
                    }
                }

                for (int i = 0; i < 7 && !TickDriver.FirstFrameSeen; i++)
                {
                    Thread.Sleep(5000);
                }

                if (!TickDriver.FirstFrameSeen)
                {
                    Boot.Log.Error("看门狗：60 秒仍未收到任何帧回调。可将 Cldou/config/loader.json 的 tickHook 设为 none 做对照，并把日志发回分析。");
                }
            }
            catch (Exception ex)
            {
                try { Boot.Log.Error("看门狗异常：" + ex); } catch { }
            }
        }
    }
}
