using System;
using System.Collections.Generic;
using System.IO;
using Cldou.Core.Json;
using Cldou.Runtime;
using UnityEngine;

namespace Cldou.Bootstrap
{
    /// <summary>独立模式配置（Cldou/config/loader.json；首次运行自动写出默认配置）。</summary>
    internal sealed class StandaloneSettings : ICldouSettings
    {
        public bool Enabled { get; private set; }
        public string Profile { get; private set; }
        public bool HotReload { get; private set; }
        public int ErrorBudget { get; private set; }
        public IntegrityMode Integrity { get; private set; }
        public bool IndexModdingApi { get; private set; }
        public bool IndexBepInExPlugins { get; private set; }
        public KeyCode SnapshotKey { get; private set; }
        public bool Verbose { get; private set; }

        /// <summary>帧回调策略：unity（默认，零补丁）/ harmony / monomod（实验性）/ none（诊断）。</summary>
        public string TickHook { get; private set; }

        private StandaloneSettings()
        {
            Enabled = true;
            Profile = "default";
            HotReload = true;
            ErrorBudget = 3;
            Integrity = IntegrityMode.Warn;
            IndexModdingApi = true;
            IndexBepInExPlugins = true;
            SnapshotKey = KeyCode.F10;
            Verbose = false;
            TickHook = "unity";
        }

        public static StandaloneSettings Load(CldouPaths paths)
        {
            var settings = new StandaloneSettings();
            string file = Path.Combine(paths.ConfigDir, "loader.json");

            try
            {
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(paths.ConfigDir);
                    File.WriteAllText(file, MiniJson.Serialize(settings.ToDict(), true));
                    return settings;
                }

                object root;
                string error;
                if (!MiniJson.TryParse(File.ReadAllText(file), out root, out error))
                {
                    return settings;
                }

                Dictionary<string, object> dict = MiniJson.AsObject(root);
                if (dict == null)
                {
                    return settings;
                }

                settings.Enabled = MiniJson.GetBool(dict, "enabled", settings.Enabled);
                settings.Profile = MiniJson.GetString(dict, "profile", settings.Profile);
                settings.HotReload = MiniJson.GetBool(dict, "hotReload", settings.HotReload);
                settings.ErrorBudget = MiniJson.GetInt(dict, "errorBudget", settings.ErrorBudget);
                settings.IndexModdingApi = MiniJson.GetBool(dict, "indexModdingApi", settings.IndexModdingApi);
                settings.IndexBepInExPlugins = MiniJson.GetBool(dict, "indexBepInExPlugins", settings.IndexBepInExPlugins);
                settings.Verbose = MiniJson.GetBool(dict, "verbose", settings.Verbose);
                settings.TickHook = MiniJson.GetString(dict, "tickHook", settings.TickHook);

                string mode = MiniJson.GetString(dict, "integrity", null);
                if (!string.IsNullOrEmpty(mode))
                {
                    try { settings.Integrity = (IntegrityMode)Enum.Parse(typeof(IntegrityMode), mode, true); }
                    catch { }
                }

                string key = MiniJson.GetString(dict, "snapshotKey", null);
                if (!string.IsNullOrEmpty(key))
                {
                    try { settings.SnapshotKey = (KeyCode)Enum.Parse(typeof(KeyCode), key, true); }
                    catch { }
                }
            }
            catch
            {
                // 读取失败时使用默认值
            }

            return settings;
        }

        private Dictionary<string, object> ToDict()
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["enabled"] = Enabled;
            dict["profile"] = Profile;
            dict["hotReload"] = HotReload;
            dict["errorBudget"] = (double)ErrorBudget;
            dict["integrity"] = Integrity.ToString();
            dict["indexModdingApi"] = IndexModdingApi;
            dict["indexBepInExPlugins"] = IndexBepInExPlugins;
            dict["snapshotKey"] = SnapshotKey.ToString();
            dict["verbose"] = Verbose;
            dict["tickHook"] = TickHook;
            return dict;
        }
    }
}
