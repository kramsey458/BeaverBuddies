using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.Modding;

namespace BeaverBuddies.Connect
{
    /// <summary>
    /// Reads this computer's enabled mods. Created with the game's mod repository in every scene, so a
    /// player can host or join from anywhere and the list is ready.
    /// </summary>
    public class ModListService
    {
        public ModListService(ModRepository modRepository)
        {
            // Read later, not now: the repository may not have finished loading when this is created.
            ModCompatibility.SetSource(() => Describe(modRepository.EnabledMods));
        }

        /// <summary>The enabled mods as entries: ID, display name and version (for example "v2.4.1").</summary>
        public static List<ModEntry> Describe(IEnumerable<Mod> mods)
        {
            return mods
                .Where(mod => mod?.Manifest != null)
                .Select(mod => new ModEntry(mod.Manifest.Id, mod.Manifest.Name, mod.Manifest.Version.Formatted))
                .ToList();
        }
    }

    /// <summary>
    /// The join-time comparison of mod lists. Both players send theirs during the compatibility handshake,
    /// each compares them, and a difference is shown as a warning. It never stops anyone joining: mods that
    /// only change the interface are harmless, and only the players can tell which mods matter.
    /// </summary>
    internal static class ModCompatibility
    {
        static readonly object gate = new object();
        static Func<IEnumerable<ModEntry>> source = () => Enumerable.Empty<ModEntry>();
        static List<ModEntry> cache;

        internal static void SetSource(Func<IEnumerable<ModEntry>> newSource)
        {
            lock (gate)
            {
                source = newSource;
                cache = null;
            }
        }

        /// <summary>This computer's enabled mods. The list cannot change while the game runs, so it is read once.</summary>
        internal static List<ModEntry> Local()
        {
            lock (gate)
            {
                if (cache == null || cache.Count == 0)
                {
                    try { cache = source().ToList(); }
                    catch (Exception error)
                    {
                        Plugin.LogWarning("Could not read the enabled mods: " + error.Message);
                        cache = new List<ModEntry>();
                    }
                }
                return cache;
            }
        }

        /// <summary>
        /// The text to send to the other player. Never null, so both sides always take part in the exchange.
        /// A modded game always has at least this mod, so an empty list means it could not be read.
        /// </summary>
        internal static string CreateAdvisory()
        {
            List<ModEntry> local = Local();
            return local.Count == 0 ? ModListCodec.Unavailable : ModListCodec.Serialize(local);
        }

        /// <summary>Called on the update thread with the other player's list.</summary>
        internal static void OnPeerAdvisory(string peerName, string advisory)
        {
            if (!ModListCodec.TryParse(advisory, out List<ModEntry> remote))
            {
                Plugin.Log("The other player's mod list could not be read; not comparing mods.");
                return;
            }
            List<ModEntry> local = Local();
            if (local.Count == 0)
            {
                Plugin.LogWarning("This computer's mod list could not be read; not comparing mods.");
                return;
            }
            ModDifference difference = ModListComparer.Compare(local, remote);
            string peer = string.IsNullOrEmpty(peerName) ? "the other player" : peerName;
            if (difference.IsEmpty)
            {
                Plugin.Log($"Mod lists match {peer}'s ({remote.Count} mods).");
                return;
            }
            Plugin.LogWarning($"Mod lists differ from {peer}'s. Only here: {Names(difference.OnlyHere)}. " +
                $"Only there: {Names(difference.OnlyThere)}. Different versions: " +
                $"{(difference.VersionsDiffer.Count == 0 ? "none" : string.Join(", ", difference.VersionsDiffer.Select(pair => $"{pair.Key.Name} {pair.Key.Version} vs {pair.Value.Version}")))}. " +
                "Differences in mods that change the game can cause a desync.");
            ModWarnings.Add(new PendingModWarning(peerName, difference));
        }

        static string Names(List<ModEntry> mods) => mods.Count == 0 ? "none" : string.Join(", ", mods.Select(mod => mod.Display));
    }
}
