using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using UnityEngine;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// Owns discovery, lifecycle, state persistence and the per-frame pump for every
    /// installed mod. Deliberately the ONLY BepInEx plugin in the kit: BepInEx's own
    /// plugins folder is all-or-nothing, so real enable/disable/uninstall semantics have
    /// to live here.
    /// </summary>
    public sealed class ModKitRuntime : MonoBehaviour
    {
        public const string HostApiVersion = "1.1";
        public const string HostVersion = "1.0.0";

        public static ModKitRuntime Instance { get; private set; }

        public ModKitLog Log { get; private set; }
        public UiService Ui { get; private set; }

        private readonly List<ModEntry> _entries = new List<ModEntry>();
        private readonly List<Action> _deferred = new List<Action>();
        private ConfigService _settings;
        private EventService _hostBus;
        private ManagerWindow _window;
        private bool _gameStarted;
        private float _nextStateSave;

        public IReadOnlyList<ModEntry> Entries => _entries;

        /// <summary>Host-level settings (ModKit.json): manager key, mod enable flags, window positions.</summary>
        public IConfigService Settings => _settings;
        public string ModsDirectory => Path.Combine(Paths.GameRootPath, "Mods");

        public void Initialize(ModKitLog log)
        {
            Instance = this;
            Log = log;
            Ui = new UiService(log);
            _window = new ManagerWindow(this);

            _settings = new ConfigService(Path.Combine(ModsDirectory, "ModKit.json"),
                                          new ModLogger(log, "modkit"));

            Log.Write("modkit", "INFO", $"Flotsam ModKit {HostVersion} (api {HostApiVersion})");
            Log.Write("modkit", "INFO", $"mods directory: {ModsDirectory}");

            // Full harvested sprite-name list, for tuning the skin without a log flood.
            NativeSkin.DumpPath = Path.Combine(ModsDirectory, "nativeskin_names.txt");

            DiscoverMods();
            WireGameEvents();
            ApplySavedStates();

            int enabled = 0;
            foreach (var e in _entries) if (e.State == ModRunState.Enabled) enabled++;
            Log.Write("modkit", "INFO", $"{enabled}/{_entries.Count} mod(s) enabled — manager key {ManagerKey}");

            // Persist defaults immediately so keybinds.json exists before the first rebind
            // (a force-killed process never reaches OnDestroy).
            foreach (var e in _entries)
            {
                try { e.Keybinds?.Save(); } catch { }
            }
            _settings.Save();

            Toast($"Flotsam ModKit 就绪：{enabled}/{_entries.Count} 个模组已启用（{ManagerKey} 打开管理器）", ToastKind.Info);
        }

        // ------------------------------------------------------------ discovery

        public void ReloadMods()
        {
            foreach (var e in _entries)
            {
                if (e.State == ModRunState.Enabled) e.Disable(keepDesiredFlag: true);
                e.Unload();
            }
            _entries.Clear();
            DiscoverMods();
            ApplySavedStates();
        }

        private void DiscoverMods()
        {
            try
            {
                if (!Directory.Exists(ModsDirectory)) Directory.CreateDirectory(ModsDirectory);

                foreach (var dir in Directory.GetDirectories(ModsDirectory))
                {
                    var folder = Path.GetFileName(dir);
                    if (folder.StartsWith(".")) continue; // .trash and friends

                    var manifestPath = Path.Combine(dir, "mod.json");
                    if (!File.Exists(manifestPath)) continue;

                    var manifest = ManifestReader.Read(manifestPath, out var error);
                    var entry = new ModEntry { Manifest = manifest ?? new ModManifest { Id = folder, Name = folder } };
                    entry.CreateServices(Log);

                    if (manifest == null)
                    {
                        entry.Fail(error);
                        _entries.Add(entry);
                        continue;
                    }

                    if (!ManifestReader.ApiCompatible(manifest, HostApiVersion, out var why))
                    {
                        entry.Fail("API 不兼容: " + why);
                        _entries.Add(entry);
                        continue;
                    }

                    entry.State = ModRunState.Disabled;
                    // Newly discovered mods start enabled (this is our own Mods folder);
                    // an explicit "enabled.<id> = 0" in ModKit.json keeps one off.
                    entry.DesiredEnabled = _settings.Get("enabled." + manifest.Id, true);
                    _entries.Add(entry);
                }

                Log.Write("modkit", "INFO", $"discovered {_entries.Count} mod(s)");
            }
            catch (Exception e)
            {
                Log.Write("modkit", "ERROR", "discovery failed: " + e);
            }
        }

        private void ApplySavedStates()
        {
            foreach (var entry in _entries)
            {
                if (entry.State == ModRunState.Failed) continue;
                if (!entry.DesiredEnabled) continue;
                if (string.Equals(entry.Manifest.Type, "asset", StringComparison.OrdinalIgnoreCase))
                {
                    entry.State = ModRunState.PendingRestart;
                    continue;
                }
                EnableMod(entry.Id);
            }
        }

        // ------------------------------------------------------------ lifecycle

        public bool EnableMod(string id)
        {
            var entry = Find(id);
            if (entry == null) return false;
            if (entry.State == ModRunState.Enabled) return true;

            bool ok = entry.Enable();
            _settings.Set("enabled." + id, true);
            SaveState();
            Toast(ok ? $"已启用 {entry.DisplayName}" : $"{entry.DisplayName} 启用失败",
                  ok ? ToastKind.Success : ToastKind.Error);
            return ok;
        }

        public bool DisableMod(string id)
        {
            var entry = Find(id);
            if (entry == null) return false;
            if (entry.State == ModRunState.Disabled) return true;

            entry.Disable();
            _settings.Set("enabled." + id, false);
            SaveState();
            Toast($"已禁用 {entry.DisplayName}（行为已回滚，程序集仍驻留内存）", ToastKind.Info);
            return true;
        }

        /// <summary>Deferred so we never delete a mod directory while its code is running.</summary>
        public void RequestDisable(string id) => _deferred.Add(() => DisableMod(id));
        public void RequestUninstall(string id) => _deferred.Add(() => UninstallMod(id));

        public bool UninstallMod(string id)
        {
            var entry = Find(id);
            if (entry == null) return false;

            if (entry.State == ModRunState.Enabled) entry.Disable();
            entry.Unload();

            try
            {
                var trash = Path.Combine(ModsDirectory, ".trash");
                if (!Directory.Exists(trash)) Directory.CreateDirectory(trash);
                var target = Path.Combine(trash, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}");
                Directory.Move(entry.Manifest.Directory, target);

                _settings.Set("enabled." + id, false);
                SaveState();
                _entries.Remove(entry);
                Toast($"{entry.DisplayName} 已卸载（可在 Mods/.trash 找回）", ToastKind.Warning);
                Log.Write("modkit", "INFO", $"uninstalled {id} -> {target}");
                return true;
            }
            catch (Exception e)
            {
                Log.Write("modkit", "ERROR", $"uninstall {id} failed: {e.Message}");
                Toast($"卸载失败: {e.Message}", ToastKind.Error);
                return false;
            }
        }

        public ModEntry Find(string id)
        {
            foreach (var e in _entries)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // ------------------------------------------------------------ game events

        private void WireGameEvents()
        {
            _hostBus = new EventService(new ModLogger(Log, "modkit"));

            _hostBus.On("GameStart", _ =>
            {
                // A new game/load replaces all live objects, so re-run the per-game hook.
                _gameStarted = true;
                if (Ui != null) Ui.ManagerWindowVisible = Ui.ManagerWindowVisible; // no-op, keeps UI alive
                foreach (var entry in _entries) entry.GameStart();
                Log.Write("modkit", "INFO", "GameStart dispatched to mods");
            });

            _hostBus.On("GameEnd", _ =>
            {
                _gameStarted = false;
                // Harvested sprites and fonts belong to the scene that just went away.
                try { Ui?.ResetSkin(); } catch { }
                foreach (var entry in _entries) entry.GameEnd();
                SaveAllConfigs();
                Log.Write("modkit", "INFO", "GameEnd dispatched to mods");
            });
        }

        // ------------------------------------------------------------ per frame

        private void Update()
        {
            try
            {
                if (_deferred.Count > 0)
                {
                    var actions = _deferred.ToArray();
                    _deferred.Clear();
                    foreach (var a in actions)
                    {
                        try { a(); } catch (Exception e) { Log.Write("modkit", "ERROR", "deferred action failed: " + e.Message); }
                    }
                }

                foreach (var entry in _entries) entry.Keybinds?.Poll();

                // Mods are plain classes, not MonoBehaviours: this is their only per-frame hook.
                foreach (var entry in _entries) entry.Tick();

                Ui?.Tick();

                if (ManagerHotkeyDown())
                {
                    Ui.ManagerWindowVisible = !Ui.ManagerWindowVisible;
                    if (Ui.ManagerWindowVisible) SaveState();
                }

                if (Time.unscaledTime > _nextStateSave)
                {
                    _nextStateSave = Time.unscaledTime + 20f;
                    foreach (var entry in _entries)
                    {
                        entry.Config?.Save();
                        entry.Keybinds?.SaveIfDirty();
                    }
                    _settings?.Save();
                }
            }
            catch (Exception e)
            {
                Log.Write("modkit", "ERROR", "update loop failed: " + e.Message);
            }
        }

        private bool ManagerHotkeyDown()
        {
            var key = ManagerKey;
            if (key == KeyCode.None) return false;
            return GameKeys.GetKeyDown(key);
        }

        public KeyCode ManagerKey
        {
            get
            {
                var raw = _settings.Get("managerKey", "F10");
                return Enum.TryParse(raw, true, out KeyCode k) ? k : KeyCode.F10;
            }
            set
            {
                _settings.Set("managerKey", value.ToString());
                SaveState();
            }
        }

        public bool GameStarted => _gameStarted;

        // ------------------------------------------------------------ persistence

        public void SaveState()
        {
            _settings?.Save();
        }

        public void SaveAllConfigs()
        {
            foreach (var entry in _entries)
            {
                entry.Config?.Save();
                entry.Keybinds?.Save();
            }
            _settings?.Save();
        }

        private void Toast(string message, ToastKind kind)
        {
            try { Ui?.Toast(message, kind); } catch { }
        }

        private void OnGUI()
        {
            if (Ui == null || !Ui.ManagerWindowVisible) return;
            try { _window?.Draw(); }
            catch (Exception e) { Log?.Write("modkit", "ERROR", "manager window draw failed: " + e.Message); }
        }

        private void OnDestroy()
        {
            try
            {
                foreach (var entry in _entries) entry.Disable(keepDesiredFlag: true);
                SaveAllConfigs();
                _hostBus?.ClearAll();
                Ui?.DestroyAll();
            }
            catch { }
            Instance = null;
        }
    }
}