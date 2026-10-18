using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cldou.Api;
using Cldou.Core.Bus;
using Cldou.Core.Diagnostics;
using Cldou.Core.Json;
using Cldou.Core.Manifests;
using Cldou.Core.Resolution;
using Cldou.Core.Versioning;
using Cldou.Runtime.Bridge;
using UnityEngine;

namespace Cldou.Runtime
{
    /// <summary>
    /// Cldou 运行时总编排：发现 → 求解 → 注入 → 激活 → 帧更新 → 热重载 → 快照 → 关闭。
    /// 全部生命周期操作发生在 Unity 主线程。
    /// </summary>
    public sealed class CldouRuntime
    {
        private readonly ICldouSettings _settings;
        private readonly IDiag _diag;
        private readonly EventBus _bus = new EventBus();
        private readonly ErrorBudget _budget;
        private readonly HotReloadWatcher _watcher = new HotReloadWatcher();
        private readonly string _loaderName;
        private readonly string _loaderVersion;
        private readonly Func<string, IModConfig> _modConfigFactory;

        private readonly List<ModHost> _hosts = new List<ModHost>();
        private readonly List<DiscoveredMod> _deferredSingles = new List<DiscoveredMod>();

        private EnvironmentInfo _environment;
        private LoadPlan _plan;
        private bool _started;
        private bool _shutDown;
        private bool _bootFailed;

        public CldouPaths Paths { get; private set; }

        public EventBus Bus
        {
            get { return _bus; }
        }

        public IDiag Log
        {
            get { return _diag; }
        }

        public CldouRuntime(IDiag log, ICldouSettings settings, CldouPaths paths, string loaderName, string loaderVersion, Func<string, IModConfig> modConfigFactory)
        {
            _settings = settings;
            _loaderName = loaderName;
            _loaderVersion = loaderVersion;
            _modConfigFactory = modConfigFactory;
            Paths = paths;
            _diag = new Diag(log, () => settings.Verbose);
            _budget = new ErrorBudget(settings.ErrorBudget);
        }

        /// <summary>为某个 mod 创建配置存储（由宿主实现：BepInEx cfg 或 JSON 文件）。</summary>
        public IModConfig CreateModConfig(string modId)
        {
            try
            {
                IModConfig config = _modConfigFactory != null ? _modConfigFactory(modId) : null;
                return config ?? NullModConfig.Instance;
            }
            catch (Exception ex)
            {
                _diag.Warn("创建 mod 配置失败 " + modId + "：" + ex.Message);
                return NullModConfig.Instance;
            }
        }

        // ---------------------------------------------------------------
        // 启动（Awake 阶段）
        // ---------------------------------------------------------------

