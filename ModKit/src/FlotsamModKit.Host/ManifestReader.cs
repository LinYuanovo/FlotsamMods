using System;
using System.IO;
using FlotsamModKit.Abstractions;
using Newtonsoft.Json;

namespace FlotsamModKit.Host
{
    /// <summary>Reads and validates mod.json files.</summary>
    public static class ManifestReader
    {
        public static ModManifest Read(string manifestPath, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(manifestPath))
                {
                    error = "mod.json not found";
                    return null;
                }

                var text = File.ReadAllText(manifestPath);
                var manifest = JsonConvert.DeserializeObject<ModManifest>(text, new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                });

                if (manifest == null)
                {
                    error = "mod.json could not be parsed";
                    return null;
                }

                manifest.ManifestPath = manifestPath;
                manifest.Directory = Path.GetDirectoryName(manifestPath) ?? "";

                if (string.IsNullOrWhiteSpace(manifest.Id))
                {
                    error = "manifest.id is required";
                    return null;
                }
                if (string.IsNullOrWhiteSpace(manifest.Name))
                    manifest.Name = manifest.Id;

                if (string.Equals(manifest.Type, "code", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
                    {
                        error = "manifest.entryAssembly is required for type=code";
                        return null;
                    }
                    if (string.IsNullOrWhiteSpace(manifest.EntryType))
                    {
                        error = "manifest.entryType is required for type=code";
                        return null;
                    }
                }

                return manifest;
            }
            catch (Exception e)
            {
                error = "mod.json read failed: " + e.Message;
                return null;
            }
        }

        /// <summary>True when the mod's declared API major version matches the host.</summary>
        public static bool ApiCompatible(ModManifest manifest, string hostApiVersion, out string reason)
        {
            reason = null;
            if (manifest == null) { reason = "no manifest"; return false; }
            if (string.IsNullOrWhiteSpace(manifest.ApiVersion)) return true;

            var modMajor = Major(manifest.ApiVersion);
            var hostMajor = Major(hostApiVersion);
            if (modMajor < 0 || hostMajor < 0) return true;
            if (modMajor != hostMajor)
            {
                reason = $"api {manifest.ApiVersion} incompatible with host {hostApiVersion}";
                return false;
            }
            return true;
        }

        private static int Major(string version)
        {
            if (string.IsNullOrEmpty(version)) return -1;
            var first = version.Split('.')[0];
            return int.TryParse(first, out var v) ? v : -1;
        }
    }
}