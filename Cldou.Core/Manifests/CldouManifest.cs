using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cldou.Core.Json;
using Cldou.Core.Versioning;

namespace Cldou.Core.Manifests
{
    /// <summary>清单来源类型。</summary>
    public enum ManifestSourceKind
    {
        /// <summary>文件夹形态 mod 的 cldou.mod.json。</summary>
        FolderSidecar,
        /// <summary>单文件 dll 旁的 &lt;name&gt;.dll.cldou.json。</summary>
        SingleFileSidecar,
        /// <summary>读取自程序集嵌入资源（注入阶段解析）。</summary>
        Embedded,
        Unknown
    }

    /// <summary>
    /// Cldou Mod 清单（cldou.mod.json）模型。
    /// 详见 docs/MANIFEST.md。
    /// </summary>
    public sealed class CldouManifest
    {
        public const string SchemaV1 = "cldou/v1";

        private static readonly Regex IdPattern = new Regex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

        public string Schema = SchemaV1;
        public string Id;
        public string Name;
        public string Version;
        public SemVersion SemVersion;
        public List<string> Authors = new List<string>();
        public string Description;
        public string Entry;
        public string Api;
        public string Game;
        public string Activation = "startup";
        public string Hash;
        public Dictionary<string, string> Dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> OptionalDependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> LoadAfter = new List<string>();
        public List<string> LoadBefore = new List<string>();

        /// <summary>清单文件路径（或嵌入来源程序集路径），用于诊断。</summary>
        public string SourcePath;
        public ManifestSourceKind SourceKind = ManifestSourceKind.Unknown;

        public bool IsManualActivation
        {
            get { return string.Equals(Activation, "manual", StringComparison.OrdinalIgnoreCase); }
        }

        public static bool TryParseJson(string json, string sourcePath, ManifestSourceKind kind, out CldouManifest manifest, out List<string> errors)
        {
            manifest = null;
            errors = new List<string>();

            object root;
            string jsonError;
            if (!MiniJson.TryParse(json, out root, out jsonError))
            {
                errors.Add("JSON 解析失败：" + jsonError);
                return false;
            }

            Dictionary<string, object> dict = MiniJson.AsObject(root);
            if (dict == null)
            {
                errors.Add("清单根节点必须是 JSON 对象");
                return false;
            }

            return TryFromDict(dict, sourcePath, kind, out manifest, out errors);
        }