        public void Boot()
        {
            _diag.Info(_loaderName + " v" + _loaderVersion + " 正在启动（运行时 Mod 注入层）…");

            if (!_settings.Enabled)
            {
                _diag.Warn("Cldou 已在配置中禁用，跳过全部加载。");
                return;
            }

            try
            {
                Paths.EnsureCreated();
            }
            catch (Exception ex)
            {
                _diag.Error("目录初始化失败：" + ex);
                _bootFailed = true;
                return;
            }

            AssemblyResolveHook.Install(Paths, _diag);
            _bus.DispatchError += (ex, topic) =>
                _diag.Warn("总线订阅者异常（主题 " + topic + "）：" + ex.Message);

            // 总线命令：允许任意 Cldou mod 通过总线申请启停其它 mod
            _bus.Subscribe("cldou.command.enable", OnCommandEnable, 0, this);
            _bus.Subscribe("cldou.command.disable", OnCommandDisable, 0, this);

            // 发现
            List<DiscoveredMod> discovered = Discovery.DiscoverAll(Paths, _diag);
            _deferredSingles.Clear();
            _deferredSingles.AddRange(discovered.Where(d => d.ManifestFromEmbedded));
            List<DiscoveredMod> manifestMods = discovered.Where(d => !d.ManifestFromEmbedded).ToList();

            // 按清单 game 字段过滤（支持 hollowknight / silksong；空=通配）
            string gameId = "unknown";
            try { gameId = Paths.DetectGameId(); } catch { }
            try
            {
                if (_environment != null && !string.IsNullOrEmpty(_environment.GameId))
                    gameId = _environment.GameId;
            }
            catch { }
            if (!string.IsNullOrEmpty(gameId) && gameId != "unknown")
            {
                var filtered = new List<DiscoveredMod>();
                foreach (DiscoveredMod d in manifestMods)
                {
                    string mg = d.Manifest != null ? d.Manifest.Game : null;
                    if (CldouPaths.MatchesGameFilter(mg, gameId))
                    {
                        filtered.Add(d);
                    }
                    else
                    {
                        _diag.Info("跳过 mod（game 不匹配当前游戏 " + gameId + "）："
                            + (d.Manifest != null ? d.Manifest.Id : "?")
                            + " [manifest.game=" + (mg ?? "<empty>") + "]");
                    }
                }
                manifestMods = filtered;
            }
            _diag.Info("当前游戏识别：" + gameId + "（共 " + manifestMods.Count + " 个匹配清单的 mod）");

            // 依赖求解（确定性拓扑）
            _plan = DependencyGraph.Resolve(manifestMods.Select(d => d.Manifest).ToList());
            LogPlan();

            // 建宿主
            foreach (CldouManifest manifest in _plan.Ordered)
            {
                DiscoveredMod source = manifestMods.First(d => ReferenceEquals(d.Manifest, manifest));
                var host = new ModHost(this, _diag, source)
                {
                    ForcedActivation = _plan.ForcedActivation.Contains(manifest.Id)
                };
                _hosts.Add(host);
            }

            foreach (ExcludedEntry excluded in _plan.Excluded)
            {
                _diag.Error("已排除 " + excluded.Mod.Id + "：" + excluded.Reason);
            }

            _diag.Info("发现 " + discovered.Count + " 个 mod：计划加载 " + _hosts.Count
                + " 个，排除 " + _plan.Excluded.Count + " 个，延迟清单（单文件）" + _deferredSingles.Count + " 个。");

            // 加载剖面
            ApplyProfile(_settings.Profile);
        }

        // ---------------------------------------------------------------
        // 运行（Start 阶段：所有插件 Awake 完成之后）
        // ---------------------------------------------------------------

        public void StartRuntime()
        {
            if (_shutDown || _bootFailed || Paths == null)
            {
                return;
            }
            _started = true;

            try
            {
                // 1) 按依赖顺序注入启动态 mods
                foreach (ModHost host in _hosts)
                {
                    if (host.State == ModState.Deferred)
                    {
                        continue;
                    }

                    if (host.Manifest.IsManualActivation && !host.ForcedActivation)
                    {
                        host.SetDeferred("manifest 声明为 manual 激活");
                        continue;
                    }

                    if (!VerifyIntegrity(host.Source))
                    {
                        continue;
                    }

                    if (!host.InjectAndLoad())
                    {
                        continue;
                    }

                    host.Activate();
                    _diag.Info("已加载 " + host.Id + " v" + host.Manifest.Version + "（" + host.LastLoadMs + " ms）");
                }

                // 2) 延迟清单单文件（不参与依赖求解；按文件名序）
                foreach (DiscoveredMod single in _deferredSingles)
                {
                    var host = new ModHost(this, _diag, single);
                    if (!VerifyIntegrity(single))
                    {
                        continue;
                    }

                    if (host.InjectAndLoad() && host.Activate())
                    {
                        _hosts.Add(host);
                        _diag.Info("已加载（单文件）" + host.Id + " v" + host.Manifest.Version);
                    }
                    else
                    {
                        _diag.Error("单文件 mod 加载失败：" + (host.LastError ?? host.Source.AssemblyPaths[0]));
                    }
                }

                // 3) 环境哨兵 + 外部只读索引
                RefreshEnvironment();

                // 4) 热重载侦听
                if (_settings.HotReload)
                {
                    _watcher.Start(new[] { Paths.ModsDir, Paths.StagingDir }, _diag);
                }

                // 5) 就绪
                int active = _hosts.Count(h => h.State == ModState.Active);
                int failed = _hosts.Count(h => h.State == ModState.Failed);
                int deferred = _hosts.Count(h => h.State == ModState.Deferred);
                _diag.Info("Cldou 就绪：" + active + " 个激活，" + failed + " 个失败，" + deferred + " 个延迟。状态快照：Cldou/state/registry.json");

                PublishLoaderEvent("cldou.loader.ready", _loaderVersion);
                WriteSnapshot("startup");
            }
            catch (Exception ex)
            {
                _diag.Error("Cldou 启动失败：" + ex);
            }
        }

