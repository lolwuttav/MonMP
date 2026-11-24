using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Pack_Chalumeau is a counted dispenser for Tool_TubingChalumeau, not a fire starter.
    // This route arbitrates one exact pack use; SharedItems owns its count and spawned tool,
    // and SharedTubing owns the later spout placement.
    internal sealed class SharedChalumeauPack : IDisposable
    {
        private const float Range = 2.4f;
        private const float PoseMaxAge = 2f;
        private const float RequestTimeout = 8f;
        private const int PendingLimit = 16;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), ChalumeauPackReply> results =
            new Dictionary<(Guid, Guid), ChalumeauPackReply>();
        private readonly Dictionary<Guid, (ulong Entity, float Deadline)> pending =
            new Dictionary<Guid, (ulong, float)>();
        private readonly Dictionary<Guid, ulong> awaitingTools = new Dictionary<Guid, ulong>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence =
            new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool actionsBlocked, disposed, uncertain;

        internal static SharedChalumeauPack? Active { get; private set; }
        internal bool NewActionsBlocked { get => actionsBlocked; set => actionsBlocked = value; }
        internal bool TransactionsSettled => pending.Count == 0 && !uncertain;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal ChalumeauPackReply? TestLastReply { get; private set; }
#endif


        internal SharedChalumeauPack(bool host, SharedItems items)
        {
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            if (Active != null && Active != this) throw new InvalidOperationException("A chalumeau pack adapter is already active.");
            Active = this;
        }

        internal IEnumerable<WorldRecord> InitialRecords() { yield return ChalumeauPackPolicy.RootRecord(); }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        // Harmony returns true to suppress the native path. Only loose, catalogued guest packs
        // are routed here; host and offline use remain vanilla.
        internal bool InterceptUse(PlayerInteractable player, InteractableItems? held, InteractableItems? current)
        {
            if (disposed || host || session?.IsRunning != true) return false;
            if (held == null && player.Get_ActiveTool != null) return false; // native path only unequips it
            if (held != null && held.GetType() != typeof(Pack_Chalumeau)) return false;
            if (held is Pack_Chalumeau) current = held;
            if (current?.GetType() != typeof(Pack_Chalumeau)) return false;

            if (actionsBlocked || items.TransfersBlocked)
            {
                Report("Wait for the multiplayer save to finish before using that pack.");
                return true;
            }
            if (channel?.FullySynchronized != true || !items.Ready)
            {
                Report("Chalumeau packs are unavailable until the host world is synchronized.");
                return true;
            }
            var pack = (Pack_Chalumeau)current!;
            if (!pack.gameObject.scene.IsValid() || pack.PrefabName != "Pack/ChalumeauPack" || !items.Handles(pack))
            {
                Report("This chalumeau pack is not registered in the shared item world.");
                return true;
            }
            if (pending.Count >= PendingLimit)
            {
                Report("Wait for your previous pack request to finish.");
                return true;
            }
            ulong entity = items.EntityOf(pack);
            ItemState? state = entity == 0 ? null : items.StateOf(entity);
            if (state == null || !state.Exists || (state.Lease.Owner != ItemLease.Unowned && state.Lease.Owner != session.LocalPeerId) || state.Values.Value1 < 1)
            {
                Report(state?.Lease.Owner != ItemLease.Unowned ? "That pack is busy with another player." : "That chalumeau pack is empty or unavailable.");
                return true;
            }
            var request = new ChalumeauPackRequest(Guid.NewGuid(), entity, state.Lease.Generation);
            pending.Add(request.Operation, (entity, Time.unscaledTime + RequestTimeout));
            channel!.Request(ChalumeauPackPolicy.Root, request.Encode());
            return true;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnPeerLeft(uint peer) => presence.Remove(peer);
        [MonMP.Core.World.WorldCommandAdapters(ChalumeauPackPolicy.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload) => Purchase(peer, id, payload, stale: false);
        private void Rejected(uint peer, ulong id, byte[] payload) => Purchase(peer, id, payload, stale: true);

        private void Purchase(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || id != ChalumeauPackPolicy.Root || channel == null || session == null ||
                !ChalumeauPackRequest.Is(payload) || !session.Peers.TryGetValue(peer, out var buyer)) return;
            ChalumeauPackRequest request;
            try { request = ChalumeauPackRequest.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }

            var key = (buyer.ProfileId, request.Operation);
            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var previous)) channel.Reply(peer, ChalumeauPackPolicy.Root, previous.Encode());
                else
                {
                    uncertain = true;
                    channel.Reply(peer, ChalumeauPackPolicy.Root,
                        new ChalumeauPackReply(request.Operation, ChalumeauPackStatus.Uncertain).Encode());
                }
                return;
            }

            void Finish(ChalumeauPackStatus status, bool committed = false, ulong tool = 0)
            {
                var reply = new ChalumeauPackReply(request.Operation, status, committed, tool);
                results[key] = reply;

#if MONMP_DEVELOPMENT
                TestLastReply = reply;
#endif

                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                {
                    try { channel.Reply(peer, ChalumeauPackPolicy.Root, reply.Encode()); }
                    catch (Exception ex) { uncertain = true; Debug.LogException(ex); }
                }
            }

            var packItem = items.ItemOf(request.PackEntity);
            var packState = items.StateOf(request.PackEntity);
            var pack = packItem as Pack_Chalumeau;
            bool exactPack = pack != null && pack.GetType() == typeof(Pack_Chalumeau) && pack.gameObject.scene.IsValid() &&
                pack.PrefabName == "Pack/ChalumeauPack";
            bool fresh = presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time >= 0 &&
                Time.unscaledTime - seen.Time <= PoseMaxAge;
            bool canInteract = fresh && ItemInteractionPosePolicy.Allows(seen.Pose);
            bool near = canInteract && pack != null &&
                (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - pack.transform.position).sqrMagnitude <= Range * Range;
            bool unowned = packState != null && (packState.Lease.Owner == ItemLease.Unowned || packState.Lease.Owner == peer);
            bool noOtherHeld = !items.PeerHoldingOtherThan(peer, request.PackEntity);
            bool stock = exactPack && ValidCount(pack!.Value1, pack.Value1_Max) &&
                pack.Value1 == packState!.Values.Value1 && pack.Value1 > 0;
            GameObject? factory = exactPack ? pack!.ObjectToSpawn : null;
            InteractableItems? factoryItem = factory == null ? null : factory.GetComponent<InteractableItems>();
            bool exactFactory = factoryItem != null && factoryItem.GetType() == typeof(Tool_TubingChalumeau) &&
                items.PurchasePrefab(factory!) != 0;
            bool exactTarget = id == ChalumeauPackPolicy.Root && pack != null && request.PackEntity > WorldRecord.DynamicIdStart;
            bool generationMatches = !stale && packState != null && packState.Lease.Generation == request.Generation;
            var decision = ChalumeauPackPolicy.Validate(channel.FullySynchronized && items.Ready,
                actionsBlocked || items.TransfersBlocked, results.Count < PurchaseClaims.Capacity,
                exactTarget, exactPack, generationMatches, unowned, fresh, canInteract, near,
                noOtherHeld, stock, exactFactory);
            if (decision != ChalumeauPackStatus.Accepted)
            {

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Finish(decision);
                return;
            }

            var savedPack = pack!;
            float countBefore = savedPack.Value1;
            float unpackTimeBefore = savedPack.LastTimeItemUnpack;
            ulong toolEntity = 0;
            bool destroyPack = false;
            try
            {
                // Vanilla puts a held pack down before taking its contents. Publish the lease
                // release first so the tool's arrival can equip without two held objects.
                if (packState!.Lease.Owner == peer && !items.HostReclaim(savedPack))
                { Finish(ChalumeauPackStatus.SpawnFailed); return; }
                // The tool uses the existing item catalog and is leased directly to this requester.
                toolEntity = items.HostSpawnLeasedItem(factory!, savedPack.transform.position, savedPack.transform.rotation, peer);
                if (toolEntity == 0)
                {

#if MONMP_DEVELOPMENT
                    TestRejected++;
#endif

                    Finish(ChalumeauPackStatus.SpawnFailed);
                    return;
                }

                savedPack.Value1 = countBefore - 1f;
                savedPack.LastTimeItemUnpack = Time.realtimeSinceStartup;
                savedPack.MouseOut();
                items.PublishHostItem(request.PackEntity);
                destroyPack = savedPack.DestroyWhenEmpty && savedPack.Value1 <= 0;
            }
            catch (Exception ex)
            {
                uncertain = true;
                try
                {
                    savedPack.Value1 = countBefore;
                    savedPack.LastTimeItemUnpack = unpackTimeBefore;
                    items.PublishHostItem(request.PackEntity);
                    if (toolEntity != 0) items.TakeFrom(peer, toolEntity, typeof(Tool_TubingChalumeau));
                }
                catch (Exception rollbackError) { Debug.LogException(rollbackError); }

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Finish(ChalumeauPackStatus.Uncertain, committed: toolEntity != 0, tool: toolEntity);
                Debug.LogException(ex);
                return;
            }


