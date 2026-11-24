using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(BoltHoleDetails), nameof(BoltHoleDetails.Attach))]
    internal static class SharedBoltInstallPatch
    {
        private static void Postfix(BoltHoleDetails __instance, Hardware_Bolt _bolt) =>
            MonMPPlugin.Instance?.World?.Bolts.NativeInstalled(__instance, _bolt);
    }

    [HarmonyPatch(typeof(BoltHoleDetails), nameof(BoltHoleDetails.Unscrewed))]
    internal static class SharedBoltRemovePatch
    {
        private static void Postfix(BoltHoleDetails __instance) => MonMPPlugin.Instance?.World?.Bolts.NativeRemoved(__instance);
    }

    [HarmonyPatch]
    internal static class SharedBoltToolInputPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (Type type in new[] { typeof(Tool_AdjustableSpanner), typeof(Tool_Air_Impact) })
                foreach (string name in new[] { "MouseDown", "MouseUp", "MouseRightDown", "MouseRightUp" })
                {
                    var method = AccessTools.Method(type, name, Type.EmptyTypes);
                    if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                        throw new InvalidOperationException("Unsupported native bolt tool input: " + type.Name + "." + name);
                    yield return method;
                }
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(InteractableTools __instance, MethodBase __originalMethod)
        {
            var bolts = MonMPPlugin.Instance?.World?.Bolts;
            if (bolts == null) return true;
            bool right = __originalMethod.Name.StartsWith("MouseRight", StringComparison.Ordinal);
            bool pressed = __originalMethod.Name.EndsWith("Down", StringComparison.Ordinal);
            return bolts.InterceptToolInput(__instance,
                right ? BoltToolDirection.Loosen : BoltToolDirection.Tighten, pressed);
        }
    }

    [HarmonyPatch]
    internal static class SharedBoltToolCoroutinePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (Type type in new[] { typeof(Tool_AdjustableSpanner), typeof(Tool_Air_Impact) })
            {
                var factory = AccessTools.Method(type, "Spanner", Type.EmptyTypes);
                var moveNext = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
                if (moveNext == null)
                    throw new InvalidOperationException("Unsupported native bolt tool coroutine: " + type.Name + ".Spanner");
                yield return moveNext;
            }
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static void Prefix(object __instance, out int __state)
        {
            __state = MonMPPlugin.Instance?.World?.Bolts?.MaskGuestProgress(__instance) ?? 0;
        }

        private static Exception? Finalizer(object __instance, int __state, Exception? __exception)
        {
            MonMPPlugin.Instance?.World?.Bolts?.RestoreGuestProgress(__instance, __state);
            return __exception;
        }
    }
}
