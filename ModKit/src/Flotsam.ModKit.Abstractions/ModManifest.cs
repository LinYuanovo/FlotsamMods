using System.Collections.Generic;

namespace FlotsamModKit.Abstractions
{
    /// <summary>
    /// Deserialized from mod.json. Never references game types so that the manifest
    /// can be read before any game assembly is touched.
    /// </summary>
    public sealed class ModManifest
    {
        // ---- authored fields (mod.json) ----
        public int SchemaVersion { get; set; } = 1;

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "1.0.0";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>ModKit API version this mod was built against, e.g. "1.0".</summary>
        public string ApiVersion { get; set; } = "1.0";

        /// <summary>Informational game version range, e.g. "&gt;=1.0.0 &lt;1.1.0".</summary>
        public string GameVersionRange { get; set; } = "";

        /// <summary>code | data | asset | foreign</summary>
        public string Type { get; set; } = "code";

        /// <summary>True when enable/disable cannot take effect without a game restart.</summary>
        public bool RestartRequired { get; set; }

        public string EntryAssembly { get; set; } = "";
        public string EntryType { get; set; } = "";

        public List<ModDependency> Dependencies { get; set; } = new List<ModDependency>();
        public List<string> LoadAfter { get; set; } = new List<string>();
        public List<string> Permissions { get; set; } = new List<string>();
        public List<KeybindDeclaration> Keybinds { get; set; } = new List<KeybindDeclaration>();

        // ---- runtime fields (filled by the host, not from json) ----
        public string Directory { get; set; } = "";
        public string ManifestPath { get; set; } = "";

        public override string ToString() => $"{Name} ({Id}) v{Version}";
    }

    public sealed class ModDependency
    {
        public string Id { get; set; } = "";
        public string MinVersion { get; set; } = "";
    }

    public sealed class KeybindDeclaration
    {
        public string Id { get; set; } = "";
        /// <summary>UnityEngine.KeyCode name, e.g. "W" or "F10".</summary>
        public string Default { get; set; } = "";
        public string Display { get; set; } = "";
    }
}