        // ---------------------------------------------------------------
        // 帧驱动
        // ---------------------------------------------------------------

        public void Tick(float deltaTime)
        {
            if (_shutDown)
            {
                return;
            }

            // 1) 总线泵送（mod 的订阅回调在这里执行）
            _bus.Pump(256);

            // 2) 每帧回调
            for (int i = 0; i < _hosts.Count; i++)
            {
                _hosts[i].RaiseUpdate(deltaTime);
            }

            // 3) 热重载批次（去抖后处理）
            if (_started && _settings.HotReload && _watcher.HasPending)
            {
                bool quiet;
                List<string> changed = _watcher.DrainIfQuiet(TimeSpan.FromMilliseconds(750), out quiet);
                if (quiet && changed.Count > 0)
                {
                    ProcessChanges(changed);
                }
            }

            // 4) 状态快照快捷键
            try
            {
                if (Input.GetKeyDown(_settings.SnapshotKey))
                {
                    WriteSnapshot("manual:" + _settings.SnapshotKey);
                    _diag.Info("运行状态快照已刷新（" + _settings.SnapshotKey + "）。");
                }
            }
            catch
            {
                // 输入模块缺失时跳过（例如无头环境）
            }
        }

        // ---------------------------------------------------------------
        // 关闭
        // ---------------------------------------------------------------

