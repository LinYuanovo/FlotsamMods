using System;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// The single BepInEx plugin of the kit. Everything else is loaded and managed by
    /// ModKitRuntime out of the game's Mods/ directory.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ModKitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "flotsam.modkit.host";
        public const string PluginName = "Flotsam ModKit";
        public const string PluginVersion = "1.0.0";

        private void Awake()
        {
            var log = new ModKitLog(Logger);

            try
            {
                log.Write("modkit", "INFO", $"game root: {Paths.GameRootPath}");
                log.Write("modkit", "INFO", $"bepinex root: {Paths.BepInExRootPath}");
                log.Write("modkit", "INFO", $"unity {Application.unityVersion}, game {Application.version}");
            }
            catch (Exception e)
            {
                log.Write("modkit", "WARN", "path probe failed: " + e.Message);
            }

            EnsureSharedAssemblies(log);

            var go = new GameObject("[FlotsamModKit.Runtime]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            var runtime = go.AddComponent<ModKitRuntime>();
            runtime.Initialize(log);

            log.Write("modkit", "INFO", "host ready — press the manager key (default F10) in game");
        }

        /// <summary>
        /// Loads the shared contract assemblies from our own plugin folder before any mod
        /// assembly is loaded, so every mod binds to the SAME IFlotsamMod type identity.
        /// </summary>
        private static void EnsureSharedAssemblies(ModKitLog log)
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return;

                foreach (var name in new[] { "Flotsam.ModKit.Abstractions.dll", "Flotsam.ModKit.Game.dll" })
                {
                    var path = Path.Combine(dir, name);
                    if (!File.Exists(path))
                    {
                        log.Write("modkit", "WARN", $"shared assembly missing: {name}");
                        continue;
                    }
                    try
                    {
                        Assembly.LoadFrom(path);
                    }
                    catch (Exception e)
                    {
                        log.Write("modkit", "WARN", $"preload {name} failed: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                log.Write("modkit", "WARN", "shared assembly preload failed: " + e.Message);
            }
        }
    }
}