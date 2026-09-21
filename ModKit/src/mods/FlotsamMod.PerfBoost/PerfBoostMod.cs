using System;
using System.Reflection;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using HarmonyLib;
using UnityEngine;

namespace FlotsamMods.PerfBoost
{
    /// <summary>
    /// 性能优化与探针 (F9) — see specs/2026-09-21-perfboost-design.md.
    ///
    /// The game renders on the dGPU just fine (Player.log: RTX 4070 SUPER); the low framerate
    /// in big towns is a CPU-main-thread bottleneck, so this mod (a) removes known pure-waste
    /// work and (b) shows exactly where frame time goes, so the next optimization round is
    /// data-driven instead of guesswork.
    ///
    /// Optimizations (each a config switch, all reversible; game access lives in GamePerf):
    ///   disableCtaa            CTAA_PC sits on 3 cameras but the game is URP, so its
    ///                          OnRenderImage never runs — it only forces a MotionVectors pass
    ///                          and per-frame projection jitter. Disable = free frame time.
    ///   suppressLogSpam        ParticleController/PrefabPool log on every destroy; plus
    ///                          stack-trace capture is switched off for Log/Warning.
    ///   shadowDistance          runtime CAP on ShadowManager.SetShadowDistance: the game
    ///                          re-evaluates a zoom curve through that method on every zoom,
    ///                          so a static write would be stomped — clamp every request.
    ///   cullOffscreenAnimators AlwaysAnimate → CullUpdateTransforms for world animators.
    ///   particleRateScale      emission multipliers of pooled ParticleControllers × scale.
    ///   renderScale            URP render scale (default 1.0 = untouched).
    ///   fixedDeltaMs           experimental physics timestep override (default 0 = off).
    /// </summary>
    public sealed class PerfBoostMod : FlotsamModBase
    {
        private IKeybind _toggleKey;
        private IHudButton _hudButton;
        private GameObject _driverObject;
        private PerfOverlay _overlay;
        private bool _animatorDirty;
        private float _nextAnimatorScan;
        private float _nextCtaaCheck;
        private float _nextFreezeRefresh;
        private int _lastFrozenReported = -1;
        private bool _shadowApplied;
        private float _nextShadowRetry;
        private bool _pipelineApplied;
        private float _nextPipelineRetry;

        public IModContext Context => Ctx;
        public PerfProbe Probe { get; } = new PerfProbe();

        public bool ShowOverlay { get; private set; } = true;
        public bool DisableCtaa { get; private set; }
        public bool SuppressLogSpam { get; private set; }
        public bool CullAnimators { get; private set; }
        public bool AnimatorFreezeCfg { get; private set; }
        public bool AnimatorFixedCfg { get; private set; }
        public bool Hotspots { get; private set; }
        public bool SrpBatcherCfg { get; private set; }
        public bool ForceProfilerCfg { get; private set; }
        public float ShadowDistance { get; private set; }
        public float ParticleScale { get; private set; }
        public float RenderScaleCfg { get; private set; }
        public float FixedDeltaMs { get; private set; }
        public float LodBias { get; private set; }
        public int ShadowCascadesCfg { get; private set; }
        public int ShadowmapResCfg { get; private set; }
        public int VSyncCfg { get; private set; }
        public int TargetFpsCfg { get; private set; }
        public float LogIntervalSec { get; private set; }
        public bool Verbose { get; private set; }

        // Static: Harmony patches must be static and need the config + a log sink.
        private static float _particleScaleStatic = 1f;
        private static Action<string> _patchLog;

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            _toggleKey = Keybinds.Register("perfboost.overlay", KeyCode.F9, "开关性能面板(F9)");