#if MONMP_DEVELOPMENT
            TestAccepted++;
#endif

            if (destroyPack)
            {
                savedPack.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(savedPack.gameObject);
            }
            Finish(ChalumeauPackStatus.Accepted, committed: true, tool: toolEntity);
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || id != ChalumeauPackPolicy.Root || !ChalumeauPackReply.Is(payload)) return;
            var reply = ChalumeauPackReply.Decode(payload);
            if (!pending.TryGetValue(reply.Operation, out var request) || request.Entity == 0 || awaitingTools.ContainsKey(reply.Operation)) return;
            if (reply.Status == ChalumeauPackStatus.Accepted)
            {
                pending[reply.Operation] = (request.Entity, float.PositiveInfinity);
                awaitingTools[reply.Operation] = reply.ToolEntity;
                ResolveTools();
            }
            else pending.Remove(reply.Operation);

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == ChalumeauPackStatus.Uncertain) uncertain = true;
            if (reply.Status != ChalumeauPackStatus.Accepted) Report(Message(reply.Status));
            else
            {
                var sound = Singleton<SOUND_Manager>.i;
                if (sound != null) sound.PlayOneShot2D(sound.Pack_GrabPlastic, _randomPitch: true);
            }
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            ResolveTools();
            if (pending.Count > 0) foreach (var operation in pending.Where(pair => Time.unscaledTime >= pair.Value.Deadline).Select(pair => pair.Key).ToArray())
            {
                pending[operation] = (pending[operation].Entity, float.PositiveInfinity);
                Report("Pack use confirmation is delayed. Check the tool and pack before trying again.");
            }
        }

        private void ResolveTools()
        {
            if (awaitingTools.Count == 0 || channel == null) return;
            foreach (var entry in awaitingTools.ToArray())
            {
                if (!channel.Records.TryGetValue(entry.Value, out var record) || record.Adapter != ItemState.Adapter) continue;
                var state = ItemState.Decode(record.Payload);
                // A delayed receipt describes an earlier handoff, not current ownership.
                if (state.Exists && items.ItemOf(entry.Value) == null) continue;
                awaitingTools.Remove(entry.Key);
                pending.Remove(entry.Key);
            }
        }

        private static bool ValidCount(float count, float maximum) => !float.IsNaN(count) && !float.IsInfinity(count) &&
            count >= 0 && count <= maximum && Mathf.Abs(count - Mathf.Round(count)) < 0.001f;

        private static string Message(ChalumeauPackStatus status) => status switch
        {
            ChalumeauPackStatus.Unavailable => "That chalumeau pack is unavailable in the host's world.",
            ChalumeauPackStatus.InvalidTarget => "That chalumeau pack is busy with another player.",
            ChalumeauPackStatus.Stale => "The pack changed before the host could approve it. Try again.",
            ChalumeauPackStatus.SaveBlocked => "Wait for the multiplayer save to finish before using that pack.",
            ChalumeauPackStatus.TooFar => "Move closer to the chalumeau pack.",
            ChalumeauPackStatus.NotWalking => "You cannot use the chalumeau pack in your current state.",
            ChalumeauPackStatus.HandsBusy => "Put down your other item or tool before using the pack.",
            ChalumeauPackStatus.Empty => "That chalumeau pack is empty.",
            ChalumeauPackStatus.UnsupportedFactory => "This chalumeau pack factory is not supported by multiplayer.",
            ChalumeauPackStatus.SpawnFailed => "The host could not create the tubing spout tool.",
            ChalumeauPackStatus.Uncertain => "The pack use outcome is uncertain. Check the pack and tool before retrying.",
            _ => "The host declined the chalumeau pack use."
        };

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnPeerLeft; }
            if (channel != null)
            {
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected;
                channel.ReplyReceived -= Reply;
            }
            pending.Clear();
            awaitingTools.Clear();
            presence.Clear();
            if (Active == this) Active = null;
            session = null;
            channel = null;
        }
    }
}
