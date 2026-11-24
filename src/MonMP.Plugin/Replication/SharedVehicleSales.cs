using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Host-owned protocol for the six stock VehicleForSale_Display entries. The native method is
    // still the only code that charges cash and applies its ToDo event; this adapter validates and
    // scopes that method, then mirrors the resulting scene clone for late joiners.
    internal sealed class SharedVehicleSales : IDisposable
    {
        private const float CommandTimeout = 10f;
        private const float CaptureInterval = 0.2f;
        private const int ClaimCapacity = 16384;
        private static readonly Vehicle_Type[] SupportedTypes = {
            Vehicle_Type.OlTruck, Vehicle_Type.SmollATV, Vehicle_Type.DollyTrailer,
            Vehicle_Type.WoodSplitter, Vehicle_Type.TrailerSyrupTank, Vehicle_Type.AluminiumTrailer
        };

        private sealed class Offer
        {
            public readonly ulong Id;
            public readonly string SpawnKey;
            public readonly VehicleForSale_Display Display;
            public readonly Vehicle_Type Type;
            public readonly GameObject Prefab;
            public readonly ulong Fingerprint;
            public readonly GameObject? OriginalClone;
            public readonly bool OriginalCloneActive;
            public readonly Vector3 OriginalClonePosition;
            public readonly Quaternion OriginalCloneRotation;
            public readonly Transform? OriginalCloneParent;
            public VehicleSaleListingState Original = default;
            public VehicleSaleListingState Current = default;
            public GameObject? NetworkClone;
            public GameObject? NativeOwnershipReplica;
            public bool OwnsNetworkClone;
            public int OriginalPrice;
            public bool OriginalDisplayActive;
            public bool OriginalStoreOpen;
            // Host: the native clone found beside the display, rescanned at most once a second.
            public GameObject? FoundClone;
            public float NextCloneScan;
            public byte[]? LastPublished;

            public Offer(ulong id, string spawnKey, VehicleForSale_Display display, Vehicle_Type type,
                GameObject prefab, ulong fingerprint, GameObject? existingClone)
            {
                Id = id; SpawnKey = spawnKey; Display = display; Type = type; Prefab = prefab;
                Fingerprint = fingerprint; OriginalClone = existingClone; NetworkClone = existingClone;
                OriginalCloneActive = existingClone != null && existingClone.activeSelf;
                OriginalClonePosition = existingClone != null ? existingClone.transform.position : default;
                OriginalCloneRotation = existingClone != null ? existingClone.transform.rotation : Quaternion.identity;
                OriginalCloneParent = existingClone != null ? existingClone.transform.parent : null;
                OriginalPrice = display.price;
                OriginalDisplayActive = display.gameObject.activeSelf;
                OriginalStoreOpen = display.store.isOpen;
            }
        }

        private sealed class NativeExecution
        {
            public readonly Offer Offer;
            public readonly uint Peer;
            public readonly Guid Operation;
            public readonly int CashBefore;
            public readonly HashSet<GameObject> ExistingChildren;
            public readonly bool Remote;
            public NativeExecution(Offer offer, uint peer, Guid operation, int cashBefore,
                HashSet<GameObject> existingChildren, bool remote)
            { Offer = offer; Peer = peer; Operation = operation; CashBefore = cashBefore; ExistingChildren = existingChildren; Remote = remote; }
        }

        private sealed class NativeOwnershipBaseline
        {
            internal readonly HardModeData Data;
            internal readonly bool Truck, Atv, Splitter;
            internal NativeOwnershipBaseline(HardModeData data)
            {
                Data = data;
                Truck = data.Bought_OlTruck;
                Atv = data.Bought_SmollATV;
                Splitter = data.Bought_WoodSplitter;
            }
            internal void Restore()
            {
                Data.Bought_OlTruck = Truck;
                Data.Bought_SmollATV = Atv;
                Data.Bought_WoodSplitter = Splitter;
            }
        }

        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly bool host;
        private readonly Cash_Manager cash;
        private readonly ToDoList_Manager todo;
        private readonly NativeEntityRegistry identities;
        private readonly Dictionary<ulong, Offer> offers = new Dictionary<ulong, Offer>();
        private readonly Dictionary<VehicleForSale_Display, Offer> byDisplay = new Dictionary<VehicleForSale_Display, Offer>();
        private readonly Dictionary<(Guid Profile, Guid Operation), (ulong Listing, VehicleSaleReply Reply)> results =
            new Dictionary<(Guid, Guid), (ulong, VehicleSaleReply)>();
        private readonly HashSet<(Guid Profile, Guid Operation)> claims = new HashSet<(Guid, Guid)>();
        private readonly Dictionary<Guid, (ulong Listing, float Deadline)> pending = new Dictionary<Guid, (ulong, float)>();
        private readonly Dictionary<Guid, ulong> timedOut = new Dictionary<Guid, ulong>();
        private readonly Dictionary<Guid, (ulong Listing, ulong Entity, float Deadline)> committedRepliesAwaitingState =
            new Dictionary<Guid, (ulong, ulong, float)>();
        private readonly HashSet<ulong> notifiedSpawnEntities = new HashSet<ulong>();
        private readonly List<NativeOwnershipBaseline> nativeOwnershipBaselines = new List<NativeOwnershipBaseline>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private NativeExecution? executing;
        private bool disposed, initialized, uncertainOutcome;
        private float nextCapture;

        internal static SharedVehicleSales? Active { get; private set; }
        internal bool NewPurchasesBlocked { get; set; }
        internal bool TransactionsSettled => !uncertainOutcome && pending.Count == 0 && timedOut.Count == 0 &&
            committedRepliesAwaitingState.Count == 0 && executing == null;
        internal uint? ExecutingPeer => executing?.Remote == true ? executing.Peer : (uint?)null;

#if MONMP_DEVELOPMENT
        internal int TestNativePurchases { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal VehicleSaleReply? TestLastReply { get; private set; }


        internal VehicleForSale_Display? TestDisplay(ulong id) => offers.TryGetValue(id, out var offer) ? offer.Display : null;


        internal VehicleSaleListingState TestState(ulong id) => offers[id].Current;
#endif


        // Native VehicleForSale_Display.MouseHover only asks isCanAfford to color its
        // offer. Allow that query for this exact registered listing and its current shared price.
        internal bool IsPriceQuoteFor(VehicleForSale_Display display) => !host && !disposed &&
            initialized && channel?.FullySynchronized == true && display != null &&
            byDisplay.TryGetValue(display, out var offer) && offer.Current.Available &&
            !offer.Current.Purchased && offer.Current.StoreOpen && offer.Current.PriceCents >= 0;

        internal bool TryGetQuotePrice(VehicleForSale_Display display, out float price)
        {
            if (byDisplay.TryGetValue(display, out var offer) && IsPriceQuoteFor(display))
            { price = offer.Current.PriceCents / 100f; return true; }
            price = 0; return false;
        }

        internal int QuoteCashCents => cash != null ? cash.GetCash : 0;

        // Fired for a host-loaded or newly reconstructed purchased vehicle. SharedVehicles and
        // SharedTrailers must bind the returned native object into their dynamic state streams.
        internal event Action<ulong, Vehicle_Type, GameObject>? PurchasedVehicleAvailable;

        internal SharedVehicleSales(Gameplay gameplay, bool host, SharedItems items,
            NativeEntityRegistry identities, string diagnostics)
        {
            if (gameplay == null) throw new ArgumentNullException(nameof(gameplay));
            this.gameplay = gameplay;
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
            cash = Object.FindObjectOfType<Cash_Manager>() ?? throw new InvalidOperationException("Vehicle sale cash manager is unavailable.");
            todo = Object.FindObjectOfType<ToDoList_Manager>() ?? throw new InvalidOperationException("Vehicle sale progression manager is unavailable.");

            var pathIds = new IdentityRegistry();
            var displays = Object.FindObjectsOfType<VehicleForSale_Display>(true)
                .Where(display => display != null && display.gameObject.scene.IsValid()).ToArray();
            foreach (var display in displays)
            {
                if (!TryDescribe(display, out Vehicle_Type type, out GameObject? prefab, out ulong fingerprint)) continue;
                string path = RuntimeProbe.CanonicalPath(display.transform);
                string identity = path + "/vehicle-sale-v1/" + (int)type + "/" + prefab!.name;
                ulong id = pathIds.Register(identity);
                ulong vehicleEntity = DynamicVehicleId(id);
                if (offers.Values.Any(existing => DynamicVehicleId(existing.Id) == vehicleEntity))
                    throw new InvalidOperationException("Vehicle sale dynamic identity collision at " + path);
                string spawnKey = identity + "/purchased-instance-v1";
                GameObject? existingClone = FindExistingClone(display, prefab, type);
                var offer = new Offer(id, spawnKey, display, type, prefab, fingerprint, existingClone);
                var registered = identities.Bind(id, VehicleSaleListingState.Adapter, display.gameObject, identity,
                    true, EntityPersistence.SessionOnly);
                if (registered.gameObject != display.gameObject || registered.AdapterType != VehicleSaleListingState.Adapter ||
                    registered.SpawnKey != identity)
                    throw new InvalidOperationException("Vehicle sale listing did not register as the exact static display.");
                offer.Current = Capture(offer);
                offer.Original = offer.Current;
                offers.Add(id, offer);
                byDisplay.Add(display, offer);
            }
            if (offers.Count == 0) throw new InvalidOperationException("No supported stock VehicleForSale_Display listings were found.");


#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-vehicle-sales.tsv"),
                new[] { "id\tnativeType\tprefab\tpriceCents\tpurchased\tavailable\tpath" }.Concat(offers.Values.OrderBy(o => o.Id)
                    .Select(o => o.Id.ToString("X16", CultureInfo.InvariantCulture) + "\t" + o.Type + "\t" + o.Prefab.name + "\t" +
                        o.Current.PriceCents + "\t" + o.Current.Purchased + "\t" + o.Current.Available + "\t" + RuntimeProbe.CanonicalPath(o.Display.transform))));
#endif

            Active = this;
        }

        private static ulong DynamicVehicleId(ulong listing) =>
            StaticId.Hash("vehicle-sale-instance-v1/" + listing.ToString("X16", CultureInfo.InvariantCulture)) | WorldRecord.DynamicIdStart;

        private static bool IsSupported(Vehicle_Type type) => SupportedTypes.Contains(type);

        private static bool TryDescribe(VehicleForSale_Display display, out Vehicle_Type type,
            out GameObject? prefab, out ulong fingerprint)
        {
            type = display.TypeForSale;
            prefab = display.RealObjectToBuy;
            fingerprint = 0;
            if (!IsSupported(type) || prefab == null || display.store == null || display.transform.parent == null || display.price < 0 ||
                display.price > VehicleSaleListingState.MaximumPriceCents / 100 ||
                !PrefabMatchesType(prefab, type, requireScene: false)) return false;
            string path = RuntimeProbe.CanonicalPath(display.transform);
            fingerprint = StaticId.Hash("vehicle-sale-catalog-v1/" + path + "/" + (int)type + "/" + prefab.name);
            return fingerprint != 0;
        }

        private static bool PrefabMatchesType(GameObject prefab, Vehicle_Type type, bool requireScene = true)
        {
            bool vehicleMotion = SharedVehicles.SupportsPurchasedVehicle(type, prefab, requireScene);
            bool trailerAuthority = SharedTrailers.SupportsPurchasedVehicle(type, prefab, requireScene);
            return type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV
                ? vehicleMotion && trailerAuthority
                : !vehicleMotion && trailerAuthority;
        }

        private static bool MatchesRegisteredSaleInstance(Offer offer, GameObject instance)
        {
            if (PrefabMatchesType(instance, offer.Type)) return true;
            // Reapplying a sale listing must preserve its already registered Remote clone.
            // Raw/native clones still pass the strict predicate above before any binding exists.
            return (offer.Type == Vehicle_Type.OlTruck || offer.Type == Vehicle_Type.SmollATV) &&
                MonMPPlugin.Instance?.World?.Vehicles.HasPurchasedVehicleBinding(DynamicVehicleId(offer.Id), offer.Type, instance) == true &&
                SharedTrailers.HasSupportedPurchasedHitch(offer.Type, instance);
        }

        private struct CloneSearch
        {
            private Dictionary<UnityEngine.SceneManagement.Scene, GameObject[]>? rootsByScene;

            internal GameObject[] Roots(UnityEngine.SceneManagement.Scene scene)
            {
                if (rootsByScene == null)
                    rootsByScene = new Dictionary<UnityEngine.SceneManagement.Scene, GameObject[]>();
                if (!rootsByScene.TryGetValue(scene, out var roots))
                {
                    roots = scene.GetRootGameObjects();
                    rootsByScene.Add(scene, roots);
                }
                return roots;
            }
        }

        private static GameObject? FindExistingClone(VehicleForSale_Display display, GameObject prefab, Vehicle_Type type)
        {
            var search = default(CloneSearch);
            return FindExistingClone(display, prefab, type, ref search);
        }

        private static GameObject? FindExistingClone(VehicleForSale_Display display, GameObject prefab, Vehicle_Type type,
            ref CloneSearch search)
        {
            var parent = display.transform.parent;
            if (parent == null) return null;
            string expectedCloneName = prefab.name + "(Clone)";
            var matches = new List<GameObject>();
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i).gameObject;
                if (ReferenceEquals(child, display.gameObject) || child.name != expectedCloneName ||
                    !PrefabMatchesType(child, type)) continue;
                matches.Add(child);
            }
            // The native save loader restores bought trailers at the scene root, not beside the display.
            if (matches.Count == 0)
            {
                var scene = display.gameObject.scene;
                foreach (var root in search.Roots(scene))
                    // Membership is a pass snapshot; validity and names remain live if a publication
                    // callback destroys, reparents or renames an object before another offer is read.
                    if (root != null && root.name == expectedCloneName && root.transform.parent == null &&
                        root.scene == scene && PrefabMatchesType(root, type)) matches.Add(root);
            }
            if (matches.Count > 1)
                throw new InvalidOperationException("Multiple saved vehicle clones match listing " + RuntimeProbe.CanonicalPath(display.transform));
            return matches.Count == 1 ? matches[0] : null;
        }

        private VehicleSaleListingState Capture(Offer offer)
        {
            var search = default(CloneSearch);
            return Capture(offer, ref search);
        }

        private VehicleSaleListingState Capture(Offer offer, ref CloneSearch search)
        {
            GameObject? clone = offer.OriginalClone;
            if (executing != null && ReferenceEquals(executing.Offer, offer))
                clone = FindNewNativeClone(executing, out _);
            if (clone == null)
            {
                // Scanning the scene roots every capture stalled large saves; a native purchase
                // outside our transaction is picked up within a second.
                if (offer.FoundClone == null && Time.unscaledTime >= offer.NextCloneScan)
                {
                    offer.NextCloneScan = Time.unscaledTime + 1f;
                    offer.FoundClone = FindExistingClone(offer.Display, offer.Prefab, offer.Type, ref search);
                }
                clone = offer.FoundClone;
            }
            bool purchased = clone != null;
            ulong entity = purchased ? DynamicVehicleId(offer.Id) : 0;
            // The listing carries the spawn pose only; the vehicle's movement is replicated by its
            // own adapter. Republishing the live pose streamed the listing every capture and made
            // guests rebuild trailer links each time.
            ItemPose? pose = !purchased ? (ItemPose?)null
                : offer.Current.Purchased && offer.Current.VehicleEntity == entity && offer.Current.VehiclePose.HasValue
                    ? offer.Current.VehiclePose : ToPose(clone!.transform);
            return new VehicleSaleListingState((int)offer.Type, checked(offer.Display.price * 100), offer.Fingerprint,
                offer.Display.gameObject.activeSelf && !purchased, offer.Display.store.isStoreOpen(), purchased, entity, pose);
        }

        private static ItemPose ToPose(Transform transform)
        {
            var p = transform.position;
            var q = transform.rotation;
            return new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
        }

        private static bool HasHostPrerequisites(Offer offer, ToDoList_Manager todo) =>
            offer.Type != Vehicle_Type.TrailerSyrupTank ||
            (todo.isDone(ToDoList_Line.Sugarshack_SugarshackIsBuild) && todo.isDone(ToDoList_Line.SugarShack_BedTankOutdoorPump));

        private string? PrerequisiteError(Offer offer)
        {
            if (offer.Type != Vehicle_Type.TrailerSyrupTank) return null;
            if (!todo.isDone(ToDoList_Line.Sugarshack_SugarshackIsBuild)) return "ERROR_FederationSyrup_YouDontHaveASugarshack";
            if (!todo.isDone(ToDoList_Line.SugarShack_BedTankOutdoorPump)) return "ERROR_FederationSyrup_YouNeedToBuyTheOutdoorAndBedTankPumpFirst";
            return null;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => offers.Values.OrderBy(o => o.Id)
            .Select(o => new WorldRecord(o.Id, 1, VehicleSaleListingState.Adapter, (host ? Capture(o) : o.Original).Encode()));

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        // True means a multiplayer guest or a save-blocked host click was consumed. Native host
        // clicks proceed with a short scoped context so only the exact listing transaction runs.
        internal bool InterceptMouseDown(VehicleForSale_Display display, out bool finishLocalClick)
        {
            finishLocalClick = false;
            if (disposed || session?.IsRunning != true) return false;
            if (!byDisplay.TryGetValue(display, out var offer))
            {
                Report("This vehicle listing is not supported in the current multiplayer session.");
                return true;
            }
            if (executing != null && ReferenceEquals(executing.Offer.Display, display)) return false;
            if (host)
            {
                if (NewPurchasesBlocked)
                {
                    Report("Wait for the multiplayer save to finish before buying a vehicle.");
                    return true;
                }
                var current = Capture(offer);
                if (!current.Available || current.Purchased)
                {
                    Report("That vehicle listing is no longer available.");
                    return true;
                }
                if (executing != null)
                {
                    Report("Wait for the current vehicle purchase to finish.");
                    return true;
                }
                executing = NewExecution(offer, 0, Guid.Empty, remote: false);
                finishLocalClick = true;
                return false;
            }
            if (NewPurchasesBlocked)
            {
                Report("Wait for the multiplayer save to finish before buying a vehicle.");
                return true;
            }
            if (channel?.FullySynchronized != true || !initialized)
            {
                Report("The vehicle listing is unavailable until the host world is synchronized.");
                return true;
            }
            if (pending.Count + timedOut.Count + committedRepliesAwaitingState.Count >= 8)
            {
                Report("Wait for the pending vehicle purchase to finish.");
                return true;
            }
            if (!offer.Current.Available || offer.Current.Purchased)
            {
                Report("That vehicle listing is no longer available.");
                return true;
            }
            // The native click checks the sugar shack before anything else; the to-do list is the host's.
            if (PrerequisiteError(offer) is { } missing)
            {
                Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText(missing));
                return true;
            }
            var request = new VehicleSaleRequest(Guid.NewGuid(), offer.Id, (int)offer.Type, offer.Current.PriceCents);
            pending.Add(request.Operation, (offer.Id, Time.unscaledTime + CommandTimeout));
            channel.Request(offer.Id, request.Encode());
            return true;
        }

        internal void FinishLocalClick(VehicleForSale_Display display, Exception? exception)
        {
            var context = executing;
            if (context == null || context.Remote || !ReferenceEquals(context.Offer.Display, display)) return;
            try
            {
                if (exception != null) Debug.LogException(exception);
                FinalizeNativePurchase(context, exception != null);
                Publish(context.Offer);
            }
            finally { executing = null; }
        }

        internal bool IsExecutingNative(VehicleForSale_Display display) =>
            host && session?.IsRunning == true && executing != null && ReferenceEquals(executing.Offer.Display, display);

        internal bool AllowNativeEconomyMutation(string methodName) => executing != null && host &&
            session?.IsRunning == true && (methodName == nameof(Cash_Manager.Event_Spent) ||
                methodName == nameof(Cash_Manager.TryToBuy_Vehicle));

        internal bool AllowNativeAffordabilityQuote(float price) => IsRemoteExecution() &&
            executing != null && price == executing.Offer.Display.price;

        internal bool IsRemoteExecution() => host && session?.IsRunning == true && executing?.Remote == true && executing.Peer != 0;

        internal IEnumerable<(ulong Entity, Vehicle_Type Type, GameObject Instance)> PurchasedVehicles()
        {
            if (!host && !initialized) yield break;
            foreach (var offer in offers.Values.Where(offer => offer.Current.Purchased))
            {
                var instance = offer.NetworkClone ?? offer.OriginalClone;
                if (instance != null) yield return (offer.Current.VehicleEntity, offer.Type, instance);
            }
        }

        // Validated native catalog only. A live host door cue can arrive before its purchased
        // listing/replica; its identity is still restricted to this exact prefab and sale entity.
        internal IEnumerable<(ulong Entity, NWH.VehiclePhysics2.VehicleController Prefab)> TruckDoorCueCatalog()
        {
            if (disposed) yield break;
            foreach (var offer in offers.Values.Where(o => o.Type == Vehicle_Type.OlTruck && o.Prefab != null))
            {
                var vehicle = offer.Prefab.GetComponent<NWH.VehiclePhysics2.VehicleController>();
                if (vehicle != null) yield return (DynamicVehicleId(offer.Id), vehicle);
            }
        }

        // True for anything inside a purchased vehicle; its state belongs to the purchase, not the scene catalog.
        internal bool OwnsSaleClone(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent)
                if (IsVehicleSaleClone(t.gameObject)) return true;
            return false;
        }

        internal bool IsVehicleSaleClone(GameObject instance)
        {
            if (instance == null) return false;
            return offers.Values.Any(offer => offer.OriginalClone == instance || offer.NetworkClone == instance);
        }

        internal bool TryGetVehicleEntity(GameObject instance, out Vehicle_Type type, out ulong entity)
        {
            type = default;
            entity = 0;
            if (instance == null || (!host && !initialized)) return false;
            var offer = offers.Values.FirstOrDefault(candidate =>
                candidate.Current.Purchased && (candidate.OriginalClone == instance || candidate.NetworkClone == instance));
            if (offer == null) return false;
            type = offer.Type;
            entity = offer.Current.VehicleEntity;
            return entity > WorldRecord.DynamicIdStart;
        }

        private NativeExecution NewExecution(Offer offer, uint peer, Guid operation, bool remote)
        {
            var parent = offer.Display.transform.parent ?? throw new InvalidOperationException("Vehicle sale display has no native spawn parent.");
            var children = new HashSet<GameObject>();
            for (int i = 0; i < parent.childCount; i++) children.Add(parent.GetChild(i).gameObject);
            return new NativeExecution(offer, peer, operation, cash.GetCash, children, remote);
        }

        private GameObject? FindNewNativeClone(NativeExecution execution, out int count)
        {
            var parent = execution.Offer.Display.transform.parent;
            var matches = new List<GameObject>();
            if (parent != null)
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i).gameObject;
                    if (execution.ExistingChildren.Contains(child) || child.name != execution.Offer.Prefab.name + "(Clone)" ||
                        !PrefabMatchesType(child, execution.Offer.Type)) continue;
                    matches.Add(child);
                }
            count = matches.Count;
            return matches.Count == 1 ? matches[0] : null;
        }

        private PurchaseStatus FinalizeNativePurchase(NativeExecution context, bool threw)
        {
            var clone = FindNewNativeClone(context, out int count);
            int expectedCash = context.CashBefore - context.Offer.Display.price * 100;
            if (clone == null)
            {
                if (threw || count != 0 || cash.GetCash != context.CashBefore || !context.Offer.Display.gameObject.activeSelf)
                    return PurchaseStatus.Uncertain;
                return PurchaseStatus.NativeRejected;
            }


#if MONMP_DEVELOPMENT
            TestNativePurchases++;
#endif

            if (count != 1 || threw || cash.GetCash != expectedCash || context.Offer.Display.gameObject.activeSelf ||
                !PrefabMatchesType(clone, context.Offer.Type))
                return PurchaseStatus.Uncertain;

            ulong entity = DynamicVehicleId(context.Offer.Id);
            var state = new VehicleSaleListingState((int)context.Offer.Type, context.Offer.Display.price * 100,
                context.Offer.Fingerprint, false, context.Offer.Display.store.isStoreOpen(), true, entity, ToPose(clone.transform));
            context.Offer.Current = state;
            RegisterSpawn(context.Offer, entity, clone, state.VehiclePose!.Value);
            if (context.Remote)
            {
                Complete(context.Peer, session!.Peers[context.Peer].ProfileId, context.Offer.Id,
                    new VehicleSaleReply(context.Operation, PurchaseStatus.Placed, entity));
            }
            return PurchaseStatus.Placed;
        }

        private void RegisterSpawn(Offer offer, ulong entity, GameObject clone, ItemPose pose)
        {
            if (entity != DynamicVehicleId(offer.Id) || clone == null || !MatchesRegisteredSaleInstance(offer, clone))
                throw new InvalidOperationException("Purchased vehicle does not match its validated sale family.");
            offer.NetworkClone = clone;
            if ((host || initialized) && !notifiedSpawnEntities.Contains(entity))
            {
                offer.NativeOwnershipReplica = null;
                var bind = PurchasedVehicleAvailable;
                bind?.Invoke(entity, offer.Type, clone);
                // Only mark successful delivery after every adapter accepted the instance. A
                // failed apply must retry the idempotent registrations, rather than strand a
                // movement-only clone and skip the remaining adapters on the next attempt.
                notifiedSpawnEntities.Add(entity);
                if (bind != null) offer.NativeOwnershipReplica = clone;
            }
            _ = pose;
        }

        private void ExecuteNativePurchase(uint peer, Guid profile, Offer offer, VehicleSaleRequest request)
        {
            var context = NewExecution(offer, peer, request.Operation, remote: true);
            executing = context;
            PurchaseStatus status;
            try
            {
                cash.TryToBuy_Vehicle(offer.Display);
                status = FinalizeNativePurchase(context, false);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = FinalizeNativePurchase(context, true);
            }
            finally { executing = null; }
            if (status != PurchaseStatus.Placed)
                Complete(peer, profile, offer.Id, new VehicleSaleReply(request.Operation, status));
            Publish(offer);
        }

        private void Complete(uint peer, Guid profile, ulong listing, VehicleSaleReply reply)
        {
            if (reply.Status == PurchaseStatus.Uncertain) uncertainOutcome = true;
            results[(profile, reply.Operation)] = (listing, reply);
            if (session?.Peers.TryGetValue(peer, out var buyer) == true && buyer.ProfileId == profile)
                channel!.Reply(peer, listing, reply.Encode());
        }

        [MonMP.Core.World.WorldCommandAdapters(VehicleSaleListingState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload) => HandleCommand(peer, id, payload, false);
        private void Rejected(uint peer, ulong id, byte[] payload) => HandleCommand(peer, id, payload, true);

        private void HandleCommand(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || disposed || !offers.TryGetValue(id, out var offer) || session == null || channel == null ||
                !session.Peers.TryGetValue(peer, out var buyer)) return;
            VehicleSaleRequest request;
            try { request = VehicleSaleRequest.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (request.Listing != id) return;
            var key = (buyer.ProfileId, request.Operation);
            if (results.TryGetValue(key, out var prior))
            {
                channel.Reply(peer, id, (prior.Listing == id ? prior.Reply : new VehicleSaleReply(request.Operation, PurchaseStatus.RetryRejected)).Encode());
                return;
            }
            if (claims.Count >= ClaimCapacity)
            {
                channel.Reply(peer, id, new VehicleSaleReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }
            if (!claims.Add(key))
            {

#if MONMP_DEVELOPMENT
                TestDuplicateReplays++;
#endif

                channel.Reply(peer, id, new VehicleSaleReply(request.Operation, PurchaseStatus.RetryRejected).Encode());
                return;
            }

            var current = Capture(offer);
            bool range = items.PurchaseInRange(peer, offer.Display.transform.position);
            bool handsFree = !items.PeerHolding(peer);
            bool canAfford = cash.GetCash >= request.PriceCents;
            PurchaseStatus decision = VehicleSalePolicy.ValidateHostRequest(stale, channel.FullySynchronized,
                NewPurchasesBlocked, claims.Count <= ClaimCapacity, byDisplay.TryGetValue(offer.Display, out var registered) &&
                ReferenceEquals(registered, offer), current.Available, current.StoreOpen, HasHostPrerequisites(offer, todo),
                request.PriceCents == current.PriceCents, request.NativeVehicleType == (int)offer.Type && current.NativeVehicleType == (int)offer.Type &&
                current.CatalogFingerprint == offer.Fingerprint, range, handsFree, canAfford);
            if (decision != PurchaseStatus.Placed)
            {
                Complete(peer, buyer.ProfileId, id, new VehicleSaleReply(request.Operation, decision));
                return;
            }
            if (executing != null)
            {
                Complete(peer, buyer.ProfileId, id, new VehicleSaleReply(request.Operation, PurchaseStatus.NativeRejected));
                return;
            }
            ExecuteNativePurchase(peer, buyer.ProfileId, offer, request);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || disposed) return;
            var staged = records.Values.Where(r => r.Adapter == VehicleSaleListingState.Adapter)
                .Select(record => (Record: record, State: Decode(record))).ToArray();
            initialized = true;
            foreach (var entry in staged) { Apply(entry.Record, entry.State, true); ConfirmCommittedState(entry.Record.Id, entry.State); }
        }

        private VehicleSaleListingState Decode(WorldRecord record)
        {
            if (record.Adapter != VehicleSaleListingState.Adapter || !offers.TryGetValue(record.Id, out var offer))
                throw new InvalidDataException("Unknown vehicle sale listing record.");
            var state = VehicleSaleListingState.Decode(record.Payload);
            if (state.NativeVehicleType != (int)offer.Type || state.CatalogFingerprint != offer.Fingerprint ||
                (state.Purchased && state.VehicleEntity != DynamicVehicleId(offer.Id)))
                throw new InvalidDataException("Vehicle sale listing identity changed.");
            return state;
        }

        private void Changed(WorldRecord record)
        {
            if (host || disposed || record.Adapter != VehicleSaleListingState.Adapter) return;
            var state = Decode(record);
            Apply(record, state);
            ConfirmCommittedState(record.Id, state);
        }

        private void ConfirmCommittedState(ulong listing, VehicleSaleListingState state)
        {
            foreach (var entry in committedRepliesAwaitingState.Where(p => p.Value.Listing == listing &&
                p.Value.Entity == state.VehicleEntity && state.Purchased).Select(p => p.Key).ToArray())
                committedRepliesAwaitingState.Remove(entry);
        }

        private void Apply(WorldRecord record, VehicleSaleListingState state, bool snapSpawnPose = false)
        {
            var offer = offers[record.Id];
            bool firstSpawn = snapSpawnPose || !offer.Current.Purchased || offer.Current.VehicleEntity != state.VehicleEntity;
            offer.Current = state;
            try
            {
                offer.Display.price = state.PriceCents / 100;
                offer.Display.store.isOpen = state.StoreOpen;
                offer.Display.gameObject.SetActive(state.Available);
                if (state.Purchased)
                {
                    var clone = EnsureReplica(offer, state.VehiclePose!.Value, firstSpawn);
                    RegisterSpawn(offer, state.VehicleEntity, clone, state.VehiclePose.Value);
                }
                else if (offer.OriginalClone != null) offer.OriginalClone.SetActive(false);
            }
            finally { RefreshNativeOwnership(); }
        }

        private bool HasPurchasedNativeOwnership(Vehicle_Type type)
        {
            foreach (var offer in offers.Values)
            {
                if (offer.Type != type || !offer.Current.Purchased ||
                    offer.Current.VehicleEntity != DynamicVehicleId(offer.Id) ||
                    offer.NativeOwnershipReplica == null ||
                    !ReferenceEquals(offer.NativeOwnershipReplica, offer.NetworkClone) ||
                    !notifiedSpawnEntities.Contains(offer.Current.VehicleEntity) ||
                    !MatchesRegisteredSaleInstance(offer, offer.NativeOwnershipReplica)) continue;
                var primary = identities.Resolve(offer.Current.VehicleEntity);
                ushort adapter = type == Vehicle_Type.WoodSplitter ? TrailerWorldState.Adapter : VehicleState.Adapter;
                if (primary != null && primary.AdapterType == adapter &&
                    primary.transform.IsChildOf(offer.NativeOwnershipReplica.transform)) return true;
            }
            return false;
        }

        private void RefreshNativeOwnership()
        {
            if (host || disposed || !initialized || gameplay == null || !gameplay.Get_isHardMode) return;
            var data = gameplay.Get_HardModeData;
            if (data == null) return;
            bool truck = HasPurchasedNativeOwnership(Vehicle_Type.OlTruck);
            bool atv = HasPurchasedNativeOwnership(Vehicle_Type.SmollATV);
            bool splitter = HasPurchasedNativeOwnership(Vehicle_Type.WoodSplitter);
            NativeOwnershipBaseline? baseline = null;
            foreach (var saved in nativeOwnershipBaselines)
                if (ReferenceEquals(saved.Data, data)) { baseline = saved; break; }
            if (baseline == null)
            {
                if (!truck && !atv && !splitter) return;
                baseline = new NativeOwnershipBaseline(data);
                nativeOwnershipBaselines.Add(baseline);
            }
            // Native hard-mode initialization must first remove absent canonical starters.
            // Only a later authoritative listing with its exact bound replica grants local
            // towing eligibility; this never invokes a native purchase or copies PlayerData.
            data.Bought_OlTruck = baseline.Truck || truck;
            data.Bought_SmollATV = baseline.Atv || atv;
            data.Bought_WoodSplitter = baseline.Splitter || splitter;
        }

        private void RestoreNativeOwnership()
        {
            // A native reset may replace HardModeData during the borrowed scene. Restore
            // each object we changed, leaving any unobserved replacement/world untouched.
            foreach (var baseline in nativeOwnershipBaselines) baseline.Restore();
            nativeOwnershipBaselines.Clear();
        }

        private GameObject EnsureReplica(Offer offer, ItemPose pose, bool applyPose)
        {
            var clone = offer.NetworkClone ?? offer.OriginalClone;
            if (clone == null)
            {
                clone = Object.Instantiate(offer.Prefab, offer.Display.transform);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.SetParent(offer.Display.transform.parent);
                offer.OwnsNetworkClone = true;
            }
            if (!MatchesRegisteredSaleInstance(offer, clone)) throw new InvalidDataException("Purchased vehicle prefab does not match its listing.");
            bool appeared = applyPose || !clone.activeSelf || !ReferenceEquals(offer.NetworkClone, clone);
            if (applyPose)
                clone.transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw));
            clone.SetActive(true);
            if (appeared) SyncTrailers();
            offer.NetworkClone = clone;
            return clone;
        }

        private static void SyncTrailers()
        {
            foreach (var hitch in Object.FindObjectsOfType<NWH.VehiclePhysics2.Modules.Trailer.TrailerHitchModuleWrapper>())
                hitch.module?.SyncTrailers();
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (!offers.TryGetValue(id, out var offer)) return;
            var reply = VehicleSaleReply.Decode(payload);
            bool wasPending = pending.TryGetValue(reply.Operation, out var request) && request.Listing == id;
            bool hadTimedOut = timedOut.TryGetValue(reply.Operation, out ulong timedOutListing) && timedOutListing == id;
            if (!wasPending && !hadTimedOut) return;
            pending.Remove(reply.Operation);
            timedOut.Remove(reply.Operation);

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == PurchaseStatus.Uncertain) uncertainOutcome = true;
            if (reply.Status == PurchaseStatus.Placed)
            {
                if (!offer.Current.Purchased || offer.Current.VehicleEntity != reply.VehicleEntity)
                {
                    channel?.RequestResync(SnapshotScope.ForEntity(VehicleSaleListingState.Adapter, id), "Vehicle sale result arrived before listing state.");
                    committedRepliesAwaitingState[reply.Operation] = (id, reply.VehicleEntity, Time.unscaledTime + 3f);
                }
                return;
            }
            // A host that has not met the listing's native prerequisite reports it the native way.
            if (reply.Status == PurchaseStatus.Unavailable && offer.Current.Available && !offer.Current.Purchased &&
                PrerequisiteError(offer) is { } missing)
                Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText(missing));
            else Report(Message(reply.Status));
        }

        private void Publish(Offer offer)
        {
            var search = default(CloneSearch);
            Publish(offer, ref search);
        }

        private void Publish(Offer offer, ref CloneSearch search)
        {
            if (channel == null || !host) return;
            var state = Capture(offer, ref search);
            offer.Current = state;
            byte[] payload = state.Encode();
            if (offer.LastPublished != null && payload.AsSpan().SequenceEqual(offer.LastPublished)) return;
            offer.LastPublished = payload;
            channel.Publish(offer.Id, payload);
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (pending.Count != 0)
            {
                foreach (var entry in pending.Where(p => Time.unscaledTime >= p.Value.Deadline).ToArray())
                {
                    pending.Remove(entry.Key);
                    timedOut[entry.Key] = entry.Value.Listing;
                    Report("Vehicle purchase confirmation is delayed. Check the listing and shared cash before trying again.");
                }
            }
            if (committedRepliesAwaitingState.Count != 0)
            {
                foreach (var entry in committedRepliesAwaitingState.Where(p => Time.unscaledTime >= p.Value.Deadline).ToArray())
                {
                    // Retain the exact listing/entity claim until its authoritative state
                    // arrives. A delivery deadline is not a native uncertain outcome.
                    committedRepliesAwaitingState[entry.Key] = (entry.Value.Listing, entry.Value.Entity, float.PositiveInfinity);
                    Report("The purchased vehicle appeared, but its shared spawn state is still missing. Ask the host to resynchronize.");
                }
            }
            if (host && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + CaptureInterval;
                // Roots are acquired only for due fallback searches and shared by this pass alone.
                var search = default(CloneSearch);
                foreach (var offer in offers.Values) Publish(offer, ref search);
            }
            else if (!host && initialized)
            {
                RefreshNativeOwnership();
                foreach (var offer in offers.Values)
                    if (offer.Display.price * 100 != offer.Current.PriceCents ||
                        offer.Display.gameObject.activeSelf != offer.Current.Available ||
                        offer.Display.store.isOpen != offer.Current.StoreOpen)
                        Apply(new WorldRecord(offer.Id, 1, VehicleSaleListingState.Adapter, offer.Current.Encode()), offer.Current);
            }
        }

        private static string Message(PurchaseStatus status) => status switch
        {
            PurchaseStatus.Unavailable => "That vehicle listing is no longer available or locked in the host's world.",
            PurchaseStatus.Closed => "The vehicle store is closed.",
            PurchaseStatus.PriceChanged => "The vehicle listing changed. Check the price and try again.",
            PurchaseStatus.TooFar => "Move closer to the vehicle listing before buying it.",
            PurchaseStatus.HandsBusy => "Put down your current item before buying a vehicle.",
            PurchaseStatus.Funds => "Not enough shared cash.",
            PurchaseStatus.NativeRejected => "The native vehicle purchase could not be completed.",
            PurchaseStatus.Capacity => "This session cannot accept more vehicle purchases.",
            PurchaseStatus.Uncertain => "Vehicle purchase outcome is uncertain. Check the vehicle and shared cash before retrying.",
            _ => "That vehicle purchase request has already been handled."
        };

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (channel != null)
                {
                    channel.SnapshotApplied -= Snapshot;
                    channel.Changed -= Changed;
                    channel.CommandRequested -= Command;
                    channel.CommandRejected -= Rejected;
                    channel.ReplyReceived -= Reply;
                }
                if (!host)
                {
                    foreach (var offer in offers.Values)
                    {
                        if (offer.Display != null)
                        {
                            offer.Display.price = offer.OriginalPrice;
                            if (offer.Display.store != null) offer.Display.store.isOpen = offer.OriginalStoreOpen;
                            offer.Display.gameObject.SetActive(offer.OriginalDisplayActive);
                        }
                        if (offer.OriginalClone != null)
                        {
                            offer.OriginalClone.transform.SetParent(offer.OriginalCloneParent != null ? offer.OriginalCloneParent : null, true);
                            offer.OriginalClone.transform.SetPositionAndRotation(offer.OriginalClonePosition, offer.OriginalCloneRotation);
                            offer.OriginalClone.SetActive(offer.OriginalCloneActive);
                        }
                        if (offer.OwnsNetworkClone && offer.NetworkClone != null) Object.Destroy(offer.NetworkClone);
                    }
                }
            }
            finally
            {
                try { RestoreNativeOwnership(); }
                finally
                {
                    pending.Clear(); timedOut.Clear(); committedRepliesAwaitingState.Clear(); session = null; channel = null;
                    if (ReferenceEquals(Active, this)) Active = null;
                }
            }
        }
    }
}