            ShowOverlay = Config.Get("showOverlay", true);
            DisableCtaa = Config.Get("disableCtaa", true);
            SuppressLogSpam = Config.Get("suppressLogSpam", true);
            CullAnimators = Config.Get("cullOffscreenAnimators", true);
            // Tier 2: offscreen animators stop COMPLETELY (enabled=false → PlayableGraph
            // evaluation stops — DirectorUpdate measured 19.6ms of a 30ms frame).
            AnimatorFreezeCfg = Config.Get("animatorFreeze", true);
            // Tier 3: sway animators (90% of registered = Morale_Decoration/FarmingCrops/
            // BasicParent) evaluate at 50Hz FixedUpdate instead of every frame — visually
            // identical for sway loops from a top-down camera.
            AnimatorFixedCfg = Config.Get("animatorFixedUpdate", true);
            ShadowDistance = Mathf.Clamp(Config.Get("shadowDistance", 100f), 0f, 1000f);   // 0 = don't touch
            ParticleScale = Mathf.Clamp(Config.Get("particleRateScale", 0.5f), 0.1f, 1f);   // 1 = don't touch
            RenderScaleCfg = Mathf.Clamp(Config.Get("renderScale", 1f), 0.5f, 1.5f);        // 1 = don't touch
            FixedDeltaMs = Mathf.Clamp(Config.Get("fixedDeltaMs", 0f), 0f, 100f);           // 0 = don't touch
            LodBias = Mathf.Clamp(Config.Get("lodBias", 1f), 0.2f, 3f);   // 1 = don't touch
            SrpBatcherCfg = Config.Get("srpBatcher", true);               // true = force on
            // 2 = the optimization: vanilla runs FOUR cascades, so every shadow caster is
            // re-rendered 4x per frame; with our shadow distance capped at 100 two
            // cascades cover the whole range at high resolution (set 1 for max saving,
            // 4 to restore vanilla, 0 = leave untouched).
            ShadowCascadesCfg = Mathf.Clamp(Config.Get("shadowCascades", 2), 0, 4);
            // Vanilla = 4096. 2048 quarters the shadow-pass fill cost; at shadow distance
            // 100 the quality drop is minor (set 0 = untouched, 4096 = vanilla).
            ShadowmapResCfg = Mathf.Clamp(Config.Get("shadowmapRes", 2048), 0, 8192);
            // ProfilerRecorder counters may only flow when the profiler engine takes
            // frames; force it on while the overlay lives (0 = off if it costs fps).
            ForceProfilerCfg = Config.Get("forceProfiler", true);
            // vSync=1 costs one refresh of wait per frame (measured: 53.95ms main thread
            // vs 46.9ms period at 144Hz). 0 = off (default here), -1 = leave vanilla.
            // targetFps: 0 = unlimited (default), >0 = soft cap.
            VSyncCfg = Mathf.Clamp(Config.Get("vSync", 0), -1, 4);
            TargetFpsCfg = Mathf.Clamp(Config.Get("targetFps", 0), 0, 360);
            LogIntervalSec = Mathf.Clamp(Config.Get("logIntervalSec", 10f), 2f, 120f);
            Verbose = Config.Get("verbose", false);
            Hotspots = Config.Get("hotspots", true);
            if (Config.Get("schema", 0) < 2)
            {
                Config.Set("schema", 2);
                // v1.2 tripled the panel rows (remapped taps + script hotspots); reset the
                // remembered window size so nothing is clipped.
                Config.Set("window.w", 330f);
                Config.Set("window.h", 640f);
                Config.Save();
            }

            Log.Info($"loaded — ctaa:{On(DisableCtaa)}, logspam:{On(SuppressLogSpam)}, " +
                     $"shadow:{(ShadowDistance > 0f ? ShadowDistance.ToString("0") : "off")}, " +
                     $"animCull:{On(CullAnimators)}{(AnimatorFreezeCfg ? "+freeze" : "")}{(AnimatorFixedCfg ? "+fixed" : "")}, particles:x{ParticleScale:0.##}, " +
                     $"renderScale:{(RenderScaleCfg < 0.999f ? RenderScaleCfg.ToString("0.##") : "off")}, " +
                     $"fixedDelta:{(FixedDeltaMs > 0f ? FixedDeltaMs + "ms" : "off")}, " +
                     $"hotspots:{On(Hotspots)}, srpBatcher:{On(SrpBatcherCfg)}, " +
                     $"cascades:{(ShadowCascadesCfg > 0 ? ShadowCascadesCfg.ToString() : "auto")}, " +
                     $"shadowmap:{(ShadowmapResCfg > 0 ? ShadowmapResCfg.ToString() : "auto")}, " +
                     $"vSync:{(VSyncCfg < 0 ? "auto" : VSyncCfg.ToString())}, " +
                     $"targetFps:{(TargetFpsCfg > 0 ? TargetFpsCfg.ToString() : "unlimited")}, " +
                     $"profiler:{On(ForceProfilerCfg)}, overlay:{On(ShowOverlay)}");
        }

        private static string On(bool b) => b ? "on" : "off";

