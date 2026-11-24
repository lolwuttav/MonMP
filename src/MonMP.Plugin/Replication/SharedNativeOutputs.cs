using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        private readonly Dictionary<ulong, InteractableItems> pendingNativeOutputs = new Dictionary<ulong, InteractableItems>();
        private readonly HashSet<ulong> deferredNativeOutputSpawns = new HashSet<ulong>();
        internal bool CanRegisterNativeOutput(InteractableItems item)
        {
            var prefab = Supported(item);
            if (!host || prefab == null) return false;
            try { ValidateValues(prefab, new ItemValues(item.Value1, item.Value2, item.IntValue1, item.IntValue2)); return true; }
            catch (InvalidDataException) { return false; }
        }
        internal ulong HostRegisterNativeOutput(InteractableItems item, uint peer, bool held)
        {
            if (!host || Placed(item) || !CanRegisterNativeOutput(item)) return 0;
            if (byItem.TryGetValue(item, out var existing)) return EnsureNativeOutputRecord(existing) ? existing.Id : 0;
            var prefab = Supported(item)!;
            // The accepted root removal is the native hand pickup. Only these two vetted
            // subtypes record that permanent event; detached children have no pickup event.
            if (held && (item is Part_Suspension_Hub_Stake || item is Part_Spoiler)) item.IntValue2 = 1;
            item.transform.SetParent(null, true);
            // Child detaches stay loose. A removal's hand lease cannot displace another held item.
            uint owner = held && !PeerHandOccupied(peer) ? peer : ItemLease.Unowned;
            try
            {
                var binding = Register(item, prefab, owner);
                ConfigureBody(binding, false); Publish(binding);
                return EnsureNativeOutputRecord(binding) ? binding.Id : 0;
            }
            catch (Exception ex)
            {
                // Registration precedes native retirement. A later presentation callback cannot
                // turn that durable identity into a denied output or cause replay to allocate again.
                Debug.LogException(ex);
                return byItem.TryGetValue(item, out var committed) && EnsureNativeOutputRecord(committed) ? committed.Id : 0;
            }
        }
        private bool EnsureNativeOutputRecord(Binding binding)
        {
            if (channel == null || !binding.State.Exists) return false;
            if (!channel.Records.ContainsKey(binding.Id))
                channel.Register(new WorldRecord(binding.Id, 1, ItemState.Adapter, binding.State.Encode()));
            return channel.Records.TryGetValue(binding.Id, out var record) && record.Adapter == ItemState.Adapter && ItemState.Decode(record.Payload).Exists;
        }
        internal void CompleteNativeOutput(ulong id, InteractableItems item)
        {
            ItemChanged(item);
            if (host || id <= WorldRecord.DynamicIdStart || disposed) return;
            pendingNativeOutputs[id] = item;
            baseline.Remove(item); ignored.Remove(item); hidden.Remove(item);
            if (channel != null && channel.Records.TryGetValue(id, out var record)) BindNativeOutput(id, Decode(record));
        }
        private bool BindNativeOutput(ulong id, ItemState state)
        {
            if (!pendingNativeOutputs.TryGetValue(id, out var item)) return false;
            pendingNativeOutputs.Remove(id); deferredNativeOutputSpawns.Remove(id);
            if (item == null) return false;
            if (!state.Exists || Supported(item)?.Id != state.Prefab)
            { CancelLocal(item); ignored.Add(item); item.gameObject.SetActive(false); Object.Destroy(item.gameObject); return false; }
            // A reused physical part must leave its consumed input identity behind.
            // Otherwise a late input tombstone can retire the newly removed output too.
            if (byItem.TryGetValue(item, out var previous) && previous.Id != id)
            {
                previous.Absorbed = true; identities.Unbind(previous.Id);
                byItem.Remove(item); previous.Item = null;
            }
            PrepareNativeOutput(item);
            if (!bindings.TryGetValue(id, out var binding))
            {
                binding = new Binding { Id = id, Prefab = state.Prefab, State = state };
                bindings.Add(id, binding); ObserveBinding(binding);
            }
            var replica = binding.Item;
            if (replica != null && !ReferenceEquals(replica, item))
            {
                CancelLocal(replica); byItem.Remove(replica); ignored.Add(replica);
                replica.gameObject.SetActive(false); Object.Destroy(replica.gameObject);
            }
            identities.Unbind(id); binding.Absorbed = false; binding.Item = item; binding.State = state;
            byItem[item] = binding; identities.Bind(id, ItemState.Adapter, item.gameObject, prefabs[state.Prefab].Path, false);
            if (OwnedLocally(binding) && LocalHeld(item))
            {
                // Preserve native hand placement; permanent pickup history is already canonical.
                ApplyValues(item, state.Values, force: true);
                ConfigureBody(binding, true); Request(binding, ItemAction.Move);
            }
            else
            {
                if (OwnedLocally(binding)) { binding.Releasing = true; binding.HasTarget = false; Request(binding, ItemAction.Drop); }
                Apply(id, state, true);
            }
            return true;
        }
        private void PrepareNativeOutput(InteractableItems item)
        {
            // An existing authoritative item record describes a loose output, even if
            // a late native callback left its original part/bolt marked as installed.
            if (item is InteractableParts part)
            {
                part.TempPlaced = false;
                if (part.isPlaced) part.UpdatePartPlacement(false);
            }
            else if (item is Hardware_Bolt bolt) bolt.isPlaced = false;
            if (!LocalHeld(item)) item.transform.SetParent(null, true);
            item.CreateBasics();
            if (item.itemCollider != null) item.itemCollider.enabled = true;
        }
        private bool NativeRemovalPending => MonMPPlugin.Instance?.World?.VehicleParts.HasPendingNativeRemoval == true ||
            MonMPPlugin.Instance?.World?.Bolts.HasPendingNativeRemoval == true;
        private bool HasPendingNativeOutput(InteractableItems item)
        {
            if (pendingNativeOutputs.Count == 0) return false;
            foreach (var pending in pendingNativeOutputs)
                if (ReferenceEquals(pending.Value, item)) return true;
            return false;
        }
        private bool IsPendingNativeOutput(InteractableItems item) => HasPendingNativeOutput(item) ||
            MonMPPlugin.Instance?.World?.VehicleParts.IsPartChangePending(item) == true || MonMPPlugin.Instance?.World?.Bolts.IsMountPending(item) == true;
        internal bool IsExactNativeOutputPending(InteractableItems item) => IsPackedAdoptionPending(item) || HasPendingNativeOutput(item) ||
            MonMPPlugin.Instance?.World?.VehicleParts.IsPartChangePending(item) == true || MonMPPlugin.Instance?.World?.Bolts.IsPendingRemovalOutput(item) == true;
        private bool ShouldDeferNativeOutput(ItemState state) => !host && NativeRemovalPending &&
            (typeof(InteractableParts).IsAssignableFrom(prefabs[state.Prefab].Type) || prefabs[state.Prefab].Type == typeof(Hardware_Bolt));
        private void ResolveNativeOutputs()
        {
            if (host || channel == null) return;
            foreach (var pair in pendingNativeOutputs.ToArray())
                if (channel.Records.TryGetValue(pair.Key, out var record)) BindNativeOutput(pair.Key, Decode(record));
            if (NativeRemovalPending) return;
            foreach (ulong id in deferredNativeOutputSpawns.ToArray())
            {
                deferredNativeOutputSpawns.Remove(id);
                if (channel.Records.TryGetValue(id, out var record)) Apply(id, Decode(record));
            }
        }
        private void DisposeNativeOutputs()
        {
            foreach (var item in pendingNativeOutputs.Values)
                if (item != null) { CancelLocal(item); ignored.Add(item); item.gameObject.SetActive(false); Object.Destroy(item.gameObject); }
            pendingNativeOutputs.Clear(); deferredNativeOutputSpawns.Clear();
        }
    }
}
