using System;
using System.Collections.Generic;
using System.IO;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using Newtonsoft.Json;
using UnityEngine;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// Keybinds are polled through Rewired's keyboard directly, NOT through Rewired
    /// actions, so a bind keeps working even when the relevant action map category is
    /// disabled (e.g. town movement keys while the world map is closed).
    /// </summary>
    public sealed class KeybindService : IKeybindService
    {
        private readonly Dictionary<string, Keybind> _binds = new Dictionary<string, Keybind>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;
        private readonly ILog _log;
        private bool _dirty;

        public KeybindService(string path, ILog log)
        {
            _path = path;
            _log = log;
            try
            {
                if (File.Exists(path))
                {
                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                    if (parsed != null)
                        foreach (var kv in parsed) _saved[kv.Key] = kv.Value;
                }
            }
            catch (Exception e)
            {
                _log?.Warn($"keybinds read failed: {e.Message}");
            }
        }

        public IReadOnlyList<IKeybind> All
        {
            get
            {
                var list = new List<IKeybind>();
                foreach (var b in _binds.Values) list.Add(b);
                list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                return list;
            }
        }

        public IKeybind Register(string id, KeyCode defaultKey, string displayName = null,
                                 KeybindOptions options = KeybindOptions.None)
        {
            if (string.IsNullOrEmpty(id)) id = "bind";
            if (_binds.TryGetValue(id, out var existing)) return existing;

            var bind = new Keybind(id, string.IsNullOrEmpty(displayName) ? id : displayName, defaultKey, options);
            if (_saved.TryGetValue(id, out var stored) &&
                Enum.TryParse(stored, true, out KeyCode parsed) && parsed != KeyCode.None)
            {
                bind.Key = parsed;
            }
            bind.Rebound += (k) => { _dirty = true; Save(); };
            _binds[id] = bind;
            return bind;
        }

        public IKeybind Find(string id)
        {
            return id != null && _binds.TryGetValue(id, out var b) ? b : null;
        }

        public void Save()
        {
            try
            {
                var map = new Dictionary<string, string>();
                foreach (var kv in _binds) map[kv.Key] = kv.Value.Key.ToString();
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonConvert.SerializeObject(map, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception e)
            {
                _log?.Warn($"keybinds write failed: {e.Message}");
            }
        }

        public void SaveIfDirty()
        {
            if (_dirty) Save();
        }

        /// <summary>Called once per frame by the runtime, before mods are ticked.</summary>
        public void Poll()
        {
            bool typing = GameApi.IsTyping;
            bool paused = GameApi.IsPaused;
            bool playing = GameApi.IsPlaying;
            foreach (var bind in _binds.Values) bind.Poll(typing, paused, playing);
        }

        private sealed class Keybind : IKeybind
        {
            private bool _wasHeld;
            private KeyCode _key;

            public Keybind(string id, string displayName, KeyCode key, KeybindOptions options)
            {
                Id = id;
                DisplayName = displayName;
                _key = key;
                Options = options;
            }

            public string Id { get; }
            public string DisplayName { get; }
            public KeybindOptions Options { get; }

            /// <summary>Assigning raises Rebound so the service can persist the change.</summary>
            public KeyCode Key
            {
                get => _key;
                set
                {
                    if (_key == value) return;
                    _key = value;
                    try { Rebound?.Invoke(value); } catch { }
                }
            }

            public bool IsDown { get; private set; }
            public bool IsHeld { get; private set; }
            public bool IsUp { get; private set; }

            public event Action<KeyCode> Rebound;

            public void Poll(bool typing, bool paused, bool playing)
            {
                bool blocked = !playing
                               || (typing && (Options & KeybindOptions.AllowWhileTyping) == 0)
                               || (paused && (Options & KeybindOptions.AllowWhilePaused) == 0);

                if (blocked)
                {
                    IsDown = false;
                    IsHeld = false;
                    IsUp = false;
                    _wasHeld = false;
                    return;
                }

                bool held = GameKeys.GetKey(Key);
                IsDown = held && !_wasHeld;
                IsUp = !held && _wasHeld;
                IsHeld = held;
                _wasHeld = held;
            }
        }
    }
}