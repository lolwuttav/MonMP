using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedStores : IDisposable
    {
        private sealed class Listing
        {
            public ulong Id; public ItemForSale_Display Display = null!; public Stores Store = null!; public ushort Prefab;
            public bool WindowTint, CarLift, SpeedwayCarLift, PrefilledCanCrate;
            public Color WindowTintColor;
            public bool RequiresLeoTamed;
            public ToDoList_Line? RequiredToDo;
            public StoreListingState Original = null!; public StoreListingState Current = null!;
        }
        private readonly Dictionary<ulong, Listing> listings = new Dictionary<ulong, Listing>();
        private readonly Dictionary<ItemForSale_Display, Listing> byDisplay = new Dictionary<ItemForSale_Display, Listing>();
        private readonly SharedItems items;
        private readonly bool host;
        private readonly Cash_Manager cash;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), (ulong Listing, PurchaseResult Result)> results = new Dictionary<(Guid, Guid), (ulong, PurchaseResult)>();
        private readonly List<(uint Peer, Guid Profile, Guid Operation, ulong Listing, InteractableItems Item, float Deadline, bool Uncertain)> deliveries = new List<(uint, Guid, Guid, ulong, InteractableItems, float, bool)>();
        private readonly Dictionary<Guid, (ulong Listing, float Deadline)> pending = new Dictionary<Guid, (ulong, float)>();
        private readonly List<(SharedKegPurchase Adapter, uint Peer, Guid Profile, KegPurchaseRequest Request, InteractableItems Item, float Deadline, bool Uncertain)> kegDeliveries =
            new List<(SharedKegPurchase, uint, Guid, KegPurchaseRequest, InteractableItems, float, bool)>();
        private readonly Dictionary<Guid, ulong> awaitingItems = new Dictionary<Guid, ulong>();

#if MONMP_DEVELOPMENT
        internal PurchaseResult? TestLastResult { get; private set; }


        internal int TestSuccessfulReplies { get; private set; }


        internal int TestReplies { get; private set; }


        internal int TestLeoDenials { get; private set; }


        internal int TestToDoDenials { get; private set; }


        internal bool TestDeferDelivery { get; set; }


        internal Guid? TestNextOperation { get; set; }
#endif

        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextCapture, nextRepair;
        private bool disposed, initialized;
        private Listing? executingListing;
        private ItemForSale_Kegs? executingKegListing;
        private bool executingNativePurchase;
        internal Func<Guid, Guid, ulong, bool>? DurableClaim { get; set; }
        private static readonly FieldInfo? WindowTintToolColor = AccessTools.Field(typeof(Tool_WindowTint), "tintColor");
        private static readonly FieldInfo? CarLiftSpeedwayField = AccessTools.Field(typeof(ItemForSale_Display_CarLift), "SpeedwayLift");
        public bool NewPurchasesBlocked { get; set; }
        // A completed uncertain receipt is retained for deduplication. Only outstanding work can still mutate this save.
        public bool TransactionsSettled => pending.Count == 0 && deliveries.Count == 0 && kegDeliveries.Count == 0 && !ExecutingPeer.HasValue;
        internal uint? ExecutingPeer { get; private set; }
        internal ItemForSale_Display? HoverDisplay { get; set; }
        internal bool CanQuote(float price) => HoverDisplay != null && (byDisplay.TryGetValue(HoverDisplay, out var l) ? l.Current.Price == price :
            HoverDisplay is ItemForSale_Kegs && HoverDisplay.Get_price == price); // kegs buy through SharedKegPurchase
        internal bool AllowNativePurchaseCall(ItemForSale_Display display) => executingNativePurchase &&
            ((executingListing != null && (executingListing.WindowTint || executingListing.CarLift) && ReferenceEquals(executingListing.Display, display)) ||
             (executingKegListing != null && ReferenceEquals(executingKegListing, display)));
        internal bool AllowNativeAffordabilityQuote(float price) => executingNativePurchase &&
            (executingListing?.WindowTint == true || executingListing?.CarLift == true || executingKegListing != null) &&
            (executingKegListing != null ? executingKegListing.Get_price : executingListing!.Display.Get_price) == price;
        internal bool AllowNativeEconomyMutation(string methodName) => executingNativePurchase &&
            (executingListing?.WindowTint == true || executingListing?.CarLift == true || executingKegListing != null) &&
            methodName == nameof(Cash_Manager.Event_Spent);

#if MONMP_DEVELOPMENT
        internal int TestPurchases { get; private set; }


        internal ulong[] TestListings => listings.Keys.OrderBy(id => id).ToArray();


        internal ItemForSale_Display TestDisplay(ulong id) => listings[id].Display;


        internal StoreListingState TestState(ulong id) => listings[id].Current;


        internal bool TestRestored => listings.Values.All(l => Capture(l).Encode().SequenceEqual(l.Original.Encode()));


        internal void TestRequest(ulong id, Guid operation, float price) => SendRequest(id, new PurchaseRequest(operation, price));