        public void Shutdown(string reason)
        {
            if (_shutDown)
            {
                return;
            }
            _shutDown = true;

            try
            {
                _watcher.Dispose();

                for (int i = _hosts.Count - 1; i >= 0; i--)
                {
                    ModHost host = _hosts[i];
                    if (host.State == ModState.Active)
                    {
                        host.Deactivate("关闭：" + reason);
                    }
                    if (host.Instance != null)
                    {
                        host.Unload("关闭：" + reason);
                    }
                }

                if (Paths != null)
                {
                    WriteSnapshot("shutdown");
                }

                _bus.Clear();
                _diag.Info("Cldou 已关闭（" + reason + "）。");
            }
            catch (Exception ex)
            {
                _diag.Error("关闭阶段异常：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // 安全调用 / 熔断
        // ---------------------------------------------------------------

        internal bool Safe(ModHost host, string label, Action action)
        {
            try
            {
                action();
                _budget.ReportSuccess(host.Id);
                return true;
            }
            catch (Exception ex)
            {
                host.ReportException(label, ex);
                int failures = _budget.ReportFailure(host.Id);

                if (!host.CircuitTripped && _budget.IsTripped(host.Id))
                {
                    host.Deactivate("错误预算熔断");
                    host.MarkCircuitTripped(failures);
                }
                return false;
            }
        }

        public void PublishLoaderEvent(string topic, object payload)
        {
            _bus.Publish(topic, payload, this);
        }

        // ---------------------------------------------------------------
        // 热重载处理
        // ---------------------------------------------------------------

        private void ProcessChanges(List<string> changedPaths)
        {
            _diag.Info("检测到 " + changedPaths.Count + " 处文件变化，开始处理…");

            var stagingItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modsItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in changedPaths)
            {
                string stagingName = FirstSegmentUnder(path, Paths.StagingDir);
                if (stagingName != null)
                {
                    if (!IsNoise(stagingName))
                    {
                        stagingItems.Add(Path.Combine(Paths.StagingDir, stagingName));
                    }
                    continue;
                }

                string modsName = FirstSegmentUnder(path, Paths.ModsDir);
                if (modsName != null && !IsNoise(modsName))
                {
                    modsItems.Add(modsName);
                }
            }

            foreach (string item in stagingItems)
            {
                ProcessStagingItem(item);
            }

            foreach (string name in modsItems)
            {
                ProcessModsChange(name);
            }
        }

        private void ProcessStagingItem(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    return;
                }

                string manifestPath = Path.Combine(dir, Discovery.ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    _diag.Warn("staging 项缺少清单，忽略：" + dir);
                    return;
                }

                DiscoveredMod fresh = Discovery.TryLoadFolderMod(dir, manifestPath, _diag);
                if (fresh == null)
                {
                    return;
                }

                string target = Path.Combine(Paths.ModsDir, Path.GetFileName(dir));
                if (Directory.Exists(target))
                {
                    _diag.Warn("staging 目标已存在，忽略：" + target);
                    return;
                }

                Directory.Move(dir, target);
                _diag.Info("staging 项已移入 mods：" + Path.GetFileName(dir));

                DiscoveredMod moved = Discovery.TryLoadFolderMod(target, Path.Combine(target, Discovery.ManifestFileName), _diag);
                if (moved != null)
                {
                    ApplyFresh(moved);
                }
            }
            catch (Exception ex)
            {
                _diag.Error("staging 处理失败：" + ex.Message);
            }
        }

        private void ProcessModsChange(string name)
        {
            try
            {
                string path = Path.Combine(Paths.ModsDir, name);

                if (Directory.Exists(path))
                {
                    string manifestPath = Path.Combine(path, Discovery.ManifestFileName);
                    if (!File.Exists(manifestPath))
                    {
                        HandleRemoved(name, "清单文件已移除");
                        return;
                    }

                    DiscoveredMod fresh = Discovery.TryLoadFolderMod(path, manifestPath, _diag);
                    if (fresh != null)
                    {
                        ApplyFresh(fresh);
                    }
                    return;
                }

                if (File.Exists(path) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    DiscoveredMod fresh = Discovery.TryLoadSingleFileMod(path, _diag);
                    if (fresh != null)
                    {
                        ApplyFresh(fresh);
                    }
                    return;
                }

                HandleRemoved(name, "文件或目录已移除");
            }
            catch (Exception ex)
            {
                _diag.Error("处理变更失败 " + name + "：" + ex.Message);
            }
        }

