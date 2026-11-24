using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Retain native overlap/range/order and debit/output; only shared input admission changes.
    [HarmonyPatch]
    internal static class ProductionInputSelectionPatch
    {
        private static readonly ConditionalWeakTable<GameObject, object> consumed = new ConditionalWeakTable<GameObject, object>();
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return typeof(Furniture_CannabisGrinder).GetMethod(nameof(Furniture_CannabisGrinder.MouseDown), Type.EmptyTypes)!;
            yield return typeof(Furniture_CannabisPackager).GetMethod(nameof(Furniture_CannabisPackager.MouseDown), Type.EmptyTypes)!;
            yield return typeof(Furniture_FrenchFryCutter).GetMethod(nameof(Furniture_FrenchFryCutter.MouseDown), Type.EmptyTypes)!;
            yield return typeof(Home_Bunker).GetMethod("ScanForThisPlant", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(int), typeof(SeedsType) }, null)!;
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            bool Overlap(CodeInstruction i) => i.operand is MethodInfo method && method.DeclaringType == typeof(Physics) &&
                method.Name == nameof(Physics.OverlapSphere) && method.ReturnType == typeof(Collider[]);
            if (code.Count(Overlap) != 1) throw new InvalidOperationException("Unsupported production input overlap layout.");
            foreach (var instruction in code)
            {
                if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Object) &&
                    method.Name == nameof(UnityEngine.Object.Destroy) && method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(UnityEngine.Object))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = typeof(ProductionInputSelectionPatch).GetMethod(nameof(DestroyInput), BindingFlags.Static | BindingFlags.NonPublic)!;
                }
                yield return instruction;
                if (Overlap(instruction)) yield return new CodeInstruction(OpCodes.Call,
                    typeof(ProductionInputSelectionPatch).GetMethod(nameof(Filter), BindingFlags.Static | BindingFlags.NonPublic)!);
            }
        }
        private static void DestroyInput(UnityEngine.Object input)
        {
            if (input is GameObject go && MonMPPlugin.Instance?.Session?.IsRunning == true && MonMPPlugin.Instance.Session.IsHost)
                consumed.GetValue(go, _ => new object());
            // Destroy remains deferred by Unity. The admission marker prevents another
            // synchronous machine callback from charging that same pending-destroy pack.
            UnityEngine.Object.Destroy(input);
        }
        internal static Collider[] Filter(Collider[] inputs)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || !plugin.Session.IsHost || plugin.Items == null) return inputs;
            uint peer = SharedRemoteClicks.ProductionPeer;
            var selected = new List<Collider>();
            var seen = new HashSet<InteractableItems>();
            foreach (var collider in inputs)
            {
                if (collider == null) continue;
                if (consumed.TryGetValue(collider.gameObject, out _)) continue;
                var item = collider.GetComponent<InteractableItems>();
                if (collider.tag == "Player" || !(item is Tool_HarvestBasket || item is Item_WeedPreRollPack || item is Item_WeedPreRollBigPack))
                { selected.Add(collider); continue; }
                uint? owner = plugin.Items.LeaseOwnerOf(item);
                if (owner.HasValue && owner != peer && owner != ItemLease.Unowned) continue;
                if (seen.Add(item)) selected.Add(collider);
            }
            return selected.ToArray();
        }
    }
}
