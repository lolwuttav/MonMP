using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Shared log replicas may enter native receivers on both peers. A guest must not
    // apply sawmill payout or log-consumption effects against its local replica (#147).
    [HarmonyPatch(typeof(Store_Sawmill_ReceptionWoodLogs), "OnTriggerEnter", new[] { typeof(Collider) })]
    internal static class SharedSawmillWoodLogPatch
    {
        private static bool Prefix(Store_Sawmill_ReceptionWoodLogs __instance, Collider other) =>
            other == null || other.tag == "Player" || SharedWoodLogTriggerGuard.AllowNative(__instance, other);
    }

    // The host owns the wood splitter's log consumption and the resulting firewood spawns. Like the
    // native splitter, it takes a log on contact even while a guest still holds it (#167): the native
    // Destroy ends the host copy and the next capture removes the guest's held copy.
    [HarmonyPatch(typeof(WoodSplitter), "OnTriggerEnter", new[] { typeof(Collider) })]
    internal static class SharedWoodSplitterLogPatch
    {
        private static readonly AccessTools.FieldRef<WoodSplitter, bool> Cutting = AccessTools.FieldRefAccess<WoodSplitter, bool>("isCutting");

        private static bool Prefix(WoodSplitter __instance, Collider other, out bool __state)
        {
            __state = Cutting(__instance);
            if (other == null || other.GetComponent<Item_WoodLog>() == null) return true;
            var session = MonMPPlugin.Instance?.Session;
            if (session?.IsRunning != true || session.IsHost) return true;
            return MonMPPlugin.Instance!.World?.Forest.GuestBlocks != true;
        }

        private static void Postfix(WoodSplitter __instance, bool __state)
        {
            if (!__state && Cutting(__instance)) MonMPPlugin.Instance?.World?.WoodSplitters?.HostCut(__instance);
        }
    }

    // #167: a guest's pull cord and kill switch go to the host's splitter; the guest's copy only mirrors.
    [HarmonyPatch]
    internal static class SharedWoodSplitterControlPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (string name in new[] { nameof(WoodSplitter.StartEngine), nameof(WoodSplitter.KillSwitchOn), nameof(WoodSplitter.KillSwitchOff) })
                yield return AccessTools.Method(typeof(WoodSplitter), name, Type.EmptyTypes)
                    ?? throw new InvalidOperationException("Missing native WoodSplitter." + name);
            // The guest's copy must not burn fuel for the host's running engine.
            yield return AccessTools.Method(typeof(WoodSplitter), "Tick", new[] { typeof(int) })
                ?? throw new InvalidOperationException("Missing native WoodSplitter.Tick");
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(WoodSplitter __instance, MethodBase __originalMethod)
        {
            var splitters = MonMPPlugin.Instance?.World?.WoodSplitters;
            if (splitters == null || !splitters.GuestBlocks) return true;
            byte kind = __originalMethod.Name == nameof(WoodSplitter.StartEngine) ? SharedWoodSplitters.Start
                : __originalMethod.Name == nameof(WoodSplitter.KillSwitchOn) ? SharedWoodSplitters.SwitchOn
                : __originalMethod.Name == nameof(WoodSplitter.KillSwitchOff) ? SharedWoodSplitters.SwitchOff : (byte)0;
            if (kind != 0) splitters.Intercept(__instance, kind);
            return false;
        }
    }

    [HarmonyPatch(typeof(Item_FireWood), nameof(Item_FireWood.Start))]
    internal static class SharedFirewoodSpawnPatch
    {
        private static void Postfix()
        {
            // Start creates the output's body/collider; discovery before it skips the new halves (#148).
            if (MonMPPlugin.Instance?.Session?.IsHost == true) MonMPPlugin.Instance.Items?.DiscoverSoon();
        }
    }

    // A held remote replica is a trigger on the host. If its first native Enter is
    // suppressed, a later guest Drop may leave it overlapping without another Enter.
    // Remember that specific overlap and replay the native callback after a validated
    // lease release, preserving ordinary drop-into-machine behavior.
    internal static class SharedWoodLogTriggerGuard
    {
        private const int MaxPendingTriggers = 128;
        private static readonly MethodInfo? SplitterTriggerMethod = AccessTools.Method(typeof(WoodSplitter), "OnTriggerEnter", new[] { typeof(Collider) });
        private static readonly MethodInfo? SawmillTriggerMethod = AccessTools.Method(typeof(Store_Sawmill_ReceptionWoodLogs), "OnTriggerEnter", new[] { typeof(Collider) });
        private static readonly List<PendingTrigger> pending = new List<PendingTrigger>();

        private sealed class PendingTrigger
        {
            internal SharedItems Items = null!;
            internal ulong ItemId;
            internal uint Owner, Generation;
            internal Component Receiver = null!;
            internal Collider Other = null!;
        }

        internal static bool AllowNative(Component receiver, Collider other)
        {
            if (other == null || other.GetComponent<Item_WoodLog>() is not Item_WoodLog log) return true;
            var plugin = MonMPPlugin.Instance;
            if (plugin == null) return true;
            var session = plugin.Session;
            if (session?.IsRunning != true) return true;
            if (!session.IsHost) return plugin.World?.Forest.GuestBlocks != true;

            var items = plugin.Items;
            ulong id = items?.EntityOf(log) ?? 0;
            if (id == 0 || items == null || items.FreeOrOwnedBy(id, 0)) return true;
            var state = items.StateOf(id);
            if (state == null || state.Lease.Owner == ItemLease.Unowned || state.Lease.Owner == 0) return true;

            Remember(items, id, state.Lease.Owner, state.Lease.Generation, receiver, other);
            return false;
        }

        internal static void ReplayAfterDrop(SharedItems items, uint peer, ulong id, ItemCommand command)
        {
            if (command.Action != ItemAction.Drop && command.Action != ItemAction.Throw) return;
            var matches = pending.Where(p => p.Items == items && p.ItemId == id && p.Owner == peer &&
                p.Generation == command.Generation).ToArray();
            if (matches.Length == 0) return;
            pending.RemoveAll(p => matches.Contains(p));

            var state = items.StateOf(id);
            var item = items.ItemOf(id) as Item_WoodLog;
            if (state == null || !state.Exists || state.Lease.Owner != ItemLease.Unowned || item == null) return;

            foreach (var trigger in matches)
            {
                if (trigger.Receiver == null || trigger.Other == null ||
                    trigger.Other.GetComponent<Item_WoodLog>() != item || !StillOverlapping(trigger.Receiver, trigger.Other)) continue;
                var native = trigger.Receiver is WoodSplitter ? SplitterTriggerMethod :
                    trigger.Receiver is Store_Sawmill_ReceptionWoodLogs ? SawmillTriggerMethod : null;
                if (native == null) continue;
                try { native.Invoke(trigger.Receiver, new object[] { trigger.Other }); }
                catch (TargetInvocationException ex) { Debug.LogException(ex.InnerException ?? ex); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        internal static void Forget(SharedItems items) => pending.RemoveAll(p => p.Items == items);

        private static void Remember(SharedItems items, ulong id, uint owner, uint generation,
            Component receiver, Collider other)
        {
            Prune();
            if (pending.Any(p => p.Items == items && p.ItemId == id && p.Owner == owner &&
                p.Generation == generation && p.Receiver == receiver && p.Other == other)) return;
            while (pending.Count >= MaxPendingTriggers) pending.RemoveAt(0);
            pending.Add(new PendingTrigger
            { Items = items, ItemId = id, Owner = owner, Generation = generation, Receiver = receiver, Other = other });
        }

        private static void Prune()
        {
            pending.RemoveAll(p => p.Items == null || p.Receiver == null || p.Other == null ||
                !p.Items.OwnedBy(p.ItemId, p.Owner) || p.Items.StateOf(p.ItemId)?.Lease.Generation != p.Generation);
        }

        private static bool StillOverlapping(Component receiver, Collider other)
        {
            // A validated drop just moved the item; test its current pose before the next physics sync (#147).
            foreach (var trigger in receiver.GetComponentsInChildren<Collider>(true))
            {
                if (trigger == null || !trigger.enabled || !trigger.isTrigger || !trigger.gameObject.activeInHierarchy) continue;
                try
                {
                    if (Physics.ComputePenetration(trigger, trigger.transform.position, trigger.transform.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out _)) return true;
                }
                catch (ArgumentException) { /* Unity cannot compute penetration for this collider pair. */ }
            }
            return false;
        }
    }
}
