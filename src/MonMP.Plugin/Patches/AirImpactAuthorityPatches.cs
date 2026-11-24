using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // SharedVehicleBolts leases progress only for registered fleet holes. Keep the native
    // coroutine available for targeting and loose-bolt handling, but mask its held buttons when
    // it aims at an installed hole that has no shared bolt identity. This also covers a held click
    // that moves from a shared hole onto an unsupported vehicle before release.
    [HarmonyPatch]
    internal static class AirImpactUnsharedBoltProgressGuardPatch
    {
        private static readonly FieldInfo MainCamera = RequireField("mainCamera");
        private static readonly FieldInfo LeftDown = RequireField("isMouseLeftClickDown");
        private static readonly FieldInfo RightDown = RequireField("isMouseRightClickDown");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var factory = AccessTools.Method(typeof(Tool_Air_Impact), "Spanner", Type.EmptyTypes);
            var moveNext = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
            if (moveNext == null)
                throw new InvalidOperationException("Unsupported native air-impact coroutine: Tool_Air_Impact.Spanner");
            yield return moveNext;
        }

        private static void Prefix(object __instance, out int __state)
        {
            __state = 0;
            var plugin = MonMPPlugin.Instance;
            var session = plugin?.Session;
            if (plugin == null || session?.IsRunning != true || session.IsHost ||
                !(__instance.GetType().GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(__instance) is Tool_Air_Impact tool)) return;

            var camera = MainCamera.GetValue(tool) as Camera ?? Camera.main;
            if (camera == null || !Physics.Raycast(MonMP.Plugin.Integration.LocalInteraction.SampleTool(camera).Ray,
                out var hit, 2f, LayerMask.GetMask("BoltHole")) ||
                hit.collider.GetComponent<Generated_BoltHole>() is not Generated_BoltHole generated ||
                generated.boltHoleDetails == null || !generated.boltHoleDetails.isBoltInstalled) return;

            bool shared = IsSharedBoltHole(plugin, generated.boltHoleDetails);
            if (!AirImpactAuthorityPolicy.BlocksGuestUnsharedInstalledBoltProgress(true, false, true, shared)) return;

            if (LeftDown.GetValue(tool) is true) __state |= 1;
            if (RightDown.GetValue(tool) is true) __state |= 2;
            if (__state != 0)
            {
                LeftDown.SetValue(tool, false);
                RightDown.SetValue(tool, false);
            }
        }

        private static Exception? Finalizer(object __instance, int __state, Exception? __exception)
        {
            if (__state == 0) return __exception;
            var owner = __instance.GetType().GetField("<>4__this",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__instance) as Tool_Air_Impact;
            if (owner != null)
            {
                if ((__state & 1) != 0) LeftDown.SetValue(owner, true);
                if ((__state & 2) != 0) RightDown.SetValue(owner, true);
            }
            return __exception;
        }

        private static bool IsSharedBoltHole(MonMPPlugin plugin, BoltHoleDetails hole)
        {
            var world = plugin.World;
            var identity = hole.HoleLocation != null ? hole.HoleLocation.GetComponent<NetworkIdentity>() : null;
            return world != null && identity != null && identity.AdapterType == BoltHoleState.Adapter;
        }

        private static FieldInfo RequireField(string name) => AccessTools.Field(typeof(Tool_Air_Impact), name)
            ?? throw new MissingFieldException(typeof(Tool_Air_Impact).FullName, name);
    }
}
