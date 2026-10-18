using System;
using System.Collections.Generic;
using System.IO;
using Cldou.Api;
using Cldou.Core.Json;

namespace Cldou.Runtime
{
    /// <summary>
    /// 独立模式下的 mod 配置：JSON 文件（Cldou/config/mods/&lt;id&gt;.json）。
    /// BepInEx 宿主使用 BepInExModConfig（.cfg），二者对 mod 完全透明。
    /// </summary>
    public sealed class JsonModConfig : IModConfig, IDisposable
    {
        private readonly string _path;
        private readonly Dictionary<string, object> _data = new Dictionary<string, object>(StringComparer.Ordinal);
        private bool _dirty;

        public JsonModConfig(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    object root;
                    string error;
                    if (MiniJson.TryParse(File.ReadAllText(path), out root, out error))
                    {
                        Dictionary<string, object> dict = MiniJson.AsObject(root);
                        if (dict != null)
                        {
                            foreach (KeyValuePair<string, object> pair in dict)
                            {
                                _data[pair.Key] = pair.Value;
                            }
                        }
                    }
                }
            }
            catch
            {
                // 损坏的配置按空处理
            }
        }

        public string GetString(string key, string fallback = null)
        {
            object value;
            if (_data.TryGetValue(key, out value) && value is string)
            {
                return (string)value;
            }
            return fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            object value;
            if (_data.TryGetValue(key, out value) && value is bool)
            {
                return (bool)value;
            }
            return fallback;
        }

        public int GetInt(string key, int fallback = 0)
        {
            object value;
            if (_data.TryGetValue(key, out value) && value is double)
            {
                return (int)(double)value;
            }
            return fallback;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            object value;
            if (_data.TryGetValue(key, out value) && value is double)
            {
                return (float)(double)value;
            }
            return fallback;
        }

        public void SetString(string key, string value) { _data[key] = value ?? ""; _dirty = true; }
        public void SetBool(string key, bool value) { _data[key] = value; _dirty = true; }
        public void SetInt(string key, int value) { _data[key] = (double)value; _dirty = true; }
        public void SetFloat(string key, float value) { _data[key] = (double)value; _dirty = true; }

        public void Save()
        {
            try
            {
                if (!_dirty && File.Exists(_path))
                {
                    return;
                }

                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_path, MiniJson.Serialize(_data, true));
                _dirty = false;
            }
            catch
            {
                // 容忍写入失败
            }
        }

        public void Dispose()
        {
            Save();
        }
    }
}
