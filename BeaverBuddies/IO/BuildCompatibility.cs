using TimberNet;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.IO
{
    public sealed class BuildCompatibility : ILoadableSingleton
    {
        static BuildCompatibility current;
        readonly ModRepository repository;
        readonly ModSettingsOwnerRegistry settings;
        // Hash once per unchanged file; never scan files from the simulation tick.
        static readonly Dictionary<string, (long Length, DateTime Modified, string Hash)> fileCache = new(StringComparer.OrdinalIgnoreCase);
        public BuildCompatibility(ModRepository repository, ModSettingsOwnerRegistry settings)
        { this.repository = repository; this.settings = settings; }
        public void Load() => current = this;

        internal static string CreateIdentity()
        {
            if (current == null) throw new IOException("Mod compatibility service is not ready. Return to the main menu and try again.");
            return current.Capture();
        }
        string Capture()
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["game"] = Application.version,
                ["beaverbuddies"] = Plugin.Version,
                ["loaded/BeaverBuddies"] = typeof(Plugin).Module.ModuleVersionId.ToString("D"),
                ["loaded/TimberNet"] = typeof(TimberNetBase).Module.ModuleVersionId.ToString("D"),
                ["session/detailedTracing"] = Settings.Debug.ToString()
            };
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            int order = 0;
            foreach (var mod in repository.EnabledMods)
            {
                string id = mod.Manifest.Id;
                entries.Add("mod/" + id, mod.Manifest.Version.Full);
                entries.Add("order/" + (order++).ToString("D4", CultureInfo.InvariantCulture), id);
                string root = mod.ModDirectory.Path;
                var fingerprints = new List<string>();
                foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(RelevantFile)
                    .OrderBy(p => p.Substring(root.Length), StringComparer.Ordinal))
                {
                    var file = new FileInfo(path);
                    if (!fileCache.TryGetValue(path, out var cached) || cached.Length != file.Length || cached.Modified != file.LastWriteTimeUtc)
                    {
                        using var input = File.OpenRead(path);
                        using var sha = SHA256.Create();
                        cached = (file.Length, file.LastWriteTimeUtc, BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""));
                        fileCache[path] = cached;
                    }
                    string relative = path.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
                    fingerprints.Add(relative + "=" + cached.Hash);
                    if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var name = AssemblyName.GetAssemblyName(path);
                        // Timberborn loads DLL bytes, so Assembly.Location is empty. Use
                        // the identities of the matching modules actually in this process.
                        string loaded = string.Join(",", assemblies.Where(a => a.FullName == name.FullName)
                            .Select(a => a.ManifestModule.ModuleVersionId.ToString("D")).Distinct().OrderBy(x => x, StringComparer.Ordinal));
                        entries.Add("code/" + id + "/" + relative, loaded.Length == 0 ? "not-loaded" : loaded);
                    }
                    catch (BadImageFormatException) { /* Native DLL: its file hash is still checked. */ }
                }
                entries.Add("files/" + id, CompatibilityProfile.Digest(string.Join("\n", fingerprints)));
                if (id == Plugin.ID || !settings.HasModSettings(mod)) continue;
                foreach (var owner in settings.GetModSettingOwners(mod))
                {
                    var properties = owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => typeof(ModSetting).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0).ToArray();
                    int index = 0;
                    foreach (var setting in owner.ModSettings)
                    {
                        int ordinal = index++;
                        if (setting is NonPersistentSetting) continue;
                        var valueProperty = setting.GetType().GetProperty("Value");
                        if (valueProperty == null) throw new IOException("Cannot read registered mod setting: " + owner.GetType().FullName);
                        string name = properties.FirstOrDefault(p => ReferenceEquals(p.GetValue(owner), setting))?.Name ?? "custom-" + ordinal;
                        string key = "setting/" + id + "/" + owner.GetType().FullName + "/" + name;
                        entries.Add(key, CompatibilityProfile.Digest(Canonical(valueProperty.GetValue(setting))));
                    }
                }
            }
            return CompatibilityProfile.Encode(entries);
        }
        static string Canonical(object value) => value switch {
            null => "null", bool b => b ? "bool:true" : "bool:false", float f => "float:" + f.ToString("R", CultureInfo.InvariantCulture),
            double d => "double:" + d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => value.GetType().FullName + ":" + f.ToString(null, CultureInfo.InvariantCulture),
            string s => "string:" + s,
            _ => throw new IOException("Unsupported registered setting value: " + value.GetType().FullName)
        };
        static bool RelevantFile(string path)
        {
            if (Path.GetFileName(path).Equals("workshop_data.json", StringComparison.OrdinalIgnoreCase)) return false;
            switch (Path.GetExtension(path).ToLowerInvariant())
            { case ".dll": case ".json": case ".bundle": case ".assetbundle": case ".cfg": case ".ini": case ".toml": return true; default: return false; }
        }
    }
}
