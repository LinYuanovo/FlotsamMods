using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.ModKit.Probe
{
    /// <summary>
    /// Toolchain probe: proves that we can compile a BepInEx plugin offline against
    /// the game's own assemblies (Assembly-CSharp + Unity modules + Rewired) and that
    /// game types are resolvable at compile time.
    /// </summary>
    [BepInPlugin("flotsam.modkit.probe", "Flotsam ModKit Probe", "0.0.1")]
    public sealed class ProbePlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            var gm = GameManager.Instance;
            var uiState = UIManager.State;
            var evt = GameEventType.GameStart;
            var harmony = new Harmony("flotsam.modkit.probe");
            var action = RewiredConsts.Action.Townheart__Forward;
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { ok = true });

            Logger.LogInfo($"probe: gm={gm} uiState={uiState} evt={evt} harmony={harmony.Id} " +
                           $"townheartForward={action} json={json}");
            Debug.Log("probe: game types resolved");
        }
    }
}