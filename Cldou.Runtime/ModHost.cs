using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Cldou.Api;
using Cldou.Core.Manifests;

namespace Cldou.Runtime
{
    /// <summary>
    /// 单个 mod 的宿主：持有其程序集、实例、配置与状态机。
    ///
    /// 状态机：
    ///   Discovered → Loading → Loaded → Active ⇄ Disabled
    ///                         ↘ Failed（注入失败 / 熔断）
    ///                         ↘ PendingRestart（文件更新但不可热重载）
    ///                         ↘ Deferred（manual 激活，等待启用）
    /// </summary>
    internal sealed class ModHost
    {
        private readonly CldouRuntime _runtime;
        private readonly IDiag _diag;

        public DiscoveredMod Source { get; private set; }
        public CldouManifest Manifest { get; private set; }
        public ICldouMod Instance { get; private set; }
        public Assembly EntryAssembly { get; private set; }
        public IModConfig ConfigAdapter { get; private set; }
        public string DataPath { get; private set; }

        public ModState State { get; private set; }
        public string LastError { get; private set; }
        public long LastLoadMs { get; private set; }
        public bool ForcedActivation { get; set; }
        public bool CircuitTripped { get; private set; }

        private bool _deactivating;

        public ModHost(CldouRuntime runtime, IDiag diag, DiscoveredMod source)
        {
            _runtime = runtime;
            _diag = diag;
            Source = source;
            Manifest = source.Manifest;
            State = ModState.Discovered;
        }

        public string Id
        {
            get { return Manifest != null ? Manifest.Id : "(嵌入清单未解析)"; }
        }

        public bool IsActiveOrLoaded
        {
            get { return State == ModState.Active || State == ModState.Loaded; }
        }

        public bool IsReloadable
        {
            get { return Instance is IReloadable; }
        }

        // ---------------------------------------------------------------
        // 生命周期
        // ---------------------------------------------------------------

        /// <summary>注入程序集并调用 OnLoad。成功返回 true。</summary>
        public bool InjectAndLoad()
        {
            State = ModState.Loading;
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                if (Source.ManifestFromEmbedded)
                {
                    if (!TryResolveEmbeddedManifest())
                    {
                        return false;
                    }
                }

                if (EntryAssembly == null)
                {
                    EntryAssembly = AssemblyInjector.LoadEntryAssembly(Source.AssemblyPaths, Manifest.Entry, _diag);
                }

                Instance = AssemblyInjector.CreateEntry(EntryAssembly, Manifest);

                ConfigAdapter = _runtime.CreateModConfig(Manifest.Id);
                DataPath = _runtime.Paths.DataPathFor(Manifest.Id);

                var context = new ModContext(
                    new ModInfo(
                        Manifest.Id,
                        Manifest.Name,
                        Manifest.Version,
                        Manifest.Authors.Count > 0 ? string.Join(", ", Manifest.Authors.ToArray()) : "",
                        Manifest.SourcePath),
                    new ModLogAdapter(_runtime.Log, Manifest.Id),
                    new BusAdapter(_runtime.Bus, this),
                    ConfigAdapter,
                    Source.RootDir,
                    DataPath,
                    CldouVersion.Version);

                if (!_runtime.Safe(this, "OnLoad", delegate { Instance.OnLoad(context); }))
                {
                    return false;
                }

                stopwatch.Stop();
                LastLoadMs = stopwatch.ElapsedMilliseconds;
                LastError = null;
                State = ModState.Loaded;
                return true;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LastLoadMs = stopwatch.ElapsedMilliseconds;
                Fail("注入/加载失败：" + ex.Message, ex);
                return false;
            }
        }

        private bool TryResolveEmbeddedManifest()
        {
            try
            {
                string path = Source.AssemblyPaths[0];
                Assembly assembly = AssemblyInjector.InjectFromFile(path);

                string json;
                if (!AssemblyInjector.TryReadEmbeddedManifest(assembly, out json))
                {
                    Fail("单文件 mod 缺少清单：未找到旁挂 " + Path.GetFileName(path) + ".cldou.json，程序集内也没有嵌入 cldou.mod.json", null);
                    return false;
                }

                CldouManifest manifest;
                List<string> errors;
                if (!CldouManifest.TryParseJson(json, path + " (embedded)", ManifestSourceKind.Embedded, out manifest, out errors))
                {
                    Fail("嵌入清单校验失败：\n  - " + string.Join("\n  - ", errors.ToArray()), null);
                    return false;
                }

                Manifest = manifest;
                EntryAssembly = assembly;
                return true;
            }
            catch (Exception ex)
            {
                Fail("读取嵌入清单失败：" + ex.Message, ex);
                return false;
            }
        }

        /// <summary>调用 OnEnable，进入 Active。</summary>
        public bool Activate()
        {
            if (Instance == null)
            {
                return false;
            }
            if (State == ModState.Active)
            {
                return true;
            }

            if (!_runtime.Safe(this, "OnEnable", delegate { Instance.OnEnable(); }))
            {
                return false;
            }

            State = ModState.Active;
            _runtime.PublishLoaderEvent("cldou.mod.enabled", Id);
            return true;
        }

