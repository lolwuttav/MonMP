using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Adapter 71: the host owns KaliGas can stock, purchase cash, and new mixed cans.
    // The color picker remains a local UI; only its bounded RGB code crosses the network.
    internal sealed class SharedPaintMixing : IDisposable
    {
        private const float InteractionRange = 4.5f;
        private const float EmptyCanRange = 10f;
        private const float PeerItemRange = 6f;
        private const float RequestTimeout = 10f;
        private const float CaptureInterval = 0.25f;
        private const float BudgetWindow = 2f;
        private const int RequestsPerWindow = 8;
        private const int MaxPending = 16;
        private const int MaxResults = PaintMixingPolicy.MaxOperations;
        private static readonly FieldInfo EmptyCanField = AccessTools.Field(typeof(Workbench_PaintMixing), "EmptyCanToFill");

        private sealed class Binding
        {
            public ulong Id;
            public Workbench_PaintMixing Workbench = null!;
            public GameObject[] Slots = null!;
            public bool[] OriginalSlots = null!;
            public PaintMixingState State;
        }

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Workbench_PaintMixing, Binding> byWorkbench = new Dictionary<Workbench_PaintMixing, Binding>();
        private readonly Dictionary<uint, (float Started, int Count)> budgets = new Dictionary<uint, (float, int)>();
        private readonly Dictionary<Guid, (ulong Station, float Deadline)> pending = new Dictionary<Guid, (ulong, float)>();
        // A delayed confirmation is still the same purchase. New actions remain blocked while
        // unresolved, so this table is bounded by the admitted pending window (MaxPending).
        private readonly Dictionary<Guid, ulong> unresolved = new Dictionary<Guid, ulong>();
        private readonly HashSet<(Guid Profile, Guid Operation)> claims = new HashSet<(Guid, Guid)>();
        private readonly Dictionary<(Guid Profile, Guid Operation), (ulong Station, PaintMixingReply Reply)> results =
            new Dictionary<(Guid, Guid), (ulong, PaintMixingReply)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed, uncertainOutcome, localNativePurchase, executingNativeSpend;
        private float nextCapture;

        internal static SharedPaintMixing? Active { get; private set; }
        internal bool NewActionsBlocked { get; set; }
        internal bool HasUncertainOutcome => uncertainOutcome || unresolved.Count != 0;
        internal bool TransactionsSettled => !HasUncertainOutcome && pending.Count == 0;
        // SharedEconomyMutationPatch and SharedEconomyAffordPatch should consult these narrow
        // gates so the native cash/stat/UI methods run only inside a validated paint purchase.
        internal bool AllowNativeEconomyMutation(string method) =>
            method == nameof(Cash_Manager.Event_Spent) && (localNativePurchase || executingNativeSpend);
        internal bool AllowNativeAffordabilityQuote(float price) =>
            localNativePurchase && Mathf.Abs(price - PaintMixingPolicy.Price) < 0.001f;

        internal SharedPaintMixing(Gameplay gameplay, bool host, SharedItems items, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            if (EmptyCanField?.FieldType != typeof(GameObject[]))
                throw new InvalidOperationException("Paint mixer native empty-can stock field is missing or changed.");

            var workbenches = Object.FindObjectsOfType<Workbench_PaintMixing>(true)
                .Where(workbench => workbench != null && workbench.gameObject.scene.IsValid()).ToArray();
            if (workbenches.Length == 0)
                throw new InvalidOperationException("No static paint mixer was found in the gameplay scene.");

            foreach (var workbench in workbenches)
            {
                var slots = EmptyCanField.GetValue(workbench) as GameObject[];
                if (slots == null || slots.Length > PaintMixingState.MaxSlots || slots.Any(slot => slot == null) ||
                    slots.Distinct().Count() != slots.Length || workbench.CanSpawnLocation == null || workbench.store_Attached == null)
                    throw new InvalidOperationException("Paint mixer has an unsupported stock, spawn, or store layout.");

                string path = SharedBuildingDoors.StablePath(workbench.transform) + "/paint-mixing-v1";
                ulong id = StaticId.Hash(path);
                identities.Bind(id, PaintMixingState.Adapter, workbench.gameObject, path, true, EntityPersistence.SessionOnly);
                var binding = new Binding
                {
                    Id = id,
                    Workbench = workbench,
                    Slots = slots,
                    OriginalSlots = slots.Select(slot => slot.activeSelf).ToArray(),
                    State = Capture(slots)
                };
                bindings.Add(id, binding);
                byWorkbench.Add(workbench, binding);
            }

            var factory = Singleton<GameAssets>.i == null ? null : Singleton<GameAssets>.i.SprayPaintCan;
            if (factory == null || items.PurchasePrefab(factory) == 0)
                throw new InvalidOperationException("Native SprayPaintCan factory is not in the shared item catalog.");
            Active = this;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(binding =>
            new WorldRecord(binding.Id, 1, PaintMixingState.Adapter, Capture(binding.Slots).Encode()));

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        // True means suppress the native local purchase. Host and offline purchases remain native.
        internal bool InterceptTryToBuy(Workbench_PaintMixing workbench, int colorCode)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (host)
            {
                if (ActionsBlocked())
                {
                    Report(HasUncertainOutcome
                        ? "A previous paint purchase has an uncertain outcome. Check shared cash and the mixer before retrying."
                        : "Paint mixing is paused while the multiplayer save finishes.");
                    return true;
                }
                localNativePurchase = true;
                return false;
            }
            if (workbench == null || !byWorkbench.TryGetValue(workbench, out var binding) || channel?.FullySynchronized != true)
            {
                Report("This paint mixer is unavailable until the host world is synchronized.");
                return true;
            }
            if (ActionsBlocked())
            {
                Report(HasUncertainOutcome
                    ? "A previous paint purchase has an uncertain outcome. Check shared cash and the mixer before retrying."
                    : "Paint mixing is paused while the multiplayer save finishes.");
                return true;
            }
            if (pending.Count >= MaxPending)
            {
                Report("Wait for the pending paint-mixing purchase to finish.");
                return true;
            }
            if (gameplay.Get_PlayerState != PlayerState.Walking ||
                (gameplay.PlayerWalking.transform.position - workbench.transform.position).sqrMagnitude > InteractionRange * InteractionRange)
            {
                Report("Move closer to the paint mixer before buying a can.");
                return true;
            }
            Guid operation = Guid.NewGuid();
            PaintMixingRequest request;
            try { request = new PaintMixingRequest(operation, colorCode); }
            catch (InvalidDataException)
            {
                Report("The selected paint color is invalid.");
                return true;
            }
            pending.Add(operation, (binding.Id, Time.unscaledTime + RequestTimeout));
            channel!.Request(binding.Id, request.Encode());
            return true;
        }

        // A guest must not reset local refill meshes from its independent store-open event.
        // A host must also keep stock frozen while save/new-action gates are active.
        internal bool SuppressRestock(Workbench_PaintMixing workbench)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (host)
            {
                if (!ActionsBlocked()) return false;
                Report(HasUncertainOutcome
                    ? "Paint stock is paused because a prior purchase has an uncertain outcome."
                    : "Paint stock is paused while the multiplayer save finishes.");
                return true;
            }
            if (!byWorkbench.ContainsKey(workbench)) Report("This paint mixer is not registered by the host.");
            return true;
        }

        internal void AfterNativePurchase(Workbench_PaintMixing workbench)
        {
            try
            {
                if (host && !disposed && session?.IsRunning == true && channel?.Ready == true && byWorkbench.TryGetValue(workbench, out var binding))
                    Publish(binding);
            }
            finally { localNativePurchase = false; }
        }

        internal void EndNativePurchase() => localNativePurchase = false;

        internal void AfterNativeRestock(Workbench_PaintMixing workbench) => AfterNativePurchase(workbench);

        private static PaintMixingState Capture(GameObject[] slots)
        {
            ulong mask = 0;
            for (int i = 0; i < slots.Length; i++) if (slots[i].activeSelf) mask |= 1UL << i;
            return new PaintMixingState(slots.Length, mask);
        }

        private bool snapshotReceived;
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var staged = records.Values.Where(record => record.Adapter == PaintMixingState.Adapter)
                .Select(record => (record.Id, State: Decode(record))).ToArray();
            // Scoped repairs carry a subset (or none) of these records; only the initial world
            // snapshot must cover every local workbench.
            if ((!snapshotReceived && staged.Length != bindings.Count) || staged.Any(pair => !bindings.ContainsKey(pair.Id)))
                throw new InvalidDataException("Paint mixer snapshot does not match the local workbench set.");
            snapshotReceived = true;
            foreach (var pair in staged) Apply(bindings[pair.Id], pair.State);
        }

        private PaintMixingState Decode(WorldRecord record)
        {
            if (record.Adapter != PaintMixingState.Adapter || !bindings.TryGetValue(record.Id, out var binding))
                throw new InvalidDataException("Unknown paint mixer state record.");
            var state = PaintMixingState.Decode(record.Payload);
            if (state.SlotCount != binding.Slots.Length) throw new InvalidDataException("Paint mixer stock layout changed.");
            return state;
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != PaintMixingState.Adapter) return;
            var state = Decode(record);
            Apply(bindings[record.Id], state);
        }

        private static void Apply(Binding binding, PaintMixingState state)
        {
            binding.State = state;
            for (int i = 0; i < binding.Slots.Length; i++)
                if (binding.Slots[i].activeSelf != state.IsActive(i)) binding.Slots[i].SetActive(state.IsActive(i));
        }

        private void Publish(Binding binding)
        {
            binding.State = Capture(binding.Slots);
            channel!.Publish(binding.Id, binding.State.Encode());
        }

        private void Rejected(uint peer, ulong id, byte[] payload) => Command(peer, id, payload, true);
        [MonMP.Core.World.WorldCommandAdapters(PaintMixingState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload) => Command(peer, id, payload, false);

        private void Command(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || disposed || session?.IsHost != true || channel == null ||
                !session.Peers.TryGetValue(peer, out var actor) || !bindings.TryGetValue(id, out var binding)) return;
            PaintMixingRequest request;
            try { request = PaintMixingRequest.Decode(payload); }
            catch (InvalidDataException) { return; }

            var key = (actor.ProfileId, request.Operation);
            if (results.TryGetValue(key, out var prior))
            {
                channel.Reply(peer, id, (prior.Station == id ? prior.Reply :
                    new PaintMixingReply(request.Operation, PaintMixingStatus.RetryRejected)).Encode());
                return;
            }
            if (results.Count >= MaxResults)
            {
                channel.Reply(peer, id, new PaintMixingReply(request.Operation, PaintMixingStatus.Capacity).Encode());
                return;
            }

            bool budget = SpendBudget(peer);
            bool inRange = !stale && items.HostPeerNear(peer, binding.Workbench.transform.position, InteractionRange);
            var cash = Object.FindObjectOfType<Cash_Manager>();
            bool canAfford = cash != null && cash.GetCash >= Mathf.RoundToInt(PaintMixingPolicy.Price * 100f);
            bool handsFree = !items.PeerHolding(peer);
            var resource = SelectResource(peer, binding, out Tool_SprayPaintCan? emptyCan, out ulong emptyCanId, out int stockSlot);
            PaintMixingStatus decision = PaintMixingPolicy.Validate(channel.FullySynchronized, ActionsBlocked(), budget,
                !stale && byWorkbench.TryGetValue(binding.Workbench, out var exact) && ReferenceEquals(exact, binding),
                binding.Workbench.store_Attached.isStoreOpen(), inRange, request.ColorCode, canAfford,
                resource != ResourceKind.None && resource != ResourceKind.Blocked, handsFree);
            if (resource == ResourceKind.Blocked && decision == PaintMixingStatus.Accepted) decision = PaintMixingStatus.NoCan;
            if (decision != PaintMixingStatus.Accepted)
            {
                Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, decision));
                return;
            }

            if (!claims.Add(key))
            {
                Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, PaintMixingStatus.RetryRejected));
                return;
            }

            ulong spawnedId = 0;
            bool stockConsumed = false;
            bool debitAttempted = false;
            try
            {
                var factory = Singleton<GameAssets>.i.SprayPaintCan;
                var location = binding.Workbench.CanSpawnLocation;
                spawnedId = items.HostSpawnLeasedItem(factory, location.position, location.rotation, peer);
                if (spawnedId == 0 || items.ItemOf(spawnedId) is not Tool_SprayPaintCan mixedCan)
                {
                    Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, PaintMixingStatus.NativeRejected));
                    return;
                }
                mixedCan.IntValue1 = request.ColorCode;
                if (!items.HostPublishItem(spawnedId))
                    throw new InvalidOperationException("Mixed spray can was created but could not be published.");

                if (resource == ResourceKind.EmptyCan)
                {
                    if (emptyCan == null || emptyCanId == 0 || !items.TakeFrom(peer, emptyCanId, typeof(Tool_SprayPaintCan)))
                    {
                        RollbackSpawn(peer, spawnedId);
                        spawnedId = 0;
                        Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, PaintMixingStatus.NoCan));
                        return;
                    }
                }
                else
                {
                    if (stockSlot < 0 || stockSlot >= binding.Slots.Length || !binding.Slots[stockSlot].activeSelf)
                    {
                        RollbackSpawn(peer, spawnedId);
                        spawnedId = 0;
                        Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, PaintMixingStatus.NoCan));
                        return;
                    }
                    binding.Slots[stockSlot].SetActive(false);
                    stockConsumed = true;
                }

                int cashBefore = cash!.GetCash;
                debitAttempted = true;
                executingNativeSpend = true;
                try { cash.Event_Spent(PaintMixingPolicy.Price); }
                finally { executingNativeSpend = false; }
                bool debited = cash.GetCash == cashBefore - Mathf.RoundToInt(PaintMixingPolicy.Price * 100f);
                Publish(binding);
                if (!debited)
                {
                    uncertainOutcome = true;
                    Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, PaintMixingStatus.Uncertain));
                    return;
                }
                Singleton<SOUND_Manager>.i.PlaySound_OpenCan();
                Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, PaintMixingStatus.Accepted, spawnedId));
            }
            catch (Exception ex)
            {
                // Do not repeat a transaction after any native cash/stock boundary becomes uncertain.
                uncertainOutcome |= debitAttempted || stockConsumed || spawnedId != 0;
                if (stockConsumed) Publish(binding);
                Debug.LogException(ex);
                Finish(peer, actor.ProfileId, id, new PaintMixingReply(request.Operation, PaintMixingStatus.Uncertain));
            }
        }

        private enum ResourceKind { None, EmptyCan, StockSlot, Blocked }

        private ResourceKind SelectResource(uint peer, Binding binding, out Tool_SprayPaintCan? emptyCan,
            out ulong emptyCanId, out int stockSlot)
        {
            emptyCan = null;
            emptyCanId = 0;
            stockSlot = -1;
            bool blockedEmptyCan = false;
            foreach (var collider in Physics.OverlapSphere(binding.Workbench.CanSpawnLocation.position, EmptyCanRange))
            {
                if (collider == null || collider.tag == "Player") continue;
                var can = collider.GetComponent<Tool_SprayPaintCan>();
                if (can == null || can.Value1 > 0f) continue;
                ulong id = items.EntityOf(can);
                if (id == 0) id = items.EnsureHostBinding(can);
                var state = id == 0 ? null : items.StateOf(id);
                if (id == 0 || state == null || !state.Exists ||
                    (state.Lease.Owner != peer && state.Lease.Owner != ItemLease.Unowned) ||
                    !items.HostPeerNear(peer, can.transform.position, PeerItemRange))
                {
                    blockedEmptyCan = true;
                    continue;
                }
                emptyCan = can;
                emptyCanId = id;
                return ResourceKind.EmptyCan;
            }
            for (int i = 0; i < binding.Slots.Length; i++)
                if (binding.Slots[i].activeInHierarchy) { stockSlot = i; return ResourceKind.StockSlot; }
            return blockedEmptyCan ? ResourceKind.Blocked : ResourceKind.None;
        }

        private bool ActionsBlocked() => NewActionsBlocked || HasUncertainOutcome || MonMPPlugin.Instance?.World?.SaveGate.Active == true;

        private bool SpendBudget(uint peer)
        {
            float now = Time.unscaledTime;
            (float Started, int Count) bucket = budgets.TryGetValue(peer, out var existing) && now - existing.Started < BudgetWindow
                ? existing : (now, 0);
            if (bucket.Count >= RequestsPerWindow) { budgets[peer] = bucket; return false; }
            budgets[peer] = (bucket.Started, bucket.Count + 1);
            return true;
        }

        private void RollbackSpawn(uint peer, ulong entity)
        {
            if (!items.TakeFrom(peer, entity, typeof(Tool_SprayPaintCan)))
                uncertainOutcome = true;
        }

        private void Finish(uint peer, Guid profile, ulong station, PaintMixingReply reply)
        {
            results[(profile, reply.Operation)] = (station, reply);
            channel!.Reply(peer, station, reply.Encode());
        }

        private void Reply(ulong station, byte[] payload)
        {
            if (host || !PaintMixingReply.Is(payload)) return;
            PaintMixingReply reply;
            try { reply = PaintMixingReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (pending.TryGetValue(reply.Operation, out var request))
            {
                if (request.Station != station) return;
                pending.Remove(reply.Operation);
            }
            else if (!unresolved.TryGetValue(reply.Operation, out ulong originalStation) || originalStation != station) return;
            if (reply.Status == PaintMixingStatus.Uncertain) unresolved[reply.Operation] = station;
            else unresolved.Remove(reply.Operation);
            if (reply.Status != PaintMixingStatus.Accepted) Report(Message(reply.Status));
        }

        private static string Message(PaintMixingStatus status) => status switch
        {
            PaintMixingStatus.NotReady => "Wait for the shared world to finish synchronizing before mixing paint.",
            PaintMixingStatus.SaveBlocked => "Paint mixing is paused while the multiplayer save finishes.",
            PaintMixingStatus.Closed => "The host's paint store is closed.",
            PaintMixingStatus.TooFar => "Move closer to the paint mixer before buying a can.",
            PaintMixingStatus.InvalidColor => "The selected paint color is invalid.",
            PaintMixingStatus.NoFunds => "Not enough shared cash to mix paint.",
            PaintMixingStatus.NoCan => "No empty can or refill stock is available at the host's mixer.",
            PaintMixingStatus.HandsBusy => "Put down your held item before buying a mixed can.",
            PaintMixingStatus.Unsupported => "This paint mixer is not registered in the host's world.",
            PaintMixingStatus.Capacity => "Wait for your pending paint-mixing request to finish.",
            PaintMixingStatus.Uncertain => "Paint purchase outcome is uncertain. Check shared cash and the mixer before retrying.",
            PaintMixingStatus.NativeRejected => "The host could not create the mixed paint can.",
            _ => "This paint-mixing request was already handled."
        };

        private void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (host && channel?.Ready == true && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + CaptureInterval;
                foreach (var binding in bindings.Values)
                {
                    var captured = Capture(binding.Slots);
                    if (captured.ActiveMask != binding.State.ActiveMask || captured.SlotCount != binding.State.SlotCount)
                    {
                        binding.State = captured;
                        channel.Publish(binding.Id, captured.Encode());
                    }
                }
            }
            if (!host)
            {
                if (pending.Count > 0) foreach (var request in pending.Where(pair => Time.unscaledTime >= pair.Value.Deadline).ToArray())
                {
                    pending.Remove(request.Key);
                    unresolved[request.Key] = request.Value.Station;
                    Report("Paint-mixing confirmation is delayed. Check the host's cash and paint-can stock before retrying.");
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected;
                channel.ReplyReceived -= Reply;
            }
            if (!host)
                foreach (var binding in bindings.Values)
                    for (int i = 0; i < binding.Slots.Length; i++)
                        if (binding.Slots[i] != null && binding.Slots[i].activeSelf != binding.OriginalSlots[i])
                            binding.Slots[i].SetActive(binding.OriginalSlots[i]);
            if (ReferenceEquals(Active, this)) Active = null;
            pending.Clear(); unresolved.Clear(); budgets.Clear(); session = null; channel = null;
        }
    }
}
