using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlayOneShot2D),
        typeof(AudioClip), typeof(bool), typeof(float), typeof(float))]
    internal static class PlayerAnxietyWarningPatch
    {
        private static void Postfix(SOUND_Manager __instance, AudioClip _clip, AudioSource? __result) =>
            PlayerAnxietyWarningPresentation.Native(__instance, _clip, __result);
    }
}