        public override void OnEnable()
        {
            _particleScaleStatic = ParticleScale;
            _patchLog = m => Log.Warn(m);
            int applied = 0;

            if (SuppressLogSpam)
            {
                GamePerf.SuppressLogSpam(_patchLog);

                // Both method bodies contain ONLY their log call, so skipping them is exact.
                var pcDestroy = GamePatches.MethodByName(typeof(ParticleController), "OnDestroy");
                if (pcDestroy != null && Patches.Prefix(pcDestroy,
                        AccessTools.Method(typeof(PerfBoostMod), nameof(SkipSpamLog))))
                { applied++; Log.Info("prefix  ParticleController.OnDestroy <- SkipSpamLog"); }
                else Log.Error("target not found: ParticleController.OnDestroy — " + GamePatches.Describe(typeof(ParticleController)));

                var prefabRef = typeof(PajamaLlama.PrefabPool).GetNestedType("PrefabReference", BindingFlags.NonPublic);
                var poolDestroy = prefabRef != null ? GamePatches.MethodByName(prefabRef, "OnDestroy") : null;
                if (poolDestroy != null && Patches.Prefix(poolDestroy,
                        AccessTools.Method(typeof(PerfBoostMod), nameof(SkipSpamLog))))
                { applied++; Log.Info("prefix  PrefabPool.PrefabReference.OnDestroy <- SkipSpamLog"); }
                else Log.Error("target not found: PrefabPool.PrefabReference.OnDestroy");
            }

            if (ParticleScale < 0.999f)
            {
                var pcInit = GamePatches.MethodByName(typeof(ParticleController), "Initialize");
                if (pcInit != null && Patches.Postfix(pcInit,
                        AccessTools.Method(typeof(PerfBoostMod), nameof(AfterParticleInitialize))))
                { applied++; Log.Info("postfix ParticleController.Initialize <- AfterParticleInitialize"); }
                else Log.Error("target not found: ParticleController.Initialize — " + GamePatches.Describe(typeof(ParticleController)));
            }

            if (ShadowDistance > 0f)
            {
                // Cap semantics: prefix-clamp every distance the game requests (zoom curve
                // included) instead of one static write the next zoom would stomp.
                var smSet = GamePatches.MethodByName(typeof(ShadowManager), "SetShadowDistance");
                if (smSet != null && Patches.Prefix(smSet,
                        AccessTools.Method(typeof(PerfBoostMod), nameof(ShadowDistancePrefix))))
                { applied++; Log.Info("prefix  ShadowManager.SetShadowDistance <- ShadowDistanceCap"); }
                else Log.Error("target not found: ShadowManager.SetShadowDistance — " + GamePatches.Describe(typeof(ShadowManager)));
            }

            if (Hotspots) RegisterHotspots();

            if (CullAnimators)
            {
                // New animators appear when buildings finish/place and when drifters join.
                Events.On("BuildableBuilt", _ => _animatorDirty = true);
                Events.On("BuildablePlaced", _ => _animatorDirty = true);
                Events.On("AgentAddedToPlayerCommunity", _ => _animatorDirty = true);
            }

            _driverObject = new GameObject("[FlotsamMod.PerfBoost]");
            UnityEngine.Object.DontDestroyOnLoad(_driverObject);
            _driverObject.hideFlags = HideFlags.HideAndDontSave;
            _overlay = _driverObject.AddComponent<PerfOverlay>();
            _overlay.Mod = this;

            _hudButton = Ui.AddHudButton("perfboost.overlay", OverlayLabel(), ToggleOverlay,
                                         HudAnchor.LeftTop, Config, "button");
            _hudButton.Visible = true;

            Log.Info($"perfboost ready — {applied} patch(es) applied, overlay key {_toggleKey.Key}");
        }

        public override void OnDisable()
        {
            try { _overlay?.Teardown(); } catch { }
            _overlay = null;
            if (_driverObject != null)
            {
                try { UnityEngine.Object.Destroy(_driverObject); } catch { }
                _driverObject = null;
            }
            try { _hudButton?.Destroy(); } catch { }
            _hudButton = null;

            // Restore everything this mod owns; Harmony patches are revoked by the host.
            GamePerf.RestoreCtaa(_patchLog);
            GamePerf.RestoreShadowDistance(_patchLog);
            GamePerf.RestoreSrpBatcher(_patchLog);
            GamePerf.RestoreShadowCascades(_patchLog);
            GamePerf.RestoreShadowmapResolution(_patchLog);
            GamePerf.RestoreFramePacing(_patchLog);
            GamePerf.RestoreProfilerStats(_patchLog);
            GamePerf.RestoreRenderScale(_patchLog);
            GamePerf.RestoreAnimators(_patchLog);
            GamePerf.RestoreParticles(_patchLog);
            GamePerf.RestoreFixedDelta(_patchLog);
            GamePerf.RestoreLodBias(_patchLog);
            GamePerf.RestoreLogSpam(_patchLog);
            GamePerf.ClearHotspots();
            GamePerf.RestoreLoopTimers(_patchLog);
            Probe.Stop();
            _animatorDirty = false;
            Log.Info("perfboost removed — vanilla state restored");
        }

