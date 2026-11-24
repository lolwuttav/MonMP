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
    // Host-owned gate and request ledger for the Carole friendship-locked keg listing.
    // The host executor is intentionally injected: SharedStores must perform the native purchase
    // under its existing scoped economy bypass and item-lease delivery path.
    internal sealed class SharedKegPurchase : IDisposable
    {
        private const float CaptureInterval = 0.2f;
        private const float CommandTimeout = 10f;
        private static readonly FieldInfo? ListingPrice = AccessTools.Field(typeof(ItemForSale_Display), "price");
        private static readonly FieldInfo? ListingCarole = AccessTools.Field(typeof(ItemForSale_Kegs), "ActorCarole");

        private sealed class ListingBinding
        {
            internal readonly ItemForSale_Kegs Display;
            internal readonly Actor_Carole Carole;
            internal readonly ushort ItemPrefab;
            internal readonly ulong Entity;
            internal readonly float OriginalPrice;
            internal readonly bool OriginalAvailable, OriginalStoreOpen;
            internal KegPurchaseState State;

            internal ListingBinding(ItemForSale_Kegs display, Actor_Carole carole, ushort itemPrefab,
                ulong entity, float originalPrice, bool originalAvailable, bool originalStoreOpen,
                KegPurchaseState state)
            {
                Display = display;
                Carole = carole;
                ItemPrefab = itemPrefab;
                Entity = entity;
                OriginalPrice = originalPrice;
                OriginalAvailable = originalAvailable;
                OriginalStoreOpen = originalStoreOpen;
                State = state;
            }
        }

        private readonly bool host;
        private readonly SharedItems items;
        private readonly ListingBinding[] listings;
        private readonly Dictionary<ulong, ListingBinding> listingsByEntity;
        private readonly Cash_Manager? cash;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), (ulong Entity, KegPurchaseReply Reply)> results =
            new Dictionary<(Guid, Guid), (ulong, KegPurchaseReply)>();
        private readonly Dictionary<(Guid Profile, Guid Operation), (uint Peer, ulong Entity, float Deadline)> hostPending =
            new Dictionary<(Guid, Guid), (uint, ulong, float)>();
        private readonly Dictionary<Guid, (ulong Entity, float Deadline)> pending = new Dictionary<Guid, (ulong, float)>();
        private readonly Dictionary<Guid, ulong> timedOut = new Dictionary<Guid, ulong>();
        private readonly Dictionary<Guid, ulong> awaitingItems = new Dictionary<Guid, ulong>();

        private NetworkSession? session;
        private WorldChannel? channel;
        private ItemForSale_Kegs? executingNative;
        private bool initialized, disposed;
        private float nextCapture;

        internal static SharedKegPurchase? Active { get; private set; }
        internal event Action<uint, Guid, ItemForSale_Kegs, KegPurchaseRequest>? HostPurchaseRequested;
        internal bool NewPurchasesBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0 && timedOut.Count == 0 && hostPending.Count == 0;
        internal bool HasListing => listings.Length != 0;

