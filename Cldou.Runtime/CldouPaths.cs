using System;
using System.IO;

namespace Cldou.Runtime
{
    /// <summary>
    /// 目录布局解析（宿主无关）。
    /// 全部路径相对“游戏根目录”（包含 BepInEx 或 hollow_knight.app / Silksong.app / *_Data 的那一层）：
    /// 同时支持 Hollow Knight 与 Hollow Knight: Silksong。
    ///
    ///   &lt;GameRoot&gt;/
    ///   ├── Cldou/
    ///   │   ├── mods/…             ← Cldou mods
    ///   │   ├── staging/…          ← 热安装暂存
    ///   │   ├── state/…            ← 状态快照
    ///   │   ├── profiles/…         ← 加载剖面
    ///   │   └── data/&lt;id&gt;/         ← mod 持久数据
    ///   └── config 目录由宿主决定：
    ///       · BepInEx 模式：BepInEx/config/cldou
    ///       · 独立模式：    Cldou/config
    /// </summary>
    public sealed class CldouPaths
    {
        public string GameRoot { get; private set; }
        public string Root { get; private set; }
        public string ModsDir { get; private set; }
        public string StagingDir { get; private set; }
        public string StateDir { get; private set; }
        public string ProfilesDir { get; private set; }
        public string DataDir { get; private set; }
        public string ConfigDir { get; private set; }
        public string PluginDir { get; private set; }

        public CldouPaths(string gameRoot, string configDir, string pluginDir)
        {
            GameRoot = gameRoot;
            PluginDir = pluginDir ?? "";
            Root = Path.Combine(gameRoot, "Cldou");
            ModsDir = Path.Combine(Root, "mods");
            StagingDir = Path.Combine(Root, "staging");
            StateDir = Path.Combine(Root, "state");
            ProfilesDir = Path.Combine(Root, "profiles");
            DataDir = Path.Combine(Root, "data");
            ConfigDir = configDir;
        }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(StagingDir);
            Directory.CreateDirectory(StateDir);
            Directory.CreateDirectory(ProfilesDir);
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(ConfigDir);
        }

        public string DataPathFor(string modId)
        {
            string sanitized = SanitizeName(modId);
            string path = Path.Combine(DataDir, sanitized);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "_unknown";
            }

            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '_' || c == '-';
                if (!ok)
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        // ---------------------------------------------------------------
        // 游戏根目录探测（各宿主复用）
        // ---------------------------------------------------------------

