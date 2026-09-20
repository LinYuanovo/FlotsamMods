using System;
using System.IO;
using System.Reflection;
using FlotsamModKit.Abstractions;

namespace FlotsamModKit.Host
{
    public enum ModRunState
    {
        Discovered,
        Disabled,
        Enabled,
        Failed,
        PendingRestart
    }

    /// <summary>
    /// One installed mod: its manifest, on-disk services and lifecycle.
    /// Load/Enable/Disable are individually guarded so one broken mod cannot take the
    /// game or the other mods down with it.
    /// </summary>
    public sealed class ModEntry
    {
        public ModManifest Manifest;
        public ModRunState State = ModRunState.Discovered;
        public string Error;
        public bool DesiredEnabled;

        public Assembly Assembly;
        public IFlotsamMod Instance;

        public ModLogger Log;
        public ConfigService Config;
        public KeybindService Keybinds;
        public PatchService Patches;
        public EventService Events;
        public ModContext Context;

        public bool IsLoaded => Assembly != null && Instance != null;
        public string Id => Manifest != null ? Manifest.Id : "(unknown)";
        public string DisplayName => Manifest != null ? Manifest.Name : "(unknown)";

        public string AssemblyPath
        {
            get
            {
                if (Manifest == null || string.IsNullOrEmpty(Manifest.EntryAssembly)) return null;
                return Path.Combine(Manifest.Directory, Manifest.EntryAssembly);
            }
        }

        public void CreateServices(ModKitLog log)
        {
            Log = new ModLogger(log, Id);
            Config = new ConfigService(Path.Combine(Manifest.Directory, "config.json"), Log);
            Keybinds = new KeybindService(Path.Combine(Manifest.Directory, "keybinds.json"), Log);
            Patches = new PatchService(Id, Log);
            Events = new EventService(Log);
            Context = new ModContext(this);
        }

        /// <summary>Loads the assembly and constructs the IFlotsamMod instance. Idempotent.</summary>
        public bool Load()
        {
            if (IsLoaded) return true;

            var path = AssemblyPath;
            if (path == null || !File.Exists(path))
            {
                Fail($"entry assembly not found: {path}");
                return false;
            }

            try
            {
                Assembly = Assembly.LoadFrom(path);
            }
            catch (Exception e)
            {
                Fail($"assembly load failed: {e.Message}");
                return false;
            }

            try
            {
                var type = Assembly.GetType(Manifest.EntryType, throwOnError: false);
                if (type == null)
                {
                    Fail($"entry type '{Manifest.EntryType}' not found in {Manifest.EntryAssembly}");
                    return false;
                }
                if (!typeof(IFlotsamMod).IsAssignableFrom(type))
                {
                    Fail($"entry type '{Manifest.EntryType}' does not implement IFlotsamMod");
                    return false;
                }

                Instance = (IFlotsamMod)Activator.CreateInstance(type);
            }
            catch (Exception e)
            {
                Fail($"entry type instantiation failed: {e.Message}");
                return false;
            }

            try
            {
                Instance.OnLoad(Context);
                Log.Info($"loaded v{Manifest.Version} from {Manifest.EntryAssembly}");
            }
            catch (Exception e)
            {
                Fail($"OnLoad threw: {e}");
                return false;
            }

            return true;
        }

        public bool Enable()
        {
            if (State == ModRunState.Enabled) return true;

            if (!Load()) return false;

            try
            {
                Instance.OnEnable();
            }
            catch (Exception e)
            {
                Fail($"OnEnable threw: {e}");
                SafeDisable();
                return false;
            }

            State = ModRunState.Enabled;
            DesiredEnabled = true;
            Error = null;
            return true;
        }

        public void Disable(bool keepDesiredFlag = false)
        {
            SafeDisable();
            State = ModRunState.Disabled;
            if (!keepDesiredFlag) DesiredEnabled = false;
            Error = null;
        }

        private void SafeDisable()
        {
            if (Instance != null)
            {
                try { Instance.OnDisable(); }
                catch (Exception e) { Log?.Error($"OnDisable threw: {e}"); }
            }

            // The host owns these three reversals, so even a mod that forgets to clean up
            // cannot leak patches or event listeners.
            try { Patches?.RevokeAll(); } catch { }
            try { Events?.ClearAll(); } catch { }
            try { Config?.Save(); } catch { }
            try { Keybinds?.Save(); } catch { }
        }

        public void Unload()
        {
            if (Instance != null)
            {
                try { Instance.OnUnload(); }
                catch (Exception e) { Log?.Error($"OnUnload threw: {e}"); }
            }
            Instance = null;
            // Assembly itself cannot be unloaded by Mono; it stays resident.
        }

        public void GameStart()
        {
            if (State != ModRunState.Enabled || Instance == null) return;
            try { Instance.OnGameStart(); }
            catch (Exception e) { Log?.Error($"OnGameStart threw: {e}"); }
        }

        private int _tickErrors;

        /// <summary>
        /// Per-frame pump. A throwing mod is reported a few times and then silenced, so one bad
        /// mod cannot fill the log at 60 lines a second.
        /// </summary>
        public void Tick()
        {
            if (State != ModRunState.Enabled || Instance == null) return;
            try
            {
                Instance.OnTick();
                _tickErrors = 0;
            }
            catch (Exception e)
            {
                _tickErrors++;
                if (_tickErrors <= 5) Log?.Error($"OnTick threw: {e.Message}");
                else if (_tickErrors == 6) Log?.Error("OnTick keeps throwing — further errors suppressed");
            }
        }

        public void GameEnd()
        {
            if (State != ModRunState.Enabled || Instance == null) return;
            try { Instance.OnGameEnd(); }
            catch (Exception e) { Log?.Error($"OnGameEnd threw: {e}"); }
        }

        public void Fail(string message)
        {
            Error = message;
            State = ModRunState.Failed;
            Log?.Error(message);
        }

        public string StateLabel()
        {
            switch (State)
            {
                case ModRunState.Enabled: return "已启用";
                case ModRunState.Disabled: return "已禁用";
                case ModRunState.Failed: return "加载失败";
                case ModRunState.PendingRestart: return "需重启";
                default: return "已发现";
            }
        }
    }

    /// <summary>The per-mod view of the host services handed to IFlotsamMod.</summary>
    public sealed class ModContext : IModContext
    {
        private readonly ModEntry _entry;

        public ModContext(ModEntry entry)
        {
            _entry = entry;
            Control = new ModControl(entry);
        }

        public ModManifest Manifest => _entry.Manifest;
        public string ModDirectory => _entry.Manifest != null ? _entry.Manifest.Directory : "";
        public ILog Log => _entry.Log;
        public IConfigService Config => _entry.Config;
        public IKeybindService Keybinds => _entry.Keybinds;
        public IPatchService Patches => _entry.Patches;
        public IGameEvents Events => _entry.Events;
        public IUiService Ui => ModKitRuntime.Instance != null ? ModKitRuntime.Instance.Ui : null;
        public IModControl Control { get; }
        public bool IsEnabled => _entry.State == ModRunState.Enabled;
    }

    public sealed class ModControl : IModControl
    {
        private readonly ModEntry _entry;

        public ModControl(ModEntry entry)
        {
            _entry = entry;
        }

        public void DisableSelf()
        {
            ModKitRuntime.Instance?.RequestDisable(_entry.Id);
        }

        public void RequestUninstall()
        {
            ModKitRuntime.Instance?.RequestUninstall(_entry.Id);
        }
    }
}