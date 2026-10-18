using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Cldou.Runtime.Bridge
{
    /// <summary>环境哨兵结果：描述“Cldou 身处怎样的 mod 生态”。</summary>
    internal sealed class EnvironmentInfo
    {
        public string HostName;
        public string Platform;
        public string GameVersion;
        public string GameId;
        public string GameTitle;
        public string BepInExVersion;
        public string GameRoot;
        public string ManagedDir;
        public string ApiModsDir;
        public int ApiModsFolderCount;
        public bool MonoModPatchedHint;
        public ModdingApiStatus ModdingApi = new ModdingApiStatus();
        public HKBridgeStatus HKBridge = new HKBridgeStatus();
        public List<ExternalModEntry> BepInExPlugins = new List<ExternalModEntry>();

        public Dictionary<string, object> ToDict()
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["host"] = HostName;
            dict["platform"] = Platform;
            dict["gameVersion"] = GameVersion;
            dict["gameId"] = GameId;
            dict["gameTitle"] = GameTitle;
            dict["bepinexVersion"] = BepInExVersion;
            dict["gameRoot"] = GameRoot;
            dict["managedDir"] = ManagedDir;
            dict["apiModsDir"] = ApiModsDir;
            dict["apiModsFolderCount"] = (double)ApiModsFolderCount;
            dict["monoModPatchedHint"] = MonoModPatchedHint;

            var api = new Dictionary<string, object>(StringComparer.Ordinal);
            api["present"] = ModdingApi.Present;
            api["assembly"] = ModdingApi.AssemblyName;
            api["loadedModCount"] = (double)ModdingApi.LoadedModCount;
            if (!string.IsNullOrEmpty(ModdingApi.Note))
            {
                api["note"] = ModdingApi.Note;
            }
            dict["moddingApi"] = api;

            var bridge = new Dictionary<string, object>(StringComparer.Ordinal);
            bridge["present"] = HKBridge.Present;
            if (HKBridge.HkApiPrecedence.HasValue) bridge["hkApiPrecedence"] = HKBridge.HkApiPrecedence.Value;
            if (HKBridge.HkApiLoaded.HasValue) bridge["hkApiLoaded"] = HKBridge.HkApiLoaded.Value;
            if (HKBridge.BepInExPreloaded.HasValue) bridge["bepinexPreloaded"] = HKBridge.BepInExPreloaded.Value;
            dict["hkbridge"] = bridge;

            var external = new List<object>();
            foreach (ExternalModEntry entry in ModdingApi.Mods)
            {
                external.Add(EntryToDict(entry));
            }
            foreach (ExternalModEntry entry in BepInExPlugins)
            {
                external.Add(EntryToDict(entry));
            }
            dict["external"] = external;

            return dict;
        }

        private static Dictionary<string, object> EntryToDict(ExternalModEntry entry)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["kind"] = entry.Kind;
            dict["id"] = entry.Id;
            dict["name"] = entry.Name;
            dict["version"] = entry.Version;
            return dict;
        }

        public List<string> Describe()
        {
            var lines = new List<string>();
            lines.Add("加载器：" + HostName + "，平台：" + Platform + "，游戏：" + GameTitle + " (" + GameId + ")，版本：" + GameVersion);
            lines.Add("BepInEx：" + BepInExVersion);
            lines.Add("游戏根目录：" + GameRoot);
            if (!string.IsNullOrEmpty(ApiModsDir))
            {
                lines.Add("HK API mods 目录：" + ApiModsDir + "（" + ApiModsFolderCount + " 个子目录，只读）");
            }

            if (ModdingApi.Present)
            {
                lines.Add("HK Modding API：已检测（程序集 " + ModdingApi.AssemblyName + "，已加载 " + ModdingApi.LoadedModCount + " 个 mod）");
            }
            else
            {
                lines.Add("HK Modding API：未检测到（Cldou 独立运行，互不影响）");
            }

            if (HKBridge.Present)
            {
                lines.Add("HKBridge：已检测（HkApiPrecedence=" + Fmt(HKBridge.HkApiPrecedence)
                    + ", HkApiLoaded=" + Fmt(HKBridge.HkApiLoaded)
                    + ", BepInExPreloaded=" + Fmt(HKBridge.BepInExPreloaded) + "）——只读协同");
            }

            if (MonoModPatchedHint)
            {
                lines.Add("补丁状态：检测到 Assembly-CSharp 的 MonoMod 映射（HK API 离线补丁路径，Cldou 不参与、不触碰）");
            }

            if (BepInExPlugins.Count > 0)
            {
                lines.Add("BepInEx 插件（" + BepInExPlugins.Count + " 个，只读）："
                    + string.Join(", ", ToNames(BepInExPlugins).ToArray()));
            }

            return lines;
        }

        private static List<string> ToNames(List<ExternalModEntry> entries)
        {
            var names = new List<string>();
            foreach (ExternalModEntry entry in entries)
            {
                names.Add(entry.Name + " " + entry.Version);
            }
            return names;
        }

        private static string Fmt(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "?";
        }
    }

    /// <summary>环境探测（全部只读，宿主无关）。</summary>
    internal static class EnvironmentProbe
    {
        public static EnvironmentInfo Probe(CldouPaths paths, bool indexApi, bool indexBepInEx, string hostName)
        {
            var info = new EnvironmentInfo();
            info.HostName = hostName;

            try { info.Platform = Application.platform.ToString(); } catch { info.Platform = "?"; }
            try { info.GameVersion = Application.version; } catch { info.GameVersion = "?"; }

            try
            {
                info.GameId = paths.DetectGameId();
                // Unity productName 兜底纠正
                try
                {
                    string product = Application.productName ?? "";
                    string pl = product.ToLowerInvariant();
                    if (pl.Contains("silksong") || pl.Contains("silk song"))
                        info.GameId = "silksong";
                    else if (info.GameId == "unknown" && (pl.Contains("hollow") || pl.Contains("knight")))
                        info.GameId = "hollowknight";
                }
                catch { }
                info.GameTitle = info.GameId == "silksong" ? "Hollow Knight: Silksong"
                    : info.GameId == "hollowknight" ? "Hollow Knight" : "Unknown";
            }
            catch
            {
                info.GameId = "unknown";
                info.GameTitle = "Unknown";
            }

            try
            {
                Type chainloader = ModdingApiBridge.FindTypeAnywhere("BepInEx.Bootstrap.Chainloader");
                info.BepInExVersion = chainloader != null
                    ? chainloader.Assembly.GetName().Version.ToString()
                    : "未安装";
            }
            catch
            {
                info.BepInExVersion = "?";
            }

            info.GameRoot = paths.GameRoot;
            info.ManagedDir = paths.ResolveManagedDir();
            info.ApiModsDir = paths.ResolveApiModsDir();

            try
            {
                if (!string.IsNullOrEmpty(info.ApiModsDir) && Directory.Exists(info.ApiModsDir))
                {
                    info.ApiModsFolderCount = Directory.GetDirectories(info.ApiModsDir).Length;
                }
            }
            catch
            {
                // 容忍
            }

            // MonoMod 化提示（只读文件系统检查）
            try
            {
                string monomodDir = Path.Combine(paths.GameRoot, "BepInEx", "monomod");
                if (Directory.Exists(monomodDir))
                {
                    foreach (string file in Directory.GetFiles(monomodDir, "*.mm.dll"))
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
                        {
                            info.MonoModPatchedHint = true;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // 容忍
            }

            if (indexApi)
            {
                info.ModdingApi = ModdingApiBridge.ProbeModdingApi();
            }

            info.HKBridge = ModdingApiBridge.ProbeHKBridge();

            if (indexBepInEx)
            {
                info.BepInExPlugins = ModdingApiBridge.IndexBepInExPlugins();
            }

            return info;
        }
    }
}