        public override void OnGameStart()
        {
            // Fresh save = fresh averages.
            GamePerf.HotspotReset();
            GamePerf.ResetCameraTiming();
            // Engine loop phase timers (Update/FixedUpdate/PreLateUpdate/PostLateUpdate/…)
            // — the frame anatomy for the ~17ms that manager+render stopwatches miss.
            if (Hotspots && GamePerf.InstallLoopTimers(_patchLog))
                Log.Info("player loop phase timers installed");

            // Scene-owned switches are (re)applied here: cameras and animators die with a scene.
            if (DisableCtaa)
            {
                int n = GamePerf.DisableCtaa(_patchLog);
                Log.Info($"ctaa disabled on {n} component(s) (motion-vectors pass removed)");
            }
            if (ShadowDistance > 0f)
            {
                // The pipeline asset is often unreachable this early in the load — OnTick
                // retries every 5s until the cap sticks (the prefix is already active, so
                // nothing can exceed the cap in the meantime once _shadowCap is set).
                _shadowApplied = GamePerf.ApplyShadowCap(ShadowDistance, _patchLog);
                _nextShadowRetry = Time.unscaledTime + 5f;
                if (_shadowApplied)
                    Log.Info($"shadow cap {GamePerf.ShadowCap:0} applied (now {GamePerf.CurrentShadowDistance():0})");
            }
            // Frame pacing is process-level (no pipeline dependency) — apply directly.
            if (VSyncCfg >= 0 || TargetFpsCfg > 0)
            {
                GamePerf.SetFramePacing(VSyncCfg, TargetFpsCfg, _patchLog);
                Log.Info($"frame pacing: vSync {(VSyncCfg < 0 ? "(untouched)" : "-> " + VSyncCfg)}, " +
                         $"targetFps {(TargetFpsCfg == 0 ? "unlimited" : TargetFpsCfg.ToString())}");
            }

            if (SrpBatcherCfg || ShadowCascadesCfg > 0 || ShadowmapResCfg > 0)
            {
                _pipelineApplied = ApplyPipelineSwitches();
                _nextPipelineRetry = Time.unscaledTime + 5f;
            }
            if (RenderScaleCfg < 0.999f && GamePerf.SetRenderScale(RenderScaleCfg, _patchLog))
                Log.Info($"render scale x{RenderScaleCfg:0.##}");
            if (CullAnimators)
            {
                int n = GamePerf.CullOffscreenAnimators(m => Log.Info(m));
                GamePerf.SetAnimatorFreeze(AnimatorFreezeCfg);
                GamePerf.SetAnimatorFixed(AnimatorFixedCfg);
                if (AnimatorFixedCfg)
                {
                    int f = GamePerf.ApplyAnimatorFixed(null);
                    Log.Info($"offscreen animator culling on {n} animator(s)" +
                             (AnimatorFreezeCfg ? " (freeze tier armed)" : "") +
                             (f > 0 ? $" (fixed-rate on {f})" : ""));
                }
                else
                {
                    Log.Info($"offscreen animator culling on {n} animator(s)" +
                             (AnimatorFreezeCfg ? " (freeze tier armed)" : ""));
                }
            }
            if (FixedDeltaMs > 0f && GamePerf.SetFixedDeltaMs(FixedDeltaMs, _patchLog))
                Log.Info($"fixed timestep {FixedDeltaMs:0}ms");
            if (LodBias < 0.999f && GamePerf.SetLodBias(LodBias, _patchLog))
                Log.Info($"lod bias x{LodBias:0.##}");
        }

        public override void OnGameEnd()
        {
            try { _overlay?.Teardown(); } catch { }
            // Scene instances are gone; this just clears the bookkeeping. Process-level
            // switches (renderScale/fixedDelta) stay applied and are re-asserted on the
            // next game start; they are restored for real in OnDisable.
            GamePerf.RestoreCtaa(null);
            GamePerf.RestoreAnimators(null);
            GamePerf.RestoreParticles(null);
            GamePerf.SetAnimatorFreeze(false);
            GamePerf.SetAnimatorFixed(false);
            _lastFrozenReported = -1;
            GamePerf.ResetCameraTiming();
            GamePerf.RestoreProfilerStats(null);
            GamePerf.RestoreLoopTimers(null);
            // Cap off: the prefix no-ops until the next game start re-applies it, so the
            // main-menu scene gets vanilla shadow behaviour.
            _shadowApplied = false;
            _nextShadowRetry = 0f;
            _pipelineApplied = false;
            _nextPipelineRetry = 0f;
            GamePerf.ClearShadowCap();
            _animatorDirty = false;
        }

