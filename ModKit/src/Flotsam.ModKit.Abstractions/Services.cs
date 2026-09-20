using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FlotsamModKit.Abstractions
{
    // ---------------------------------------------------------------- logging

    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Error(string message, Exception exception);
    }

    // ---------------------------------------------------------------- config

    public interface IConfigService
    {
        T Get<T>(string key, T defaultValue);
        void Set<T>(string key, T value);
        bool Has(string key);
        void Save();
        event Action Changed;
    }

    // ---------------------------------------------------------------- keybinds

    public enum KeybindOptions
    {
        None = 0,
        /// <summary>Keep reporting key state while the game UI is in typing mode.</summary>
        AllowWhileTyping = 1,
        /// <summary>Keep reporting key state while the game is paused.</summary>
        AllowWhilePaused = 2
    }

    public interface IKeybind
    {
        string Id { get; }
        string DisplayName { get; }
        KeyCode Key { get; set; }
        bool IsDown { get; }
        bool IsHeld { get; }
        bool IsUp { get; }
        KeybindOptions Options { get; }
        event Action<KeyCode> Rebound;
    }

    public interface IKeybindService
    {
        IKeybind Register(string id, KeyCode defaultKey, string displayName = null,
                          KeybindOptions options = KeybindOptions.None);
        IKeybind Find(string id);
        IReadOnlyList<IKeybind> All { get; }
        void Save();
    }

    // ---------------------------------------------------------------- patching

    /// <summary>
    /// Thin wrapper over the mod's own Harmony instance. Patch methods follow the
    /// usual Harmony conventions (__instance/__result/ref parameters).
    /// </summary>
    public interface IPatchService
    {
        bool Prefix(MethodBase target, MethodInfo patch, int priority = 400);
        bool Postfix(MethodBase target, MethodInfo patch, int priority = 400);
        int RevokeAll();
        int PatchCount { get; }
    }

    // ---------------------------------------------------------------- events

    /// <summary>
    /// Typed access to the game's GameEventDispatcher without leaking game types into
    /// the abstractions: the event name is the GameEventType member name.
    /// Every subscription is disposed automatically when the mod is disabled.
    /// </summary>
    public interface IGameEvents
    {
        IDisposable On(string gameEventType, Action<object> handler);
        void Signal(string gameEventType);
    }

    // ---------------------------------------------------------------- ui

    public enum HudAnchor
    {
        RightMiddle,
        RightTop,
        LeftMiddle,
        LeftTop,
        BottomRight
    }

    public enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    public interface IHudButton
    {
        string Id { get; }
        bool Visible { get; set; }
        void SetLabel(string text);

        /// <summary>Optional native icon shown left of the label.</summary>
        void SetIcon(Sprite icon);

        /// <summary>Anchored-position offset; the mod decides whether to persist it.</summary>
        Vector2 Position { get; set; }

        /// <summary>Lets the player drag the button to a custom spot.</summary>
        bool Draggable { get; set; }

        /// <summary>Raised after the player finished dragging (save the position here).</summary>
        event Action<Vector2> Moved;

        /// <summary>Restores the default slot for this button's anchor.</summary>
        void ResetPosition();

        void Destroy();
    }

    public interface IUiService
    {
        /// <summary>Creates (or returns) a mod-owned overlay canvas. Parent it yourself.</summary>
        GameObject CreateOverlay(string id, int sortOrder = 30000);

        IHudButton AddHudButton(string id, string label, Action onClick,
                                HudAnchor anchor = HudAnchor.RightMiddle);

        /// <summary>
        /// Same, but the button becomes draggable with its position remembered automatically
        /// under "&lt;positionKey&gt;.x" / "&lt;positionKey&gt;.y" in the given config store.
        /// </summary>
        IHudButton AddHudButton(string id, string label, Action onClick, HudAnchor anchor,
                                IConfigService positionStore, string positionKey);

        void Toast(string message, ToastKind kind = ToastKind.Info);

        /// <summary>The ModKit manager window (mods list / keybinds / log).</summary>
        bool ManagerWindowVisible { get; set; }
    }

    // ---------------------------------------------------------------- context

    public interface IModControl
    {
        void DisableSelf();
        void RequestUninstall();
    }

    public interface IModContext
    {
        ModManifest Manifest { get; }
        string ModDirectory { get; }
        ILog Log { get; }
        IConfigService Config { get; }
        IKeybindService Keybinds { get; }
        IPatchService Patches { get; }
        IGameEvents Events { get; }
        IUiService Ui { get; }
        IModControl Control { get; }
        bool IsEnabled { get; }
    }
}