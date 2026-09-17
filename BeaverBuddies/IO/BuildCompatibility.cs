using TimberNet;
using UnityEngine;

namespace BeaverBuddies.IO
{
    internal static class BuildCompatibility
    {
        // Loaded module IDs identify the binaries actually executing, even if
        // the user replaced the files on disk without restarting the game.
        internal static string CreateIdentity() =>
            $"game={Application.version};mod={Plugin.Version};" +
            $"modBuild={typeof(Plugin).Module.ModuleVersionId:D};netBuild={typeof(TimberNetBase).Module.ModuleVersionId:D}";
    }
}
