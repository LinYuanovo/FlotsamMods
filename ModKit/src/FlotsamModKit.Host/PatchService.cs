using System;
using System.Reflection;
using FlotsamModKit.Abstractions;
using HarmonyLib;

namespace FlotsamModKit.Host
{
    /// <summary>
    /// One Harmony instance per mod, so disabling a mod can atomically revert every one
    /// of its patches (Harmony.UnpatchSelf) without touching other mods' patches.
    /// </summary>
    public sealed class PatchService : IPatchService
    {
        private readonly Harmony _harmony;
        private readonly ILog _log;
        private int _count;

        public PatchService(string modId, ILog log)
        {
            _log = log;
            _harmony = new Harmony("flotsam.modkit." + modId);
        }

        public int PatchCount => _count;
        public string HarmonyId => _harmony.Id;

        public bool Prefix(MethodBase target, MethodInfo patch, int priority = 400)
            => Apply(target, patch, true, priority);

        public bool Postfix(MethodBase target, MethodInfo patch, int priority = 400)
            => Apply(target, patch, false, priority);

        private bool Apply(MethodBase target, MethodInfo patch, bool isPrefix, int priority)
        {
            if (target == null)
            {
                _log?.Error($"patch target is null ({patch?.Name})");
                return false;
            }
            if (patch == null)
            {
                _log?.Error($"patch method is null for target {target.DeclaringType?.Name}.{target.Name}");
                return false;
            }

            try
            {
                var method = new HarmonyMethod(patch) { priority = priority };
                _harmony.Patch(target, isPrefix ? method : null, isPrefix ? null : method);
                _count++;
                _log?.Info($"{(isPrefix ? "prefix" : "postfix")} {target.DeclaringType?.Name}.{target.Name} <- {patch.Name}");
                return true;
            }
            catch (Exception e)
            {
                _log?.Error($"patch failed for {target.DeclaringType?.Name}.{target.Name}: {e.Message}");
                return false;
            }
        }

        public int RevokeAll()
        {
            int n = _count;
            try
            {
                _harmony.UnpatchSelf();
            }
            catch (Exception e)
            {
                _log?.Error($"unpatch failed: {e.Message}");
            }
            _count = 0;
            if (n > 0) _log?.Info($"revoked {n} patch(es)");
            return n;
        }
    }
}