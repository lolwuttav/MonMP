using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Tool_Fence), nameof(Tool_Fence.MouseDown))]
    internal static class SharedFenceInstallPatch
    {
        private static bool Prefix(Tool_Fence __instance) => SharedFenceAuthority.Allow(__instance, FenceAction.Install);
    }

    [HarmonyPatch(typeof(Tool_Fence), nameof(Tool_Fence.MouseRightDown))]
    internal static class SharedFenceRemovePatch
    {
        private static bool Prefix(Tool_Fence __instance) => SharedFenceAuthority.Allow(__instance, FenceAction.BeginRemoval);
    }

    [HarmonyPatch(typeof(Tool_Fence), nameof(Tool_Fence.MouseRightUp))]
    internal static class SharedFenceReleasePatch
    {
        private static bool Prefix(Tool_Fence __instance) => SharedFenceAuthority.Allow(__instance, FenceAction.CancelRemoval);
    }

    [HarmonyPatch(typeof(Tool_Fence), nameof(Tool_Fence.Use))]
    internal static class SharedFenceOptionPatch
    {
        private static bool Prefix(Tool_Fence __instance, ref bool __result)
        {
            if (SharedFenceAuthority.Allow(__instance, FenceAction.ChangeStyle)) return true;
            __result = false;
            return false;
        }
    }

    // End a client-side removal iterator cleanly if a multiplayer session starts while a
    // native removal hold is already in progress. The shared host command owns new holds.
    [HarmonyPatch]
    internal static class SharedFenceRemovalCoroutinePatch
    {
        // A cancelled hold leaves one native step that clears its ring. Remember that
        // iterator weakly through cancellation/teardown; the action's claim may be gone.
        private static readonly ConditionalWeakTable<object, object> remoteIterators = new ConditionalWeakTable<object, object>();
        internal static IEnumerable<MethodInfo> Targets()
        {
            var iterator = AccessTools.Method(typeof(Tool_Fence), "Coroutine_UnFreezing", Type.EmptyTypes);
            var moveNext = iterator == null ? null : AccessTools.EnumeratorMoveNext(iterator);
            if (moveNext == null || moveNext.ReturnType != typeof(bool))
                throw new InvalidOperationException("Expected Tool_Fence.Coroutine_UnFreezing iterator MoveNext method.");
            yield return moveNext;
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(object __instance, ref bool __result, out IDisposable? __state)
        {
            __state = null;
            var plugin = MonMPPlugin.Instance;
            var ownerField = AccessTools.Field(__instance.GetType(), "<>4__this");
            var owner = ownerField?.GetValue(__instance) as Tool_Fence;
            if (remoteIterators.TryGetValue(__instance, out _) ||
                (owner != null && plugin?.World?.FenceActions?.IsRemoteRemoval(owner) == true))
            {
                remoteIterators.GetValue(__instance, _ => new object());
                __state = FenceRemovalPresentationScope.Enter();
                return true;
            }
            if (plugin?.Items?.GuestRunning != true)
            {
                return true;
            }
            try
            {
                var fence = owner;
                if (fence != null)
                {
                    AccessTools.Field(typeof(Tool_Fence), "TryingToRemoveThis")?.SetValue(fence, false);
                    AccessTools.Field(typeof(Interactable), "ui_Crosshair")?.GetValue(fence)?.GetType()
                        .GetMethod("RemoveAPart", new[] { typeof(float) })?.Invoke(
                            AccessTools.Field(typeof(Interactable), "ui_Crosshair")?.GetValue(fence), new object[] { -1f });
                }
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP fence local-removal cleanup failed: " + ex.Message); }
            __result = false;
            return false;
        }
        // Scope only this iterator step, including its final clear. Never retain a scope
        // across a yield or an exception and suppress another host interaction's UI.
        private static void Finalizer(object __instance, bool __result, IDisposable? __state, Exception? __exception)
        {
            __state?.Dispose();
            if (!__result || __exception != null) remoteIterators.Remove(__instance);
        }
    }

    internal static class FenceRemovalPresentationScope
    {
        private static int depth;
        internal static bool Active => depth > 0;
        internal static IDisposable Enter() { depth++; return new Exit(); }
        private sealed class Exit : IDisposable
        {
            private bool done;
            public void Dispose() { if (!done) { done = true; depth--; } }
        }
    }

    [HarmonyPatch(typeof(UI_Crosshair), nameof(UI_Crosshair.RemoveAPart))]
    internal static class SharedFenceRemovalCrosshairPatch
    {
        private static bool Prefix() => !FenceRemovalPresentationScope.Active;
    }

    internal static class SharedFenceAuthority
    {
        internal static bool Allow(Tool_Fence fence, FenceAction action)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Items?.GuestRunning != true) return true;
            if (plugin.World?.FenceActions?.GuestAction(fence, action) == true) return false;
            plugin.ReportItemUseUnavailable("The host controls fence changes in this multiplayer session.");
            return false;
        }
    }
}
