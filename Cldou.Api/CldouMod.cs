using System;

namespace Cldou.Api
{
    /// <summary>
    /// 推荐的 mod 基类：将 ICldouMod 的显式接口实现映射到受保护的虚方法，
    /// 并提供 Context / Log / Bus / Config 便捷访问。
    ///
    /// 最小示例：
    /// <code>
    /// public sealed class HelloMod : CldouMod
    /// {
    ///     protected override void OnLoad()
    ///     {
    ///         Log.Info("Hello from " + Context.Info.Id);
    ///     }
    ///
    ///     protected override void OnEnable()
    ///     {
    ///         Bus.Subscribe("my.topic", payload => Log.Info("收到: " + payload), 0);
    ///     }
    /// }
    /// </code>
    /// </summary>
    public abstract class CldouMod : ICldouMod
    {
        public ModContext Context { get; private set; }

        protected IModLogger Log
        {
            get { return Context != null ? Context.Logger : null; }
        }

        protected ICldouBus Bus
        {
            get { return Context != null ? Context.Bus : null; }
        }

        protected IModConfig Config
        {
            get { return Context != null ? Context.Config : null; }
        }

        void ICldouMod.OnLoad(ModContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }
            Context = context;
            OnLoad();
        }

        void ICldouMod.OnEnable()
        {
            OnEnable();
        }

        void ICldouMod.OnDisable()
        {
            OnDisable();
        }

        void ICldouMod.OnUnload()
        {
            OnUnload();
        }

        void ICldouMod.OnUpdate(float deltaTime)
        {
            OnUpdate(deltaTime);
        }

        protected virtual void OnLoad() { }
        protected virtual void OnEnable() { }
        protected virtual void OnDisable() { }
        protected virtual void OnUnload() { }
        protected virtual void OnUpdate(float deltaTime) { }
    }
}
