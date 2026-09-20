using System;
using System.Collections.Generic;
using System.Reflection;

namespace FlotsamModKit.Game
{
    /// <summary>
    /// Safe patch-target lookup.
    ///
    /// Harmony's AccessTools.Method throws AmbiguousMatchException when a name exists more than
    /// once (including in base types) — one such lookup inside OnEnable marks the whole mod as
    /// failed. These helpers never throw: they search declared members only and return null when
    /// the match is not unique, so a mod can degrade gracefully instead of dying.
    /// </summary>
    public static class GamePatches
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;

        /// <summary>Exact overload by parameter types; null when not found or on error.</summary>
        public static MethodBase Method(Type type, string name, params Type[] parameters)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                if (parameters == null || parameters.Length == 0) return MethodByName(type, name);
                return type.GetMethod(name, All, null, parameters, null);
            }
            catch { return null; }
        }

        /// <summary>Single declared method with this name; null when zero or several match.</summary>
        public static MethodBase MethodByName(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var methods = type.GetMethods(All);
                MethodBase found = null;
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != name) continue;
                    if (found != null) return null;   // ambiguous on purpose: do not guess
                    found = methods[i];
                }
                return found;
            }
            catch { return null; }
        }

        /// <summary>Property getter/setter as a MethodInfo; null when missing.</summary>
        public static MethodInfo PropertyGetter(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var prop = type.GetProperty(name, All);
                return prop != null ? prop.GetGetMethod(true) : null;
            }
            catch { return null; }
        }

        /// <summary>Lists declared method names — useful when a lookup fails at runtime.</summary>
        public static string Describe(Type type)
        {
            if (type == null) return "(null type)";
            try
            {
                var names = new List<string>();
                foreach (var m in type.GetMethods(All)) names.Add(m.Name);
                names.Sort(StringComparer.Ordinal);
                return type.Name + ": " + string.Join(", ", names.ToArray());
            }
            catch { return type.Name + ": (unreadable)"; }
        }
    }
}