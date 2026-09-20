using System;
using System.Collections.Generic;
using FlotsamModKit.Abstractions;
using UnityEngine;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// The in-game mod manager (IMGUI on purpose: it must not depend on the game's
    /// prefab-driven panel system, and it has to be reachable from any UI state).
    /// Tabs: mods / keybinds / log.
    /// </summary>
    public sealed class ManagerWindow
    {
        private const int WindowId = 0x4D4B; // 'MK'

        private readonly ModKitRuntime _rt;
        private Rect _rect = new Rect(90f, 70f, 1000f, 640f);
        private int _tab;
        private Vector2 _modsScroll;
        private Vector2 _keysScroll;
        private Vector2 _logScroll;
        private string _captureOwner;
        private string _captureBindId;
        private GUIStyle _title;
        private GUIStyle _muted;
        private GUIStyle _badge;

        public ManagerWindow(ModKitRuntime runtime)
        {
            _rt = runtime;
            RestorePosition();
        }

        /// <summary>The manager window remembers where the player left it, like every mod window.</summary>
        private void RestorePosition()
        {
            try
            {
                var s = _rt.Settings;
                if (s == null) return;
                float x = s.Get("manager.x", float.NaN);
                float y = s.Get("manager.y", float.NaN);
                if (float.IsNaN(x) || float.IsNaN(y)) return;
                _rect.x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - 120f));
                _rect.y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - 40f));
            }
            catch { }
        }

        private void SavePosition()
        {
            try
            {
                var s = _rt.Settings;
                if (s == null) return;
                s.Set("manager.x", _rect.x);
                s.Set("manager.y", _rect.y);
                s.Save();
            }
            catch { }
        }

        private void EnsureStyles()
        {
            if (_title != null) return;

            // Match the in-game light theme instead of Unity's default grey IMGUI chrome.
            var white = new Texture2D(1, 1);
            white.SetPixel(0, 0, new Color(0.98f, 0.99f, 0.98f, 1f));
            white.Apply();
            var mint = new Texture2D(1, 1);
            mint.SetPixel(0, 0, new Color(0.66f, 0.91f, 0.81f, 1f));
            mint.Apply();
            var sunken = new Texture2D(1, 1);
            sunken.SetPixel(0, 0, new Color(0.90f, 0.95f, 0.93f, 1f));
            sunken.Apply();

            var skin = UnityEngine.Object.Instantiate(GUI.skin);
            skin.window.normal.background = mint;
            skin.window.onNormal.background = mint;
            skin.box.normal.background = sunken;
            skin.button.normal.background = white;
            skin.button.hover.background = mint;
            skin.button.active.background = sunken;
            skin.toggle.normal.textColor = new Color(0.20f, 0.26f, 0.25f);
            skin.label.normal.textColor = new Color(0.20f, 0.26f, 0.25f);
            skin.textField.normal.background = white;
            skin.textField.normal.textColor = new Color(0.20f, 0.26f, 0.25f);
            skin.verticalScrollbar.normal.background = sunken;
            skin.verticalScrollbarThumb.normal.background = mint;
            GUI.skin = skin;

            _title = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _title.normal.textColor = new Color(0.15f, 0.21f, 0.20f);
            _muted = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _muted.normal.textColor = new Color(0.44f, 0.50f, 0.49f);
            _badge = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleRight };
            _badge.normal.textColor = new Color(0.20f, 0.26f, 0.25f);
        }

        public void Draw()
        {
            EnsureStyles();
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.85f, 2f);
            var prev = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            var before = _rect;
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "Flotsam ModKit " + ModKitRuntime.HostVersion,
                                     GUILayout.Width(1000f), GUILayout.Height(640f));
            GUI.matrix = prev;

            if (_rect.position != before.position && Event.current != null && Event.current.type == EventType.Used)
                SavePosition();
        }

        private void DrawWindow(int id)
        {
            HandleCapture();

            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            _tab = GUILayout.Toolbar(_tab, new[] { "模组", "按键", "日志" }, GUILayout.Height(26f));
            if (GUILayout.Button("复位", GUILayout.Width(60f), GUILayout.Height(26f)))
            {
                _rect = new Rect(90f, 70f, 1000f, 640f);
                SavePosition();
                _rt.Ui.Toast("管理器窗口位置已复位（模组窗口请双击各自的标题栏复位）", ToastKind.Info);
            }
            if (GUILayout.Button("重载", GUILayout.Width(60f), GUILayout.Height(26f)))
            {
                _rt.ReloadMods();
                _rt.Ui.Toast("已重载模组列表", ToastKind.Info);
            }
            if (GUILayout.Button("关闭", GUILayout.Width(60f), GUILayout.Height(26f)))
                _rt.Ui.ManagerWindowVisible = false;
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            switch (_tab)
            {
                case 0: DrawMods(); break;
                case 1: DrawKeybinds(); break;
                default: DrawLog(); break;
            }

            GUILayout.EndVertical();

            // Drag by the top strip (above the toolbar is the window title bar).
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        // ------------------------------------------------------------ mods tab

        private void DrawMods()
        {
            GUILayout.Label($"游戏版本 {Application.version}   模组目录 {_rt.ModsDirectory}", _muted);
            GUILayout.Space(4f);

            _modsScroll = GUILayout.BeginScrollView(_modsScroll, GUILayout.ExpandHeight(true));
            if (_rt.Entries.Count == 0)
            {
                GUILayout.Label("Mods 目录下没有发现任何 mod（需要 <mod>/mod.json）。", _muted);
            }

            foreach (var entry in _rt.Entries)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);

                GUILayout.BeginVertical(GUILayout.Width(560f));
                GUILayout.Label($"{entry.DisplayName}   v{(entry.Manifest != null ? entry.Manifest.Version : "?")}", _title);
                GUILayout.Label($"id: {entry.Id}    作者: {(entry.Manifest != null ? entry.Manifest.Author : "")}", _muted);
                if (!string.IsNullOrEmpty(entry.Error))
                    GUILayout.Label(entry.Error, _muted);
                if (entry.Patches != null && entry.Patches.PatchCount > 0)
                    GUILayout.Label($"补丁数: {entry.Patches.PatchCount}", _muted);
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();
                GUILayout.Label(entry.StateLabel(), _badge, GUILayout.Width(80f));

                bool enabled = entry.State == ModRunState.Enabled;
                var label = enabled ? "禁用" : "启用";
                if (GUILayout.Button(label, GUILayout.Width(70f), GUILayout.Height(30f)))
                {
                    if (enabled) _rt.DisableMod(entry.Id);
                    else _rt.EnableMod(entry.Id);
                }

                if (GUILayout.Button("卸载", GUILayout.Width(70f), GUILayout.Height(30f)))
                {
                    if (entry.State == ModRunState.Enabled)
                        _rt.Ui.Toast("请先禁用再卸载", ToastKind.Warning);
                    else
                        _rt.RequestUninstall(entry.Id);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(2f);
            }
            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------ keybinds tab

        private void DrawKeybinds()
        {
            _keysScroll = GUILayout.BeginScrollView(_keysScroll, GUILayout.ExpandHeight(true));
            bool any = false;

            foreach (var entry in _rt.Entries)
            {
                if (entry.Keybinds == null) continue;
                var binds = entry.Keybinds.All;
                if (binds.Count == 0) continue;
                any = true;

                GUILayout.Label(entry.DisplayName, _title);
                foreach (var bind in binds)
                {
                    GUILayout.BeginHorizontal(GUI.skin.box);
                    GUILayout.Label($"{bind.DisplayName}  ({bind.Id})", GUILayout.Width(420f));

                    bool capturing = _captureBindId == bind.Id && _captureOwner == entry.Id;
                    GUILayout.Label(capturing ? "按下新按键…" : bind.Key.ToString(),
                                    GUILayout.Width(160f));

                    if (GUILayout.Button(capturing ? "取消" : "改键", GUILayout.Width(70f)))
                    {
                        if (capturing) { _captureBindId = null; _captureOwner = null; }
                        else { _captureBindId = bind.Id; _captureOwner = entry.Id; }
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(6f);
            }

            if (!any)
                GUILayout.Label("当前没有 mod 注册按键。", _muted);

            GUILayout.Space(8f);
            GUILayout.Label($"管理窗口按键: {_rt.ManagerKey}（在 ModKit.json 里改 managerKey）", _muted);
            GUILayout.EndScrollView();
        }

        private void HandleCapture()
        {
            if (_captureBindId == null) return;
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.None) return;

            if (e.keyCode == KeyCode.Escape)
            {
                _captureBindId = null;
                _captureOwner = null;
                e.Use();
                return;
            }

            var entry = _rt.Find(_captureOwner);
            var bind = entry?.Keybinds?.Find(_captureBindId);
            if (bind != null)
            {
                bind.Key = e.keyCode;
                entry.Keybinds.Save();
                _rt.Ui.Toast($"{bind.DisplayName} -> {e.keyCode}", ToastKind.Success);
            }
            _captureBindId = null;
            _captureOwner = null;
            e.Use();
        }

        // ------------------------------------------------------------ log tab

        private void DrawLog()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("清空", GUILayout.Width(70f))) _rt.Log.Clear();
            GUILayout.Label("最近 400 行（完整日志见 BepInEx/LogOutput.log）", _muted);
            GUILayout.EndHorizontal();

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.ExpandHeight(true));
            var lines = _rt.Log.Snapshot();
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                var style = line.Level == "ERROR" ? _title : _muted;
                GUILayout.Label(line.Format(), style);
            }
            GUILayout.EndScrollView();
        }
    }
}