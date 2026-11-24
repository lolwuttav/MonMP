using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Per-keg item values and placement already travel through SharedItems. This adapter owns only
    // fermentation authority and wine extraction, whose native use spawns and grabs a second item.
    internal sealed class SharedWoodenKegs : IDisposable
    {
        private const float RequestTimeout = 8f;
        private const float BudgetWindow = 3f;
        private const int RequestsPerWindow = 4;
        private const int ClaimCapacity = 4096;

        private sealed class NativeExtraction
        {
            public uint Peer;
            public ulong KegId, WinePackId;
            public WoodenKegUseRequest Request;
            public float PreviousSyrup, PreviousFermentation;
            public bool NativePackObserved, DeliveryFailed;
        }

        private static readonly FieldInfo? UseKeyTextField = AccessTools.Field(typeof(Item_WoodenKeg), "useKeyText");
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Started, int Count)> budgets = new Dictionary<uint, (float, int)>();
        private readonly Dictionary<(Guid Profile, Guid Operation), WoodenKegUseReply> results =
            new Dictionary<(Guid, Guid), WoodenKegUseReply>();
        private readonly Queue<(Guid Profile, Guid Operation)> resultOrder = new Queue<(Guid, Guid)>();
        private readonly Dictionary<Guid, (ulong Keg, float Deadline)> pending = new Dictionary<Guid, (ulong, float)>();
        private readonly Dictionary<Guid, ulong> timedOut = new Dictionary<Guid, ulong>();

        private NetworkSession? session;
        private WorldChannel? channel;
        private NativeExtraction? extraction;
        private bool disposed, uncertainOutcome;

        internal static SharedWoodenKegs? Current { get; private set; }
        // The exact approved native Use call redirects its result before the host's hand
        // receives it. Only that call may bypass the single-player host-hand precondition.
        internal static bool RemoteExtractionActive => Current?.extraction != null;
        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => !uncertainOutcome && pending.Count == 0 && timedOut.Count == 0 && extraction == null;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal int TestDuplicateReplays { get; private set; }