        public static bool TryFromDict(Dictionary<string, object> dict, string sourcePath, ManifestSourceKind kind, out CldouManifest manifest, out List<string> errors)
        {
            errors = new List<string>();
            manifest = new CldouManifest
            {
                SourcePath = sourcePath,
                SourceKind = kind
            };

            manifest.Schema = MiniJson.GetString(dict, "schema", SchemaV1) ?? SchemaV1;
            if (!string.Equals(manifest.Schema, SchemaV1, StringComparison.Ordinal))
            {
                errors.Add("schema 必须为 '" + SchemaV1 + "'（实际为 '" + manifest.Schema + "'）");
            }

            manifest.Id = MiniJson.GetString(dict, "id");
            manifest.Name = MiniJson.GetString(dict, "name");
            manifest.Version = MiniJson.GetString(dict, "version");
            manifest.Description = MiniJson.GetString(dict, "description");
            manifest.Entry = MiniJson.GetString(dict, "entry");
            manifest.Api = MiniJson.GetString(dict, "api");
            manifest.Game = MiniJson.GetString(dict, "game");
            manifest.Activation = MiniJson.GetString(dict, "activation", "startup") ?? "startup";
            manifest.Hash = MiniJson.GetString(dict, "hash");

            // authors / author
            List<object> authors = MiniJson.GetArray(dict, "authors");
            if (authors != null)
            {
                foreach (object item in authors)
                {
                    string author = item as string;
                    if (author != null)
                    {
                        manifest.Authors.Add(author);
                    }
                }
            }
            else
            {
                string singleAuthor = MiniJson.GetString(dict, "author");
                if (!string.IsNullOrEmpty(singleAuthor))
                {
                    manifest.Authors.Add(singleAuthor);
                }
            }

            // dependencies / optionalDependencies
            ReadStringMap(MiniJson.GetObject(dict, "dependencies"), manifest.Dependencies, "dependencies", errors);
            ReadStringMap(MiniJson.GetObject(dict, "optionalDependencies"), manifest.OptionalDependencies, "optionalDependencies", errors);

            // loadAfter / loadBefore
            ReadStringList(MiniJson.GetArray(dict, "loadAfter"), manifest.LoadAfter);
            ReadStringList(MiniJson.GetArray(dict, "loadBefore"), manifest.LoadBefore);

            // ---- 校验 ----
            if (string.IsNullOrEmpty(manifest.Id))
            {
                errors.Add("缺少必需的 'id' 字段");
            }
            else if (!IdPattern.IsMatch(manifest.Id))
            {
                errors.Add("'id' 不合法（要求小写字母/数字开头，只含 a-z0-9._-，长度 1~64）：'" + manifest.Id + "'");
            }

            if (string.IsNullOrEmpty(manifest.Version))
            {
                errors.Add("缺少必需的 'version' 字段");
            }
            else
            {
                string versionError;
                if (!SemVersion.TryParse(manifest.Version, out manifest.SemVersion, out versionError))
                {
                    errors.Add("'version' 不是有效的版本号：'" + manifest.Version + "'（" + versionError + "）");
                }
            }

            if (string.IsNullOrEmpty(manifest.Entry))
            {
                errors.Add("缺少必需的 'entry' 字段（入口类型全名）");
            }

            if (!string.Equals(manifest.Activation, "startup", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(manifest.Activation, "manual", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("'activation' 只能是 \"startup\" 或 \"manual\"（实际为 '" + manifest.Activation + "'）");
            }

            return errors.Count == 0;
        }

        private static void ReadStringMap(Dictionary<string, object> source, Dictionary<string, string> target, string fieldName, List<string> errors)
        {
            if (source == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> pair in source)
            {
                string value = pair.Value as string;
                if (value == null)
                {
                    errors.Add("'" + fieldName + "' 中键 '" + pair.Key + "' 的值必须是字符串");
                    continue;
                }
                target[pair.Key] = value;
            }
        }

        private static void ReadStringList(List<object> source, List<string> target)
        {
            if (source == null)
            {
                return;
            }

            foreach (object item in source)
            {
                string value = item as string;
                if (!string.IsNullOrEmpty(value))
                {
                    target.Add(value);
                }
            }
        }

        /// <summary>序列化回 JSON 字典（状态快照 / 测试用）。</summary>
        public Dictionary<string, object> ToJsonDict()
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            dict["schema"] = Schema;
            dict["id"] = Id;
            dict["name"] = Name;
            dict["version"] = Version;
            if (Authors.Count > 0)
            {
                var authors = new List<object>();
                foreach (string author in Authors)
                {
                    authors.Add(author);
                }
                dict["authors"] = authors;
            }
            if (!string.IsNullOrEmpty(Description)) dict["description"] = Description;
            dict["entry"] = Entry;
            if (!string.IsNullOrEmpty(Api)) dict["api"] = Api;
            if (!string.IsNullOrEmpty(Game)) dict["game"] = Game;
            if (!string.Equals(Activation, "startup", StringComparison.OrdinalIgnoreCase)) dict["activation"] = Activation;
            if (!string.IsNullOrEmpty(Hash)) dict["hash"] = Hash;

            if (Dependencies.Count > 0)
            {
                var deps = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> pair in Dependencies)
                {
                    deps[pair.Key] = pair.Value;
                }
                dict["dependencies"] = deps;
            }

            if (OptionalDependencies.Count > 0)
            {
                var deps = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> pair in OptionalDependencies)
                {
                    deps[pair.Key] = pair.Value;
                }
                dict["optionalDependencies"] = deps;
            }

            if (LoadAfter.Count > 0)
            {
                var list = new List<object>();
                foreach (string item in LoadAfter) list.Add(item);
                dict["loadAfter"] = list;
            }

            if (LoadBefore.Count > 0)
            {
                var list = new List<object>();
                foreach (string item in LoadBefore) list.Add(item);
                dict["loadBefore"] = list;
            }

            return dict;
        }

        public override string ToString()
        {
            return (Id ?? "(无 id)") + "@" + (Version ?? "?");
        }
    }
}
