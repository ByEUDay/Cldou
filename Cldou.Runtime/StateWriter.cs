using System;
using System.Collections.Generic;
using System.IO;
using Cldou.Core.Json;

namespace Cldou.Runtime
{
    /// <summary>状态快照写入（Cldou/state/registry.json，原子替换）。</summary>
    internal static class StateWriter
    {
        public static void Write(CldouPaths paths, Dictionary<string, object> snapshot, IDiag diag)
        {
            try
            {
                Directory.CreateDirectory(paths.StateDir);
                string target = Path.Combine(paths.StateDir, "registry.json");
                string temp = target + ".tmp";

                File.WriteAllText(temp, MiniJson.Serialize(snapshot, true));

                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                File.Move(temp, target);
                diag.Debug("状态快照已写入 " + target);
            }
            catch (Exception ex)
            {
                diag.Warn("状态快照写入失败：" + ex.Message);
            }
        }
    }
}