        /// <summary>调用 OnDisable 并退订总线（不卸载程序集）。带重入保护，供熔断路径安全复用。</summary>
        public void Deactivate(string reason)
        {
            if (_deactivating)
            {
                State = ModState.Disabled;
                return;
            }

            _deactivating = true;
            try
            {
                if (State == ModState.Active || State == ModState.Loaded)
                {
                    if (Instance != null)
                    {
                        _runtime.Safe(this, "OnDisable", delegate { Instance.OnDisable(); });
                    }
                    _runtime.Bus.UnsubscribeOwner(this);
                    _diag.Info("已停用 " + Id + "（" + reason + "）");
                    _runtime.PublishLoaderEvent("cldou.mod.disabled", Id + ":" + reason);
                }

                State = ModState.Disabled;
            }
            finally
            {
                _deactivating = false;
            }
        }

        /// <summary>调用 OnUnload 并释放配置句柄（最佳努力；程序集无法真正卸载）。</summary>
        public void Unload(string reason)
        {
            if (Instance != null)
            {
                _runtime.Safe(this, "OnUnload", delegate { Instance.OnUnload(); });
                _runtime.Bus.UnsubscribeOwner(this);
            }

            if (ConfigAdapter != null)
            {
                var disposable = ConfigAdapter as IDisposable;
                if (disposable != null)
                {
                    try { disposable.Dispose(); } catch { }
                }
                ConfigAdapter = null;
            }

            Instance = null;
            _diag.Info("已卸载 " + Id + "（" + reason + "）");
        }

        /// <summary>热重载：状态迁移 → 换装新程序集 → 恢复状态 → 重新启用。</summary>
        public bool ReloadFromSource(DiscoveredMod fresh)
        {
            Dictionary<string, string> state = null;
            var reloadableOld = Instance as IReloadable;
            if (reloadableOld != null)
            {
                _runtime.Safe(this, "CaptureState", delegate { state = reloadableOld.CaptureState(); });
            }

            Deactivate("热重载：停用旧实例");
            Unload("热重载：卸载旧实例");

            Source = fresh;
            if (fresh.Manifest != null)
            {
                Manifest = fresh.Manifest;
            }
            EntryAssembly = null;

            if (!InjectAndLoad())
            {
                return false;
            }

            var reloadableNew = Instance as IReloadable;
            if (reloadableNew != null && state != null)
            {
                _runtime.Safe(this, "RestoreState", delegate { reloadableNew.RestoreState(state); });
            }

            return Activate();
        }

        /// <summary>每帧回调。</summary>
        public void RaiseUpdate(float deltaTime)
        {
            if (State != ModState.Active || Instance == null)
            {
                return;
            }
            _runtime.Safe(this, "OnUpdate", delegate { Instance.OnUpdate(deltaTime); });
        }

        /// <summary>启用（Deferred/Disabled → 注入并激活；用于 manual 激活与运行时启停）。</summary>
        public bool Enable(string reason)
        {
            if (State == ModState.Active)
            {
                return true;
            }

            if (Instance == null)
            {
                if (!InjectAndLoad())
                {
                    return false;
                }
            }

            bool ok = Activate();
            if (ok)
            {
                _diag.Info("已启用 " + Id + "（" + reason + "）");
            }
            return ok;
        }

        /// <summary>标记为“文件已变更，等下次启动生效”。</summary>
        public void SetPendingRestart(string reason)
        {
            State = ModState.PendingRestart;
            LastError = reason;
            _diag.Warn(Id + "：" + reason);
        }

        /// <summary>更新来源引用（换装新文件信息，不立即操作实例）。</summary>
        public void UpdateSource(DiscoveredMod fresh)
        {
            Source = fresh;
            if (fresh != null && fresh.Manifest != null)
            {
                Manifest = fresh.Manifest;
            }
        }

        // ---------------------------------------------------------------
        // 诊断
        // ---------------------------------------------------------------

        public void ReportException(string label, Exception ex)
        {
            LastError = label + "：" + ex.GetType().Name + " - " + ex.Message;
            _diag.Error("[" + Id + "] " + label + " 抛出异常：" + ex.ToString());
        }

        public void MarkCircuitTripped(int failureCount)
        {
            CircuitTripped = true;
            State = ModState.Failed;
            LastError = "连续失败 " + failureCount + " 次，已熔断停用";
            _diag.Error("[" + Id + "] " + LastError + "。该 mod 已隔离，其它 mod 不受影响。");
            _runtime.PublishLoaderEvent("cldou.mod.tripped", Id);
        }

        public void Fail(string message, Exception ex)
        {
            LastError = message;
            State = ModState.Failed;
            _diag.Error("[" + Id + "] " + message + (ex != null ? "\n" + ex.ToString() : ""));
        }

        public void SetDeferred(string reason)
        {
            State = ModState.Deferred;
            LastError = reason;
        }
    }
}
