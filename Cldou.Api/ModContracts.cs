using System.Collections.Generic;

namespace Cldou.Api
{
    /// <summary>Cldou mod 生命周期状态（宿主上报用）。</summary>
    public enum ModState
    {
        Discovered,
        Loading,
        Loaded,
        Active,
        Disabled,
        Failed,
        PendingRestart,
        Deferred
    }

    /// <summary>
    /// Cldou mod 入口契约。
    /// 入口类必须：可见（public）、非抽象、无参可构造、实现本接口。
    /// 生命周期由 Cldou 运行时驱动（全部回调发生在游戏主线程）：
    ///
    ///   注入程序集 → OnLoad(context) → OnEnable() → [每帧 OnUpdate(dt)]
    ///   → OnDisable()（停用/热重载前）→ OnUnload()（卸载/退出前）
    ///
    /// 约定：OnLoad 抛异常 = 加载失败（进入熔断计数）；
    ///       OnEnable 之后才对游戏世界做可见操作。
    /// </summary>
    public interface ICldouMod
    {
        void OnLoad(ModContext context);
        void OnEnable();
        void OnDisable();
        void OnUnload();
        void OnUpdate(float deltaTime);
    }

    /// <summary>
    /// 可选接口：声明该 mod 支持热重载。
    ///
    /// 热重载流程（当 Cldou/mods 中的文件发生变化时触发）：
    ///   1. CaptureState()  —— 旧实例序列化最小状态（字符串键值对）；
    ///   2. 旧实例 OnDisable() / OnUnload()；
    ///   3. 新版程序集以字节形式注入；
    ///   4. RestoreState(state) —— 新实例恢复状态；
    ///   5. OnEnable()。
    ///
    /// 注意：Unity Mono 无法卸载程序集，旧版本类型将驻留内存直到退出游戏；
    /// 实现本接口时请避免在静态字段中保存不可迁移的大对象。
    /// </summary>
    public interface IReloadable
    {
        Dictionary<string, string> CaptureState();
        void RestoreState(Dictionary<string, string> state);
    }

    /// <summary>mod 元信息（来自 cldou.mod.json）。</summary>
    public sealed class ModInfo
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
        public string Author { get; private set; }
        public string SourcePath { get; private set; }

        public ModInfo(string id, string name, string version, string author, string sourcePath)
        {
            Id = id;
            Name = name;
            Version = version;
            Author = author;
            SourcePath = sourcePath;
        }

        public override string ToString()
        {
            return Id + "@" + Version;
        }
    }
}