        /// <summary>从给定目录向上最多 6 层寻找“看起来像游戏根目录”的目录；找不到返回 null。</summary>
        public static string FindGameRoot(string startDir)
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(Path.GetFullPath(startDir));
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    if (LooksLikeGameRoot(dir.FullName))
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
            }
            catch
            {
                // 容忍
            }
            return null;
        }

        public static bool LooksLikeGameRoot(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    return false;
                }
                if (Directory.GetDirectories(dir, "*.app").Length > 0)
                {
                    return true;
                }
                if (Directory.GetDirectories(dir, "*_Data").Length > 0)
                {
                    return true;
                }
                if (File.Exists(Path.Combine(dir, "BepInEx", "core", "BepInEx.dll")))
                {
                    return true;
                }
            }
            catch
            {
                // 容忍
            }
            return false;
        }

        /// <summary>定位游戏 Managed 目录（macOS 的 .app 或 Windows/Linux 的 *_Data；只读用途）。</summary>
        public string ResolveManagedDir()
        {
            try
            {
                foreach (string app in Directory.GetDirectories(GameRoot, "*.app"))
                {
                    string macManaged = Path.Combine(app, "Contents", "Resources", "Data", "Managed");
                    if (Directory.Exists(macManaged))
                    {
                        return macManaged;
                    }
                }

                foreach (string data in Directory.GetDirectories(GameRoot, "*_Data"))
                {
                    string managed = Path.Combine(data, "Managed");
                    if (Directory.Exists(managed))
                    {
                        return managed;
                    }
                }

                string direct = Path.Combine(GameRoot, "Managed");
                if (Directory.Exists(direct))
                {
                    return direct;
                }
            }
            catch
            {
                // 容忍探测失败
            }

            return null;
        }

        /// <summary>HK Modding API 的 mods 目录（Managed/Mods；备选 游戏根/Mods）。</summary>
        public string ResolveApiModsDir()
        {
            string managed = ResolveManagedDir();
            if (managed != null)
            {
                string underManaged = Path.Combine(managed, "Mods");
                if (Directory.Exists(underManaged))
                {
                    return underManaged;
                }
            }

            string rootMods = Path.Combine(GameRoot, "Mods");
            if (Directory.Exists(rootMods))
            {
                return rootMods;
            }

            return managed != null ? Path.Combine(managed, "Mods") : rootMods;
        }

        // ---------------------------------------------------------------
        // 游戏身份识别（Hollow Knight / Silksong）
        // ---------------------------------------------------------------

        /// <summary>
        /// 规范化的游戏 ID，用于清单 <c>game</c> 字段匹配。
        /// 取值：<c>hollowknight</c>、<c>silksong</c>，无法识别时为 <c>unknown</c>。
        /// </summary>
        public string DetectGameId()
        {
            return DetectGameId(GameRoot);
        }

        /// <summary>人类可读游戏名。</summary>
        public string DetectGameTitle()
        {
            string id = DetectGameId();
            if (id == "silksong") return "Hollow Knight: Silksong";
            if (id == "hollowknight") return "Hollow Knight";
            return "Unknown";
        }

        public static string DetectGameId(string gameRoot)
        {
            if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot))
            {
                return "unknown";
            }

            try
            {
                // 1) 可执行 / .app / *_Data 目录名
                string[] hints = new string[0];
                try
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (string app in Directory.GetDirectories(gameRoot, "*.app"))
                        names.Add(Path.GetFileName(app));
                    foreach (string data in Directory.GetDirectories(gameRoot, "*_Data"))
                        names.Add(Path.GetFileName(data));
                    foreach (string file in Directory.GetFiles(gameRoot))
                        names.Add(Path.GetFileName(file));
                    hints = names.ToArray();
                }
                catch { }

                foreach (string h in hints)
                {
                    string lower = h.ToLowerInvariant();
                    if (lower.Contains("silksong") || lower.Contains("silk_song") || lower.Contains("hkss"))
                        return "silksong";
                }

                foreach (string h in hints)
                {
                    string lower = h.ToLowerInvariant();
                    if (lower.Contains("hollow") || lower.Contains("knight") || lower == "hollow_knight.app"
                        || lower.StartsWith("hollow_knight") || lower.StartsWith("hollowknight"))
                        return "hollowknight";
                }

                // 2) 进程名 / 产品名兜底（Unity Application.productName 在 Probe 时另读）
                try
                {
                    string proc = System.Diagnostics.Process.GetCurrentProcess().ProcessName ?? "";
                    string pl = proc.ToLowerInvariant();
                    if (pl.Contains("silksong")) return "silksong";
                    if (pl.Contains("hollow") || pl.Contains("knight")) return "hollowknight";
                }
                catch { }
            }
            catch
            {
                // 容忍
            }

            return "unknown";
        }

        /// <summary>
        /// 清单 <c>game</c> 字段是否匹配当前游戏。
        /// 空 / * / any → 通配；支持 hollowknight / hk / silksong / hkss 等别名。
        /// </summary>
        public static bool MatchesGameFilter(string manifestGame, string currentGameId)
        {
            if (string.IsNullOrEmpty(manifestGame)) return true;
            string g = manifestGame.Trim().ToLowerInvariant();
            if (g == "*" || g == "any" || g == "all") return true;

            string cur = (currentGameId ?? "unknown").Trim().ToLowerInvariant();
            if (cur == "unknown") return true; // 识别不出时不拦截

            // 规范化别名
            cur = NormalizeGameId(cur);
            g = NormalizeGameId(g);
            return g == cur;
        }

        public static string NormalizeGameId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unknown";
            string g = id.Trim().ToLowerInvariant();
            if (g == "hk" || g == "hollow_knight" || g == "hollow-knight" || g == "hollow knight")
                return "hollowknight";
            if (g == "hkss" || g == "silk_song" || g == "silk-song" || g == "hollow knight: silksong"
                || g == "hollowknightsilksong" || g == "hollow_knight_silksong")
                return "silksong";
            return g;
        }

    }
}