        public override void OnTick()
        {
            if (_toggleKey != null && _toggleKey.IsDown) ToggleOverlay();

            // The URP asset is not reachable during the first moments of a scene load
            // (all three lookup routes return null) — retry until the cap sticks.
            if (ShadowDistance > 0f && !_shadowApplied && GameApi.IsPlaying &&
                Time.unscaledTime >= _nextShadowRetry)
            {
                _nextShadowRetry = Time.unscaledTime + 5f;
                if (GamePerf.ApplyShadowCap(ShadowDistance, _patchLog))
                {
                    _shadowApplied = true;
                    Log.Info($"shadow cap {GamePerf.ShadowCap:0} applied (now {GamePerf.CurrentShadowDistance():0})");
                }
            }
            if ((SrpBatcherCfg || ShadowCascadesCfg > 0 || ShadowmapResCfg > 0) && !_pipelineApplied && GameApi.IsPlaying &&
                Time.unscaledTime >= _nextPipelineRetry)
            {
                _nextPipelineRetry = Time.unscaledTime + 5f;
                _pipelineApplied = ApplyPipelineSwitches();
            }

            // A CTAA camera whose GameObject activates late (e.g. a camera that only wakes on
            // first use) re-adds its flags in OnEnable — re-assert the disable periodically.
            // Idempotent: already-disabled components are skipped.
            if (DisableCtaa && GameApi.IsPlaying && Time.unscaledTime >= _nextCtaaCheck)
            {
                _nextCtaaCheck = Time.unscaledTime + 5f;
                int n = GamePerf.DisableCtaa(null);
                if (n > 0 && Verbose) Log.Info($"ctaa re-assert: +{n} component(s)");
            }

            if (CullAnimators && _animatorDirty && GameApi.IsPlaying &&
                Time.unscaledTime >= _nextAnimatorScan)
            {
                _animatorDirty = false;
                _nextAnimatorScan = Time.unscaledTime + 3f;
                int n = GamePerf.CullOffscreenAnimators(_patchLog);
                if (AnimatorFixedCfg) GamePerf.ApplyAnimatorFixed(null);   // cover newcomers
                if (n > 0 && Verbose) Log.Info($"offscreen culling +{n} animator(s)");
            }

            // Freeze-tier refresh: drive Animator.enabled from renderer visibility.
            // 0.5s cadence is plenty — CullUpdateTransforms keeps newly-visible objects
            // correct between refreshes.
            if (CullAnimators && AnimatorFreezeCfg && GameApi.IsPlaying &&
                Time.unscaledTime >= _nextFreezeRefresh)
            {
                _nextFreezeRefresh = Time.unscaledTime + 0.5f;
                int frozen = GamePerf.RefreshAnimatorFreeze(m => Log.Info(m));
                if (frozen != _lastFrozenReported)
                {
                    _lastFrozenReported = frozen;
                    Log.Info($"animator freeze: {frozen} offscreen");
                }
            }
        }

        // ------------------------------------------------------------ script hotspots
        //
        // Release players strip the built-in loop markers, so script timing comes from our
        // own stopwatches: each Hot<T> instantiation carries its own index as static state,
        // giving every (prefix, postfix) pair an identity without delegate allocation. The
        // 14 targets below are manager-level entry points (each runs at most once per frame,
        // except PhysicsManager.FixedUpdate per fixed step and CircadianMaterial per
        // instance) — patch cost is a timestamp push/pop, far below measurement noise.

        private struct HGameManager { }
        private struct HAgentManager { }
        private struct HGraph { }
        private struct HUI { }
        private struct HWorldManager { }
        private struct HWorldMap { }
        private struct HEnergy { }
        private struct HEngine { }
        private struct HTooltip { }
        private struct HPhysics { }
        private struct HInput { }
        private struct HCameraZoom { }
        private struct HCircadian { }
        private struct HAnalytics { }
        private struct HUgui { }
        private struct HSubmit { }
        private struct HUguiRender { }
        private struct HBlendable { }
        private struct HEnergyConn { }
        // Per-instance aggregates: the manager stopwatches above measured <1ms of a 26ms
        // frame — the rest lives in the hundreds of individual Behaviour updates the
        // engine loop calls directly. These add the aggregate cost per CLASS (all
        // instances summed): Buildable.Update ×266 etc. Patch overhead is a timestamp
        // push/pop per call — meaningful only while diagnosing, hence the hotspots switch.
        private struct HBuildable { }
        private struct HProducer { }
        private struct HAgentInstance { }
        private struct HNavigator { }
        private struct HParticle { }

        private static class Hot<T> where T : struct
        {
            public static int Index;
            public static void Pre() => GamePerf.HotspotBegin();
            public static void Post() => GamePerf.HotspotEnd(Index);
        }

