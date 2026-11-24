using System;
using System.Reflection;
using GleyTrafficSystem;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    internal static class TrafficAuthorityPatches
    {
        internal static int ResumedUpdates { get; private set; }
        internal static int ResumedFixedUpdates { get; private set; }
        internal static int SkippedUpdates { get; private set; }
        internal static int SkippedFixedUpdates { get; private set; }
        internal static int SkippedTriggerExits { get; private set; }
        internal static bool IsGuestSuppressed => GuestOwnsTraffic;

        private static readonly MethodInfo vehicleTriggerExit = AccessTools.Method(
            typeof(VehicleComponent), "OnTriggerExit", new[] { typeof(Collider) })
            ?? throw new InvalidOperationException("Unsupported Gley vehicle layout: trigger-exit callback is missing.");

        private static bool GuestOwnsTraffic =>
            MonMPPlugin.Instance?.GuestWorld?.SuppressDisk == true ||
            (MonMPPlugin.Instance?.Session?.IsRunning == true && MonMPPlugin.Instance.Session.IsHost == false);

        [HarmonyPatch(typeof(TrafficManager), "Update")]
        private static class GuestTrafficUpdatePatch
        {
            private static bool Prefix()
            {
                if (!GuestOwnsTraffic) return true;
                SkippedUpdates++;
                return false;
            }
            private static void Postfix() { if (!GuestOwnsTraffic) ResumedUpdates++; }
        }

        [HarmonyPatch(typeof(TrafficManager), "FixedUpdate")]
        private static class GuestTrafficFixedUpdatePatch
        {
            private static bool Prefix()
            {
                if (!GuestOwnsTraffic) return true;
                SkippedFixedUpdates++;
                return false;
            }
            private static void Postfix() { if (!GuestOwnsTraffic) ResumedFixedUpdates++; }
        }

        [HarmonyPatch(typeof(VehicleComponent), "OnTriggerExit", new[] { typeof(Collider) })]
        private static class GuestTrafficTriggerExitPatch
        {
            private static bool Prefix()
            {
                if (!GuestOwnsTraffic) return true;
                SkippedTriggerExits++;
                return false;
            }
        }

        // Guests present host-driven traffic; Gley's trigger reactions need its (skipped) simulation.
        [HarmonyPatch(typeof(VehicleComponent), "OnTriggerEnter", new[] { typeof(Collider) })]
        private static class GuestTrafficTriggerEnterPatch
        {
            private static bool Prefix() => !GuestOwnsTraffic;
        }


#if MONMP_DEVELOPMENT
        internal static bool TestGuestTriggerExitSuppression(VehicleComponent component)
        {
            if (!GuestOwnsTraffic || component == null) return false;
            int before = SkippedTriggerExits;
            try
            {
                // The guest owns no Gley AI. A null collider is safe only when
                // the exact OnTriggerExit prefix is installed and returns early.
                vehicleTriggerExit.Invoke(component, new object?[] { null });
            }
            catch
            {
                return false;
            }
            return SkippedTriggerExits == before + 1;
        }
#endif

    }
}
