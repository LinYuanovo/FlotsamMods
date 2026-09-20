namespace FlotsamModKit.Abstractions
{
    /// <summary>
    /// The single entry point of every ModKit mod.
    /// Lifecycle contract:
    ///   OnLoad   - exactly once, after the assembly is loaded. Register patches,
    ///              keybinds and config here; do NOT touch live game objects.
    ///   OnEnable - may run many times. Apply patches, subscribe, build UI.
    ///   OnDisable- must fully revert OnEnable (the host also revokes patches and
    ///              event subscriptions it owns, but you must destroy your own objects).
    ///   OnUnload - release anything left; the assembly itself stays resident in Mono.
    ///   OnTick   - once per frame while enabled, from the host's own pump. Use it to poll
    ///              keybinds; a mod class is NOT a MonoBehaviour, so an Update() on it would
    ///              never run.
    ///   OnGameStart / OnGameEnd - fired from the game's own GameEventDispatcher.
    /// </summary>
    public interface IFlotsamMod
    {
        void OnLoad(IModContext context);
        void OnEnable();
        void OnDisable();
        void OnUnload();
        void OnTick();
        void OnGameStart();
        void OnGameEnd();
    }

    /// <summary>Convenience base class; every hook is optional except OnLoad.</summary>
    public abstract class FlotsamModBase : IFlotsamMod
    {
        protected IModContext Ctx { get; private set; }
        protected ILog Log => Ctx?.Log;
        protected IConfigService Config => Ctx?.Config;
        protected IKeybindService Keybinds => Ctx?.Keybinds;
        protected IPatchService Patches => Ctx?.Patches;
        protected IGameEvents Events => Ctx?.Events;
        protected IUiService Ui => Ctx?.Ui;

        public virtual void OnLoad(IModContext context) { Ctx = context; }
        public virtual void OnEnable() { }
        public virtual void OnDisable() { }
        public virtual void OnUnload() { }
        public virtual void OnTick() { }
        public virtual void OnGameStart() { }
        public virtual void OnGameEnd() { }
    }
}