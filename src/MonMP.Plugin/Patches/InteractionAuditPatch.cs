using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class InteractionAuditPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            foreach (string name in new[] { "MouseLeftClickDown", "MouseLeftClickUp", "MouseRightClickDown", "MouseRightClickUp", "UseKeyDown", "UseKeyUp", "ScrollThisObject", "CheckForInteractable", "GrabInteractable" })
            {
                var method = AccessTools.Method(typeof(PlayerInteractable), name, name == "ScrollThisObject" ? new[] { typeof(float) } : Type.EmptyTypes);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported interaction dispatch: " + name);
                yield return method;
            }
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo method &&
                    method.DeclaringType != null && typeof(Interactable).IsAssignableFrom(method.DeclaringType))
                {
                    // Ordinary getters and helper calls are not interaction entry points.
                    // Reflection's null result avoids Harmony warnings for those expected misses.
                    var dispatch = typeof(InteractionAuditDispatch).GetMethod(method.Name, BindingFlags.Static | BindingFlags.NonPublic);
                    if (dispatch != null)
                    {
                        instruction.opcode = OpCodes.Call; instruction.operand = dispatch; replaced++;
                    }
                }
                yield return instruction;
            }
            if (replaced == 0) throw new InvalidOperationException("Expected native interaction call was not found.");
        }
    }
    internal static class InteractionAuditDispatch
    {
        private static bool Allow(Interactable item, string action) => MonMPPlugin.Instance?.InteractionAudit?.Allow(item, action) ?? true;
        internal static void MouseDown(Interactable item) { if (Allow(item, nameof(MouseDown))) item.MouseDown(); }
        internal static void MouseUp(Interactable item) { if (Allow(item, nameof(MouseUp))) item.MouseUp(); }
        internal static void MouseRightDown(Interactable item) { if (Allow(item, nameof(MouseRightDown))) item.MouseRightDown(); }
        internal static void MouseRightUp(Interactable item) { if (Allow(item, nameof(MouseRightUp))) item.MouseRightUp(); }
        internal static bool Use(Interactable item) => Allow(item, nameof(Use)) && item.Use();
        internal static void UseUp(Interactable item) { if (Allow(item, nameof(UseUp))) item.UseUp(); }
        internal static void ScrolledOn(Interactable item, float value) { if (Allow(item, nameof(ScrolledOn))) item.ScrolledOn(value); }
    }
}
