using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// Runtime performance switches (see specs/2026-09-21-perfboost-design.md).
    ///
    /// Every switch records the original value once and can be rolled back exactly. Scene-owned
    /// state (CTAA components, animators, particle systems) dies with the scene, so the mod
    /// re-applies on every game start; process-owned state (URP asset values, fixedDeltaTime,
    /// stack-trace modes) persists until explicitly restored. All methods are defensive: a
    /// failure is logged and never propagates into game code.
    /// </summary>
    public static class GamePerf
    {
        // ------------------------------------------------------------ CTAA (3 cameras)
        //
        // The game renders through URP, so CTAA_PC.OnRenderImage never runs — the component
        // only costs: it ORs Depth|MotionVectors onto every camera it sits on (a full extra
        // motion-vectors pass) and jitters the projection matrix every frame. Disable =
        // CTAA_Enabled=false + component off; we clear ONLY the MotionVectors flag — Depth
        // stays because the water shaders get it from EnableDepthInForwardCamera helpers.
        // Restore = re-enable the component, whose own OnEnable re-adds both flags (exact
        // vanilla state, nothing to remember).

        private static readonly List<CTAA_PC> _ctaaDisabled = new List<CTAA_PC>();
        public static int CtaaDisabledCount => _ctaaDisabled.Count;

        public static int DisableCtaa(Action<string> log)
        {
            int n = 0;
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<CTAA_PC>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var c in all)
                {
                    if (c == null) continue;
                    try
                    {
                        if (!c.CTAA_Enabled && !c.enabled) { if (!_ctaaDisabled.Contains(c)) _ctaaDisabled.Add(c); continue; }
                        c.CTAA_Enabled = false;
                        c.enabled = false;
                        var cam = c.GetComponent<Camera>();
                        if (cam != null)
                            cam.depthTextureMode &= ~DepthTextureMode.MotionVectors;
                        if (!_ctaaDisabled.Contains(c)) _ctaaDisabled.Add(c);
                        n++;
                    }
                    catch (Exception e) { log?.Invoke("ctaa off failed on " + SafeName(c) + ": " + e.Message); }
                }
            }
            catch (Exception e) { log?.Invoke("ctaa scan failed: " + e.Message); }
            return n;
        }

        public static int RestoreCtaa(Action<string> log)
        {
            int n = 0;
            foreach (var c in _ctaaDisabled)
            {
                if (c == null) continue;   // destroyed with its scene — nothing to restore
                try
                {
                    c.CTAA_Enabled = true;
                    c.enabled = true;   // OnEnable re-adds Depth|MotionVectors itself
                    n++;
                }
                catch (Exception e) { log?.Invoke("ctaa restore failed: " + e.Message); }
            }
            _ctaaDisabled.Clear();
            return n;
        }

        // ------------------------------------------------------------ URP asset values
        //
        // Shadow distance is a CAP, not a static write: CameraZoom re-evaluates a zoom curve
        // through ShadowManager.SetShadowDistance on every zoom (decompile 35391) and resets
        // on camera enable/disable, and ShadowManager.Awake writes the asset directly
        // (109476) — a one-shot value would be stomped within seconds. The mod layer prefixes
        // SetShadowDistance and clamps every requested distance via ClampShadowRequest;
        // ApplyShadowCap re-asserts once per game start because the prefix cannot see the
        // direct asset writes (Awake / ResetShadowDistance / ApplyDefaultShadowDistance).

        private static float _shadowCap = -1f;          // > 0 = clamp active
        private static float _shadowOriginal = -1f;    // value before our first write this process
        private static float _renderScaleOriginal = -1f;

        private static FieldInfo _smInstanceField;
        private static FieldInfo _smPipelineField;

        private static UniversalRenderPipelineAsset ActivePipeline()
        {
            try
            {
                var rp = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
                if (rp != null) return rp;
                rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (rp != null) return rp;
                // ShadowManager holds a serialized reference to the asset the game actually
                // drives — the two settings routes are both null during the first moments of
                // a scene load.
                if (_smInstanceField == null)
                    _smInstanceField = typeof(ShadowManager).GetField("_instance",
                        BindingFlags.NonPublic | BindingFlags.Static);
                if (_smPipelineField == null)
                    _smPipelineField = typeof(ShadowManager).GetField("_renderPipeline",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                var sm = _smInstanceField != null ? _smInstanceField.GetValue(null) as ShadowManager : null;
                if (sm != null && _smPipelineField != null)
                    return _smPipelineField.GetValue(sm) as UniversalRenderPipelineAsset;
            }
            catch { }
            return null;
        }

        public static float CurrentShadowDistance()
        {
            var rp = ActivePipeline();
            return rp != null ? rp.shadowDistance : -1f;
        }

        public static float CurrentRenderScale()
        {
            var rp = ActivePipeline();
            return rp != null ? rp.renderScale : -1f;
        }

        public static float ShadowCap => _shadowCap;

        /// <summary>Clamps one distance requested via ShadowManager.SetShadowDistance
        /// (called from the mod's Harmony prefix — keep allocation-free).</summary>
        public static float ClampShadowRequest(float distance)
        {
            var cap = _shadowCap;
            return cap > 0f && distance > cap ? cap : distance;
        }

        /// <summary>Installs the cap and clamps the current asset value once. Returns false
        /// while the pipeline asset is unreachable (early scene load) — the caller retries.</summary>
        public static bool ApplyShadowCap(float cap, Action<string> log)
        {
            try
            {
                var rp = ActivePipeline();
                if (rp == null) return false;
                _shadowCap = Mathf.Clamp(cap, 10f, 1000f);
                if (_shadowOriginal < 0f) _shadowOriginal = rp.shadowDistance;
                if (rp.shadowDistance > _shadowCap) rp.shadowDistance = _shadowCap;
                return true;
            }
            catch (Exception e) { log?.Invoke("shadow cap failed: " + e.Message); return false; }
        }

        /// <summary>Drops the runtime clamp at game end (the prefix no-ops, vanilla menu
        /// behaviour) without forgetting the original for the next game start.</summary>
        public static void ClearShadowCap() => _shadowCap = -1f;

        public static bool RestoreShadowDistance(Action<string> log)
        {
            if (_shadowCap < 0f && _shadowOriginal < 0f) return false;
            _shadowCap = -1f;
            try
            {
                // The game's own reset (its camera OnDisable path) restores its default; the
                // direct write covers the manager instance already being gone with the scene
                // and undoes any pollution of its cached default by earlier capped writes.
                try { ShadowManager.ResetShadowDistance(); } catch { }
                if (_shadowOriginal > 0f)
                {
                    var rp = ActivePipeline();
                    if (rp != null) rp.shadowDistance = _shadowOriginal;
                }
                _shadowOriginal = -1f;
                return true;
            }
            catch (Exception e) { log?.Invoke("shadow restore failed: " + e.Message); return false; }
        }

        public static bool SetRenderScale(float scale, Action<string> log)
        {
            try
            {
                var rp = ActivePipeline();
                if (rp == null) { log?.Invoke("renderscale: active pipeline asset is not URP"); return false; }
                if (_renderScaleOriginal < 0f) _renderScaleOriginal = rp.renderScale;
                rp.renderScale = Mathf.Clamp(scale, 0.5f, 1.5f);
                return true;
            }
            catch (Exception e) { log?.Invoke("renderscale set failed: " + e.Message); return false; }
        }

        public static bool RestoreRenderScale(Action<string> log)
        {
            if (_renderScaleOriginal < 0f) return false;
            try
            {
                var rp = ActivePipeline();
                if (rp != null) rp.renderScale = _renderScaleOriginal;
                _renderScaleOriginal = -1f;
                return true;
            }
            catch (Exception e) { log?.Invoke("renderscale restore failed: " + e.Message); return false; }
        }

        // ------------------------------------------------------------ SRP batcher & cascades
        //
        // useSRPBatcher (URP asset, public settable) is the classic CPU-side lever for URP
        // games: compatible shaders stop paying per-material state setup on the CPU, which
        // is exactly where this game's frame time goes (script side measured ~1.5ms of a
        // 24ms frame; the rest is native render work fed by ~5000 draw calls). Incompatible
        // shaders silently fall back, so forcing it on is safe. shadowCascadeCount multiplies
        // the shadow pass draw count (each cascade re-renders casters).

        private static bool? _srpOriginal;
        private static int _cascadeOriginal = -1;

        public static bool? CurrentSrpBatcher()
        {
            try { return ActivePipeline()?.useSRPBatcher; }
            catch { return null; }
        }

        public static int CurrentShadowCascades()
        {
            try { return ActivePipeline()?.shadowCascadeCount ?? -1; }
            catch { return -1; }
        }

        public static int CurrentMainShadowmapResolution()
        {
            try { return ActivePipeline()?.mainLightShadowmapResolution ?? -1; }
            catch { return -1; }
        }

        public static bool CurrentAdditionalLightShadows()
        {
            try { return ActivePipeline()?.supportsAdditionalLightShadows ?? false; }
            catch { return false; }
        }

        private static int _shadowmapOriginal = -1;

        // ------------------------------------------------------------ vsync / target fps
        //
        // Evidence from v1.7.1: CPU main-thread counter 53.95ms vs 46.9ms frame period —
        // the ~6.9ms gap is exactly one 144Hz refresh wait at vSync=1. Turning vSync off
        // removes the present quantization (Flotsam is slow-moving; tearing is barely
        // visible). targetFps stays available as a softer cap for heat/power.

        private static int _vSyncOriginal = -1;
        private static int _targetFpsOriginal = -1;

        public static void SetFramePacing(int vSyncCount, int targetFps, Action<string> log)
        {
            try
            {
                if (_vSyncOriginal < 0) _vSyncOriginal = QualitySettings.vSyncCount;
                if (_targetFpsOriginal < 0) _targetFpsOriginal = Application.targetFrameRate;
                if (vSyncCount >= 0) QualitySettings.vSyncCount = vSyncCount;
                if (targetFps != 0) Application.targetFrameRate = targetFps;
            }
            catch (Exception e) { log?.Invoke("frame pacing set failed: " + e.Message); }
        }

        public static void RestoreFramePacing(Action<string> log)
        {
            try
            {
                if (_vSyncOriginal >= 0) QualitySettings.vSyncCount = _vSyncOriginal;
                if (_targetFpsOriginal >= 0) Application.targetFrameRate = _targetFpsOriginal;
            }
            catch (Exception e) { log?.Invoke("frame pacing restore failed: " + e.Message); }
            _vSyncOriginal = -1;
            _targetFpsOriginal = -1;
        }

        public static bool SetShadowmapResolution(int resolution, Action<string> log)
        {
            try
            {
                var rp = ActivePipeline();
                if (rp == null) return false;
                if (_shadowmapOriginal < 0) _shadowmapOriginal = rp.mainLightShadowmapResolution;
                rp.mainLightShadowmapResolution = resolution;
                return true;
            }
            catch (Exception e) { log?.Invoke("shadowmap set failed: " + e.Message); return false; }
        }

        public static bool RestoreShadowmapResolution(Action<string> log)
        {
            if (_shadowmapOriginal < 0) return false;
            try
            {
                var rp = ActivePipeline();
                if (rp != null) rp.mainLightShadowmapResolution = _shadowmapOriginal;
                _shadowmapOriginal = -1;
                return true;
            }
            catch (Exception e) { log?.Invoke("shadowmap restore failed: " + e.Message); return false; }
        }

        public static bool SetSrpBatcher(bool on, Action<string> log)
        {
            try
            {
                var rp = ActivePipeline();
                if (rp == null) return false;
                if (!_srpOriginal.HasValue) _srpOriginal = rp.useSRPBatcher;
                rp.useSRPBatcher = on;
                return true;
            }
            catch (Exception e) { log?.Invoke("srp batcher set failed: " + e.Message); return false; }
        }

        public static bool RestoreSrpBatcher(Action<string> log)
        {
            if (!_srpOriginal.HasValue) return false;
            try
            {
                var rp = ActivePipeline();
                if (rp != null) rp.useSRPBatcher = _srpOriginal.Value;
                _srpOriginal = null;
                return true;
            }
            catch (Exception e) { log?.Invoke("srp batcher restore failed: " + e.Message); return false; }
        }

        public static bool SetShadowCascades(int count, Action<string> log)
        {
            try
            {
                var rp = ActivePipeline();
                if (rp == null) return false;
                if (count != 1 && count != 2 && count != 4) return false;
                if (_cascadeOriginal < 0) _cascadeOriginal = rp.shadowCascadeCount;
                rp.shadowCascadeCount = count;
                return true;
            }
            catch (Exception e) { log?.Invoke("shadow cascades set failed: " + e.Message); return false; }
        }

        public static bool RestoreShadowCascades(Action<string> log)
        {
            if (_cascadeOriginal < 0) return false;
            try
            {
                var rp = ActivePipeline();
                if (rp != null) rp.shadowCascadeCount = _cascadeOriginal;
                _cascadeOriginal = -1;
                return true;
            }
            catch (Exception e) { log?.Invoke("shadow cascades restore failed: " + e.Message); return false; }
        }

        // ------------------------------------------------------------ LOD bias
        //
        // lodBias < 1 switches LODGroups to coarser meshes sooner. (Shadows need no dedicated
        // toggle: shadowDistance=0 already makes URP skip the shadow pass entirely — one
        // public-setter lever covers the whole range.)

        private static float _lodBiasOriginal = -1f;

        public static bool SetLodBias(float bias, Action<string> log)
        {
            try
            {
                if (_lodBiasOriginal < 0f) _lodBiasOriginal = QualitySettings.lodBias;
                QualitySettings.lodBias = Mathf.Clamp(bias, 0.2f, 3f);
                return true;
            }
            catch (Exception e) { log?.Invoke("lodbias set failed: " + e.Message); return false; }
        }

        public static bool RestoreLodBias(Action<string> log)
        {
            if (_lodBiasOriginal < 0f) return false;
            try { QualitySettings.lodBias = _lodBiasOriginal; _lodBiasOriginal = -1f; return true; }
            catch (Exception e) { log?.Invoke("lodbias restore failed: " + e.Message); return false; }
        }

        // ------------------------------------------------------------ decorative animator throttling
        //
        // The v2.0.2 ownership line showed the 177 registered animators are ~90% sway
        // animations: Morale_Decoration ×46, FarmingCrops (potato/tomato/corn) ×75,
        // BasicParent ×20 … Their PlayableGraph evaluation (DirectorUpdate phase,
        // 17-19ms/frame) runs at full frame rate. updateMode=Fixed moves the evaluation
        // into the 50Hz FixedUpdate step — a sway loop at half sampling rate is visually
        // identical from a top-down camera, and DirectorUpdate drops by the same factor.
        // Frozen (offscreen) animators keep Freeze as the stronger lever; Fixed applies
        // to everything registered, visible or not.

        private static bool _animatorFixedMode;
        private static int _fixedApplied;

        public static int FixedAnimatorCount => _fixedApplied;

        public static void SetAnimatorFixed(bool enabled) => _animatorFixedMode = enabled;

        /// <summary>Switches every registered animator to updateMode=Fixed (evaluation in
        /// FixedUpdate). Originals are remembered per instance; RestoreAnimators undoes it.</summary>
        public static int ApplyAnimatorFixed(Action<string> log)
        {
            if (!_animatorFixedMode) return 0;
            int n = 0;
            foreach (var kv in _animators)
            {
                var st = kv.Value;
                if (st.Animator == null) continue;
                try
                {
                    if (!st.FixedApplied && st.Animator.updateMode != AnimatorUpdateMode.Fixed)
                    {
                        st.Animator.updateMode = AnimatorUpdateMode.Fixed;
                        st.FixedApplied = true;
                    }
                    if (st.FixedApplied) n++;
                }
                catch { }
            }
            _fixedApplied = n;
            return n;
        }

        // ------------------------------------------------------------ offscreen animators
        //
        // AnimatorCullingMode.CullUpdateTransforms still ticks the state machine (events fire,
        // timers advance) but skips writing bones/transforms while nothing renders the object.
        // We only touch animators that (a) currently run AlwaysAnimate — a game-chosen cull
        // mode is respected — and (b) have a Renderer in their subtree, so UGUI animations
        // (no Renderer bounds) are never touched.

        // Two tiers of offscreen animator savings:
        //   CullUpdateTransforms (default) still evaluates the PlayableGraph every frame
        //   (Animator == PlayableGraph → DirectorUpdate phase) — the v1.9 session measured
        //   DirectorUpdate at 19.6ms while all script hotspots totalled ~1ms.
        //   The freeze tier (animatorFreeze) drives Animator.enabled from renderer
        //   visibility: offscreen animators stop COMPLETELY (graph evaluation included),
        //   re-enabling the moment they come back into view. Visibility comes from
        //   Renderer.isVisible — cheap, updated by the engine's culling.

        private sealed class AnimatorState
        {
            public Animator Animator;
            public AnimatorCullingMode Mode;
            public Renderer Renderer;   // visibility probe for the freeze tier
            public bool Frozen;
            public bool FixedApplied;   // updateMode=Fixed applied by the throttle tier
        }

        private static readonly Dictionary<int, AnimatorState> _animators = new Dictionary<int, AnimatorState>();
        private static bool _freezeMode;
        public static int CulledAnimatorCount => _animators.Count;
        public static int FrozenAnimatorCount { get; private set; }

        public static void SetAnimatorFreeze(bool enabled) => _freezeMode = enabled;

        /// <summary>Re-evaluates the enabled flag of every registered animator from its
        /// renderer's current visibility. Called from the mod's OnTick (~10Hz is plenty;
        /// the engine fills in with CullUpdateTransforms in between for anything that
        /// scrolls into view mid-interval).</summary>
        public static int RefreshAnimatorFreeze(Action<string> log)
        {
            if (!_freezeMode) return 0;
            int frozen = 0, visible = 0;
            foreach (var kv in _animators)
            {
                var st = kv.Value;
                if (st.Animator == null || st.Renderer == null) continue;   // died with scene
                try
                {
                    bool vis = st.Renderer.isVisible;
                    if (vis) visible++;
                    bool want = !vis;
                    if (want != st.Frozen)
                    {
                        st.Animator.enabled = !want;
                        st.Frozen = want;
                    }
                    if (want) frozen++;
                }
                catch { }
            }
            FrozenAnimatorCount = frozen;
            // Diagnostic: shows whether the visibility probe is actually excluding
            // anything (a top-down camera with a long frustum can keep everything
            // "visible" — in that case the freeze tier silently does nothing).
            if (log != null && _animators.Count > 0 && frozen == 0 && _probeLoggedOnce != visible)
            {
                _probeLoggedOnce = visible;
                log($"animator freeze probe: {visible}/{_animators.Count} renderers report visible — " +
                    (visible == _animators.Count ? "visibility probe ineffective here (camera frustum covers offscreen objects)" : "ok"));
            }
            return frozen;
        }

        private static int _probeLoggedOnce = -1;

        public static int CullOffscreenAnimators(Action<string> log)
        {
            int n = 0;
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                int totalAlwaysAnimate = 0, totalOther = 0;
                foreach (var a in all)
                {
                    if (a == null) continue;
                    int id = a.GetInstanceID();
                    if (_animators.ContainsKey(id)) continue;
                    try
                    {
                        // v2.1.1: register EVERY animator (not only AlwaysAnimate ones) —
                        // the v2.1.0 experiment proved Fixed/Freeze on the 177 AlwaysAnimate
                        // ones left DirectorUpdate untouched at ~18ms, so the heavyweight
                        // graphs must sit among the 29 skipped (game-chosen culling mode)
                        // or otherwise-unregistered animators. Their original culling mode
                        // is still remembered for exact restore.
                        var r = a.GetComponentInChildren<Renderer>();
                        if (r == null) { totalOther++; continue; }
                        _animators[id] = new AnimatorState { Animator = a, Mode = a.cullingMode, Renderer = r };
                        if (a.cullingMode == AnimatorCullingMode.AlwaysAnimate)
                            a.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                        totalAlwaysAnimate++;
                        n++;
                    }
                    catch { }
                }
                // Who are the ~179 registered animators? A one-line breakdown by parent
                // object name prefix guides the next lever (animatePhysics / speed).
                if (log != null && n > 0 && _whoLoggedOnce != n)
                {
                    _whoLoggedOnce = n;
                    var groups = new Dictionary<string, int>();
                    foreach (var kv in _animators)
                    {
                        string name = SafeName(kv.Value.Animator != null ? kv.Value.Animator.gameObject : null);
                        string key = name.Length > 0 ? new string(name.Take(18).ToArray()) : "?";
                        int v; groups.TryGetValue(key, out v); groups[key] = v + 1;
                    }
                    var top = new List<string>(groups.Keys);
                    top.Sort((x, y) => groups[y].CompareTo(groups[x]));
                    var sb = new StringBuilder(128);
                    for (int i = 0; i < Math.Min(6, top.Count); i++)
                        sb.Append(top[i]).Append('×').Append(groups[top[i]]).Append(' ');
                    log($"animators: registered {n} (skipped non-AlwaysAnimate: {totalOther}); top owners: {sb}");
                }
            }
            catch (Exception e) { log?.Invoke("animator scan failed: " + e.Message); }
            return n;
        }

        private static int _whoLoggedOnce = -1;

        public static int RestoreAnimators(Action<string> log)
        {
            int n = 0;
            foreach (var kv in _animators)
            {
                var st = kv.Value;
                if (st.Animator == null) continue;   // destroyed with the scene
                try
                {
                    st.Animator.cullingMode = st.Mode;
                    if (st.Frozen) st.Animator.enabled = true;
                    if (st.FixedApplied) st.Animator.updateMode = AnimatorUpdateMode.Normal;
                    n++;
                }
                catch (Exception e) { log?.Invoke("animator restore failed: " + e.Message); }
            }
            _animators.Clear();
            FrozenAnimatorCount = 0;
            _fixedApplied = 0;
            return n;
        }

        // ------------------------------------------------------------ particle rate
        //
        // Harmony postfix on ParticleController.Initialize (both Spawn paths funnel through
        // it). Each pooled instance is scaled exactly once (instance id bookkeeping); only the
        // multiplier properties are touched, so the prefab's curve modes stay intact.

        private sealed class ParticleState
        {
            public float RateTime, RateDist;
            public int MaxParticles;
            public ParticleSystem Ps;
        }

        private static readonly Dictionary<int, ParticleState> _particles = new Dictionary<int, ParticleState>();
        private static FieldInfo _pcField;
        public static int ScaledParticleCount => _particles.Count;

        public static bool ScaleParticleController(ParticleController pc, float scale, Action<string> log)
        {
            if (pc == null || scale >= 0.999f) return false;
            try
            {
                if (_pcField == null)
                    _pcField = typeof(ParticleController).GetField("_particleSystem",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                if (_pcField == null) { log?.Invoke("particle: _particleSystem field not found"); return false; }
                var ps = _pcField.GetValue(pc) as ParticleSystem;
                if (ps == null) return false;
                int id = pc.GetInstanceID();
                if (_particles.ContainsKey(id)) return false;

                var em = ps.emission;
                var main = ps.main;
                _particles[id] = new ParticleState
                {
                    RateTime = em.rateOverTimeMultiplier,
                    RateDist = em.rateOverDistanceMultiplier,
                    MaxParticles = main.maxParticles,
                    Ps = ps
                };
                em.rateOverTimeMultiplier *= scale;
                em.rateOverDistanceMultiplier *= scale;
                main.maxParticles = Mathf.Max(1, Mathf.CeilToInt(main.maxParticles * scale));
                return true;
            }
            catch (Exception e) { log?.Invoke("particle scale failed: " + e.Message); return false; }
        }

        public static int RestoreParticles(Action<string> log)
        {
            int n = 0;
            foreach (var kv in _particles)
            {
                var st = kv.Value;
                if (st.Ps == null) continue;   // destroyed with the scene
                try
                {
                    var em = st.Ps.emission;
                    var main = st.Ps.main;
                    em.rateOverTimeMultiplier = st.RateTime;
                    em.rateOverDistanceMultiplier = st.RateDist;
                    main.maxParticles = st.MaxParticles;
                    n++;
                }
                catch (Exception e) { log?.Invoke("particle restore failed: " + e.Message); }
            }
            _particles.Clear();
            return n;
        }

        // ------------------------------------------------------------ fixed timestep

        private static float _fixedOriginal = -1f;

        public static bool SetFixedDeltaMs(float ms, Action<string> log)
        {
            try
            {
                if (_fixedOriginal < 0f) _fixedOriginal = Time.fixedDeltaTime;
                Time.fixedDeltaTime = Mathf.Clamp(ms, 10f, 100f) / 1000f;
                return true;
            }
            catch (Exception e) { log?.Invoke("fixeddelta set failed: " + e.Message); return false; }
        }

        public static bool RestoreFixedDelta(Action<string> log)
        {
            if (_fixedOriginal < 0f) return false;
            try { Time.fixedDeltaTime = _fixedOriginal; _fixedOriginal = -1f; return true; }
            catch (Exception e) { log?.Invoke("fixeddelta restore failed: " + e.Message); return false; }
        }

        // ------------------------------------------------------------ script hotspots
        //
        // Unity release players strip the built-in loop markers (BehaviourUpdate,
        // Physics.Processing, …), so script timing cannot come from ProfilerRecorder taps.
        // Instead the mod layer patches manager-level Update/LateUpdate entry points with
        // stopwatch prefix/postfix pairs (generic marker types give each pair its own index
        // without per-method delegate allocation) and every call accumulates here. The frame
        // counter turns totals into per-frame averages; MaxNs catches single-call spikes
        // (GC pauses, burst work) that the average hides.

        public sealed class Hotspot
        {
            public string Label;
            public double TotalNs;
            public double MaxNs;
            public long Calls;
        }

        private static readonly List<Hotspot> _hotspots = new List<Hotspot>();
        [ThreadStatic] private static Stack<long> _hotStarts;
        private static long _hotFrames;

        public static IReadOnlyList<Hotspot> Hotspots => _hotspots;
        public static double HotspotFrames => _hotFrames;

        public static int RegisterHotspot(string label)
        {
            _hotspots.Add(new Hotspot { Label = label });
            return _hotspots.Count - 1;
        }

        public static void ClearHotspots()
        {
            _hotspots.Clear();
            _renderTotalIdx = -1;
            HotspotReset();
        }

        public static void HotspotBegin()
        {
            var s = _hotStarts ?? (_hotStarts = new Stack<long>(8));
            s.Push(Stopwatch.GetTimestamp());
        }

        public static void HotspotEnd(int idx)
        {
            var s = _hotStarts;
            if (s == null || s.Count == 0) return;
            long t0 = s.Pop();
            if (idx < 0 || idx >= _hotspots.Count) return;
            double ns = (Stopwatch.GetTimestamp() - t0) * (1e9 / Stopwatch.Frequency);
            var h = _hotspots[idx];
            h.TotalNs += ns;
            h.Calls++;
            if (ns > h.MaxNs) h.MaxNs = ns;
        }

        /// <summary>Called once per rendered frame while playing: denominators for the
        /// averages, and a self-heal for a target that threw before its postfix ran.</summary>
        public static void HotspotFrame()
        {
            _hotFrames++;
            var s = _hotStarts;
            if (s != null && s.Count > 0) s.Clear();
        }

        public static void HotspotReset()
        {
            foreach (var h in _hotspots) { h.TotalNs = 0; h.MaxNs = 0; h.Calls = 0; }
            _hotFrames = 0;
        }

        /// <summary>Compact log form: "主泵 6.1ms(峰22) 寻路图 3.0ms(峰12) …". Rows with no
        /// signal are dropped so the fps line stays readable.</summary>
        public static string HotspotLogLine()
        {
            var sb = new StringBuilder(192);
            double frames = _hotFrames;
            foreach (var h in _hotspots)
            {
                double avg = frames > 0 ? h.TotalNs / 1e6 / frames : 0.0;
                double max = h.MaxNs / 1e6;
                if (avg < 0.05 && max < 1.0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(h.Label).Append(' ').Append(avg.ToString("0.0")).Append("ms")
                  .Append("(峰").Append(max.ToString("0")).Append(')');
            }
            return sb.Length > 0 ? sb.ToString() : "(none)";
        }

        // ------------------------------------------------------------ render total & player loop phases
        //
        // The final frame anatomy: the v1.6 session accounted only ~9ms of a ~26ms main
        // thread (camera stack 4.5 + submit 2.4 + scripts 1.5). The rest lives in engine
        // loop phases. PlayerLoop lets us insert timer delegates around every top-level
        // phase (Update / FixedUpdate / PreLateUpdate=animation / PostLateUpdate=render
        // submit / ...), and the URP Render overloads time the managed render pipeline
        // inside PostLateUpdate. Together the 26ms gets fully attributed.

        [ThreadStatic] private static int _renderTotalDepth;
        [ThreadStatic] private static long _renderTotalT0;
        private static int _renderTotalIdx = -1;

        /// <summary>Total managed render pipeline time (both URP Render overloads; the
        /// List overload forwards to the array one, so a depth guard prevents double
        /// counting). Index is registered lazily because the hotspots list is rebuilt on
        /// every mod enable.</summary>
        public static void RenderTotalBegin()
        {
            if (_renderTotalDepth > 0) { _renderTotalDepth++; return; }
            _renderTotalDepth = 1;
            _renderTotalT0 = Stopwatch.GetTimestamp();
        }

        public static void RenderTotalEnd()
        {
            if (_renderTotalDepth <= 0) return;
            if (_renderTotalDepth > 1) { _renderTotalDepth--; return; }
            _renderTotalDepth = 0;
            if (_renderTotalIdx < 0)
            {
                if (_hotspots.Count == 0) return;   // hotspots not built yet
                _renderTotalIdx = RegisterHotspot("渲染管线(总)");
            }
            if (_renderTotalIdx >= _hotspots.Count) return;
            double ns = (Stopwatch.GetTimestamp() - _renderTotalT0) * (1e9 / Stopwatch.Frequency);
            var h = _hotspots[_renderTotalIdx];
            h.TotalNs += ns;
            h.Calls++;
            if (ns > h.MaxNs) h.MaxNs = ns;
        }

        // -- player loop phase timers --

        private sealed class PhaseMarker { }   // type tag for the inserted systems

        private static PlayerLoopSystem _originalRoot;
        private static bool _loopTimersInstalled;

        public static bool InstallLoopTimers(Action<string> log)
        {
            if (_loopTimersInstalled) return true;
            try
            {
                // Unity 6: the loop root is a single PlayerLoopSystem whose subSystemList
                // holds the top-level phases. v1.7 only wrapped phases WITH subsystems —
                // but Update/PreLateUpdate/PostLateUpdate (scripts/animation/render) have
                // null subSystemList in this build and were silently skipped, leaving
                // ~15-25ms/frame unattributed. Wrapping EVERY top-level phase with
                // begin/end marker entries in the root list covers all of them regardless
                // of inner structure (same technique UniTask's PlayerLoopHelper uses).
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                var phases = root.subSystemList;
                if (phases == null || phases.Length == 0) return false;
                _originalRoot = root;

                var names = new StringBuilder(128);
                var wrapped = new PlayerLoopSystem[phases.Length * 3];
                for (int i = 0; i < phases.Length; i++)
                {
                    string label = "帧:" + (phases[i].type != null ? phases[i].type.Name : "?" + i);
                    names.Append(label).Append(phases[i].subSystemList != null ? "(subs)" : "(direct)").Append(' ');

                    // Level-2: wrap each subsystem of the phase too — the Update phase's
                    // 18ms must be split into ScriptRunBehaviourUpdate (MonoBehaviour.Update)
                    // vs coroutines vs native children before we can aim further.
                    var subs = phases[i].subSystemList;
                    if (subs != null && subs.Length > 0)
                    {
                        var w2 = new PlayerLoopSystem[subs.Length * 3];
                        for (int j = 0; j < subs.Length; j++)
                        {
                            string l2 = label + "/" + (subs[j].type != null ? subs[j].type.Name : "?" + j);
                            names.Append("  ").Append(l2).Append('\n');
                            w2[j * 3] = PhaseSystem(PhaseBegin(l2));
                            w2[j * 3 + 1] = subs[j];
                            w2[j * 3 + 2] = PhaseSystem(PhaseEnd(l2));
                        }
                        phases[i].subSystemList = w2;
                    }

                    wrapped[i * 3] = PhaseSystem(PhaseBegin(label));
                    wrapped[i * 3 + 1] = phases[i];
                    wrapped[i * 3 + 2] = PhaseSystem(PhaseEnd(label));
                }
                root.subSystemList = wrapped;
                PlayerLoop.SetPlayerLoop(root);
                _loopTimersInstalled = true;
                log?.Invoke("player loop phases:\n" + names.ToString().TrimEnd());
                return true;
            }
            catch (Exception e) { log?.Invoke("loop timer install failed: " + e.Message); return false; }
        }

        public static void RestoreLoopTimers(Action<string> log)
        {
            if (!_loopTimersInstalled) return;
            try
            {
                PlayerLoop.SetPlayerLoop(_originalRoot);
            }
            catch (Exception e) { log?.Invoke("loop timer restore failed: " + e.Message); }
            _originalRoot = default;
            _loopTimersInstalled = false;
        }

        private static PlayerLoopSystem PhaseSystem(PlayerLoopSystem.UpdateFunction fn)
            => new PlayerLoopSystem { type = typeof(PhaseMarker), updateDelegate = fn };

        [ThreadStatic] private static Stack<long> _phaseStarts;
        [ThreadStatic] private static int _phaseIdx;

        private static PlayerLoopSystem.UpdateFunction PhaseBegin(string label)
        {
            int idx = RegisterHotspot(label);
            return () =>
            {
                try
                {
                    var s = _phaseStarts ?? (_phaseStarts = new Stack<long>(8));
                    s.Push(Stopwatch.GetTimestamp());
                    _phaseIdx = idx;
                }
                catch { }
            };
        }

        private static PlayerLoopSystem.UpdateFunction PhaseEnd(string label)
        {
            return () =>
            {
                try
                {
                    var s = _phaseStarts;
                    if (s == null || s.Count == 0) return;
                    long t0 = s.Pop();
                    int idx = _phaseIdx;
                    if (idx < 0 || idx >= _hotspots.Count) return;
                    double ns = (Stopwatch.GetTimestamp() - t0) * (1e9 / Stopwatch.Frequency);
                    var h = _hotspots[idx];
                    h.TotalNs += ns;
                    h.Calls++;
                    if (ns > h.MaxNs) h.MaxNs = ns;
                }
                catch { }
            };
        }

        // ------------------------------------------------------------ GC collections

        public static string GcCountsLine()
        {
            try
            {
                return $"GC回收 g0={System.GC.CollectionCount(0)} g1={System.GC.CollectionCount(1)} g2={System.GC.CollectionCount(2)}";
            }
            catch { return "GC回收 -"; }
        }

        // ------------------------------------------------------------ memory statics
        //
        // Independent of ProfilerRecorder (works even with the profiler off): used to
        // correlate the observed fps degradation with growth in native/managed memory.

        public static long TotalAllocatedBytes => Safe(() => UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong());
        public static long TotalReservedBytes => Safe(() => UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong());
        public static long MonoUsedBytes => Safe(() => System.GC.GetTotalMemory(false));

        private static long Safe(Func<long> f)
        {
            try { return f(); }
            catch { return -1; }
        }

        /// <summary>Compact log/panel form: "堆内存 X MB · 原生 Y MB · Mono Z MB".</summary>
        public static string MemoryLine()
        {
            long alloc = TotalAllocatedBytes, reserved = TotalReservedBytes, mono = MonoUsedBytes;
            return $"堆内存 {MB(alloc)} MB · 预留 {MB(reserved)} MB · Mono {MB(mono)} MB";
        }

        private static string MB(long bytes) => bytes < 0 ? "-" : (bytes / 1048576.0).ToString("0");

        // ------------------------------------------------------------ per-camera render timing
        //
        // The 217-marker enumeration named the exact URP entry points, and they are managed
        // code — patchable exactly like game code. RenderSingleCameraInternal runs once per
        // camera per frame on the main thread (Main / UI / FOW / StaticPortrait / Reflection
        // Probes cameras are all listed by the markers), so timing it per camera name shows
        // which camera eats the frame. Both overloads are patched: when one calls the other
        // with the same camera the nested Begin is skipped via a depth counter.

        [ThreadStatic] private static Stack<string> _camNames;
        [ThreadStatic] private static Stack<long> _camStarts;
        [ThreadStatic] private static int _camSkipDepth;
        private static readonly Dictionary<string, int> _cameraIdx = new Dictionary<string, int>();

        public static void CameraRenderBegin(string name)
        {
            try
            {
                var names = _camNames ?? (_camNames = new Stack<string>(4));
                if (names.Contains(name)) { _camSkipDepth++; return; }   // nested same-camera call
                var starts = _camStarts ?? (_camStarts = new Stack<long>(4));
                starts.Push(Stopwatch.GetTimestamp());
                names.Push(name);
            }
            catch { }
        }

        public static void CameraRenderEnd()
        {
            try
            {
                if (_camSkipDepth > 0) { _camSkipDepth--; return; }
                var names = _camNames; var starts = _camStarts;
                if (names == null || starts == null || names.Count == 0) return;
                string name = names.Pop();
                long t0 = starts.Pop();
                double ns = (Stopwatch.GetTimestamp() - t0) * (1e9 / Stopwatch.Frequency);
                if (!_cameraIdx.TryGetValue(name, out int idx))
                {
                    idx = RegisterHotspot("相机:" + name);
                    _cameraIdx[name] = idx;
                }
                var h = _hotspots[idx];
                h.TotalNs += ns;
                h.Calls++;
                if (ns > h.MaxNs) h.MaxNs = ns;
            }
            catch { }
        }

        public static void ResetCameraTiming()
        {
            _cameraIdx.Clear();
            _camSkipDepth = 0;
            var names = _camNames; if (names != null) names.Clear();
            var starts = _camStarts; if (starts != null) starts.Clear();
        }

        // ------------------------------------------------------------ profiler engine
        //
        // Candidate root cause for Valid-but-zero recorders: frame counters/markers may
        // only flow when the profiler engine is taking frames. Enabled while the perf
        // overlay lives; restored exactly afterwards.

        private static bool _profilerForced;

        public static bool EnsureProfilerStats(Action<string> log)
        {
            try
            {
                if (UnityEngine.Profiling.Profiler.enabled) return true;
                UnityEngine.Profiling.Profiler.enabled = true;
                _profilerForced = true;
                log?.Invoke("profiler engine was OFF — forced on for recorder delivery");
                return true;
            }
            catch (Exception e) { log?.Invoke("profiler enable failed: " + e.Message); return false; }
        }

        public static void RestoreProfilerStats(Action<string> log)
        {
            if (!_profilerForced) return;
            try { UnityEngine.Profiling.Profiler.enabled = false; }
            catch (Exception e) { log?.Invoke("profiler restore failed: " + e.Message); }
            _profilerForced = false;
        }

        // ------------------------------------------------------------ log stack traces
        //
        // Every Debug.Log/Warning pays for a Mono stack walk by default; during heavy play the
        // game emits a constant drip of them (plus two per-destroy spammers the mod patches
        // out separately). Errors and exceptions keep their stack traces.

        private static StackTraceLogType? _stlLog;
        private static StackTraceLogType? _stlWarn;

        public static void SuppressLogSpam(Action<string> log)
        {
            try
            {
                if (!_stlLog.HasValue) _stlLog = Application.GetStackTraceLogType(LogType.Log);
                if (!_stlWarn.HasValue) _stlWarn = Application.GetStackTraceLogType(LogType.Warning);
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            }
            catch (Exception e) { log?.Invoke("stacktrace suppress failed: " + e.Message); }
        }

        public static void RestoreLogSpam(Action<string> log)
        {
            try
            {
                if (_stlLog.HasValue) { Application.SetStackTraceLogType(LogType.Log, _stlLog.Value); _stlLog = null; }
                if (_stlWarn.HasValue) { Application.SetStackTraceLogType(LogType.Warning, _stlWarn.Value); _stlWarn = null; }
            }
            catch (Exception e) { log?.Invoke("stacktrace restore failed: " + e.Message); }
        }

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.name : "(null)"; } catch { return "(?)"; }
        }
    }
}
