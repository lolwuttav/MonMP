#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
using MonMP.Plugin.Testing;
#endif
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativeCdDirectory
    {
        // Isolation rewrites native game call sites, not calls in this plugin assembly.
        // Match BuildPlayerMusicList's effective root before validating or reading its files.
        internal static string Path => System.IO.Path.Combine(
#if MONMP_DEVELOPMENT || MONMP_LOCAL_TEST_ISOLATION
            LocalTestIsolation.Installed ? LocalTestIsolation.SaveRoot :
#endif
            Application.persistentDataPath, "CDPlayer");
        internal static string CachePath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "MonMP", "cd-cache-v1");
    }
}
