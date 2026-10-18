using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Cldou.Api;

namespace Cldou.Plugin.Host
{
    /// <summary>IModConfig → 每 mod 独立 BepInEx ConfigFile（BepInEx/config/cldou/&lt;id&gt;.cfg）。</summary>
    internal sealed class BepInExModConfig : IModConfig, IDisposable
    {
        private const string Section = "Settings";

        private readonly ConfigFile _file;
        private readonly Dictionary<string, ConfigEntry<string>> _strings = new Dictionary<string, ConfigEntry<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigEntry<bool>> _bools = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigEntry<int>> _ints = new Dictionary<string, ConfigEntry<int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigEntry<float>> _floats = new Dictionary<string, ConfigEntry<float>>(StringComparer.Ordinal);

        public BepInExModConfig(string path)
        {
            _file = new ConfigFile(path, true);
        }

        private ConfigEntry<string> StringEntry(string key, string fallback)
        {
            ConfigEntry<string> entry;
            if (!_strings.TryGetValue(key, out entry))
            {
                entry = _file.Bind(Section, key, fallback ?? "", "");
                _strings[key] = entry;
            }
            return entry;
        }

        private ConfigEntry<bool> BoolEntry(string key, bool fallback)
        {
            ConfigEntry<bool> entry;
            if (!_bools.TryGetValue(key, out entry))
            {
                entry = _file.Bind(Section, key, fallback, "");
                _bools[key] = entry;
            }
            return entry;
        }

        private ConfigEntry<int> IntEntry(string key, int fallback)
        {
            ConfigEntry<int> entry;
            if (!_ints.TryGetValue(key, out entry))
            {
                entry = _file.Bind(Section, key, fallback, "");
                _ints[key] = entry;
            }
            return entry;
        }

        private ConfigEntry<float> FloatEntry(string key, float fallback)
        {
            ConfigEntry<float> entry;
            if (!_floats.TryGetValue(key, out entry))
            {
                entry = _file.Bind(Section, key, fallback, "");
                _floats[key] = entry;
            }
            return entry;
        }

        public string GetString(string key, string fallback = null)
        {
            string value = StringEntry(key, fallback).Value;
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            return BoolEntry(key, fallback).Value;
        }

        public int GetInt(string key, int fallback = 0)
        {
            return IntEntry(key, fallback).Value;
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            return FloatEntry(key, fallback).Value;
        }

        public void SetString(string key, string value) { StringEntry(key, "").Value = value ?? ""; }
        public void SetBool(string key, bool value) { BoolEntry(key, false).Value = value; }
        public void SetInt(string key, int value) { IntEntry(key, 0).Value = value; }
        public void SetFloat(string key, float value) { FloatEntry(key, 0f).Value = value; }

        public void Save()
        {
            try { _file.Save(); } catch { /* 容忍写入失败 */ }
        }

        public void Dispose()
        {
            Save();
        }
    }
}
