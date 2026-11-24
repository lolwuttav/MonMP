using System.Collections.Generic;
using HarmonyLib;
using MonMP.Plugin.UI;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Patches
{
    // T belongs to the wheel only while a synchronized active gameplay state can display it.
    // The native Sorry callbacks still run when invoked explicitly by the wheel, and outside
    // wheel-eligible play (offline or outside the gameplay states) the game's callbacks remain untouched.
    internal static class SocialInputPatches
    {
        private static readonly HashSet<PlayerVisualHands> SuppressedPhysicalSorry = new HashSet<PlayerVisualHands>();
        private static readonly Dictionary<PlayerVisualHands, (Gameplay Gameplay, int Scene)> NativePhysicalSorry =
            new Dictionary<PlayerVisualHands, (Gameplay, int)>();
        private static readonly List<PlayerVisualHands> NativeSorryRelease = new List<PlayerVisualHands>();

        internal static bool PrefixSorryDown(PlayerVisualHands hands)
        {
            if (WheelEmoteActions.InvokingNativeHandler) return true;
            var plugin = MonMPPlugin.Instance;
            bool localHands = hands != null && plugin?.Gameplay?.Player?.Get_VisualHands == hands;
            if (hands != null && localHands && plugin!.OverrideNativeSorry && WheelEmoteActions.PhysicalSorryTControlActive(hands))
            {
                SuppressedPhysicalSorry.Add(hands);
                return false;
            }

            if (hands != null)
            {
                // An accepted later press supersedes any suppressed T press. Its matching release
                // must run even if the binding, session or gameplay gate changed in between.
                SuppressedPhysicalSorry.Remove(hands);
                if (localHands && plugin!.Session?.IsRunning == true && WheelEmoteActions.PhysicalSorryHeld(hands))
                    NativePhysicalSorry[hands] = (plugin.Gameplay!, SceneManager.GetActiveScene().handle);
            }
            return true;
        }

        internal static bool PrefixSorryUp(PlayerVisualHands hands)
        {
            if (WheelEmoteActions.InvokingNativeHandler) return true;
            if (hands == null) return true;
            bool nativeOwned = NativePhysicalSorry.Remove(hands);
            bool suppressed = SuppressedPhysicalSorry.Remove(hands);
            return nativeOwned || !suppressed;
        }

        internal static bool NativeSorryHeld(PlayerVisualHands hands) =>
            NativePhysicalSorry.ContainsKey(hands) && WheelEmoteActions.PhysicalSorryHeld(hands);

        internal static void ForgetNativeSorry(PlayerVisualHands hands) => NativePhysicalSorry.Remove(hands);

        internal static void Tick(MonMPPlugin plugin)
        {
            if (NativePhysicalSorry.Count == 0) return;
            var scene = SceneManager.GetActiveScene();
            NativeSorryRelease.Clear();
            foreach (var entry in NativePhysicalSorry)
            {
                var hands = entry.Key;
                var owner = entry.Value;
                if (hands != null && plugin.Session?.IsRunning == true && plugin.Gameplay == owner.Gameplay &&
                    owner.Gameplay.Player?.Get_VisualHands == hands && hands.isActiveAndEnabled &&
                    scene.IsValid() && scene.isLoaded && scene.handle == owner.Scene && hands.gameObject.scene.handle == owner.Scene &&
                    !WheelEmoteActions.NativeSorryInputBlocked(plugin) &&
                    plugin.World?.SaveGate.Active != true && plugin.GuestWorld?.HoldPresentation != true &&
                    !owner.Gameplay.Get_isGamePaused && !owner.Gameplay.Get_IsDead && !owner.Gameplay.Get_IsSleeping &&
                    !hands.Get_IsMapOpen && !hands.Get_isGameConsoleOpen && WheelEmoteActions.PhysicalSorryHeld(hands))
                    continue;

                NativeSorryRelease.Add(entry.Key);
            }
            // Release after enumeration: native SorryUp also forgets ownership. Reusing the
            // scratch list avoids allocating a snapshot every frame while a button is held.
            foreach (var hands in NativeSorryRelease)
            {
                NativePhysicalSorry.Remove(hands);
                if (hands != null) WheelEmoteActions.ReleaseNativeSorry(hands);
            }
            NativeSorryRelease.Clear();
        }

        internal static void Reset()
        {
            // Release captured multiplayer native holds before discarding the owner ledger. This
            // also covers input disabling or reconnect when the canceled callback never arrives.
            NativeSorryRelease.Clear();
            NativeSorryRelease.AddRange(NativePhysicalSorry.Keys);
            NativePhysicalSorry.Clear();
            SuppressedPhysicalSorry.Clear();
            foreach (var owner in NativeSorryRelease)
                if (owner != null) WheelEmoteActions.ReleaseNativeSorry(owner);
            NativeSorryRelease.Clear();
        }
    }

    [HarmonyPatch(typeof(PlayerVisualHands), "SorryDown")]
    internal static class NativeSorryDownPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerVisualHands __instance) => SocialInputPatches.PrefixSorryDown(__instance);
    }

    [HarmonyPatch(typeof(PlayerVisualHands), "SorryUp")]
    internal static class NativeSorryUpPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerVisualHands __instance) => SocialInputPatches.PrefixSorryUp(__instance);
    }
}
