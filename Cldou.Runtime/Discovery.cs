using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cldou.Core.Manifests;

namespace Cldou.Runtime
{
    /// <summary>发现阶段产物：一个待加载的 mod 源。</summary>
    internal sealed class DiscoveredMod
    {
        public CldouManifest Manifest;         // 延迟清单模式（单文件无旁挂）时为 null，注入时读取
        public string RootDir;                 // 文件夹根（文件夹形态）或所在目录（单文件形态）
        public List<string> AssemblyPaths = new List<string>();
        public bool ManifestFromEmbedded;      // true = 注入阶段读取嵌入清单
    }

    /// <summary>
    /// 发现器：扫描 Cldou/mods 下的两类 mod：
    ///   A. 文件夹形态：&lt;dir&gt;/cldou.mod.json + 程序集（优先 bin/ 子目录）；
    ///   B. 单文件形态：&lt;name&gt;.dll（可选旁挂 &lt;name&gt;.dll.cldou.json；
    ///      无旁挂时清单从嵌入资源读取，加载顺序排在所有清单化 mod 之后）。
    /// </summary>
    internal static class Discovery
    {
        public const string ManifestFileName = "cldou.mod.json";

        public static List<DiscoveredMod> DiscoverAll(CldouPaths paths, IDiag diag)
        {
            var result = new List<DiscoveredMod>();
            if (!Directory.Exists(paths.ModsDir))
            {
                return result;
            }

            // A. 文件夹形态
            foreach (string dir in Directory.GetDirectories(paths.ModsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                string manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    diag.Warn("跳过目录（缺少 " + ManifestFileName + "）：" + dir);
                    continue;
                }

                DiscoveredMod discovered = TryLoadFolderMod(dir, manifestPath, diag);
                if (discovered != null)
                {
                    result.Add(discovered);
                }
            }

            // B. 单文件形态
            foreach (string dll in Directory.GetFiles(paths.ModsDir, "*.dll").OrderBy(d => d, StringComparer.Ordinal))
            {
                DiscoveredMod discovered = TryLoadSingleFileMod(dll, diag);
                if (discovered != null)
                {
                    result.Add(discovered);
                }
            }

            return result;
        }

        public static DiscoveredMod TryLoadFolderMod(string dir, string manifestPath, IDiag diag)
        {
            CldouManifest manifest;
            List<string> errors;
            string json;
            try
            {
                json = File.ReadAllText(manifestPath);
            }
            catch (Exception ex)
            {
                diag.Error("读取清单失败 " + manifestPath + "：" + ex.Message);
                return null;
            }

            if (!CldouManifest.TryParseJson(json, manifestPath, ManifestSourceKind.FolderSidecar, out manifest, out errors))
            {
                diag.Error("清单校验失败 " + manifestPath + "：\n  - " + string.Join("\n  - ", errors.ToArray()));
                return null;
            }

            var discovered = new DiscoveredMod
            {
                Manifest = manifest,
                RootDir = dir
            };
            discovered.AssemblyPaths.AddRange(FindAssemblies(dir));

            if (discovered.AssemblyPaths.Count == 0)
            {
                diag.Error("未找到任何程序集（*.dll）：" + dir);
                return null;
            }

            return discovered;
        }

        public static DiscoveredMod TryLoadSingleFileMod(string dllPath, IDiag diag)
        {
            // 旁挂清单候选：<name>.dll.cldou.json 或 <name>.cldou.json
            string[] candidates =
            {
                dllPath + ".cldou.json",
                Path.Combine(
                    Path.GetDirectoryName(dllPath) ?? ".",
                    Path.GetFileNameWithoutExtension(dllPath) + ".cldou.json")
            };

            foreach (string sidecar in candidates)
            {
                if (!File.Exists(sidecar))
                {
                    continue;
                }

                CldouManifest manifest;
                List<string> errors;
                if (!CldouManifest.TryParseJson(File.ReadAllText(sidecar), sidecar, ManifestSourceKind.SingleFileSidecar, out manifest, out errors))
                {
                    diag.Error("清单校验失败 " + sidecar + "：\n  - " + string.Join("\n  - ", errors.ToArray()));
                    return null;
                }

                var single = new DiscoveredMod
                {
                    Manifest = manifest,
                    RootDir = Path.GetDirectoryName(dllPath)
                };
                single.AssemblyPaths.Add(dllPath);
                return single;
            }

            diag.Info("单文件 mod 无旁挂清单，将在注入阶段读取嵌入清单：" + Path.GetFileName(dllPath));
            var deferred = new DiscoveredMod
            {
                Manifest = null,
                RootDir = Path.GetDirectoryName(dllPath),
                ManifestFromEmbedded = true
            };
            deferred.AssemblyPaths.Add(dllPath);
            return deferred;
        }

        /// <summary>程序集发现：优先 bin/ 子目录；否则整个目录递归（稳定排序）。</summary>
        private static IEnumerable<string> FindAssemblies(string dir)
        {
            string bin = Path.Combine(dir, "bin");
            string[] files;

            if (Directory.Exists(bin))
            {
                files = Directory.GetFiles(bin, "*.dll", SearchOption.AllDirectories);
            }
            else
            {
                files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                             && !f.Contains(Path.DirectorySeparatorChar + "src" + Path.DirectorySeparatorChar))
                    .ToArray();
            }

            return files.OrderBy(f => f, StringComparer.Ordinal);
        }
    }
}
