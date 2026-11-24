using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        // Accepted nonces cannot be evicted while the same connection can retry them.
        // Dynamic world capacity bounds receipts; disconnect discards only connection metadata.
        private readonly Dictionary<(uint Peer, uint Nonce), (ushort Prefab, ulong Entity)> adoptionReceipts =
            new Dictionary<(uint, uint), (ushort, ulong)>();
        private readonly Dictionary<uint, (ulong Pack, PackedItemSpawnRequest Request)> packedAdoptions =
            new Dictionary<uint, (ulong, PackedItemSpawnRequest)>();
        private readonly Dictionary<(uint Peer, uint Nonce), (ulong Pack, uint Generation, ushort Prefab, bool Hold, PackedItemSpawnReply Reply)> packedAdoptionReceipts =
            new Dictionary<(uint, uint), (ulong, uint, ushort, bool, PackedItemSpawnReply)>();
        private const int MaxPendingPackedOutputs = 64;
        private float nextAdoptionRetry;
        internal bool IsAdoptionPending(InteractableItems? item) => item != null && adoptionCandidates.Contains(item);
        private bool IsPackedAdoptionPending(InteractableItems item) =>
            adopting.TryGetValue(item, out var pending) && packedAdoptions.ContainsKey(pending.Nonce);

        private bool QueuePackedOutput(Binding pack, InteractableItems output)
        {
            if (byItem.ContainsKey(output) || adopting.ContainsKey(output)) return true;
            var prefab = Supported(output);
            if (prefab == null || packedAdoptions.Count >= MaxPendingPackedOutputs) return false;
            var template = (pack.Item as InteractablePacks)?.ObjectToSpawn?.GetComponent<InteractableItems>();
            if (template == null || Supported(template)?.Id != prefab.Id) return false;
            uint nonce = ++nextNonce; if (nonce == 0) nonce = ++nextNonce;
            var request = new PackedItemSpawnRequest(nonce, pack.State.Lease.Generation, prefab.Id, true,
                Pose(pack.Item!.transform), Pose(output.transform));
            // Register the exact prediction before sending: even a synchronous reply can bind it.
            adoptionCandidates.Add(output); adopting.Add(output, (nonce, Time.unscaledTime));
            adoptNonces.Add(nonce, output); adoptHeld.Add(nonce, true);
            packedAdoptions.Add(nonce, (pack.Id, request));
            channel!.Request(pack.Id, request.Encode(), true);
            return true;
        }

        private bool TryRegisterPackedOutput(InteractablePacks pack, ItemPose pose, out Binding? output)
        {
            output = null;
            if (channel == null || channel.Records.Count >= SnapshotReceiver.MaxEntities || pack.ObjectToSpawn == null) return false;
            var template = pack.ObjectToSpawn.GetComponent<InteractableItems>();
            var prefab = template == null ? null : Supported(template);
            if (prefab == null || !(General(prefab) || ItemPrefabCatalog.Simple(prefab.Type))) return false;
            InteractableItems? item = null;
            try
            {
                var go = Object.Instantiate(pack.ObjectToSpawn, Position(pose), Rotation(pose));
                item = go.GetComponent<InteractableItems>();
                // Preserve exact native variants, including a bolt box's color/saved-spawn flag.
                pack.EditSpawnedObject(go); item.CreateBasics();
                ulong id = HostRegisterNativeOutput(item, ItemLease.Unowned, held: false);
                if (id == 0) return false;
                output = bindings[id]; return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                // Presentation can fail after registration. That durable output must still be paid
                // for once; a later retry must not instantiate another one.
                if (item != null && byItem.TryGetValue(item, out var committed) &&
                    channel.Records.TryGetValue(committed.Id, out var record) && record.Adapter == ItemState.Adapter && ItemState.Decode(record.Payload).Exists)
                { output = committed; return true; }
                return false;
            }
            finally
            {
                if (output == null && item != null)
                {
                    if (byItem.TryGetValue(item, out var failed))
                    {
                        identities.Unbind(failed.Id); byItem.Remove(item); bindings.Remove(failed.Id); leases.Remove(failed.Id);
                    }
                    ignored.Add(item); item.gameObject.SetActive(false); Object.Destroy(item.gameObject);
                }
            }
        }

        private void PackedOutputCommand(uint peer, ulong packId, PackedItemSpawnRequest request)
        {
            if (peer == 0) return;
            var key = (peer, request.Nonce);
            if (packedAdoptionReceipts.TryGetValue(key, out var old))
            {
                var reply = old.Pack == packId && old.Generation == request.Generation && old.Prefab == request.Prefab && old.Hold == request.Hold ?
                    old.Reply : new PackedItemSpawnReply(request.Nonce, PackedItemSpawnStatus.Rejected);
                channel!.Reply(peer, packId, reply.Encode()); return;
            }
            bool capacity = packedAdoptionReceipts.Count + adoptionReceipts.Count < SnapshotReceiver.MaxEntities;
            bindings.TryGetValue(packId, out var binding);
            var pack = binding?.Item as InteractablePacks;
            var template = pack?.ObjectToSpawn == null ? null : pack.ObjectToSpawn.GetComponent<InteractableItems>();
            var prefab = template == null ? null : Supported(template);
            if (!capacity || TransfersBlocked || pack == null || prefab == null || prefab.Id != request.Prefab || pack.Value1 < 1 ||
                !leases.CanAct(packId, peer, request.Generation) || !InRange(peer, Position(request.PackPose), 4.5f) ||
                !InRange(peer, Position(request.OutputPose), 6))
            { RejectPackedOutput(peer, packId, request, binding, capacity); return; }
            float now = Time.unscaledTime;
            var budget = adoptBudget.TryGetValue(peer, out var previous) ?
                (Tokens: Math.Min(10, previous.Tokens + (now - previous.Time) * 4), Time: now) : (Tokens: 10f, Time: now);
            if (budget.Tokens < 1)
            {
                adoptBudget[peer] = budget;
                channel!.Reply(peer, packId, new PackedItemSpawnReply(request.Nonce, PackedItemSpawnStatus.Retry).Encode()); return;
            }
            if (!TryRegisterPackedOutput(pack, request.OutputPose, out var output))
            { RejectPackedOutput(peer, packId, request, binding, true); return; }
            adoptBudget[peer] = (budget.Tokens - 1, now);
            // No debit is committed before the corresponding output has a durable host record.
            pack.transform.SetPositionAndRotation(Position(request.PackPose), Rotation(request.PackPose));
            if (pack.rb != null) { pack.rb.position = Position(request.PackPose); pack.rb.rotation = Rotation(request.PackPose); }
            applying = true;
            try { pack.Value1 -= 1; pack.LastTimeItemUnpack = Time.realtimeSinceStartup; UpdatePack.Invoke(pack, null); }
            catch (Exception ex) { Debug.LogException(ex); }
            finally { applying = false; }
            binding!.HasTarget = false; binding.Settled = true; binding.LastOwnerUpdate = now;
            ReleaseLease(packId, peer, request.Generation);
            if (request.Hold) AcquireLease(output!.Id, peer, leases[output.Id].Generation, 2);
            var accepted = new PackedItemSpawnReply(request.Nonce, PackedItemSpawnStatus.Accepted, output!.Id);
            packedAdoptionReceipts.Add(key, (packId, request.Generation, request.Prefab, request.Hold, accepted));
            binding.State = Capture(binding);
            if (pack.DestroyWhenEmpty && pack.Value1 <= 0)
            {
                identities.Unbind(packId); byItem.Remove(pack); binding.Item = null;
                pack.gameObject.SetActive(false); Object.Destroy(pack.gameObject);
            }
            // Publication/presentation cannot turn an accepted debit into a replayable operation.
            try { output.State = Capture(output); ConfigureBody(output, false); Publish(output); } catch (Exception ex) { Debug.LogException(ex); }
            try { if (binding.Item != null) ConfigureBody(binding, false); Publish(binding); } catch (Exception ex) { Debug.LogException(ex); }
            channel!.Reply(peer, packId, accepted.Encode());
        }

        private void RejectPackedOutput(uint peer, ulong pack, PackedItemSpawnRequest request, Binding? binding, bool remember)
        {
            var reply = new PackedItemSpawnReply(request.Nonce, PackedItemSpawnStatus.Rejected);
            if (remember) packedAdoptionReceipts.Add((peer, request.Nonce), (pack, request.Generation, request.Prefab, request.Hold, reply));
            if (binding != null)
            {
                // Even an unchanged count needs a fresh lease revision so the guest's predicted
                // decrement cannot outlive a no-op publication. Never release a newer owner.
                if (leases.CanAct(pack, peer, request.Generation)) ReleaseLease(pack, peer, request.Generation);
                if (binding.Item != null) ConfigureBody(binding, false);
                Publish(binding);
            }
            channel!.Reply(peer, pack, reply.Encode());
        }

        private bool PackedOutputReply(ref ulong entity, ref byte[] payload)
        {
            if (!PackedItemSpawnReply.Is(payload)) return false;
            var reply = PackedItemSpawnReply.Decode(payload);
            if (!packedAdoptions.TryGetValue(reply.Nonce, out var pending) || pending.Pack != entity) return true;
            if (reply.Status == PackedItemSpawnStatus.Retry) return true;
            packedAdoptions.Remove(reply.Nonce);
            if (reply.Status == PackedItemSpawnStatus.Accepted)
            {
                entity = descriptorByPrefab[pending.Request.Prefab]; payload = new ItemSpawnReply(reply.Nonce, reply.Entity).Encode();
                return false;
            }
            if (adoptNonces.TryGetValue(reply.Nonce, out var item))
            {
                adoptNonces.Remove(reply.Nonce); adopting.Remove(item); adoptionCandidates.Remove(item); adoptHeld.Remove(reply.Nonce);
                if (item != null) { CancelLocal(item); ignored.Add(item); item.gameObject.SetActive(false); Object.Destroy(item.gameObject); }
            }
            if (bindings.TryGetValue(pending.Pack, out var pack))
            {
                pack.Releasing = false; pack.HasTarget = false; pack.PendingUse = false;
                Apply(pack.Id, pack.State, true);
            }
            MonMPPlugin.Instance?.ReportItemUseUnavailable("The host could not unpack that pack. Its contents were kept; try again.");
            return true;
        }

        private void RetryAdoptions(float now)
        {
            foreach (var pair in adopting.Where(pair => pair.Key == null).ToArray())
            {
                // A native transient may disappear independently. Pack operations still need their
                // receipt, so retain their nonce even if the predicted presentation has ended.
                if (packedAdoptions.ContainsKey(pair.Value.Nonce)) continue;
                adopting.Remove(pair.Key); adoptNonces.Remove(pair.Value.Nonce); adoptionRequests.Remove(pair.Value.Nonce);
                adoptHeld.Remove(pair.Value.Nonce); fishingTrashNonces.Remove(pair.Value.Nonce); adoptionCandidates.Remove(pair.Key);
            }
            adoptionCandidates.RemoveWhere(item => item == null);
            if (now < nextAdoptionRetry) return;
            foreach (var pair in adopting.OrderBy(pair => pair.Value.Time).ToArray())
            {
                if (pair.Key is null) continue;
                uint nonce = pair.Value.Nonce;
                if (now - pair.Value.Time < 0.5f) continue;
                ulong descriptor; byte[] payload; bool packed = packedAdoptions.TryGetValue(nonce, out var pendingPack);
                if (packed)
                {
                    var original = pendingPack.Request;
                    var pose = pair.Key == null ? original.OutputPose : Pose(pair.Key.transform);
                    payload = new PackedItemSpawnRequest(nonce, original.Generation, original.Prefab, original.Hold, original.PackPose, pose).Encode();
                    descriptor = pendingPack.Pack;
                }
                else
                {
                    if (pair.Key == null) continue;
                    if (TransfersBlocked && !NativeOutputDrainAllowed || !adoptionRequests.TryGetValue(nonce, out var pending)) continue;
                    var original = pending.Request;
                    var spawn = new ItemSpawnRequest(nonce, original.Hold, Pose(pair.Key.transform), original.Values);
                    payload = spawn.Encode(); descriptor = descriptorByPrefab[pending.Prefab];
                    if (fishingTrashNonces.TryGetValue(nonce, out var origin))
                        payload = new FishingTrashSpawnRequest(origin.Rod, origin.Generation, origin.Ticket, origin.Template, spawn, origin.RemainingMilliseconds).Encode();
                }
                // Destroyed Unity objects still have a CLR identity; an actual null is never a
                // valid dictionary key. Keep the former's pack receipt retry without dereferencing it.
                if (pair.Key is null) continue;
                adopting[pair.Key] = (nonce, now); nextAdoptionRetry = now + 0.25f;
                channel!.Request(descriptor, payload, packed); break;
            }
        }

        private void ReleasePeerAdoptions(uint peer)
        {
            adoptBudget.Remove(peer);
            foreach (var key in adoptionReceipts.Keys.Where(key => key.Peer == peer).ToArray()) adoptionReceipts.Remove(key);
            foreach (var key in packedAdoptionReceipts.Keys.Where(key => key.Peer == peer).ToArray()) packedAdoptionReceipts.Remove(key);
        }
    }
}
