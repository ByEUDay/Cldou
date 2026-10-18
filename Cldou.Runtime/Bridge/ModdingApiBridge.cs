using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Cldou.Runtime.Bridge
{
    /// <summary>外部生态中的一个 mod/插件条目（只读索引）。</summary>
    internal sealed class ExternalModEntry
    {
        public string Kind;    // "modding-api" | "bepinex-plugin"
        public string Id;
        public string Name;
        public string Version;
    }

    /// <summary>HK Modding API 探测结果。</summary>
    internal sealed class ModdingApiStatus
    {
        public bool Present;
        public string AssemblyName;
        public int LoadedModCount;
        public List<ExternalModEntry> Mods = new List<ExternalModEntry>();
        public string Note;
    }

    /// <summary>HKBridge（可选协同组件）状态：直接读取其共享 BridgeConfig 标志。</summary>
    internal sealed class HKBridgeStatus
    {
        public bool Present;
        public bool? HkApiPrecedence;
        public bool? HkApiLoaded;
        public bool? BepInExPreloaded;
    }

    /// <summary>
    /// 只读桥：探测 HK Modding API 与 BepInEx 生态，绝不修改它们的状态。
    ///
    /// 原则（详见 docs/COEXISTENCE.md）：
    ///   - 不调用 Modding.ModLoader 的任何加载方法（谁的孩子谁抱走）；
    ///   - 只通过反射读取其“已加载 mod”列表用于展示与快照；
    ///   - 所有反射访问用 try/catch 包裹，API 内部结构变化时优雅降级为“仅探测到存在”。
    /// </summary>
    internal static class ModdingApiBridge
    {
        public static ModdingApiStatus ProbeModdingApi()
        {
            var status = new ModdingApiStatus();

            Type modLoaderType = FindTypeAnywhere("Modding.ModLoader");
            if (modLoaderType == null)
            {
                status.Note = "未检测到 Modding.ModLoader 类型（HK Modding API 不在场或尚未初始化）";
                return status;
            }

            status.Present = true;
            status.AssemblyName = modLoaderType.Assembly.GetName().Name;
            status.Mods = IndexLoadedMods(modLoaderType);
            status.LoadedModCount = status.Mods.Count;
            if (status.Mods.Count == 0)
            {
                status.Note = "检测到 Modding.ModLoader，但未能读取已加载列表（尝试过 LoadedMods/Mods/GetLoadedMods 候选成员）";
            }
            return status;
        }

        private static List<ExternalModEntry> IndexLoadedMods(Type modLoaderType)
        {
            var result = new List<ExternalModEntry>();
            try
            {
                object collection = null;

                foreach (string name in new[] { "LoadedMods", "Mods", "loadedMods" })
                {
                    try
                    {
                        PropertyInfo prop = modLoaderType.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                        if (prop != null)
                        {
                            collection = prop.GetValue(null, null);
                            break;
                        }

                        FieldInfo field = modLoaderType.GetField(name, BindingFlags.Public | BindingFlags.Static);
                        if (field != null)
                        {
                            collection = field.GetValue(null);
                            break;
                        }
                    }
                    catch
                    {
                        // 尝试下一个候选
                    }
                }

                if (collection == null)
                {
                    foreach (string name in new[] { "GetLoadedMods", "GetMods", "LoadedMods" })
                    {
                        try
                        {
                            MethodInfo method = modLoaderType.GetMethod(
                                name, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                            if (method != null)
                            {
                                collection = method.Invoke(null, null);
                                break;
                            }
                        }
                        catch
                        {
                            // 尝试下一个候选
                        }
                    }
                }

                var enumerable = collection as IEnumerable;
                if (enumerable == null)
                {
                    return result;
                }

                foreach (object item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    string name = InvokeStringMethod(item, "GetName") ?? item.ToString();
                    string version = InvokeStringMethod(item, "GetVersion") ?? "?";
                    result.Add(new ExternalModEntry
                    {
                        Kind = "modding-api",
                        Id = "modding:" + name,
                        Name = name,
                        Version = version
                    });
                }
            }
            catch
            {
                // 结构不匹配时返回空列表，由调用方降级处理
            }

            return result.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static HKBridgeStatus ProbeHKBridge()
        {
            var status = new HKBridgeStatus();
            Type configType = FindTypeAnywhere("HKBridge.BridgeConfig");
            if (configType == null)
            {
                return status;
            }

            status.Present = true;
            status.HkApiPrecedence = ReadStaticBool(configType, "HkApiPrecedence");
            status.HkApiLoaded = ReadStaticBool(configType, "HkApiLoaded");
            status.BepInExPreloaded = ReadStaticBool(configType, "BepInExPreloaded");
            return status;
        }

        public static List<ExternalModEntry> IndexBepInExPlugins()
        {
            var result = new List<ExternalModEntry>();
            try
            {
                Type chainloaderType = FindTypeAnywhere("BepInEx.Bootstrap.Chainloader");
                if (chainloaderType == null)
                {
                    return result;
                }

                PropertyInfo infosProperty = chainloaderType.GetProperty("PluginInfos", BindingFlags.Public | BindingFlags.Static);
                if (infosProperty == null)
                {
                    return result;
                }

                var infos = infosProperty.GetValue(null, null) as IDictionary;
                if (infos == null)
                {
                    return result;
                }

                foreach (DictionaryEntry entry in infos)
                {
                    object info = entry.Value;
                    if (info == null)
                    {
                        continue;
                    }

                    object metadata = GetMemberValue(info, "Metadata");
                    if (metadata == null)
                    {
                        continue;
                    }

                    string guid = GetStringValue(metadata, "GUID") ?? Convert.ToString(entry.Key);
                    string name = GetStringValue(metadata, "Name") ?? guid;
                    string version = GetStringValue(metadata, "Version") ?? "?";

                    result.Add(new ExternalModEntry
                    {
                        Kind = "bepinex-plugin",
                        Id = guid,
                        Name = name,
                        Version = version
                    });
                }
            }
            catch
            {
                // 容忍
            }

            return result.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---------------------------------------------------------------
        // 反射助手
        // ---------------------------------------------------------------

        public static Type FindTypeAnywhere(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // 某些动态程序集可能抛异常
                }
            }
            return null;
        }

        private static string InvokeStringMethod(object target, string methodName)
        {
            try
            {
                MethodInfo method = target.GetType()
                    .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    object value = method.Invoke(target, null);
                    return value as string ?? (value != null ? value.ToString() : null);
                }
            }
            catch
            {
                // 容忍
            }
            return null;
        }

        private static bool? ReadStaticBool(Type type, string memberName)
        {
            try
            {
                PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    return (bool)prop.GetValue(null, null);
                }

                FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
                if (field != null && field.FieldType == typeof(bool))
                {
                    return (bool)field.GetValue(null);
                }
            }
            catch
            {
                // 容忍
            }
            return null;
        }

        private static object GetMemberValue(object target, string memberName)
        {
            try
            {
                PropertyInfo prop = target.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    return prop.GetValue(target, null);
                }

                FieldInfo field = target.GetType().GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    return field.GetValue(target);
                }
            }
            catch
            {
                // 容忍
            }
            return null;
        }

        private static string GetStringValue(object target, string memberName)
        {
            object value = GetMemberValue(target, memberName);
            return value != null ? value.ToString() : null;
        }
    }
}
