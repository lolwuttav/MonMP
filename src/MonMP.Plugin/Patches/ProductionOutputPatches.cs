using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Constructor-free descriptors are also inspected against native metadata by the
    // startup test host, where Harmony installation itself is intentionally not run.
    internal static class NativeProductionOutputTargets
    {
        internal static IEnumerable<(Type Owner, string Name, Type[] Args, Type[] Outputs, bool Coroutine)> Producers()
        {
            yield return (typeof(Furniture_Fryer), "BuildPoutine", Type.EmptyTypes, new[] { typeof(Item_Poutine) }, true);
            yield return (typeof(Furniture_CannabisGrinder), "BuildPack", Type.EmptyTypes, new[] { typeof(Item_WeedPreRollPack) }, true);
            yield return (typeof(Furniture_CannabisPackager), "BuildPack", Type.EmptyTypes, new[] { typeof(Item_WeedPreRollBigPack) }, true);
            yield return (typeof(Furniture_FrenchFryPackager), "BuildPack", Type.EmptyTypes, new[] { typeof(Item_FrenchFries) }, true);
            yield return (typeof(Bunker_CompostMachine), "BuildFertilizer", Type.EmptyTypes, new[] { typeof(Tool_FertilizerPack) }, true);
            yield return (typeof(Home_Bunker), "BuildFrenchFriesPack", Type.EmptyTypes, new[] { typeof(Item_FrenchFries) }, true);
            yield return (typeof(Home_Bunker), "BuildCannabisPack", new[] { typeof(bool), typeof(bool) },
                new[] { typeof(Item_WeedPreRollBigPack), typeof(Item_WeedPreRollHugePack) }, true);
            yield return (typeof(SugarShack_CrimpingMachine), "Working", new[] { typeof(Item_MapleSyrupCan_Empty) }, new[] { typeof(Item_MapleSyrupCan) }, true);
            yield return (typeof(Store_Dealer_WeedReception), "OnTriggerEnter", new[] { typeof(Collider) }, new[] { typeof(Item_WeedPreRollHugePack) }, false);
            yield return (typeof(Store_FederationSyrup), "OpenStore", Type.EmptyTypes, new[] { typeof(Item_Barrel120) }, false);
            yield return (typeof(Item_CoffeePercolator), "GrabHomemadeCoffee", Type.EmptyTypes, new[] { typeof(Item_HomemadeCoffeeMug) }, false);
            yield return (typeof(Tool_PruningLoppers), "SpawnAddTreeCutting", new[] { typeof(TreeBase) }, new[] { typeof(Garden_TreeCuttings) }, false);
            yield return (typeof(Tool_HarvestBasket), "SpawnAddSeeds", new[] { typeof(Vector3) }, new[] { typeof(Tool_Seeds) }, false);
            yield return (typeof(Part_Driveline_Wheel), "PopTheTire", Type.EmptyTypes, new[] { typeof(Item_Driveline_Tires) }, false);
            yield return (typeof(SugarShack_TubingPost), "MouseRightDown", Type.EmptyTypes, new[] { typeof(Tool_TubingPost), typeof(Tool_TubingChalumeau) }, false);
        }
    }

    [HarmonyPatch]
    internal static class SharedProductionOutputPatch
    {
        private static readonly Dictionary<MethodBase, Type[]> outputs = new Dictionary<MethodBase, Type[]>();
        private static readonly FieldInfo DealerHuge = AccessTools.Field(typeof(Store_Dealer_WeedReception), "isHugePack");
        private static readonly FieldInfo DealerLastAdded = AccessTools.Field(typeof(Store_Dealer_WeedReception), "LastTimeBigPackAdded");

        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (var producer in NativeProductionOutputTargets.Producers())
            {
                var factory = AccessTools.Method(producer.Owner, producer.Name, producer.Args);
                if (factory == null || factory.IsStatic || factory.ReturnType != (producer.Coroutine ? typeof(IEnumerator) : typeof(void)) ||
                    !factory.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(producer.Args))
                    throw new InvalidOperationException("Unsupported native item producer: " + producer.Owner.Name + "." + producer.Name);
                var target = producer.Coroutine ? AccessTools.EnumeratorMoveNext(factory) : factory;
                if (target == null || producer.Coroutine && (target.IsStatic || target.ReturnType != typeof(bool) ||
                    target.GetParameters().Length != 0 || target.DeclaringType?.DeclaringType != producer.Owner ||
                    !typeof(IEnumerator).IsAssignableFrom(target.DeclaringType)))
                    throw new InvalidOperationException("Unsupported native item production step: " + producer.Owner.Name + "." + producer.Name);
                outputs[target] = producer.Outputs;
                yield return target;
            }
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static void Prefix(object __instance, MethodBase __originalMethod, out SharedItems.NativeProductionOutputScope? __state)
        {
            var items = MonMPPlugin.Instance?.Items;
            __state = outputs.TryGetValue(__originalMethod, out var types) ? items?.BeginNativeProductionOutputCapture(types) : null;
            if (__state != null && __instance is Store_Dealer_WeedReception reception && (bool)DealerHuge.GetValue(reception)! &&
                (float)DealerLastAdded.GetValue(reception)! + 1f < Time.timeSinceLevelLoad)
                items!.CaptureNativeDealerReplacementInputs(__state, Physics.OverlapSphere(reception.transform.position, 1.65f));
        }
        private static void Postfix(SharedItems.NativeProductionOutputScope? __state) =>
            MonMPPlugin.Instance?.Items?.EndNativeProductionOutputCapture(__state);
        private static Exception? Finalizer(Exception? __exception, SharedItems.NativeProductionOutputScope? __state)
        {
            MonMPPlugin.Instance?.Items?.EndNativeProductionOutputCapture(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(InteractablePacks), nameof(InteractablePacks.Use))]
    internal static class SharedSmallPackOutputPatch
    {
        private static void Prefix(InteractablePacks __instance, out SharedItems.NativeProductionOutputScope? __state) =>
            __state = MonMPPlugin.Instance?.Items?.BeginNativeSmallPackOutputCapture(__instance);
        // Native false means the pack still has contents, even after a successful extraction.
        // Observe actual outputs rather than using that destruction decision as success status.
        private static void Postfix(SharedItems.NativeProductionOutputScope? __state) =>
            MonMPPlugin.Instance?.Items?.EndNativeProductionOutputCapture(__state);
        private static Exception? Finalizer(Exception? __exception, SharedItems.NativeProductionOutputScope? __state)
        {
            MonMPPlugin.Instance?.Items?.EndNativeProductionOutputCapture(__state);
            return __exception;
        }
    }
}
