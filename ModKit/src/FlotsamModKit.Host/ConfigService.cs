using System;
using System.Collections.Generic;
using System.IO;
using FlotsamModKit.Abstractions;
using Newtonsoft.Json;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// Per-mod configuration persisted as JSON next to the mod
    /// (Mods/&lt;id&gt;/config.json). Types are stored as JSON scalars so simple
    /// dictionaries round-trip without extra machinery.
    /// </summary>
    public sealed class ConfigService : IConfigService
    {
        private readonly string _path;
        private readonly ILog _log;
        private Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _dirty;

        public event Action Changed;

        public ConfigService(string path, ILog log)
        {
            _path = path;
            _log = log;
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var text = File.ReadAllText(_path);
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(text);
                if (parsed != null) _values = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                _log?.Warn($"config read failed ({_path}): {e.Message}");
            }
        }

        public T Get<T>(string key, T defaultValue)
        {
            if (key == null || !_values.TryGetValue(key, out var raw)) return defaultValue;
            try
            {
                var t = typeof(T);
                if (t == typeof(string)) return (T)(object)raw;
                if (t == typeof(bool)) return (T)(object)(raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));
                if (t == typeof(int)) return (T)(object)int.Parse(raw);
                if (t == typeof(float)) return (T)(object)float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (t == typeof(double)) return (T)(object)double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (t.IsEnum) return (T)Enum.Parse(t, raw, true);
                return JsonConvert.DeserializeObject<T>(raw);
            }
            catch
            {
                return defaultValue;
            }
        }

        public void Set<T>(string key, T value)
        {
            if (key == null) return;
            string raw;
            var t = typeof(T);
            if (t == typeof(string)) raw = (string)(object)value;
            else if (t == typeof(bool)) raw = ((bool)(object)value) ? "1" : "0";
            else if (t == typeof(float)) raw = ((float)(object)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            else if (t == typeof(double)) raw = ((double)(object)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            else raw = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

            if (_values.TryGetValue(key, out var old) && old == raw) return;
            _values[key] = raw;
            _dirty = true;
            try { Changed?.Invoke(); } catch { }
        }

        public bool Has(string key) => key != null && _values.ContainsKey(key);

        public void Save()
        {
            if (!_dirty) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonConvert.SerializeObject(_values, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception e)
            {
                _log?.Warn($"config write failed ({_path}): {e.Message}");
            }
        }
    }
}