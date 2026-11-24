using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicleParts
    {
        private sealed class PendingPartChange
        {
            public VehiclePartChangeCommand Command;
            public InteractableParts? Part;
            public float RetryAt;
            public float Since;
        }
        private int heldBackApplyWarnings;
        private readonly Dictionary<ulong, PendingPartChange> pendingPartChanges = new Dictionary<ulong, PendingPartChange>();
        private readonly Dictionary<ulong, uint> partReplyRevisions = new Dictionary<ulong, uint>();
        private readonly Dictionary<(uint Peer, Guid Operation), (ulong Socket, VehiclePartChangeCommand Command, bool Accepted, ulong Output)> partChangeResults =
            new Dictionary<(uint, Guid), (ulong, VehiclePartChangeCommand, bool, ulong)>();
        private readonly Dictionary<(uint Peer, Guid Parent, ulong Socket), (VehiclePartState Before, ulong Output)> detachedPartOutputs =
            new Dictionary<(uint, Guid, ulong), (VehiclePartState, ulong)>();
        private ulong executingPartOutput;
        private uint? executingPartPeer;
        private Guid executingPartOperation;
        private Guid nativeRemovalOperation;
        private ulong nativeRemovalSocket;
        private sealed class NativeRemovalScope : IDisposable
        {
            private SharedVehicleParts? owner;
            private readonly Guid previousOperation;
            private readonly ulong previousSocket;
            internal NativeRemovalScope(SharedVehicleParts owner, ulong socket)
            {
                this.owner = owner; previousOperation = owner.nativeRemovalOperation; previousSocket = owner.nativeRemovalSocket;
                if (owner.nativeRemovalOperation == Guid.Empty) { owner.nativeRemovalOperation = Guid.NewGuid(); owner.nativeRemovalSocket = socket; }
            }
            public void Dispose()
            {
                if (owner == null) return;
                var operation = owner.nativeRemovalOperation;
                owner.nativeRemovalOperation = previousOperation; owner.nativeRemovalSocket = previousSocket;
                var done = owner; owner = null;
                if (previousOperation == Guid.Empty && operation != Guid.Empty) done.ReleaseOrphanedChildren(operation);
            }
        }
        internal IDisposable? BeginNativePartRemoval(Vehicle_PartSocket socket) => !host && !applying && Ready && bySocket.TryGetValue(socket, out var binding)
            ? new NativeRemovalScope(this, binding.Id) : null;

        internal bool IsPartChangePending(InteractableItems item)
        {
            if (pendingPartChanges.Count == 0) return false;
            foreach (var pending in pendingPartChanges)
                if (ReferenceEquals(pending.Value.Part, item)) return true;
            return false;
        }
        internal bool HasPendingNativeRemoval => pendingPartChanges.Values.Any(p => !p.Command.After.Installed);
        internal bool LocalFlushComplete => pendingPartChanges.Count == 0;
        internal bool AllowPartChange(Vehicle_PartSocket socket, InteractableParts? part = null)
        {
            if (applying || !Ready || !bySocket.ContainsKey(socket)) return true;
            var items = MonMPPlugin.Instance?.Items;
            if (channel?.FullySynchronized != true || items?.TransfersBlocked != false || !PartTreeReady(socket)) return false;
            if (part == null) return true;
            return !IsPartChangePending(part) && (host || items.TryGetLocalPartInstall(part, out _, out _));
        }

        private bool PartTreeReady(Vehicle_PartSocket socket)
        {
            if (bySocket.TryGetValue(socket, out var binding) && (pendingPartChanges.ContainsKey(binding.Id) ||
                partReplyRevisions.TryGetValue(binding.Id, out uint revision) && channel!.Records[binding.Id].Revision < revision)) return false;
            return DetachedChildren(socket).All(child => child == null || PartTreeReady(child));
        }

        internal bool AllowPartValues(InteractableParts part) => applying || !IsPartChangePending(part);

        private bool NativePartSourceMatches(Binding binding)
        {
            var native = Capture(binding);
            // Bolt state has its own adapter and can change without publishing this socket.
            // Compare every part identity/value; native removal eligibility checks actual bolts.
            return new VehiclePartState(native.Installed, native.Prefab, binding.State.FullyTightened,
                native.Value1, native.Value2, native.IntValue1, native.IntValue2).Equals(binding.State);
        }

        private void BeginPartChange(Binding binding, VehiclePartState state, InteractableParts? part)
        {
            if (pendingPartChanges.ContainsKey(binding.Id)) return;
            var items = MonMPPlugin.Instance?.Items;
            ulong item = 0; uint generation = 0;
            if (part == null || channel?.FullySynchronized != true || items == null || items.TransfersBlocked ||
                state.Installed && (!items.TryGetLocalPartInstall(part, out item, out generation) || !items.FlushLocalItemPose(part)))
            {
                RollbackPartChange(binding, state.Installed, item, part);
                MonMPPlugin.Instance?.ReportItemUseUnavailable("That part could not be shared yet. Wait for the host and try again.");
                return;
            }
            binding.Edits?.Clear();
            bool child = !state.Installed && nativeRemovalOperation != Guid.Empty && nativeRemovalSocket != binding.Id;
            Guid operation = !state.Installed && !child && nativeRemovalOperation != Guid.Empty ? nativeRemovalOperation : Guid.NewGuid();
            var command = new VehiclePartChangeCommand(operation, binding.State, state, item, generation,
                child ? nativeRemovalOperation : Guid.Empty, child ? nativeRemovalSocket : 0);
            pendingPartChanges[binding.Id] = new PendingPartChange { Command = command, Part = part, RetryAt = child ? float.PositiveInfinity : Time.unscaledTime + 5, Since = Time.unscaledTime };
            if (!child) channel.Request(binding.Id, command.Encode());
        }

        // Detached children wait (RetryAt = infinity) for their parent's reply. A parent that
        // never sent its request (rolled back, already pending, or a throwing native removal)
        // would leave them pending forever, and Apply skips pending sockets: that guest then
        // ignored every later host update to those parts. Restore them from the host records.
        private void ReleaseOrphanedChildren(Guid operation)
        {
            if (pendingPartChanges.Values.Any(p => p.Command.Operation == operation && p.Command.ParentOperation == Guid.Empty)) return;
            foreach (var child in pendingPartChanges.Where(p => p.Value.Command.ParentOperation == operation).ToArray())
            {
                pendingPartChanges.Remove(child.Key);
                if (bindings.TryGetValue(child.Key, out var binding)) RollbackPartChange(binding, false, 0, child.Value.Part);
            }
        }

        // A pending socket ignores host updates until its reply. The host answers every request,
        // so a long wait means this player's view of that part is stale: say so in the log.
        private bool HoldsBackApply(ulong id)
        {
            if (!pendingPartChanges.TryGetValue(id, out var pending)) return false;
            if (Time.unscaledTime - pending.Since > 15 && heldBackApplyWarnings < 8)
            {
                heldBackApplyWarnings++;
                Debug.LogWarning("MonMP vehicle part socket " + id.ToString("X16") + " is still waiting " +
                    (Time.unscaledTime - pending.Since).ToString("0") + " s for the host's reply (" + (pending.Command.ParentOperation == Guid.Empty ? "request" : "detached child") +
                    "); host updates to that part are held back until it arrives.");
            }
            return true;
        }

        private void UpdatePartChanges()
        {
            if (channel?.FullySynchronized != true) return;
            foreach (var pair in pendingPartChanges.Where(p => Time.unscaledTime >= p.Value.RetryAt).ToArray())
            {
                // The host answers every request, so 30 s of retries without a reply means this
                // player's view of that part is stuck (a König corner with no shock, bolts snapping
                // back) until a reconnect. Resynchronize it from the host records instead; a late
                // reply finds no pending operation and is ignored.
                if (Time.unscaledTime - pair.Value.Since > UnansweredPartChangeSeconds)
                {
                    AbandonPartChange(pair.Key, pair.Value);
                    continue;
                }
                // The bounded same-operation replay only asks for the saved outcome. It cannot
                // consume an input or authorize an output a second time, including on a stale socket.
                pair.Value.RetryAt = Time.unscaledTime + 5;
                channel.Request(pair.Key, pair.Value.Command.Encode());
            }
        }

        private const float UnansweredPartChangeSeconds = 30;

        private void AbandonPartChange(ulong id, PendingPartChange pending)
        {
            if (!pendingPartChanges.Remove(id)) return;
            Debug.LogWarning("MonMP vehicle part socket " + id.ToString("X16") + " got no host reply in " +
                UnansweredPartChangeSeconds.ToString("0") + " s; restoring it from the host's state.");
            if (bindings.TryGetValue(id, out var binding)) RollbackPartChange(binding, pending.Command.After.Installed, pending.Command.Item, pending.Part);
            foreach (var child in pendingPartChanges.Where(p => p.Value.Command.ParentOperation == pending.Command.Operation).ToArray())
            {
                pendingPartChanges.Remove(child.Key);
                if (bindings.TryGetValue(child.Key, out var childBinding)) RollbackPartChange(childBinding, false, 0, child.Value.Part);
            }
        }

        private void PartChange(uint peer, Binding binding, VehiclePartChangeCommand request, bool stale)
        {
            var key = (peer, request.Operation);
            if (partChangeResults.TryGetValue(key, out var prior))
            {
                bool matches = prior.Socket == binding.Id && prior.Command.Equals(request) && prior.Accepted;
                ReplyPartChange(peer, binding, request.Operation, matches, matches ? prior.Output : 0);
                return;
            }
            if (partChangeResults.Keys.Count(k => k.Peer == peer) >= 1024)
            { ReplyPartChange(peer, binding, request.Operation, false); return; }
            bool accepted = false;
            ulong output = 0;
            var items = MonMPPlugin.Instance?.Items;
            // Record the operation before native mutation; repeats cannot reach the mutation path.
            partChangeResults[key] = (binding.Id, request, false, 0);
            if (!request.After.Installed && request.ParentOperation != Guid.Empty &&
                partChangeResults.TryGetValue((peer, request.ParentOperation), out var parent) && parent.Socket == request.ParentSocket && parent.Accepted &&
                detachedPartOutputs.TryGetValue((peer, request.ParentOperation, binding.Id), out var detached) && detached.Before.Equals(request.Before))
            {
                // The parent already authorized this exact detached source. Claiming it never
                // mutates the socket, so delayed receipts and later slot reuse cannot lose it.
                detachedPartOutputs.Remove((peer, request.ParentOperation, binding.Id)); output = detached.Output; accepted = output != 0;
            }
            else if (channel?.FullySynchronized == true && items?.TransfersBlocked == false && PartSocketAvailable(binding, request.After.Installed) &&
                items.HostPeerNear(peer, binding.Socket.gameobject.transform.position, 4.5f))
            {
                if (request.ParentOperation == Guid.Empty && !stale && NativePartSourceMatches(binding) && binding.State.Equals(request.Before) &&
                    TryResolvePart(binding, request.After, out var asset, out bool rack) &&
                    VehiclePartCommandPolicy.IsValidTransition(binding.State, request.After, rack, IsWheelBinding(binding),
                        binding.Socket.boltHolesDetails.Length == 0, MountedValuesKind(request.After.Installed ? request.After : binding.State),
                        MountedOptionCount(request.After.Installed ? asset?.GetComponent<InteractableParts>() : binding.Socket.partInstalled)))
                {
                    uint? previousPeer = executingPartPeer; Guid previousOperation = executingPartOperation; bool previousApplying = applying; ulong previousOutput = executingPartOutput;
                    executingPartPeer = peer; executingPartOperation = request.Operation; executingPartOutput = 0; applying = true;
                    try
                    {
                        if (request.After.Installed)
                            accepted = items.HostInstallPart(peer, request.Item, request.Generation, binding.Socket,
                                part => ValidPartInstall(binding, part, request.After));
                        else if (binding.Socket.CheckIfCanToRemoved() && NativeOutputsSupported(binding.Socket, items))
                        { ApplyToSocket(binding, request.After, asset, rack); output = executingPartOutput; accepted = output != 0; }
                    }
                    catch (Exception ex)
                    {
                        // Some native callbacks run after the socket has committed. Preserve that
                        // outcome and finish its original loose-item identity before replying.
                        accepted = Capture(binding).Equals(request.After);
                        if (accepted && request.After.Installed) accepted = items.HostFinalizePartInstall(peer, request.Item, request.Generation, binding.Socket);
                        if (accepted && !request.After.Installed) { output = executingPartOutput; accepted = output != 0; }
                        Debug.LogException(ex);
                    }
                    finally { applying = previousApplying; executingPartPeer = previousPeer; executingPartOperation = previousOperation; executingPartOutput = previousOutput; }
                    if (accepted)
                    {
                        partChangeResults[key] = (binding.Id, request, true, output);
                        binding.State = Capture(binding);
                        channel.Publish(binding.Id, binding.State.Encode());
                        foreach (var child in bindings.Values)
                        {
                            var childState = Capture(child);
                            if (childState.Equals(child.State)) continue;
                            child.State = childState; channel.Publish(child.Id, childState.Encode());
                        }
                    }
                }
            }
            partChangeResults[key] = (binding.Id, request, accepted, output);
            ReplyPartChange(peer, binding, request.Operation, accepted, accepted ? output : 0);
        }

        private static bool NativeOutputsSupported(Vehicle_PartSocket socket, SharedItems items) =>
            (!socket.isInstalled || socket.partInstalled != null && items.CanRegisterNativeOutput(socket.partInstalled)) &&
            DetachedChildren(socket).All(child => child == null || NativeOutputsSupported(child, items));

        private static bool PartSocketAvailable(Binding binding, bool installing)
        {
            var socket = binding.Socket;
            // Native hides empty sockets until this process's local player prepares
            // that part type. A remote install must not depend on the host's hands.
            return socket.gameobject != null && socket.gameobject.scene.IsValid() && socket.gameobject.scene.isLoaded &&
                socket.vehicleAttached != null && socket.vehicleAttached.gameObject.activeInHierarchy &&
                (installing || socket.gameobject.activeInHierarchy);
        }

        private void RetireNativePart(InteractableParts part)
        {
            if (host && executingPartPeer.HasValue)
            { executingPartOutput = MonMPPlugin.Instance!.Items!.HostRegisterNativeOutput(part, executingPartPeer.Value, true); return; }
            part.gameObject.SetActive(false); UnityEngine.Object.Destroy(part.gameObject);
        }

        private bool ValidPartInstall(Binding binding, InteractableParts part, VehiclePartState after)
        {
            if (part.car_PartsType != binding.Socket.car_PartsType) return false;
            var native = CapturePart(binding, part, binding.Socket.boltHolesDetails.Length == 0);
            if (native.Equals(after)) return true;
            var kind = MountedValuesKind(part);
            if ((kind == MountedVehiclePartKind.StakePickupHistory || kind == MountedVehiclePartKind.SpoilerPickupHistory) &&
                native.IntValue2 == 0 && after.IntValue2 == 1 &&
                new VehiclePartState(true, native.Prefab, native.FullyTightened, native.Value1, native.Value2, native.IntValue1, 1).Equals(after))
            { part.IntValue2 = 1; return true; } // exact leased pickup carries the native permanent history bit
            return false;
        }

        private void ReplyPartChange(uint peer, Binding binding, Guid operation, bool accepted, ulong output = 0)
        {
            var record = channel!.Records[binding.Id];
            channel.Reply(peer, binding.Id, new VehiclePartChangeReply(operation, accepted, record.Revision, VehiclePartState.Decode(record.Payload), output).Encode());
        }

        private void PartChangeReply(ulong id, VehiclePartChangeReply reply)
        {
            if (!pendingPartChanges.TryGetValue(id, out var pending) || pending.Command.Operation != reply.Operation || !bindings.TryGetValue(id, out var binding)) return;
            var state = reply.State;
            if (channel!.Records.TryGetValue(id, out var record) && record.Revision >= reply.Revision) state = VehiclePartState.Decode(record.Payload);
            pendingPartChanges.Remove(id);
            partReplyRevisions[id] = reply.Revision;
            if (!reply.Accepted) RollbackPartChange(binding, pending.Command.After.Installed, pending.Command.Item, pending.Part);
            else if (pending.Part != null)
            {
                if (pending.Command.After.Installed) MonMPPlugin.Instance?.Items?.CompleteLocalPartInstall(pending.Part);
                else MonMPPlugin.Instance?.Items?.CompleteNativeOutput(reply.Output, pending.Part);
            }
            if (TryResolvePart(binding, state, out var asset, out bool rack)) Apply(binding, state, asset, rack);
            foreach (var child in pendingPartChanges.Where(p => p.Value.Command.ParentOperation == reply.Operation).ToArray())
            {
                if (reply.Accepted)
                { child.Value.RetryAt = Time.unscaledTime + 5; channel.Request(child.Key, child.Value.Command.Encode()); }
                else
                { pendingPartChanges.Remove(child.Key); RollbackPartChange(bindings[child.Key], false, 0, child.Value.Part); }
            }
        }

        private void RollbackPartChange(Binding binding, bool installation, ulong item, InteractableParts? part)
        {
            bool previous = applying; applying = true;
            try
            {
                if (installation && ReferenceEquals(binding.Socket.partInstalled, part) && binding.Socket.isInstalled) binding.Socket.RemovePart();
                if (part != null)
                {
                    if (installation) MonMPPlugin.Instance?.Items?.RejectPartInstall(item, part);
                    else MonMPPlugin.Instance?.Items?.DiscardNativePart(part);
                }
                if (channel != null && channel.Records.TryGetValue(binding.Id, out var record))
                {
                    var state = VehiclePartState.Decode(record.Payload);
                    if (TryResolvePart(binding, state, out var asset, out bool rack)) Apply(binding, state, asset, rack);
                }
            }
            finally { applying = previous; }
        }

        private void PartPeerLeft(uint peer)
        {
            foreach (var key in partChangeResults.Keys.Where(k => k.Peer == peer).ToArray()) partChangeResults.Remove(key);
            foreach (var key in detachedPartOutputs.Keys.Where(k => k.Peer == peer).ToArray()) detachedPartOutputs.Remove(key);
        }

        private void DisposePartChanges()
        {
            foreach (var pair in pendingPartChanges)
                if (pair.Value.Part != null && !pair.Value.Command.After.Installed) MonMPPlugin.Instance?.Items?.DiscardNativePart(pair.Value.Part);
            pendingPartChanges.Clear(); partReplyRevisions.Clear(); partChangeResults.Clear(); detachedPartOutputs.Clear(); executingPartPeer = null;
            executingPartOperation = nativeRemovalOperation = Guid.Empty; nativeRemovalSocket = 0;
        }
    }
}