#endif


        private readonly AimSegmentTest checkSegment;

        internal SharedWoodenKegs(AimSegmentTest checkSegment, Gameplay gameplay, bool host, SharedItems items)
        {
            this.checkSegment = checkSegment;
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            if (UseKeyTextField?.FieldType != typeof(UI_UseKeyText))
                throw new InvalidOperationException("Unsupported Item_WoodenKeg native use-key layout.");
            if (Current != null) throw new InvalidOperationException("A wooden-keg authority adapter is already active.");
            Current = this;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => new[]
        {
            new WorldRecord(WoodenKegUseState.Entity, 1, WoodenKegUseState.Adapter, WoodenKegUseState.InitialPayload())
        };

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected ?? throw new ArgumentNullException(nameof(connected));
            channel = world ?? throw new ArgumentNullException(nameof(world));
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.ReplyReceived += Reply;
        }

        private bool Running => !disposed && session?.IsRunning == true;
        private bool Ready => Running && channel?.FullySynchronized == true;

        internal bool InterceptUseKeyDown(PlayerInteractable player, InteractableItems? target)
        {
            if (!Running || host || target is not Item_WoodenKeg keg) return false;
            if (!ReferenceEquals(player, gameplay.Player) || !items.Handles(keg))
            {
                Report("This keg is not registered in the shared world.");
                return true;
            }
            if (!Ready || NewActionsBlocked)
            {
                Report("Wait for the shared world and save to finish before extracting wine.");
                return true;
            }
            if (gameplay.Get_PlayerState != PlayerState.Walking || player.IsHoldingAnObject)
            {
                Report("Put down the keg and free your hands before extracting wine.");
                return true;
            }
            if (keg.Value1 < keg.Value1_Max || keg.Value2 < 1f)
            {
                Report("This keg needs a full syrup load and completed fermentation before extracting wine.");
                return true;
            }
            if (pending.Count >= 8)
            {
                Report("Wait for the pending wine extraction request to finish.");
                return true;
            }
            Camera? camera = player.mainCamera;
            ulong itemId = items.EntityOf(keg);
            if (camera == null || itemId == 0)
            {
                Report("The shared keg or local camera is unavailable.");
                return true;
            }

            Guid operation = Guid.NewGuid();
            try
            {
                var aim = Integration.LocalInteraction.SampleTool(camera);
                var request = new WoodenKegUseRequest(operation, itemId,
                    aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
                pending[request.Operation] = (itemId, Time.unscaledTime + RequestTimeout);
                channel!.Request(WoodenKegUseState.Entity, request.Encode());
                WoodenKegWinePack.HideUseHint(player, keg);
            }
            catch (InvalidDataException ex)
            {
                pending.Remove(operation);
                Report("The wine extraction request could not be encoded: " + ex.Message);
            }
            return true;
        }

        // A direct script-side Use call cannot bypass PlayerInteractable's routed guest path.
        internal bool SuppressGuestDirectUse(Item_WoodenKeg keg)
        {
            if (!Running || host) return false;
            Report("Use the shared interaction key on the keg so the host can approve extraction.");
            return true;
        }

        // UniStorm broadcasts this per process. Only the host advances fermentation; ItemState
        // values carry the host's syrup and fermented fraction to replicas.
        internal bool SuppressGuestFermentation(Item_WoodenKeg keg) => Running && !host;

        // During the routed native extraction, replace its local GrabNewItemBought product with
        // a separately catalogued pack leased directly to the requesting peer.
        internal bool RedirectNativeWinePack(PlayerInteractable player, GameObject nativePack)
        {
            var active = extraction;
            if (!host || active == null || !ReferenceEquals(player, gameplay.Player)) return false;
            active.NativePackObserved = true;
            try
            {
                var nativeItem = nativePack == null ? null : nativePack.GetComponent<InteractableItems>();
                var asset = Singleton<GameAssets>.i == null ? null : Singleton<GameAssets>.i.Pack_MapleWineBottle;
                if (asset == null)
                {
                    active.DeliveryFailed = true;
                    return true;
                }
                var factoryItem = asset.GetComponent<InteractableItems>();
                if (nativeItem == null || factoryItem == null || nativeItem.GetType() != factoryItem.GetType() ||
                    !WoodenKegWinePack.TryInitialValues(asset, nativeItem, out var wineValues))
                {
                    active.DeliveryFailed = true;
                    return true;
                }
                Vector3 direction = Quaternion.Euler(active.Request.CameraPitch, active.Request.CameraYaw, 0f) * Vector3.forward;
                Vector3 position = new Vector3(active.Request.CameraX, active.Request.CameraY, active.Request.CameraZ) + direction * 0.35f;
                active.WinePackId = items.HostSpawnLeasedItem(asset, position, Quaternion.identity, active.Peer, wineValues);
                if (active.WinePackId == 0) active.DeliveryFailed = true;
            }
            catch (Exception ex)
            {
                active.DeliveryFailed = true;
                Debug.LogWarning("MonMP could not redirect the native wooden-keg wine pack: " + ex.Message);
            }
            finally
            {
                if (nativePack != null)
                {
                    nativePack.SetActive(false);
                    Object.Destroy(nativePack);
                }
            }
            return true;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnPeerLeft(uint peer)
        {
            presence.Remove(peer);
            budgets.Remove(peer);
        }

        private bool TakeBudget(uint peer, float now)
        {
            if (!budgets.TryGetValue(peer, out var budget) || now < budget.Started || now - budget.Started >= BudgetWindow)
                budget = (now, 0);
            if (budget.Count >= RequestsPerWindow) { budgets[peer] = budget; return false; }
            budgets[peer] = (budget.Started, budget.Count + 1);
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(WoodenKegUseState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload[0] != WoodenKegUseRequest.Marker) return;
            if (!host || session?.IsHost != true || channel == null || entity != WoodenKegUseState.Entity) return;
            WoodenKegUseRequest request;
            try { request = WoodenKegUseRequest.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;

            var key = (buyer.ProfileId, request.Operation);
            if (results.TryGetValue(key, out var previous))
            {

#if MONMP_DEVELOPMENT
                TestDuplicateReplays++;
#endif

                channel.Reply(peer, WoodenKegUseState.Entity, previous.Encode());
                return;
            }
            if (results.Count >= ClaimCapacity)
            {
                Send(peer, new WoodenKegUseReply(request.Operation, WoodenKegUseStatus.Capacity));
                return;
            }

            float now = Time.unscaledTime;
            bool budget = TakeBudget(peer, now);
            bool freshPose = presence.TryGetValue(peer, out var observed) && now >= observed.Time &&
                now - observed.Time <= WoodenKegUsePolicy.FreshPoseSeconds;
            PlayerPose pose = freshPose ? observed.Pose : default;
            var kegItem = items.ItemOf(request.Keg);
            var keg = kegItem as Item_WoodenKeg;
            var itemState = items.StateOf(request.Keg);
            bool exactTarget = request.Keg != 0 && keg != null && items.Handles(keg) && items.EntityOf(keg) == request.Keg;
            bool exactType = kegItem != null && kegItem.GetType() == typeof(Item_WoodenKeg);
            bool unowned = itemState != null && itemState.Lease.Owner == ItemLease.Unowned;
            bool active = keg != null && keg.isActiveAndEnabled && keg.gameObject.activeInHierarchy;
            bool mature = keg != null && keg.Value1 >= keg.Value1_Max && keg.Value2 >= 1f;
            bool cameraFits = freshPose && request.CameraFits(pose, checkSegment);
            bool rayHits = cameraFits && keg != null && HostRayHitsKeg(keg, request);
            bool peerKnown = peer != 0;
            bool guestHandsFree = !items.PeerHolding(peer);

            var status = WoodenKegUsePolicy.ValidateHostRequest(Ready, NewActionsBlocked, budget, peerKnown,
                exactTarget, exactType, unowned, active, mature,
                freshPose && pose.State == (byte)PlayerState.Walking, cameraFits, rayHits,
                guestHandsFree);
            if (status != WoodenKegUseStatus.Accepted)
            {

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation, status));
                return;
            }

            ExecuteNativeExtraction(key, peer, request, keg!, request.Keg);
        }

        private static bool HostRayHitsKeg(Item_WoodenKeg keg, WoodenKegUseRequest request)
        {
            var origin = new Vector3(request.CameraX, request.CameraY, request.CameraZ);
            var direction = Quaternion.Euler(request.CameraPitch, request.CameraYaw, 0f) * Vector3.forward;
            return Physics.Raycast(origin, direction, out var hit, WoodenKegUsePolicy.MaxUseRange) &&
                hit.collider.GetComponentInParent<Item_WoodenKeg>() == keg;
        }

        private void ExecuteNativeExtraction((Guid Profile, Guid Operation) key, uint peer,
            WoodenKegUseRequest request, Item_WoodenKeg keg, ulong kegId)
        {
            if (!session!.Peers.ContainsKey(peer)) return;
            var active = new NativeExtraction
            {
                Peer = peer,
                KegId = kegId, Request = request,
                PreviousSyrup = keg.Value1, PreviousFermentation = keg.Value2
            };
            extraction = active;
            try
            {
                // Native Use only fills this cached reference from MouseHover, which may not have
                // run on the host for a remote guest's keg. Supply the normal scene UI dependency.
                var useText = Singleton<UI_UseKeyText>.i;
                if (useText == null)
                {
                    StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation, WoodenKegUseStatus.NativeRejected));
                    return;
                }
                UseKeyTextField!.SetValue(keg, useText);
                keg.Use(); // returns false after the stock extraction; values + the redirected spawn are the commit signal.
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (active.WinePackId != 0)
                {
                    uncertainOutcome = true;
                    StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation, WoodenKegUseStatus.Uncertain));
                }
                else
                {
                    if (!TryRestoreUncommittedKeg(keg, active)) uncertainOutcome = true;
                    StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation,
                        uncertainOutcome ? WoodenKegUseStatus.Uncertain : WoodenKegUseStatus.DeliveryFailed));
                }
                return;
            }
            finally { extraction = null; }

            bool consumed = keg.Value1 <= 0f && keg.Value2 <= 0f;
            if (consumed && active.WinePackId > WorldRecord.DynamicIdStart && !active.DeliveryFailed)
            {
                try
                {
                    if (items.HostPublishItem(kegId))
                    {

#if MONMP_DEVELOPMENT
                        TestAccepted++;
#endif

                        StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation, WoodenKegUseStatus.Accepted, active.WinePackId));
                        return;
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
                uncertainOutcome = true;
                StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation, WoodenKegUseStatus.Uncertain));
                return;
            }

            if (active.WinePackId != 0)
            {
                uncertainOutcome = true;
                StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation, WoodenKegUseStatus.Uncertain));
                return;
            }
            if (!TryRestoreUncommittedKeg(keg, active)) uncertainOutcome = true;
            WoodenKegUseStatus result = uncertainOutcome ? WoodenKegUseStatus.Uncertain :
                (active.DeliveryFailed || active.NativePackObserved ? WoodenKegUseStatus.DeliveryFailed : WoodenKegUseStatus.NativeRejected);