        private static MethodBase M(Type type, string method) => GamePatches.MethodByName(type, method);

        private int RegisterHot<T>(string label, params MethodBase[] targets) where T : struct
        {
            Hot<T>.Index = GamePerf.RegisterHotspot(label);
            var pre = AccessTools.Method(typeof(Hot<T>), nameof(Hot<T>.Pre));
            var post = AccessTools.Method(typeof(Hot<T>), nameof(Hot<T>.Post));
            int applied = 0;
            foreach (var t in targets)
            {
                if (t == null) { Log.Error($"hotspot target not found: {label}"); continue; }
                if (Patches.Prefix(t, pre) && Patches.Postfix(t, post)) applied++;
                else Log.Error($"hotspot patch failed: {label} on {t.DeclaringType?.Name}.{t.Name}");
            }
            return applied;
        }

        private void RegisterHotspots()
        {
            GamePerf.ClearHotspots();
            int n = 0;
            n += RegisterHot<HGameManager>("主泵GameManager", M(typeof(GameManager), "LateUpdate"));
            n += RegisterHot<HAgentManager>("小人AI", M(typeof(AgentManager), "LateUpdate"));
            n += RegisterHot<HGraph>("寻路图", M(typeof(GraphManager), "Update"), M(typeof(GraphManager), "LateUpdate"));
            n += RegisterHot<HUI>("UI管理", M(typeof(UIManager), "Update"), M(typeof(UIManager), "LateUpdate"));
            n += RegisterHot<HWorldManager>("世界管理", M(typeof(WorldManager), "LateUpdate"), M(typeof(WorldManager), "FixedUpdate"));
            n += RegisterHot<HWorldMap>("地图", M(typeof(WorldMap), "Update"), M(typeof(WorldMap), "LateUpdate"));
            n += RegisterHot<HEnergy>("电网", M(typeof(EnergyGridManager), "LateUpdate"));
            n += RegisterHot<HEngine>("牵引引擎", M(typeof(Engine), "LateUpdate"));
            n += RegisterHot<HTooltip>("悬浮框", M(typeof(TooltipPanel), "Update"));
            n += RegisterHot<HPhysics>("物理步", M(typeof(PhysicsManager), "FixedUpdate"));
            n += RegisterHot<HInput>("输入", M(typeof(FlotsamInputManager), "Update"));
            n += RegisterHot<HCameraZoom>("相机缩放", M(typeof(CameraZoomController), "Update"), M(typeof(CameraZoomController), "LateUpdate"));
            n += RegisterHot<HCircadian>("昼夜材质", M(typeof(CircadianMaterial), "LateUpdate"));
            n += RegisterHot<HAnalytics>("统计上报", M(typeof(AnalyticsManager), "Update"));
            // Render pipeline entry points (managed URP code — patchable like game code).
            // UGUI rebuild is the classic hidden cost of canvas-heavy games.
            n += RegisterHot<HUgui>("UGUI重建", M(typeof(UnityEngine.UI.CanvasUpdateRegistry), "PerformUpdate"));
            n += RegisterHot<HSubmit>("渲染提交", M(typeof(UnityEngine.Rendering.ScriptableRenderContext), "Submit"));
            // Per-camera URP render cost. RenderCameraStack is the entry for base cameras
            // (Main/UI/FOW/... — the Reflection-Probes-only coverage of the Internal
            // overloads proved single-camera path is not where the frame goes); the
            // RenderSingleCameraInternal overloads catch cameras rendered individually.
            // Nested same-name calls are skipped by GamePerf's dedup, so a base camera
            // rendered inside its own stack is not double-counted.
            var urpType = typeof(UnityEngine.Rendering.Universal.UniversalRenderPipeline);
            var camPre = AccessTools.Method(typeof(PerfBoostMod), nameof(CameraRenderPre));
            var stackPre = AccessTools.Method(typeof(PerfBoostMod), nameof(StackRenderPre));
            var camPost = AccessTools.Method(typeof(PerfBoostMod), nameof(CameraRenderPost));
            int cams = 0;
            foreach (var m in urpType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            {
                try
                {
                    if (m.Name == "RenderCameraStack" && Patches.Prefix(m, stackPre) && Patches.Postfix(m, camPost))
                    { cams++; continue; }
                    if (m.Name == "RenderSingleCameraInternal" && Patches.Prefix(m, camPre) && Patches.Postfix(m, camPost))
                    { cams++; continue; }
                }
                catch (Exception e) { Log.Error("camera render patch failed: " + e.Message); }
            }
            if (cams > 0) Log.Info($"per-camera render timing on {cams} entry point(s)");

            // Total managed render pipeline (both URP Render overrides — one forwards to
            // the other, GamePerf's depth guard prevents double counting).
            int renders = 0;
            var totalPre = AccessTools.Method(typeof(PerfBoostMod), nameof(RenderTotalPre));
            var totalPost = AccessTools.Method(typeof(PerfBoostMod), nameof(RenderTotalPost));
            foreach (var m in urpType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "Render") continue;
                try
                {
                    if (Patches.Prefix(m, totalPre) && Patches.Postfix(m, totalPost)) renders++;
                }
                catch (Exception e) { Log.Error("render-total patch failed: " + e.Message); }
            }
            if (renders > 0) Log.Info($"render-pipeline total timing on {renders} override(s)");

            // UGUI canvas render (native-facing entry: SendWillRenderCanvases).
            n += RegisterHot<HUguiRender>("UI渲染(画布)", GamePatches.MethodByName(typeof(Canvas), "SendWillRenderCanvases"));

            // Blendable-material family: decompile verification shows these classes have
            // NO Update/LateUpdate of their own (Blendable<>/Blender<> base classes neither) —
            // only CircadianRotation carries an Update in that family range. The family is
            // therefore NOT a per-instance-Update suspect; keep the real members only.
            n += RegisterHot<HBlendable>("材质混合(每实例)", M(typeof(CircadianRotation), "Update"));
            // NightLight carries only Tween/SetColor (no Update) — removed; the cable
            // follower below is real (verified in decompile 39223).
            // Per-connection cable stretch (decompile 39223: Lerp+scale every connection, every frame).
            n += RegisterHot<HEnergyConn>("电缆跟随(每根)", M(typeof(EnergyGridConnection), "Update"));
            // per-instance aggregates (all instances of the class, summed per frame)
            n += RegisterHot<HBuildable>("建筑Update(全266座)", M(typeof(Buildable), "Update"),
                                         M(typeof(Buildable), "LateUpdate"), M(typeof(Buildable), "FixedUpdate"));
            n += RegisterHot<HProducer>("生产Update(每座)", M(typeof(Producer), "Update"), M(typeof(Producer), "LateUpdate"));
            n += RegisterHot<HAgentInstance>("小人Update(每个)", M(typeof(Agent), "LateUpdate"));
            n += RegisterHot<HNavigator>("寻路执行(每个)", M(typeof(Navigator), "Update"), M(typeof(Navigator), "LateUpdate"));
            n += RegisterHot<HParticle>("粒子Update(每个)", M(typeof(ParticleController), "Update"));
            if (n > 0) Log.Info($"hotspot stopwatches on {n} method(s)");
        }