        private void ApplyFresh(DiscoveredMod fresh)
        {
            try
            {
                ModHost host = null;
                if (fresh.Manifest != null)
                {
                    host = _hosts.FirstOrDefault(h => h.Manifest != null && h.Manifest.Id == fresh.Manifest.Id);
                }
                else
                {
                    host = _hosts.FirstOrDefault(h => SameSourceFile(h.Source, fresh));
                }

                if (host == null)
                {
                    // 新 mod（热安装）
                    var newHost = new ModHost(this, _diag, fresh);
                    if (fresh.Manifest != null)
                    {
                        string reason;
                        if (!CheckDependenciesAgainstActive(fresh.Manifest, out reason))
                        {
                            newHost.Fail("热安装依赖未满足：" + reason, null);
                            _hosts.Add(newHost);
                            return;
                        }
                    }

                    if (newHost.InjectAndLoad() && newHost.Activate())
                    {
                        _hosts.Add(newHost);
                        _diag.Info("热安装完成：" + newHost.Id + " v" + (newHost.Manifest != null ? newHost.Manifest.Version : "?"));
                        WriteSnapshot("hot-install");
                    }
                    else
                    {
                        _diag.Error("热安装失败：" + (newHost.LastError ?? "未知原因"));
                    }
                    return;
                }

                // 既有 mod 更新
                if (host.State == ModState.Active || host.State == ModState.Loaded)
                {
                    if (host.IsReloadable)
                    {
                        if (host.ReloadFromSource(fresh))
                        {
                            _diag.Info("热重载完成：" + host.Id + "（注意：旧程序集驻留内存直至退出，属 Mono 限制）");
                            WriteSnapshot("hot-reload");
                        }
                        else
                        {
                            host.SetPendingRestart("热重载失败，已标记为下次启动生效");
                        }
                    }
                    else
                    {
                        host.SetPendingRestart("检测到文件更新：mod 未实现 Cldou.Api.IReloadable，更新将在下次启动生效");
                    }
                    return;
                }

                // Failed / Disabled / Deferred：更新来源后重试
                host.UpdateSource(fresh);
                if (host.Instance == null)
                {
                    host.Enable("文件更新重试");
                    WriteSnapshot("hot-retry");
                }
                else
                {
                    host.SetPendingRestart("已存在失败/停用实例，无法在运行时替换，更新将在下次启动生效");
                }
            }
            catch (Exception ex)
            {
                _diag.Error("应用变更失败：" + ex.Message);
            }
        }

        private void HandleRemoved(string name, string reason)
        {
            string path = Path.Combine(Paths.ModsDir, name);
            ModHost host = _hosts.FirstOrDefault(h =>
                (h.Source.RootDir != null && string.Equals(Path.GetFileName(h.Source.RootDir), name, StringComparison.OrdinalIgnoreCase))
                || (h.Source.AssemblyPaths.Count > 0 && string.Equals(Path.GetFileName(h.Source.AssemblyPaths[0]), name, StringComparison.OrdinalIgnoreCase)));

            if (host == null)
            {
                return;
            }

            if (host.State == ModState.Active || host.State == ModState.Loaded)
            {
                host.Deactivate(reason);
            }
            host.SetPendingRestart(reason + "；下次启动后不再加载");
            _diag.Warn("mod 来源已移除：" + name + "（" + reason + "）");
        }