#endif

        public SharedStores(bool host, SharedItems items, string diagnostics)
        {
            this.host = host; this.items = items; cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            var identities = new IdentityRegistry();
            var excluded = new List<string>();
            var hardware = UnityEngine.Object.FindObjectOfType<HardwareStore>(true);
            foreach (var display in UnityEngine.Object.FindObjectsOfType<ItemForSale_Display>(true))
            {
                var store = HardModeWorldLayout.ResolveItemStore(display, hardware);
                bool dogProduct = display is ItemForSale_Display_DogProduct;
                bool todoUnlock = display.GetType() == typeof(ItemForSale_Display_ToDoListToUnlock);
                bool windowTint = display.GetType() == typeof(ItemForSale_Display_WindowTint);
                bool carLift = display.GetType() == typeof(ItemForSale_Display_CarLift);
                bool speedwayCarLift = false;
                if (carLift)
                {
                    if (CarLiftSpeedwayField == null || CarLiftSpeedwayField.FieldType != typeof(bool) ||
                        CarLiftSpeedwayField.GetValue(display) is not bool speedwayValue) continue;
                    speedwayCarLift = speedwayValue;
                }
                if ((display.GetType() != typeof(ItemForSale_Display) && !dogProduct && !todoUnlock && !windowTint && !carLift) || !display.gameObject.scene.IsValid() || store == null ||
                    ((!windowTint) && display.CustomIntValue1 != -1 && (display.GetType() != typeof(ItemForSale_Display) || display.CustomIntValue1 < 0)) || (windowTint && display.CustomIntValue1 != -1 &&
                    (display.CustomIntValue1 < 0 || display.CustomIntValue1 > 6)))
                { excluded.Add(display.GetType().Name + "	custom=" + display.CustomIntValue1 + "	" + display.Get_price + "	" + (display.Get_RealObjectToBuy == null ? "?" : display.Get_RealObjectToBuy.name) + "	" + SharedBuildingDoors.StablePath(display.transform)); continue; }
                ushort prefab = items.StorePurchasePrefab(display.Get_RealObjectToBuy!, out bool prefilledCanCrate);
                if (prefab == 0) { excluded.Add(display.GetType().Name + "	no prefab	" + display.Get_price + "	" + (display.Get_RealObjectToBuy == null ? "?" : display.Get_RealObjectToBuy.name) + "	" + SharedBuildingDoors.StablePath(display.transform)); continue; }
                Color windowTintColor = default;
                if (windowTint && !TryWindowTintFactory(display, out windowTintColor)) continue;
                if (carLift && !TryCarLiftFactory(display, prefab, out speedwayCarLift)) continue;
                // A raw sibling-index path can shift when a host's real save deletes a neighboring
                // default item that a menu-joined guest's suppressed scene never deletes (see
                // GlobalClassManager.CheckIfItemNeedToBeDeleted). The same-name ordinal is immune.
                bool requiresLeoTamed = dogProduct && (bool)AccessTools.Field(typeof(ItemForSale_Display_DogProduct), "NeedLeoTamedToPurschase")!.GetValue(display)!;
                ToDoList_Line? requiredToDo = null;
                if (todoUnlock)
                {
                    var lineField = AccessTools.Field(typeof(ItemForSale_Display_ToDoListToUnlock), "toDoList_Line");
                    if (lineField == null || lineField.FieldType != typeof(ToDoList_Line) || lineField.GetValue(display) is not ToDoList_Line line ||
                        !Enum.IsDefined(typeof(ToDoList_Line), line)) continue;
                    requiredToDo = line;
                }
                if (carLift && speedwayCarLift) requiredToDo = ToDoList_Line.Speedway_Completed;
                string identity = SharedBuildingDoors.StablePath(display.transform) + "/shared-store-v1/" + prefab + "/" + display.Get_CanBuyMultiplesADay +
                    (dogProduct ? "/dog-leo-" + requiresLeoTamed : "") +
                    (requiredToDo.HasValue ? "/todo-line-" + (int)requiredToDo.Value : "") +
                    (windowTint ? "/window-tint-roll-v1-" + TintKey(windowTintColor) + "/uses-" + display.CustomIntValue1 : "") +
                    (carLift ? (speedwayCarLift ? "/car-lift-speedway-v1" : "/car-lift-home-v1") : "") +
                    (prefilledCanCrate ? "/prefilled-empty-cans-v1" : "") +
                    // Bolt packs and tires share one prefab per size; native TryToBuy stamps this size on the
                    // bought item. Skipping them left guests quoted "not enough cash" for every size.
                    // The traveling van rolls its bolt pack colours into CustomIntValue1 on every opening, so
                    // that value is stock (SharedTravelingVan), not identity.
                    (!windowTint && display.CustomIntValue1 >= 0 && !(store is Store_TravelingVan) ? "/custom-int-" + display.CustomIntValue1 : "");
                var l = new Listing { Id = identities.Register(identity), Display = display, Store = store, Prefab = prefab, WindowTint = windowTint,
                    WindowTintColor = windowTintColor, RequiresLeoTamed = requiresLeoTamed, RequiredToDo = requiredToDo,
                    CarLift = carLift, SpeedwayCarLift = speedwayCarLift, PrefilledCanCrate = prefilledCanCrate };
                l.Original = l.Current = Capture(l); listings.Add(l.Id, l); byDisplay.Add(display, l);
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-stores-excluded.tsv"), excluded);


            File.WriteAllLines(Path.Combine(diagnostics, "shared-stores.tsv"), new[] { "id\tprefab\tprice\tmultiple\trequiredTodo\twindowTint\tpath" }.Concat(listings.Values.Select(l =>
                l.Id.ToString("X16") + "\t" + l.Prefab + "\t" + l.Current.Price.ToString("R", CultureInfo.InvariantCulture) + "\t" + l.Current.Multiple + "\t" +
                (l.RequiredToDo.HasValue ? l.RequiredToDo.Value.ToString() : "") + "\t" + (l.WindowTint ? TintKey(l.WindowTintColor) : "") + "\t" + SharedBuildingDoors.StablePath(l.Display.transform))));
#endif

        }
        private StoreListingState Capture(Listing l) => new StoreListingState(l.Prefab, l.Display.Get_price, l.Display.gameObject.activeSelf, l.Store.isStoreOpen(), l.Display.Get_CanBuyMultiplesADay);
        private bool TryWindowTintFactory(ItemForSale_Display display, out Color color)
        {
            color = default;
            if (display == null || display.GetType() != typeof(ItemForSale_Display_WindowTint) ||
                WindowTintToolColor == null || display.Get_RealObjectToBuy == null) return false;
            var tool = display.Get_RealObjectToBuy.GetComponent<Tool_WindowTint>();
            if (tool == null || tool.GetType() != typeof(Tool_WindowTint) ||
                display.Get_RealObjectToBuy.GetComponentsInChildren<InteractableItems>(true).Length != 1 ||
                WindowTintToolColor.GetValue(tool) is not Color toolColor || !ValidTintColor(toolColor)) return false;
            // Native colored displays use rounded preview colors that differ from the actual roll.
            // Only the canonical purchased tool supplies gameplay color and replica identity.
            color = toolColor;
            return true;
        }
        private static bool ValidTintColor(Color color) =>
            color.r >= 0 && color.r <= 1 && color.g >= 0 && color.g <= 1 &&
            color.b >= 0 && color.b <= 1 && color.a >= 0 && color.a <= 1;
        private bool MatchesPurchaseFactory(Listing listing) =>
            items.StorePurchasePrefab(listing.Display.Get_RealObjectToBuy, out bool prefilledCanCrate) == listing.Prefab &&
            prefilledCanCrate == listing.PrefilledCanCrate;
        private bool TryCarLiftFactory(ItemForSale_Display display, ushort expectedPrefab, out bool speedway)
        {
            speedway = false;
            if (display == null || display.GetType() != typeof(ItemForSale_Display_CarLift) || CarLiftSpeedwayField == null ||
                CarLiftSpeedwayField.FieldType != typeof(bool) || CarLiftSpeedwayField.GetValue(display) is not bool variant ||
                display.Get_RealObjectToBuy == null || display.CustomIntValue1 != -1) return false;
            var factory = display.Get_RealObjectToBuy;
            var item = factory.GetComponent<InteractableItems>();
            if (item == null || item.GetType() != typeof(Furniture_CarLift) ||
                factory.GetComponentsInChildren<InteractableItems>(true).Length != 1 || items.PurchasePrefab(factory) != expectedPrefab) return false;
            speedway = variant;
            return true;
        }
        private bool TryCarLiftDestination(Listing listing, out Transform? destination)
        {
            destination = null;
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            if (gameplay == null) return false;
            if (listing.SpeedwayCarLift)
            {
                if (gameplay.Speedway == null) return false;
                destination = gameplay.Speedway.Get_CarLiftSpawnLocation;
            }
            else
            {
                if (gameplay.home == null) return false;
                destination = gameplay.home.Get_CarLiftSpawnLocation;
            }
            return destination != null && destination.gameObject.scene.IsValid();
        }
        private static bool SameColor(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        private static string TintKey(Color color) => color.r.ToString("R", CultureInfo.InvariantCulture) + "," +
            color.g.ToString("R", CultureInfo.InvariantCulture) + "," + color.b.ToString("R", CultureInfo.InvariantCulture) + "," +
            color.a.ToString("R", CultureInfo.InvariantCulture);
        private bool IsWindowTintProduct(Listing listing, InteractableItems? item) => listing.WindowTint && item is Tool_WindowTint tool &&
            tool.GetType() == typeof(Tool_WindowTint) && TryWindowTintFactory(listing.Display, out var factoryColor) &&
            SameColor(factoryColor, listing.WindowTintColor) && WindowTintToolColor?.GetValue(tool) is Color itemColor &&
            SameColor(itemColor, listing.WindowTintColor) && tool.IntValue1 >= 0 && tool.IntValue1 <= tool.IntValue1_Max &&
            tool.IntValue1 == (listing.Display.CustomIntValue1 == -1 ? tool.IntValue1_Max : listing.Display.CustomIntValue1);
        private bool IsCarLiftInstance(Listing listing, InteractableItems? item)
        {
            if (!listing.CarLift || item == null || item.GetType() != typeof(Furniture_CarLift) ||
                item.gameObject.GetComponent<InteractableItems>() != item ||
                item.GetComponentsInChildren<InteractableItems>(true).Length != 1) return false;
            var factory = listing.Display.Get_RealObjectToBuy;
            var factoryItem = factory == null ? null : factory.GetComponent<InteractableItems>();
            return factory != null && factoryItem != null && factoryItem.GetType() == typeof(Furniture_CarLift) &&
                factory.GetComponentsInChildren<InteractableItems>(true).Length == 1 &&
                items.PurchasePrefab(factory) == listing.Prefab && item.PrefabName == factoryItem.PrefabName;
        }
        public IEnumerable<WorldRecord> InitialRecords() => listings.Values.Select(l => new WorldRecord(l.Id, 1, StoreListingState.Adapter, l.Original.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        { this.session = session; this.channel = channel; channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command; channel.CommandRejected += Stale; channel.ReplyReceived += Reply; }
        private StoreListingState Decode(WorldRecord record)
        {
            if (record.Adapter != StoreListingState.Adapter || !listings.TryGetValue(record.Id, out var l)) throw new InvalidDataException("Unknown store listing.");
            var state = StoreListingState.Decode(record.Payload);
            if (state.Prefab != l.Prefab || state.Multiple != l.Original.Multiple) throw new InvalidDataException("Changed store listing identity."); return state;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var staged = records.Values.Where(r => r.Adapter == StoreListingState.Adapter).Select(r => (r.Id, State: Decode(r))).ToArray();
            foreach (var p in staged) { listings[p.Id].Current = p.State; Apply(listings[p.Id], p.State); } initialized = true;
        }
        private void Changed(WorldRecord record)
        { if (host || record.Adapter != StoreListingState.Adapter) return; var state = Decode(record); listings[record.Id].Current = state; Apply(listings[record.Id], state); }
        private static void Apply(Listing l, StoreListingState state)
        {
            AccessTools.Field(typeof(ItemForSale_Display), "price").SetValue(l.Display, state.Price);
            l.Store.isOpen = state.Open; l.Display.gameObject.SetActive(state.Available);
        }
        // True means the adapter handled the attempt; the native method must not execute locally.
        public bool Request(ItemForSale_Display display)
        {
            if (disposed || session?.IsRunning != true || ExecutingPeer.HasValue || display == null || !byDisplay.TryGetValue(display, out var l)) return false;
            if (channel?.FullySynchronized != true) return true;
            if (NewPurchasesBlocked) { MonMPPlugin.Instance!.ReportItemUseUnavailable("Wait for the multiplayer save to finish before buying."); return true; }
            Guid operation = Guid.NewGuid();
#if MONMP_DEVELOPMENT
            operation = TestNextOperation ?? operation;
TestNextOperation = null;
#endif

            SendRequest(l.Id, new PurchaseRequest(operation, l.Current.Price)); return true;
        }
        // This display subclass checks ToDo locally before calling the base MouseDown. During
        // multiplayer, always route supported listings through the host so the host's progress
        // controls the decision; refuse an unregistered variant rather than allowing local code.
        internal bool RequestProgressionDisplay(ItemForSale_Display display)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (display == null || !byDisplay.TryGetValue(display, out var listing) || !listing.RequiredToDo.HasValue)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("This locked display is not supported in the current multiplayer session.");
                return true;
            }
            if (!Request(display))
                MonMPPlugin.Instance?.ReportItemUseUnavailable("This purchase cannot be processed right now.");
            return true;
        }
        private void SendRequest(ulong id, PurchaseRequest request)
        {
            if (pending.Count >= 16 && !pending.ContainsKey(request.Operation))
            { MonMPPlugin.Instance!.ReportItemUseUnavailable("Wait for your pending purchases to finish."); return; }
            pending[request.Operation] = (id, Time.unscaledTime + 10);
            items.NotePurchaseView();
            channel!.Request(id, request.Encode());
        }
        private void Reply(ulong id, byte[] payload)
        {
            if (!listings.ContainsKey(id)) return;
            var result = PurchaseResult.Decode(payload);
            if (!pending.TryGetValue(result.Operation, out var request) || request.Listing != id || awaitingItems.ContainsKey(result.Operation)) return;

#if MONMP_DEVELOPMENT
            TestLastResult = result;

 TestReplies++;
#endif

            if (result.Status == PurchaseStatus.Held || result.Status == PurchaseStatus.Placed)
            {
                GuestAchievements.Purchased(listings[id].Display);

#if MONMP_DEVELOPMENT
                TestSuccessfulReplies++;
#endif

                // Replies can precede the queued item record and its native binding.
                // Keep the transaction gated until the current authoritative item is known.
                pending[result.Operation] = (id, float.PositiveInfinity);
                awaitingItems[result.Operation] = result.Item;
                ResolvePurchaseItems();
                // Vanilla purchases confirm through the cash animation, item and actor cue.
                return;
            }
            else pending.Remove(result.Operation);
            var listing = listings[id];
            string message = result.Status == PurchaseStatus.Unavailable && listing.RequiresLeoTamed && listing.Display != null && listing.Display.gameObject.activeInHierarchy
                ? "Leo must be tamed in the host's world before buying this product." : Message(result.Status);
            if (result.Status == PurchaseStatus.Unavailable && listing.RequiredToDo.HasValue)
                message = "This display is locked or unavailable in the host's world.";
            MonMPPlugin.Instance!.ReportItemUseUnavailable(message);
        }
        private void ResolvePurchaseItems()
        {
            if (awaitingItems.Count == 0 || channel == null) return;
            foreach (var entry in awaitingItems.ToArray())
            {
                if (!channel.Records.TryGetValue(entry.Value, out var record) || record.Adapter != ItemState.Adapter) continue;
                var state = ItemState.Decode(record.Payload);
                // A late result describes a past handoff. The item may already have
                // been dropped or consumed; do not require the original Held lease.
                if (state.Exists && items.ItemOf(entry.Value) == null) continue;
                awaitingItems.Remove(entry.Key);
                pending.Remove(entry.Key);
            }
        }
        private static string Message(PurchaseStatus status)
        {
            switch (status)
            {
                case PurchaseStatus.Unavailable: return "That item is no longer available.";
                case PurchaseStatus.Closed: return "The store is closed.";
                case PurchaseStatus.PriceChanged: return "The listing changed. Check the price and try again.";
                case PurchaseStatus.TooFar: return "Move closer to the item before buying it.";
                case PurchaseStatus.HandsBusy: return "Put down your current item before buying another.";
                case PurchaseStatus.Funds: return "Not enough shared cash.";
                case PurchaseStatus.NativeRejected: return "The store declined this purchase.";
                case PurchaseStatus.Capacity: return "This session cannot accept more purchases.";
                case PurchaseStatus.Uncertain: return "Purchase outcome is uncertain. Check your cash and the display before buying again.";
                default: return "That purchase request has already been handled.";
            }
        }
        private void Finish(uint peer, Guid profile, ulong id, PurchaseResult result)
        {
            results[(profile, result.Operation)] = (id, result);
            if (session!.Peers.TryGetValue(peer, out var buyer) && buyer.ProfileId == profile)
                channel!.Reply(peer, id, result.Encode());
        }
        [MonMP.Core.World.WorldCommandAdapters(StoreListingState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload) => Purchase(peer, id, payload, false);
        private void Stale(uint peer, ulong id, byte[] payload) => Purchase(peer, id, payload, true);
        private bool PeerPurchasePending(uint peer)
        {
            return items.PeerHolding(peer) || deliveries.Any(d => d.Peer == peer) || kegDeliveries.Any(d => d.Peer == peer);
        }
        private void Purchase(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || !listings.TryGetValue(id, out var l)) return;
            var request = PurchaseRequest.Decode(payload);
            if (!session!.Peers.TryGetValue(peer, out var buyer)) return;
            if (!claims.Claim(buyer.ProfileId, request.Operation))
            {
                if (results.TryGetValue((buyer.ProfileId, request.Operation), out var prior)) channel!.Reply(peer, id, (prior.Listing == id ? prior.Result : new PurchaseResult(request.Operation, PurchaseStatus.RetryRejected)).Encode());
                else if (!deliveries.Any(d => d.Profile == buyer.ProfileId && d.Operation == request.Operation))
                    channel!.Reply(peer, id, new PurchaseResult(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }
            void Reject(PurchaseStatus reason) => Finish(peer, buyer.ProfileId, id, new PurchaseResult(request.Operation, reason));
            // Already committed operations above still return their original result while draining.
            if (NewPurchasesBlocked) { Reject(PurchaseStatus.NativeRejected); return; }
            if (l.RequiresLeoTamed && !HostLeoTamed()) {
#if MONMP_DEVELOPMENT
TestLeoDenials++;
#endif
 Reject(PurchaseStatus.Unavailable); return; }
            if (l.RequiredToDo.HasValue && !HostTodoDone(l.RequiredToDo.Value)) {
#if MONMP_DEVELOPMENT
TestToDoDenials++;
#endif
 Reject(PurchaseStatus.Unavailable); return; }
            var display = l.Display;
            if (stale || request.Price != display.Get_price) { Reject(PurchaseStatus.PriceChanged); return; }
            if (!display.gameObject.activeInHierarchy || !display.gameObject.activeSelf) { Reject(PurchaseStatus.Unavailable); return; }
            if (!l.Store.isStoreOpen()) { Reject(PurchaseStatus.Closed); return; }
            // Cash_Manager.TryToBuy dereferences the native pointer. Inactive special
            // placeholders may legitimately have no pointer; an active purchase must
            // have completed native enabling and still belong to its registered owner.
            if (display.store == null || display.store != l.Store) { Reject(PurchaseStatus.NativeRejected); return; }
            if (!items.PurchaseInRange(peer, display.transform.position)) { Reject(PurchaseStatus.TooFar); return; }
            if (PeerPurchasePending(peer)) { Reject(PurchaseStatus.HandsBusy); return; }
            if (!MatchesPurchaseFactory(l)) { Reject(PurchaseStatus.NativeRejected); return; }
            if (l.WindowTint && (items.PurchasePrefab(display.Get_RealObjectToBuy) != l.Prefab ||
                !TryWindowTintFactory(display, out var registeredColor) || !SameColor(registeredColor, l.WindowTintColor)))
            { Reject(PurchaseStatus.NativeRejected); return; }
            Transform? carLiftDestination = null;
            if (l.CarLift && (!TryCarLiftFactory(display, l.Prefab, out bool speedwayVariant) || speedwayVariant != l.SpeedwayCarLift ||
                (l.SpeedwayCarLift && !HostTodoDone(ToDoList_Line.Speedway_Completed)) || !TryCarLiftDestination(l, out carLiftDestination)))
            { Reject(PurchaseStatus.Unavailable); return; }
            if ((l.WindowTint || l.CarLift) && !HostPoliceAllowsPurchase(l.Store))
            { Reject(PurchaseStatus.Unavailable); return; }
            if (!cash.isCanAfford(display.Get_price)) { Reject(PurchaseStatus.Funds); return; }
            if (ExecutingPeer.HasValue) { Reject(PurchaseStatus.NativeRejected); return; }
            // Write the operation claim before native cash/item mutation. A previous host
            // process may have died after this point, so the same operation is uncertain.
            try
            {
                if (DurableClaim == null || !DurableClaim(buyer.ProfileId, request.Operation, id))
                { Reject(PurchaseStatus.Uncertain); return; }
            }
            catch (TransactionReceiptCapacityException)
            { Reject(PurchaseStatus.Capacity); return; }
            catch (Exception ex)
            {
                Reject(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
                return;
            }
            ExecutingPeer = peer;
            executingListing = l.WindowTint || l.CarLift ? l : null;
            executingNativePurchase = l.WindowTint || l.CarLift;
            try
            {
                int cashBefore = l.WindowTint || l.CarLift || l.PrefilledCanCrate ? cash.GetCash : 0;
                var bought = cash.TryToBuy(display, display.CustomIntValue1, display.ActorCommentingOnProduct, false);
                if (bought == null) Reject(PurchaseStatus.NativeRejected);
                else
                {
                    bool uncertain = false;
                    if (l.CarLift)
                    {
                        // The native subclass normally places this instance after TryToBuy returns.
                        // We invoke the factory directly under the scoped economy bypass, then apply
                        // the exact host-owned home/Speedway destination before item discovery.
                        if (!TryCarLiftFactory(display, l.Prefab, out bool speedwayResultVariant) || speedwayResultVariant != l.SpeedwayCarLift ||
                            !IsCarLiftInstance(l, bought) || carLiftDestination == null)
                        {

#if MONMP_DEVELOPMENT
                            TestPurchases++;
#endif

                            Reject(PurchaseStatus.Uncertain);
                            return;
                        }
                        bought.transform.SetParent(null, true);
                        bought.transform.SetPositionAndRotation(carLiftDestination.position, carLiftDestination.rotation);
                        if (Vector3.Distance(bought.transform.position, carLiftDestination.position) > 0.01f ||
                            Quaternion.Angle(bought.transform.rotation, carLiftDestination.rotation) > 0.1f)
                        {

#if MONMP_DEVELOPMENT
                            TestPurchases++;
#endif

                            Reject(PurchaseStatus.Uncertain);
                            return;
                        }
                    }
                    else bought.transform.SetParent(null, true);

#if MONMP_DEVELOPMENT
                    TestPurchases++;
#endif

                    if (l.WindowTint)
                        uncertain |= cash.GetCash != cashBefore - Mathf.RoundToInt(display.Get_price * 100f) ||
                            display.gameObject.activeSelf != display.Get_CanBuyMultiplesADay || !IsWindowTintProduct(l, bought);
                    if (l.CarLift || l.PrefilledCanCrate)
                        uncertain |= cash.GetCash != cashBefore - Mathf.RoundToInt(display.Get_price * 100f) ||
                            display.gameObject.activeSelf != display.Get_CanBuyMultiplesADay;
                    deliveries.Add((peer, buyer.ProfileId, request.Operation, id, bought, Time.unscaledTime + 5, uncertain));
                }
            }
            catch (Exception ex) { Reject(PurchaseStatus.Uncertain); Debug.LogException(ex); }
            finally { executingNativePurchase = false; executingListing = null; ExecutingPeer = null; Publish(l); }
            // Lease the item to the buyer in the same frame it is bound, so its first record already
            // puts it in the guest's hand instead of spawning it loose at the display for a round trip.

#if MONMP_DEVELOPMENT
            if (!TestDeferDelivery) Deliver();
#else
Deliver();
#endif

        }
        private void Deliver()
        {
            foreach (var delivery in deliveries.ToArray())
            {
                if (delivery.Item != null && (!listings[delivery.Listing].PrefilledCanCrate || PrefilledSyrupCanCrate.Ready(delivery.Item)) &&
                    items.DeliverPurchase(delivery.Item, delivery.Peer, out ulong entity, out bool held))
                {
                    deliveries.Remove(delivery);
                    Finish(delivery.Peer, delivery.Profile, delivery.Listing, new PurchaseResult(delivery.Operation,
                        delivery.Uncertain ? PurchaseStatus.Uncertain : held ? PurchaseStatus.Held : PurchaseStatus.Placed,
                        delivery.Uncertain ? 0 : entity));
                }
                else if (delivery.Item == null || Time.unscaledTime >= delivery.Deadline)
                {
                    deliveries.Remove(delivery);
                    Finish(delivery.Peer, delivery.Profile, delivery.Listing, new PurchaseResult(delivery.Operation, PurchaseStatus.Uncertain));
                }
            }
        }
        private void Publish(Listing l)
        { l.Current = Capture(l); channel!.Publish(l.Id, l.Current.Encode()); }
        private static bool HostLeoTamed()
        {
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            return gameplay != null && gameplay.Get_Leo_Manager != null && gameplay.Get_Leo_Manager.Get_IsTamed;
        }
        internal void ExecuteKegPurchase(SharedKegPurchase adapter, uint peer, Guid profile, ItemForSale_Kegs display, KegPurchaseRequest request)
        {
            void Complete(PurchaseStatus status, ulong entity = 0) =>
                adapter.CompleteHostPurchase(profile, new KegPurchaseReply(request.Operation, status, entity));

            if (!host || disposed || display == null || NewPurchasesBlocked || !session!.Peers.TryGetValue(peer, out var buyer) || buyer.ProfileId != profile)
            { Complete(PurchaseStatus.NativeRejected); return; }
            if (PeerPurchasePending(peer))
            { Complete(PurchaseStatus.HandsBusy); return; }

            GameObject? prefab = display.Get_RealObjectToBuy;
            InteractableItems? prefabItem = prefab == null ? null : prefab.GetComponent<InteractableItems>();
            if (display.GetType() != typeof(ItemForSale_Kegs) || display.store is not Store_Bar || prefabItem?.GetType() != typeof(Item_WoodenKeg) ||
                items.PurchasePrefab(prefab!) != request.ItemPrefab || !display.gameObject.activeInHierarchy || !display.store.isStoreOpen())
            { Complete(PurchaseStatus.Unavailable); return; }
            if (!items.PurchaseInRange(peer, display.transform.position)) { Complete(PurchaseStatus.TooFar); return; }
            if (!cash.isCanAfford(display.Get_price)) { Complete(PurchaseStatus.Funds); return; }
            if (ExecutingPeer.HasValue) { Complete(PurchaseStatus.NativeRejected); return; }

            // Keg purchases bypass SharedStores.Purchase, so persist the same idempotency
            // receipt before the native cash/item mutation.
            try
            {
                if (DurableClaim == null || !DurableClaim(buyer.ProfileId, request.Operation, request.Listing))
                { Complete(PurchaseStatus.Uncertain); return; }
            }
            catch (TransactionReceiptCapacityException)
            { Complete(PurchaseStatus.Capacity); return; }
            catch (Exception ex)
            {
                Complete(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
                return;
            }

            ExecutingPeer = peer;
            executingKegListing = display;
            executingNativePurchase = true;
            int cashBefore = cash.GetCash;
            float priceBefore = display.Get_price;
            InteractableItems? bought = null;
            bool nativeThrew = false;
            try
            {
                bought = cash.TryToBuy(display, display.CustomIntValue1, display.ActorCommentingOnProduct, false);
            }
            catch (Exception ex)
            {
                nativeThrew = true;
                Complete(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
            }
            finally
            {
                executingNativePurchase = false;
                executingKegListing = null;
                ExecutingPeer = null;
            }

            if (nativeThrew) return;
            if (bought == null)
            {
                Complete(PurchaseStatus.NativeRejected);
                return;
            }

            if (bought.GetType() != typeof(Item_WoodenKeg))
            {
                Complete(PurchaseStatus.Uncertain);
                return;
            }
            bought.transform.SetParent(null, true);
            bool uncertain = cash.GetCash != cashBefore - Mathf.RoundToInt(priceBefore * 100f) ||
                display.gameObject.activeSelf != display.Get_CanBuyMultiplesADay;
            kegDeliveries.Add((adapter, peer, profile, request, bought, Time.unscaledTime + 5f, uncertain));

#if MONMP_DEVELOPMENT
            if (!TestDeferDelivery) DeliverKegPurchases();
#else
DeliverKegPurchases();
#endif

        }

        private void DeliverKegPurchases()
        {
            foreach (var delivery in kegDeliveries.ToArray())
            {
                if (delivery.Item != null && items.DeliverPurchase(delivery.Item, delivery.Peer, out ulong entity, out bool held))
                {
                    kegDeliveries.Remove(delivery);
                    var status = delivery.Uncertain ? PurchaseStatus.Uncertain : held ? PurchaseStatus.Held : PurchaseStatus.Placed;
                    delivery.Adapter.CompleteHostPurchase(delivery.Profile,
                        new KegPurchaseReply(delivery.Request.Operation, status, delivery.Uncertain ? 0 : entity));
                }
                else if (delivery.Item == null || Time.unscaledTime >= delivery.Deadline)
                {
                    kegDeliveries.Remove(delivery);
                    delivery.Adapter.CompleteHostPurchase(delivery.Profile,
                        new KegPurchaseReply(delivery.Request.Operation, PurchaseStatus.Uncertain));
                }
            }
        }
        private static bool HostPoliceAllowsPurchase(Stores store)
        {
            if (store.GetIsPlayerCanBuyWithCriminalRecord) return true;
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            return gameplay != null && gameplay.Store_PoliceInterceptor != null && !gameplay.Store_PoliceInterceptor.Get_HasCriminalRecord;
        }
        private static bool HostTodoDone(ToDoList_Line line)
        {
            var progress = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            return progress != null && progress.isDone(line);
        }
        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            ResolvePurchaseItems();
            if (pending.Count != 0)
            foreach (var entry in pending.Where(p => Time.unscaledTime >= p.Value.Deadline).ToArray())
            {
                // A transport deadline is not a definitive native purchase outcome.
                // Retain the bounded operation identity for a late result, without retrying.
                pending[entry.Key] = (entry.Value.Listing, float.PositiveInfinity);
                MonMPPlugin.Instance!.ReportItemUseUnavailable("Purchase confirmation is delayed. Check your cash and item before trying again.");
            }
            if (host
#if MONMP_DEVELOPMENT
                && !TestDeferDelivery
#endif
            ) { Deliver(); DeliverKegPurchases(); }
            if (host && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + 0.2f;
                // Compare before encoding: hundreds of listings, almost none change between captures.
                foreach (var l in listings.Values)
                    if (l.Display.Get_price != l.Current.Price || l.Display.gameObject.activeSelf != l.Current.Available ||
                        l.Store.isStoreOpen() != l.Current.Open || l.Display.Get_CanBuyMultiplesADay != l.Current.Multiple) Publish(l);
            }
            else if (!host && initialized && Time.unscaledTime >= nextRepair)
            {
                // Repairs native store refreshes; four times a second is plenty and keeps the
                // per-listing store-hours check off every frame.
                nextRepair = Time.unscaledTime + 0.25f;
                foreach (var l in listings.Values)
                    if (l.Display.Get_price != l.Current.Price || l.Display.gameObject.activeSelf != l.Current.Available || l.Store.isStoreOpen() != l.Current.Open) Apply(l, l.Current);
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.CommandRejected -= Stale; channel.ReplyReceived -= Reply; }
            if (!host) foreach (var l in listings.Values) if (l.Display != null) Apply(l, l.Original);
            deliveries.Clear(); kegDeliveries.Clear(); pending.Clear(); awaitingItems.Clear(); session = null; channel = null;
        }
    }
}
