using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cldou.Api;
using Cldou.Core.Manifests;

namespace Cldou.Runtime
{
    /// <summary>
    /// 程序集注入器。
    ///
    /// 关键设计：以字节方式加载（Assembly.Load(byte[])），而非 LoadFrom：
    ///   - 不锁定磁盘文件 —— mod 文件可在游戏运行时被替换（热重载前提）；
    ///   - 允许同标识程序集并存加载 —— 热重载时新旧版本可同时驻留；
    ///   - 不污染默认加载上下文，减少与 BepInEx / 游戏程序集的命名冲突。
    /// </summary>
    internal static class AssemblyInjector
    {
        public static Assembly InjectFromFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Assembly.Load(bytes);
        }

        /// <summary>
        /// 按清单 Entry 类型定位入口程序集：依次加载候选程序集并探测类型。
        /// 返回包含入口类型的程序集；其余已加载的程序集随后由解析钩子复用。
        /// </summary>
        public static Assembly LoadEntryAssembly(List<string> paths, string entryTypeName, IDiag diag)
        {
            Assembly firstLoaded = null;

            foreach (string path in paths)
            {
                Assembly assembly;
                try
                {
                    assembly = InjectFromFile(path);
                }
                catch (Exception ex)
                {
                    diag.Error("程序集加载失败 " + path + "：" + ex.Message);
                    continue;
                }

                if (firstLoaded == null)
                {
                    firstLoaded = assembly;
                }

                if (!string.IsNullOrEmpty(entryTypeName) && assembly.GetType(entryTypeName, false) != null)
                {
                    return assembly;
                }
            }

            if (firstLoaded != null && paths.Count == 1)
            {
                // 单程序集时即便未匹配到 entry（如 entry 错误）也返回，让 CreateEntry 给出更明确的报错
                return firstLoaded;
            }

            throw new InvalidOperationException(
                "在 " + paths.Count + " 个候选程序集中未找到入口类型 '" + entryTypeName + "'");
        }

        /// <summary>读取内嵌清单（资源名 "cldou.mod.json" 或 "*.cldou.mod.json"）。</summary>
        public static bool TryReadEmbeddedManifest(Assembly assembly, out string json)
        {
            json = null;
            string[] names = assembly.GetManifestResourceNames();
            string match = null;
            foreach (string name in names)
            {
                if (name == "cldou.mod.json"
                    || name.EndsWith(".cldou.mod.json", StringComparison.OrdinalIgnoreCase))
                {
                    match = name;
                    break;
                }
            }

            if (match == null)
            {
                return false;
            }

            using (Stream stream = assembly.GetManifestResourceStream(match))
            {
                if (stream == null)
                {
                    return false;
                }
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                    return true;
                }
            }
        }

        /// <summary>从程序集创建入口实例（校验非抽象、实现 ICldouMod、可无参构造）。</summary>
        public static ICldouMod CreateEntry(Assembly assembly, CldouManifest manifest)
        {
            Type type = assembly.GetType(manifest.Entry, false);
            if (type == null)
            {
                throw new InvalidOperationException(
                    "找不到入口类型 '" + manifest.Entry + "'（程序集 " + assembly.GetName().Name + "）");
            }

            if (type.IsAbstract || type.IsInterface)
            {
                throw new InvalidOperationException("入口类型不能是抽象类或接口：" + type.FullName);
            }

            if (!typeof(ICldouMod).IsAssignableFrom(type))
            {
                throw new InvalidOperationException("入口类型未实现 Cldou.Api.ICldouMod：" + type.FullName);
            }

            object instance = Activator.CreateInstance(type);
            ICldouMod mod = instance as ICldouMod;
            if (mod == null)
            {
                throw new InvalidOperationException("入口类型实例化失败：" + type.FullName);
            }
            return mod;
        }
    }
}
