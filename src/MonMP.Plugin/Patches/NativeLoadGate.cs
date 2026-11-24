using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Patches
{
    // Gameplay.Player can exist while SaveSystem's multi-frame load coroutine is still
    // applying native save data. Do not snapshot that intermediate state as a world baseline.
    internal static class NativeLoadGate
    {
        private static int? loadingScene, completedScene;
        private static int starts, completions;
        internal static bool Ready => completedScene.HasValue && completedScene.Value == SceneManager.GetActiveScene().handle;
        internal static string Status => "starts=" + starts + "; completions=" + completions +
            "; loadingScene=" + loadingScene + "; completedScene=" + completedScene +
            "; activeScene=" + SceneManager.GetActiveScene().handle;

        internal static void Started()
        {
            loadingScene = SceneManager.GetActiveScene().handle;
            completedScene = null;
            starts++;
            Debug.Log("MONMP_NATIVE_LOAD_STARTED " + Status);
        }

        internal static void Completed()
        {
            int scene = SceneManager.GetActiveScene().handle;
            completions++;
            if (scene == loadingScene) completedScene = scene;
            Debug.Log("MONMP_NATIVE_LOAD_COMPLETED " + Status);
        }
    }

    [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.Load))]
    internal static class NativeLoadStartPatch
    {
        private static void Prefix()
        {
            Replication.NativePlayerSpawn.Capture(UnityEngine.Object.FindObjectOfType<Gameplay>());
            NativeLoadGate.Started();
        }
    }

    [HarmonyPatch(typeof(SaveSystem), "LoadAfterEverythingMoved")]
    internal static class NativeLoadCompletePatch
    {
        private static void Postfix()
        {
            NativeKonigSteeringRackLoadFix.RestoreFromSelectedSave();
            NativeLoadGate.Completed();
        }
    }
}
