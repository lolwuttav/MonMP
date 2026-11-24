using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Fence save values already travel in ItemState. This adapter runs the native one-shot
    // mutations on the host and keeps fence collider, body and mesh presentation in sync.
    internal sealed class SharedFenceActions : IDisposable
    {
        private static readonly FieldInfo TemporaryPlacement = AccessTools.Field(typeof(Tool_Fence), "isTempPlaced")!;
        private static readonly FieldInfo Crosshair = AccessTools.Field(typeof(Interactable), "ui_Crosshair")!;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly bool host;
        private readonly FenceActionClaims claims = new FenceActionClaims();
        private readonly Dictionary<(uint Peer, Guid Operation), FenceActionReply> results = new Dictionary<(uint, Guid), FenceActionReply>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly Dictionary<ulong, (uint Peer, Tool_Fence Fence, float Started)> removals = new Dictionary<ulong, (uint, Tool_Fence, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        private const float CommandTimeout = 4f;
        private const float RemovalSeconds = 0.8f;
        private Tool_Fence? localRemoval;
        private UI_Crosshair? localCrosshair;
        private Guid localRemovalOperation;
        private float localRemovalElapsed;
        private bool localRemovalAccepted;

        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0 && removals.Count == 0;

#if MONMP_DEVELOPMENT
        internal int TestTooFarRejections { get; private set; }
#endif


        internal SharedFenceActions(Gameplay gameplay, bool host, SharedItems items)
        { this.gameplay = gameplay; this.host = host; this.items = items; }

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.CommandRequested += Command; world.ReplyReceived += Reply;
            connected.PeerLeft += PeerLeft;
        }

        internal bool GuestAction(Tool_Fence fence, FenceAction action)
        {
            var connected = session;
            if (action == FenceAction.CancelRemoval) StopLocalRemoval();
            if (host || disposed || connected?.IsRunning != true || fence == null) return false;
            if (channel?.FullySynchronized != true || !items.Ready)
            { Report("Wait for the shared world to finish loading before changing this fence."); return true; }
            ulong id = items.EntityOf(fence);
            if (id == 0) { Report("This fence is not registered in the shared world."); return true; }
            uint generation = 0;
            if (action == FenceAction.Install)
            {
                if (!items.TryGetLocalToolLease(fence, out ulong leasedId, out generation) || leasedId != id)
                { Report("Equip the shared fence before installing it."); return true; }
            }
            else if (action != FenceAction.CancelRemoval && fence.IntValue1 != 1)
            { Report("This fence is not installed."); return true; }
            var request = new FenceActionCommand(Guid.NewGuid(), action, generation, Pose(fence.transform));
            if (action == FenceAction.BeginRemoval)
            {
                StopLocalRemoval();
                localRemoval = fence; localRemovalOperation = request.Operation;
                localCrosshair = Crosshair.GetValue(fence) as UI_Crosshair;
            }
            pending[request.Operation] = Time.unscaledTime + CommandTimeout;
            channel!.Request(id, request.Encode(), true);
            return true;
        }

        internal void Update()
        {
            if (disposed) return;
            if (pending.Count > 0) foreach (var pair in pending.Where(value => Time.unscaledTime >= value.Value).ToArray())
            {
                pending.Remove(pair.Key);
                if (pair.Key == localRemovalOperation) StopLocalRemoval();
                Report("The host did not respond to the fence action.");
            }
            if (!host) { UpdateLocalRemoval(); return; }
            foreach (var pair in removals.ToArray())
            {
                if (pair.Value.Fence == null) { removals.Remove(pair.Key); continue; }
                if (pair.Value.Fence.IntValue1 != 0) continue;
                removals.Remove(pair.Key);
                items.HostPublishItem(pair.Key);
            }
        }

        [MonMP.Core.World.WorldCommandAdapters(ItemState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !FenceActionCommand.Is(payload) || channel == null || session == null) return;
            FenceActionCommand request;
            try { request = FenceActionCommand.Decode(payload); }
            catch (InvalidDataException) { return; }
            var key = (peer, request.Operation);
            var claim = claims.Claim(peer, request.Operation);
            if (claim == FenceActionClaim.Duplicate)
            {
                if (results.TryGetValue(key, out var previous) && session.Peers.ContainsKey(peer)) channel.Reply(peer, id, previous.Encode());
                return;
            }
            var status = claim == FenceActionClaim.Capacity ? FenceActionStatus.Capacity : Execute(peer, id, request);
            if (status == FenceActionStatus.TooFar)
#if MONMP_DEVELOPMENT
TestTooFarRejections++;
#else
{}
#endif

            var reply = new FenceActionReply(request.Operation, request.Action, status);
            results[key] = reply;
            if (session.Peers.ContainsKey(peer)) channel.Reply(peer, id, reply.Encode());
        }

        private FenceActionStatus Execute(uint peer, ulong id, FenceActionCommand request)
        {
            if (channel?.FullySynchronized != true ||
                session?.Peers.ContainsKey(peer) != true || items.ItemOf(id) is not Tool_Fence fence || items.StateOf(id)?.Exists != true)
                return FenceActionStatus.NotReady;
            // Releases drain accepted work, including when save collection has blocked
            // new actions. Match the existing actor before changing the native hold.
            if (request.Action == FenceAction.CancelRemoval)
            {
                if (!removals.TryGetValue(id, out var active)) return FenceActionStatus.Accepted;
                if (active.Peer != peer) return FenceActionStatus.Busy;
                fence.MouseRightUp();
                removals.Remove(id);
                return FenceActionStatus.Accepted;
            }
            if (NewActionsBlocked || items.TransfersBlocked) return FenceActionStatus.NotReady;
            Vector3 requestedPosition = Position(request.Pose);
            if (!items.HostPeerNear(peer, requestedPosition, 4.5f)) return FenceActionStatus.TooFar;

            switch (request.Action)
            {
                case FenceAction.Install:
                    if (fence.IntValue1 == 1 || !items.HostPeerNear(peer, requestedPosition, 4.5f))
                        return FenceActionStatus.NotReady;
                    if (!gameplay.IsOnHomeLand(requestedPosition)) return FenceActionStatus.NotOnHomeLand;
                    if (!items.HostSetLeasedToolPose(peer, id, request.Generation, typeof(Tool_Fence), request.Pose))
                        return FenceActionStatus.NotReady;
                    if (!items.HostMutateLeasedTool(peer, id, request.Generation, typeof(Tool_Fence), native =>
                    {
                        var tool = (Tool_Fence)native;
                        tool.isEquiped = true;
                        TemporaryPlacement.SetValue(tool, true);
                        tool.MouseDown();
                        return tool.IntValue1 == 1;
                    }, releaseOnSuccess: true)) return FenceActionStatus.NotReady;
                    return FenceActionStatus.Accepted;

                case FenceAction.ChangeStyle:
                    if (fence.IntValue1 != 1) return FenceActionStatus.NotInstalled;
                    if (Vector3.Distance(requestedPosition, fence.transform.position) > 1.5f) return FenceActionStatus.TooFar;
                    int oldStyle = fence.IntValue2;
                    fence.Use();
                    if (fence.IntValue2 == oldStyle) return FenceActionStatus.NotReady;
                    items.HostPublishItem(id);
                    return FenceActionStatus.Accepted;

                case FenceAction.BeginRemoval:
                    if (fence.IntValue1 != 1) return FenceActionStatus.NotInstalled;
                    if (Vector3.Distance(requestedPosition, fence.transform.position) > 1.5f) return FenceActionStatus.TooFar;
                    if (removals.TryGetValue(id, out var existing))
                        return existing.Peer == peer ? FenceActionStatus.Accepted : FenceActionStatus.Busy;
                    removals[id] = (peer, fence, Time.unscaledTime);
                    // StartCoroutine advances synchronously to its first yield. Register the
                    // remote hold first so that even its first crosshair write is suppressed.
                    try { fence.MouseRightDown(); }
                    catch { removals.Remove(id); throw; }
                    return FenceActionStatus.Accepted;

                default:
                    return FenceActionStatus.NotReady;
            }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || !FenceActionReply.Is(payload)) return;
            FenceActionReply reply;
            try { reply = FenceActionReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!pending.Remove(reply.Operation)) return;
            if (reply.Operation == localRemovalOperation)
            {
                if (reply.Status == FenceActionStatus.Accepted)
                { localRemovalAccepted = true; localRemovalElapsed = 0f; }
                else StopLocalRemoval();
            }
            if (reply.Status == FenceActionStatus.Accepted) return;
            Report(reply.Status switch
            {
                FenceActionStatus.Busy => "Another player is removing this fence.",
                FenceActionStatus.TooFar => "Move closer to the fence.",
                FenceActionStatus.NotInstalled => "This fence is no longer installed.",
                FenceActionStatus.NotOnHomeLand => "Fences can only be installed on your land.",
                _ => "The host could not apply this fence action."
            });
        }

        private void PeerLeft(uint peer)
        {
            if (!host) return;
            foreach (var pair in removals.Where(value => value.Value.Peer == peer).ToArray())
            {
                try { pair.Value.Fence.MouseRightUp(); }
                catch (Exception ex) { Debug.LogWarning("MonMP fence removal cleanup failed: " + ex.Message); }
                removals.Remove(pair.Key);
            }
        }

        private static ItemPose Pose(Transform transform)
        { var p = transform.position; var q = transform.rotation; return new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w); }
        private static Vector3 Position(ItemPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        internal bool IsRemoteRemoval(Tool_Fence fence) => host && removals.Values.Any(value => value.Fence == fence);

        private void UpdateLocalRemoval()
        {
            if (localRemovalOperation == Guid.Empty) return;
            if (session?.IsRunning != true || localRemoval == null || localRemoval.IntValue1 != 1)
            { StopLocalRemoval(); return; }
            if (!localRemovalAccepted) return;
            // Native hold progression uses deltaTime and pauses with scaled gameplay time.
            localRemovalElapsed += Time.deltaTime;
            if (localRemovalElapsed >= RemovalSeconds) { StopLocalRemoval(); return; }
            localCrosshair?.RemoveAPart(Mathf.Clamp01(localRemovalElapsed / RemovalSeconds));
        }

        private void StopLocalRemoval()
        {
            if (localRemovalAccepted) localCrosshair?.RemoveAPart(-1f);
            localRemoval = null; localCrosshair = null; localRemovalOperation = Guid.Empty;
            localRemovalAccepted = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopLocalRemoval();
            if (session != null) session.PeerLeft -= PeerLeft;
            if (channel != null) { channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (host)
                foreach (var pair in removals.ToArray())
                    try { pair.Value.Fence.MouseRightUp(); } catch (Exception ex) { Debug.LogWarning("MonMP fence removal cleanup failed: " + ex.Message); }
            removals.Clear(); pending.Clear(); results.Clear(); claims.Clear(); session = null; channel = null;
        }
    }
}