        private bool CheckDependenciesAgainstActive(CldouManifest manifest, out string reason)
        {
            reason = null;
            foreach (KeyValuePair<string, string> dep in manifest.Dependencies)
            {
                ModHost depHost = _hosts.FirstOrDefault(h =>
                    h.Manifest != null && h.Manifest.Id == dep.Key && h.IsActiveOrLoaded);

                if (depHost == null)
                {
                    reason = "缺少已激活的依赖 '" + dep.Key + "'";
                    return false;
                }

                VersionRange range;
                string error;
                if (VersionRange.TryParse(dep.Value, out range, out error)
                    && depHost.Manifest.SemVersion != null
                    && !range.Contains(depHost.Manifest.SemVersion))
                {
                    reason = "依赖 '" + dep.Key + "' 版本不满足（需要 " + dep.Value + "，实际 " + depHost.Manifest.Version + "）";
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------
        // 完整性校验
        // ---------------------------------------------------------------

        private bool VerifyIntegrity(DiscoveredMod source)
        {
            var mode = _settings.Integrity;
            if (mode == IntegrityMode.Off)
            {
                return true;
            }

            CldouManifest manifest = source.Manifest;
            if (manifest == null || string.IsNullOrEmpty(manifest.Hash))
            {
                return true; // 无 hash 或延迟清单（注入时自校验嵌入内容）
            }

            string primary = ResolvePrimaryAssembly(source);
            if (primary == null)
            {
                _diag.Warn(manifest.Id + "：无法确定主程序集，跳过 hash 校验（建议 dll 与 mod 目录同名，或使用单文件形态）");
                return true;
            }

            try
            {
                string actual = ComputeSha256(primary);
                string expected = manifest.Hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? manifest.Hash.Substring(7)
                    : manifest.Hash;

                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string message = manifest.Id + "：hash 校验失败\n  期望 " + expected + "\n  实际 " + actual;

                if (mode == IntegrityMode.Strict)
                {
                    ModHost host = _hosts.FirstOrDefault(h => ReferenceEquals(h.Source, source));
                    if (host != null)
                    {
                        host.Fail("hash 校验失败（Strict 模式拒绝加载）", null);
                    }
                    _diag.Error(message + "（Strict：拒绝加载）");
                    return false;
                }

                _diag.Warn(message + "（Warn：继续加载）");
                return true;
            }
            catch (Exception ex)
            {
                _diag.Warn(manifest.Id + "：hash 校验异常 " + ex.Message + "（继续加载）");
                return true;
            }
        }

        private static string ResolvePrimaryAssembly(DiscoveredMod source)
        {
            if (source.AssemblyPaths.Count == 1)
            {
                return source.AssemblyPaths[0];
            }

            string dirName = Path.GetFileName(source.RootDir ?? "");
            if (!string.IsNullOrEmpty(dirName))
            {
                foreach (string path in source.AssemblyPaths)
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(path), dirName, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
            return null;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        // ---------------------------------------------------------------
        // 加载剖面
        // ---------------------------------------------------------------

        private void ApplyProfile(string profileName)
        {
            if (string.IsNullOrEmpty(profileName) || Paths == null)
            {
                return;
            }

            string path = Path.Combine(Paths.ProfilesDir, profileName + ".json");
            if (!File.Exists(path))
            {
                _diag.Debug("剖面文件不存在（按默认全启用）：" + path);
                return;
            }

            try
            {
                object root;
                string error;
                if (!MiniJson.TryParse(File.ReadAllText(path), out root, out error))
                {
                    _diag.Warn("剖面解析失败：" + error);
                    return;
                }

                Dictionary<string, object> dict = MiniJson.AsObject(root);
                if (dict == null)
                {
                    return;
                }

                HashSet<string> disabled = ReadStringArray(dict, "disabled");
                HashSet<string> manual = ReadStringArray(dict, "manual");

                foreach (ModHost host in _hosts)
                {
                    if (disabled.Contains(host.Id))
                    {
                        host.SetDeferred("剖面 '" + profileName + "' 禁用");
                    }
                    else if (manual.Contains(host.Id))
                    {
                        host.SetDeferred("剖面 '" + profileName + "' 标记为手动");
                    }
                }

                _diag.Info("已应用加载剖面：" + profileName + "（disabled=" + disabled.Count + ", manual=" + manual.Count + "）");
            }
            catch (Exception ex)
            {
                _diag.Warn("剖面应用失败：" + ex.Message);
            }
        }

        private static HashSet<string> ReadStringArray(Dictionary<string, object> dict, string key)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            List<object> array = MiniJson.GetArray(dict, key);
            if (array != null)
            {
                foreach (object item in array)
                {
                    string value = item as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        set.Add(value);
                    }
                }
            }
            return set;
        }

        // ---------------------------------------------------------------
        // 总线命令
        // ---------------------------------------------------------------

        private void OnCommandEnable(BusEvent busEvent)
        {
            string id = busEvent.Payload as string;
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            ModHost host = _hosts.FirstOrDefault(h => h.Id == id);
            if (host == null)
            {
                _diag.Warn("启用命令：未找到 " + id);
                return;
            }

            host.Enable("总线命令");
            WriteSnapshot("command-enable");
        }

        private void OnCommandDisable(BusEvent busEvent)
        {
            string id = busEvent.Payload as string;
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            ModHost host = _hosts.FirstOrDefault(h => h.Id == id);
            if (host == null)
            {
                _diag.Warn("停用命令：未找到 " + id);
                return;
            }

            host.Deactivate("总线命令");
            WriteSnapshot("command-disable");
        }

        // ---------------------------------------------------------------
        // 环境与快照
        // ---------------------------------------------------------------

        private void RefreshEnvironment()
        {
            _environment = EnvironmentProbe.Probe(
                Paths,
                _settings.IndexModdingApi,
                _settings.IndexBepInExPlugins,
                _loaderName);

            foreach (string line in _environment.Describe())
            {
                _diag.Info("环境哨兵 | " + line);
            }
        }

        public void WriteSnapshot(string trigger)
        {
            try
            {
                var root = new Dictionary<string, object>(StringComparer.Ordinal);
                root["schema"] = "cldou/state/v1";
                root["generatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                root["trigger"] = trigger;
                root["loader"] = _loaderName + " " + _loaderVersion;

                root["environment"] = _environment != null
                    ? _environment.ToDict()
                    : new Dictionary<string, object>(StringComparer.Ordinal);

                var mods = new List<object>();
                foreach (ModHost host in _hosts)
                {
                    var item = new Dictionary<string, object>(StringComparer.Ordinal);
                    item["id"] = host.Id;
                    item["name"] = host.Manifest != null ? host.Manifest.Name : "";
                    item["version"] = host.Manifest != null ? host.Manifest.Version : "";
                    item["state"] = host.State.ToString();
                    item["loadMs"] = (double)host.LastLoadMs;
                    item["reloadable"] = host.IsReloadable;
                    item["source"] = host.Source.RootDir ?? "";
                    if (!string.IsNullOrEmpty(host.LastError))
                    {
                        item["lastError"] = host.LastError;
                    }
                    mods.Add(item);
                }
                root["mods"] = mods;

                var excluded = new List<object>();
                if (_plan != null)
                {
                    foreach (ExcludedEntry entry in _plan.Excluded)
                    {
                        var item = new Dictionary<string, object>(StringComparer.Ordinal);
                        item["id"] = entry.Mod.Id;
                        item["reason"] = entry.Reason;
                        excluded.Add(item);
                    }
                }
                root["excluded"] = excluded;

                if (_plan != null && _plan.Warnings.Count > 0)
                {
                    var warnings = new List<object>();
                    foreach (string warning in _plan.Warnings)
                    {
                        warnings.Add(warning);
                    }
                    root["warnings"] = warnings;
                }

                StateWriter.Write(Paths, root, _diag);
            }
            catch (Exception ex)
            {
                _diag.Warn("构建快照失败：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // 工具
        // ---------------------------------------------------------------

        private void LogPlan()
        {
            if (_plan == null)
            {
                return;
            }

            _diag.Info("依赖求解完成：加载 " + _plan.Ordered.Count + " 个，排除 " + _plan.Excluded.Count
                + " 个，警告 " + _plan.Warnings.Count + " 条。");

            foreach (string warning in _plan.Warnings)
            {
                _diag.Warn("求解警告：" + warning);
            }

            foreach (CldouManifest manifest in _plan.Ordered)
            {
                _diag.Debug("加载顺序：" + manifest.Id + " v" + manifest.Version);
            }
        }

        private static bool SameSourceFile(DiscoveredMod a, DiscoveredMod b)
        {
            if (a == null || b == null || a.AssemblyPaths.Count == 0 || b.AssemblyPaths.Count == 0)
            {
                return false;
            }
            return string.Equals(a.AssemblyPaths[0], b.AssemblyPaths[0], StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNoise(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            if (name.StartsWith(".", StringComparison.Ordinal))
            {
                return true;
            }
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static string FirstSegmentUnder(string path, string dir)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(dir);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                string relative = full.Substring(root.Length + 1);
                int index = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                return index < 0 ? relative : relative.Substring(0, index);
            }
            catch
            {
                return null;
            }
        }
    }
}