        /// <summary>Applies the pipeline-asset switches (SRP batcher, shadow cascades) once
        /// the asset is reachable; logs the states it found so the vanilla config is
        /// visible in the log even when we change nothing.</summary>
        private bool ApplyPipelineSwitches()
        {
            var batcher = GamePerf.CurrentSrpBatcher();
            var cascades = GamePerf.CurrentShadowCascades();
            if (batcher == null && cascades < 0) return false;   // asset not reachable yet
            try
            {
                Log.Info($"pipeline: srpBatcher={batcher}, shadowCascades={cascades}, " +
                         $"mainShadowmap={GamePerf.CurrentMainShadowmapResolution()}, " +
                         $"additionalLightShadows={GamePerf.CurrentAdditionalLightShadows()}, " +
                         $"vSync={QualitySettings.vSyncCount}, targetFps=" +
                         $"{(Application.targetFrameRate > 0 ? Application.targetFrameRate.ToString() : "none")}, " +
                         $"monitorHz={(float)Screen.currentResolution.refreshRateRatio.value:0}");
            }
            catch { }
            bool any = false;
            if (SrpBatcherCfg && batcher.HasValue && !batcher.Value &&
                GamePerf.SetSrpBatcher(true, _patchLog))
            {
                any = true;
                Log.Info("srp batcher forced ON (was off — CPU render submission discount)");
            }
            else if (SrpBatcherCfg && batcher.HasValue && batcher.Value)
            {
                Log.Info("srp batcher already on");
            }
            if (ShadowCascadesCfg > 0 && cascades > 0 && cascades != ShadowCascadesCfg &&
                GamePerf.SetShadowCascades(ShadowCascadesCfg, _patchLog))
            {
                any = true;
                Log.Info($"shadow cascades {cascades} -> {ShadowCascadesCfg}");
            }
            if (ShadowmapResCfg > 0)
            {
                var cur = GamePerf.CurrentMainShadowmapResolution();
                if (cur > 0 && cur != ShadowmapResCfg &&
                    GamePerf.SetShadowmapResolution(ShadowmapResCfg, _patchLog))
                {
                    any = true;
                    Log.Info($"shadowmap resolution {cur} -> {ShadowmapResCfg}");
                }
            }
            return any || batcher.HasValue;
        }