#if MONMP_DEVELOPMENT
            TestRejected++;
#endif

            StoreAndSend(key, peer, new WoodenKegUseReply(request.Operation, result));
        }

        private bool TryRestoreUncommittedKeg(Item_WoodenKeg keg, NativeExtraction active)
        {
            try
            {
                keg.Value1 = active.PreviousSyrup;
                keg.Value2 = active.PreviousFermentation;
                return items.HostPublishItem(active.KegId);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return false;
            }
        }

        private void StoreAndSend((Guid Profile, Guid Operation) key, uint peer, WoodenKegUseReply reply)
        {
            if (!results.ContainsKey(key))
            {
                results[key] = reply;
                resultOrder.Enqueue(key);
                while (resultOrder.Count > ClaimCapacity)
                {
                    var oldest = resultOrder.Dequeue();
                    results.Remove(oldest);
                }
            }
            Send(peer, reply);
        }

        private void Send(uint peer, WoodenKegUseReply reply)
        {
            if (session?.Peers.TryGetValue(peer, out var connected) == true &&
                channel != null && connected.ProfileId != Guid.Empty)
                channel.Reply(peer, WoodenKegUseState.Entity, reply.Encode());
        }

        private bool endpointReceived;
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var endpoint = records.Values.SingleOrDefault(value => value.Adapter == WoodenKegUseState.Adapter);
            // Scoped repairs need not carry the endpoint; the initial world snapshot must.
            if (endpoint == null && endpointReceived) return;
            endpointReceived = true;
            if (endpoint == null || endpoint.Id != WoodenKegUseState.Entity || !WoodenKegUseState.ValidPayload(endpoint.Payload))
                throw new InvalidDataException("Shared wooden-keg use endpoint is missing or invalid.");
        }

        private void Changed(WorldRecord record)
        {
            if (record.Adapter == WoodenKegUseState.Adapter &&
                (record.Id != WoodenKegUseState.Entity || !WoodenKegUseState.ValidPayload(record.Payload)))
                throw new InvalidDataException("Unexpected wooden-keg use endpoint update.");
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != WoodenKegUseState.Entity || !WoodenKegUseReply.HasMarker(payload)) return;
            WoodenKegUseReply reply;
            try { reply = WoodenKegUseReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            ulong kegId;
            if (pending.TryGetValue(reply.Operation, out var waiting))
            { kegId = waiting.Keg; pending.Remove(reply.Operation); }
            else if (timedOut.TryGetValue(reply.Operation, out kegId)) timedOut.Remove(reply.Operation);
            else return;
            // Native extraction confirms through the wine pickup sound and delivered pack.
            if (reply.Status == WoodenKegUseStatus.Accepted)
            {
                if (items.ItemOf(kegId) is Item_WoodenKeg keg)
                    WoodenKegWinePack.HideUseHint(gameplay.Player, keg, items.ItemOf(reply.WinePack));
                return;
            }
            Report(reply.Status switch
            {
                WoodenKegUseStatus.HandsBusy => "Free your hands before extracting wine from the keg.",
                WoodenKegUseStatus.NotMature => "The host's keg is not full and fermented yet.",
                WoodenKegUseStatus.TooFar => "Move closer and look directly at the keg before extracting wine.",
                WoodenKegUseStatus.InvalidTarget => "The host could not verify that shared keg.",
                WoodenKegUseStatus.DeliveryFailed => "The wine could not be delivered; the host restored the keg contents.",
                WoodenKegUseStatus.Uncertain => "Keg extraction outcome is uncertain. Check the keg and wine pack before retrying.",
                WoodenKegUseStatus.Capacity => "Wait before requesting another wine extraction.",
                WoodenKegUseStatus.NativeRejected => "The host could not complete the keg interaction; its contents were restored.",
                _ => "The host declined wine extraction."
            });
        }

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        internal void SetNewActionsBlocked(bool value) => NewActionsBlocked = value;

        internal void Update()
        {
            if (!Running) return;
            float now = Time.unscaledTime;
            if (pending.Count > 0) foreach (var expired in pending.Where(value => now >= value.Value.Deadline).Select(value => value.Key).ToArray())
            {
                ulong kegId = pending[expired].Keg;
                pending.Remove(expired);
                timedOut[expired] = kegId;
                Report("Wine extraction confirmation is delayed. Check the keg and wine pack before retrying.");
            }
            if (timedOut.Count > 4096) timedOut.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null)
            {
                session.PoseReceived -= OnPose;
                session.PeerLeft -= OnPeerLeft;
            }
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.CommandRequested -= Command;
                channel.ReplyReceived -= Reply;
            }
            if (Current == this) Current = null;
        }
    }
}