#if MONMP_DEVELOPMENT
        internal KegPurchaseState TestState => listings.Length == 0 ? default : listings[0].State;


        internal KegPurchaseReply? TestLastReply { get; private set; }


        internal int TestNativePurchases { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal int TestLockDenials { get; private set; }
#endif


        internal SharedKegPurchase(bool host, SharedItems items)
        {
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            cash = Object.FindObjectOfType<Cash_Manager>();

            var sceneCandidates = Object.FindObjectsOfType<ItemForSale_Kegs>(true)
                .Where(value => value.gameObject.scene.IsValid()).ToArray();
            var candidates = sceneCandidates.Where(value =>
            {
                var carole = ListingCarole?.GetValue(value) as Actor_Carole;
                GameObject? prefab = value.Get_RealObjectToBuy;
                var item = prefab == null ? null : prefab.GetComponent<InteractableItems>();
                return value.store is Store_Bar && carole != null && carole.actor_Type == Actor_Type.Carole &&
                    item != null && item.GetType() == typeof(Item_WoodenKeg) &&
                    prefab!.GetComponentsInChildren<InteractableItems>(true).Length == 1;
            }).ToArray();
            if (ListingPrice == null || GameManager.Get_Friendship == null ||
                GameManager.Get_Friendship.Length <= (int)Actor_Type.Carole)
                throw new InvalidOperationException("The native keg listing or Carole friendship field is unsupported.");

            var found = new List<ListingBinding>(candidates.Length);
            var ids = new HashSet<ulong>();
            foreach (var display in candidates.OrderBy(value => SharedBuildingDoors.StablePath(value.transform), StringComparer.Ordinal))
            {
                var carole = ListingCarole?.GetValue(display) as Actor_Carole;
                GameObject? prefab = display.Get_RealObjectToBuy;
                var item = prefab == null ? null : prefab.GetComponent<InteractableItems>();
                ushort itemPrefab = prefab == null ? (ushort)0 : items.PurchasePrefab(prefab);
                if (!display.gameObject.scene.IsValid() || display.GetType() != typeof(ItemForSale_Kegs) ||
                    display.store is not Store_Bar || carole == null ||
                    carole.actor_Type != Actor_Type.Carole || itemPrefab == 0 ||
                    item == null || item.GetType() != typeof(Item_WoodenKeg) ||
                    prefab!.GetComponentsInChildren<InteractableItems>(true).Length != 1 ||
                    !ReferenceEquals(ListingCarole?.GetValue(display), carole))
                    throw new InvalidOperationException("The native keg listing, Carole gate, or wooden keg prefab is unsupported.");

                string path = SharedBuildingDoors.StablePath(display.transform) + "/keg-purchase-v1/" + itemPrefab;
                ulong entity = StaticId.Hash("monmp/store/kegs/v1/" + path);
                if (entity == 0 || !ids.Add(entity))
                    throw new InvalidOperationException("Native keg listings do not have unique stable identities: " + path);
                float originalPrice = display.Get_price;
                bool originalAvailable = display.gameObject.activeSelf;
                bool originalStoreOpen = display.store.isOpen;
                var binding = new ListingBinding(display, carole, itemPrefab, entity, originalPrice,
                    originalAvailable, originalStoreOpen, default);
                binding.State = Capture(binding);
                found.Add(binding);
            }
            listings = found.ToArray();
            listingsByEntity = listings.ToDictionary(value => value.Entity);
            if (Active != null && Active != this)
                throw new InvalidOperationException("A keg purchase adapter is already active.");
            Active = this;
        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            foreach (var listing in listings)
                yield return new WorldRecord(listing.Entity, 1, KegPurchaseState.Adapter, listing.State.Encode());
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            if (listings.Length == 0) return;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        // Harmony permits this one native call after the host has authorized a request.
        internal bool IsExecutingNative(ItemForSale_Kegs candidate) => host && ReferenceEquals(executingNative, candidate);

        // True means the click was handled and the game's local method must stop.
        internal bool Purchase(ItemForSale_Kegs candidate)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (host && IsExecutingNative(candidate)) return false;
            var listing = listings.FirstOrDefault(value => ReferenceEquals(value.Display, candidate));
            if (listing == null || channel?.FullySynchronized != true)
            {
                Report("Keg purchases are unavailable until the host world is synchronized.");
                return true;
            }
            if (NewPurchasesBlocked)
            {
                Report("Wait for the multiplayer save to finish before buying kegs.");
                return true;
            }
            if (pending.Count + timedOut.Count >= 8)
            {
                Report("Wait for the pending keg purchase to finish.");
                return true;
            }
            if (!host && !initialized)
            {
                Report("Keg purchases are unavailable until the host world is synchronized.");
                return true;
            }

            var quote = host ? Capture(listing) : listing.State;
            Send(listing, new KegPurchaseRequest(Guid.NewGuid(), listing.Entity, quote.ItemPrefab, quote.PriceCents), track: true);
            return true;
        }

        private void Send(ListingBinding listing, KegPurchaseRequest request, bool track)
        {
            if (channel == null) return;
            if (track && !pending.ContainsKey(request.Operation) && !timedOut.ContainsKey(request.Operation))
                pending.Add(request.Operation, (listing.Entity, Time.unscaledTime + CommandTimeout));
            channel.Request(listing.Entity, request.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || listings.Length == 0) return;
            // Repair batches are partial; availability comes from the complete committed
            // world. A full snapshot missing a listing still leaves initialization false.
            var accepted = channel?.Records ?? records;
            bool complete = true;
            foreach (var listing in listings)
            {
                if (!accepted.TryGetValue(listing.Entity, out var record)) { complete = false; continue; }
                Apply(listing, record);
            }
            initialized = complete;
        }

        private void Changed(WorldRecord record)
        {
            if (host || !listingsByEntity.TryGetValue(record.Id, out var listing) ||
                record.Adapter != KegPurchaseState.Adapter) return;
            Apply(listing, record);
        }

        private void Apply(ListingBinding listing, WorldRecord record)
        {
            if (record.Id != listing.Entity || record.Adapter != KegPurchaseState.Adapter)
                throw new InvalidDataException("Unknown keg purchase state record.");
            var next = KegPurchaseState.Decode(record.Payload);
            if (next.ItemPrefab != listing.ItemPrefab) throw new InvalidDataException("Keg listing item identity changed.");
            listing.State = next;
            ListingPrice!.SetValue(listing.Display, next.PriceCents / 100f);
            listing.Display.store.isOpen = next.StoreOpen;
            listing.Display.gameObject.SetActive(next.Available);
        }

        private KegPurchaseState Capture(ListingBinding listing)
        {
            if (listing == null || ListingPrice == null || GameManager.Get_Friendship == null ||
                GameManager.Get_Friendship.Length <= (int)Actor_Type.Carole)
                throw new InvalidOperationException("The native keg listing is unavailable.");
            int priceCents = Mathf.RoundToInt(listing.Display.Get_price * 100f);
            return new KegPurchaseState(listing.ItemPrefab, priceCents, GameManager.Get_Friendship[(int)Actor_Type.Carole],
                listing.Display.gameObject.activeSelf, listing.Display.store != null && listing.Display.store.isStoreOpen());
        }

        private void Rejected(uint peer, ulong id, byte[] payload) => PurchaseCommand(peer, id, payload, stale: true);
        [MonMP.Core.World.WorldCommandAdapters(KegPurchaseState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload) => PurchaseCommand(peer, id, payload, stale: false);

        private void PurchaseCommand(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || channel == null || session == null || cash == null ||
                !listingsByEntity.TryGetValue(id, out var listing) || !KegPurchaseRequest.Is(payload)) return;
            var request = KegPurchaseRequest.Decode(payload);
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);
            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var previous) && previous.Entity == id)
                {

#if MONMP_DEVELOPMENT
                    TestDuplicateReplays++;
#endif

                    channel.Reply(peer, id, previous.Reply.Encode());
                }
                else if (!hostPending.ContainsKey(key))
                    channel.Reply(peer, id, new KegPurchaseReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }

            void Finish(PurchaseStatus status, ulong item = 0)
            {
                var reply = new KegPurchaseReply(request.Operation, status, item);

#if MONMP_DEVELOPMENT
                TestLastReply = reply;
#endif

                results[key] = (id, reply);
                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, id, reply.Encode());
            }

            var current = Capture(listing);
            if (items.PeerHolding(peer) || hostPending.Values.Any(value => value.Peer == peer))
            {
                Finish(PurchaseStatus.HandsBusy);
                return;
            }
            bool exactTarget = id == listing.Entity && request.Listing == listing.Entity;
            bool exactListing = listing.Display.gameObject.scene.IsValid() && listing.Display.GetType() == typeof(ItemForSale_Kegs) &&
                listing.Display.store is Store_Bar && ReferenceEquals(ListingCarole?.GetValue(listing.Display), listing.Carole) &&
                listing.Carole.actor_Type == Actor_Type.Carole;
            GameObject? currentItem = listing.Display.Get_RealObjectToBuy;
            bool exactItem = request.ItemPrefab == listing.ItemPrefab && currentItem != null && items.PurchasePrefab(currentItem) == listing.ItemPrefab &&
                currentItem != null && currentItem.GetComponent<InteractableItems>()?.GetType() == typeof(Item_WoodenKeg);
            bool inRange = items.PurchaseInRange(peer, listing.Display.transform.position);
            bool canAfford = cash.GetCash >= current.PriceCents;
            PurchaseStatus decision = KegPurchasePolicy.ValidateHostRequest(stale, channel.FullySynchronized,
                NewPurchasesBlocked, results.Count < PurchaseClaims.Capacity, exactTarget, exactListing, exactItem,
                request.PriceCents, current.PriceCents, current.CaroleFriendship, current.Available, current.StoreOpen,
                inRange, canAfford);
            if (decision != PurchaseStatus.Placed)
            {
                if (decision == PurchaseStatus.Unavailable && current.CaroleFriendship < KegPurchaseState.MinimumFriendship)

#if MONMP_DEVELOPMENT
                    TestLockDenials++;
#else
{}
#endif

                Finish(decision);
                return;
            }

            // SharedStores owns the native Cash_Manager bypass and item lease. It subscribes here,
            // performs the native buy, and calls CompleteHostPurchase after delivery is registered.
            var executor = HostPurchaseRequested;
            if (executor == null)
            {
                Finish(PurchaseStatus.NativeRejected);
                return;
            }
            hostPending[key] = (peer, listing.Entity, Time.unscaledTime + 10f);
            try
            {
                executingNative = listing.Display;
                executor(peer, buyer.ProfileId, listing.Display, request);
            }
            catch (Exception ex)
            {
                executingNative = null;
                hostPending.Remove(key);
                Finish(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
            }
            finally { executingNative = null; Publish(); }
        }

        internal bool CompleteHostPurchase(Guid profile, KegPurchaseReply reply)
        {
            if (!host || session == null || channel == null || reply.Operation == Guid.Empty) return false;
            var key = (profile, reply.Operation);
            if (!hostPending.Remove(key, out var request)) return false;
            if (reply.Status == PurchaseStatus.Held || reply.Status == PurchaseStatus.Placed)
#if MONMP_DEVELOPMENT
TestNativePurchases++;
#else
{}
#endif


#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            results[key] = (request.Entity, reply);
            if (session.Peers.TryGetValue(request.Peer, out var buyer) && buyer.ProfileId == profile)
                channel.Reply(request.Peer, request.Entity, reply.Encode());
            Publish();
            return true;
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || !KegPurchaseReply.Is(payload)) return;
            if (!pending.Values.Any(value => value.Entity == id) && !timedOut.Values.Any(entity => entity == id)) return;
            var reply = KegPurchaseReply.Decode(payload);
            if (awaitingItems.ContainsKey(reply.Operation)) return;
            if (pending.TryGetValue(reply.Operation, out var waiting) && waiting.Entity == id)
                pending.Remove(reply.Operation);
            else if (timedOut.TryGetValue(reply.Operation, out var expiredEntity) && expiredEntity == id)
                timedOut.Remove(reply.Operation);
            else return;

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == PurchaseStatus.Held || reply.Status == PurchaseStatus.Placed)
            {
                // Confirmation can arrive before the queued item record/native binding.
                pending[reply.Operation] = (id, float.PositiveInfinity);
                awaitingItems[reply.Operation] = reply.Item;
                ResolvePurchaseItems();
                return;
            }
            int friendship = listingsByEntity.TryGetValue(id, out var listing) ? listing.State.CaroleFriendship : 0;
            Report(Message(reply.Status, friendship));
        }

        private void ResolvePurchaseItems()
        {
            if (awaitingItems.Count == 0 || channel == null) return;
            foreach (var entry in awaitingItems.ToArray())
            {
                if (!channel.Records.TryGetValue(entry.Value, out var record) || record.Adapter != ItemState.Adapter) continue;
                var state = ItemState.Decode(record.Payload);
                // Late confirmation describes a past handoff; dropped/consumed items
                // resolve against current state rather than the historical Held lease.
                if (state.Exists && items.ItemOf(entry.Value) == null) continue;
                awaitingItems.Remove(entry.Key);
                pending.Remove(entry.Key);
            }
        }

        private static string Message(PurchaseStatus status, int hostFriendship) => status switch
        {
            PurchaseStatus.Unavailable when hostFriendship < KegPurchaseState.MinimumFriendship => "Carole must reach 50 friendship before the host can buy kegs.",
            PurchaseStatus.Unavailable => "The keg listing is unavailable in the host's world.",
            PurchaseStatus.Closed => "The bar is closed.",
            PurchaseStatus.PriceChanged => "The keg price changed. Check the listing and try again.",
            PurchaseStatus.TooFar => "Move closer to the keg listing before buying.",
            PurchaseStatus.Funds => "Not enough shared cash.",
            PurchaseStatus.Uncertain => "Keg purchase outcome is uncertain. Check shared cash and ownership before trying again.",
            PurchaseStatus.Capacity => "The host cannot accept more keg purchases.",
            _ => "The host declined the keg purchase."
        };

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        private void Publish()
        {
            if (!host || listings.Length == 0 || channel == null || disposed) return;
            foreach (var listing in listings)
            {
                var next = Capture(listing);
                byte[] payload = next.Encode();
                if (listing.State.Encode().SequenceEqual(payload)) continue;
                listing.State = next;
                channel.Publish(listing.Entity, payload);
            }
        }

        public void Update()
        {
            if (disposed || session?.IsRunning != true || listings.Length == 0 || channel == null) return;
            ResolvePurchaseItems();
            if (pending.Count > 0) foreach (var entry in pending.Where(value => Time.unscaledTime >= value.Value.Deadline).ToArray())
            {
                pending.Remove(entry.Key);
                timedOut[entry.Key] = entry.Value.Entity;
                Report("Keg purchase confirmation is delayed. Check shared cash and the listing before retrying.");
            }
            if (host)
            {
                if (hostPending.Count > 0) foreach (var entry in hostPending.Where(value => Time.unscaledTime >= value.Value.Deadline).ToArray())
                {
                    hostPending.Remove(entry.Key);
                    var reply = new KegPurchaseReply(entry.Key.Operation, PurchaseStatus.Uncertain);
                    results[entry.Key] = (entry.Value.Entity, reply);

#if MONMP_DEVELOPMENT
                    TestLastReply = reply;
#endif

                    if (session.Peers.TryGetValue(entry.Value.Peer, out var buyer) && buyer.ProfileId == entry.Key.Profile)
                        channel.Reply(entry.Value.Peer, entry.Value.Entity, reply.Encode());
                }
            }
            if (Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            if (host) Publish();
            else if (initialized)
            {
                foreach (var listing in listings)
                {
                    var actual = Capture(listing);
                    if (actual.Encode().SequenceEqual(listing.State.Encode())) continue;
                    ListingPrice!.SetValue(listing.Display, listing.State.PriceCents / 100f);
                    listing.Display.store.isOpen = listing.State.StoreOpen;
                    listing.Display.gameObject.SetActive(listing.State.Available);
                }
            }
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest(Guid operation, int priceCents)
        {
            if (listings.Length == 0) return;
            var listing = listings[0];
            Send(listing, new KegPurchaseRequest(operation, listing.Entity, listing.ItemPrefab, priceCents), track: true);
        }
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (channel != null && listings.Length != 0)
                {
                    channel.SnapshotApplied -= Snapshot;
                    channel.Changed -= Changed;
                    channel.CommandRequested -= Command;
                    channel.CommandRejected -= Rejected;
                    channel.ReplyReceived -= Reply;
                }
                if (!host)
                {
                    foreach (var listing in listings)
                    {
                        var display = listing.Display;
                        // OnDestroy can run after the native scene has destroyed this component.
                        // A surviving listing and a surviving store restore independently.
                        if (display == null) continue;
                        ListingPrice?.SetValue(display, listing.OriginalPrice);
                        if (display.store != null) display.store.isOpen = listing.OriginalStoreOpen;
                        display.gameObject.SetActive(listing.OriginalAvailable);
                    }
                }
            }
            finally
            {
                pending.Clear();
                timedOut.Clear();
                awaitingItems.Clear();
                hostPending.Clear();
                if (Active == this) Active = null;
                session = null;
                channel = null;
            }
        }

    }
}