        // ------------------------------------------------------------ overlay plumbing

        private string OverlayLabel() => ShowOverlay ? "隐藏性能面板" : "性能面板";

        public void SetOverlayVisible(bool visible)
        {
            ShowOverlay = visible;
            Config.Set("showOverlay", visible);
            Config.Save();
            try { _hudButton?.SetLabel(OverlayLabel()); } catch { }
        }

        private void ToggleOverlay() => SetOverlayVisible(!ShowOverlay);

        /// <summary>One-line summary shown at the bottom of the overlay window.</summary>
        public string OptimizationsSummary()
        {
            var sb = new System.Text.StringBuilder(128);
            sb.Append("优化: ");
            bool any = false;
            if (DisableCtaa && GamePerf.CtaaDisabledCount > 0)
            { sb.Append("CTAA关×").Append(GamePerf.CtaaDisabledCount); any = true; }
            if (ShadowDistance > 0f && GamePerf.CurrentShadowDistance() > 0f)
            { if (any) sb.Append(' '); sb.Append("阴影").Append(GamePerf.CurrentShadowDistance().ToString("0")); any = true; }
            if (SrpBatcherCfg)
            {
                var b = GamePerf.CurrentSrpBatcher();
                if (b.HasValue)
                { if (any) sb.Append(' '); sb.Append("SRP批").Append(b.Value ? "开" : "关"); any = true; }
            }
            if (RenderScaleCfg < 0.999f && GamePerf.CurrentRenderScale() > 0f)
            { if (any) sb.Append(' '); sb.Append("渲染×").Append(GamePerf.CurrentRenderScale().ToString("0.##")); any = true; }
            if (CullAnimators && GamePerf.CulledAnimatorCount > 0)
            {
                if (any) sb.Append(' ');
                sb.Append("动画").Append(GamePerf.CulledAnimatorCount);
                if (AnimatorFixedCfg && GamePerf.FixedAnimatorCount > 0)
                    sb.Append("(半频").Append(GamePerf.FixedAnimatorCount).Append(')');
                if (AnimatorFreezeCfg && GamePerf.FrozenAnimatorCount > 0)
                    sb.Append("(冻结").Append(GamePerf.FrozenAnimatorCount).Append(')');
                any = true;
            }
            if (ParticleScale < 0.999f)
            { if (any) sb.Append(' '); sb.Append("粒子×").Append(ParticleScale.ToString("0.##")); any = true; }
            if (FixedDeltaMs > 0f)
            { if (any) sb.Append(' '); sb.Append("物理").Append(FixedDeltaMs.ToString("0")).Append("ms"); any = true; }
            if (LodBias < 0.999f)
            { if (any) sb.Append(' '); sb.Append("LOD×").Append(LodBias.ToString("0.##")); any = true; }
            if (!any) sb.Append("无");
            return sb.ToString();
        }

        // ------------------------------------------------------------ Harmony patches

        /// <summary>Both target method bodies contain only their Debug.Log call.</summary>
        public static bool SkipSpamLog() => false;

        /// <summary>Clamps the game's own shadow-distance requests (zoom curve) to the cap.
        /// Void prefix: the original always runs with the clamped argument.</summary>
        public static void ShadowDistancePrefix(ref float distance)
        {
            distance = GamePerf.ClampShadowRequest(distance);
        }

        /// <summary>URP per-camera render stopwatch in (matched by parameter name).</summary>
        public static void CameraRenderPre(Camera camera)
        {
            GamePerf.CameraRenderBegin(camera != null ? camera.name : "?");
        }

        /// <summary>RenderCameraStack's camera parameter is named baseCamera.</summary>
        public static void StackRenderPre(Camera baseCamera)
        {
            GamePerf.CameraRenderBegin(baseCamera != null ? baseCamera.name : "?");
        }

        public static void CameraRenderPost() => GamePerf.CameraRenderEnd();

        /// <summary>Total managed render pipeline (URP Render overrides).</summary>
        public static void RenderTotalPre() => GamePerf.RenderTotalBegin();

        public static void RenderTotalPost() => GamePerf.RenderTotalEnd();

        public static void AfterParticleInitialize(ParticleController __instance)
        {
            if (_particleScaleStatic < 0.999f)
                GamePerf.ScaleParticleController(__instance, _particleScaleStatic, _patchLog);
        }
    }
}
