using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Host-authoritative purchases for the three static dealer machine upgrades and the outdoor
    // planter packs. A planter pack runs natively on the host, whose spawned planters are shared
    // items; guests see its stack hide (#233).
    internal sealed class SharedDealerMachines : IDisposable
    {
        private const float CommandTimeout = 10f;
        private const float CaptureInterval = 0.1f;

        private static readonly FieldInfo ListingStore = RequiredField(typeof(ItemForSale_Dealer), "store");
        private static readonly FieldInfo ListingPrice = RequiredField(typeof(ItemForSale_Dealer), "price");
        private static readonly FieldInfo HugePackagerFlag = RequiredField(typeof(ItemForSale_Dealer), "isHugePackingMachine");
        private static readonly FieldInfo CompostMachineFlag = RequiredField(typeof(ItemForSale_Dealer), "isCompostMachine");
        private static readonly FieldInfo FriesPressFlag = RequiredField(typeof(ItemForSale_Dealer), "isFrenchFriesPress");
        private static readonly FieldInfo OutdoorPlanterFlag = RequiredField(typeof(ItemForSale_Dealer), "isOutdoorPlanter");
        private static readonly FieldInfo PlanterStackField = RequiredField(typeof(ItemForSale_Dealer), "AllPlanters");
        private static readonly FieldInfo PackagerBigField = RequiredField(typeof(Home_Bunker), "PackagerMachine_Big");
        private static readonly FieldInfo PackagerHugeField = RequiredField(typeof(Home_Bunker), "PackagerMachine_Huge");
        private static readonly FieldInfo CompostMachineField = RequiredField(typeof(Home_Bunker), "BunkerCompostMachine");
        private static readonly FieldInfo FriesPressField = RequiredField(typeof(Home_Bunker), "BunkerFrenchFriesPress");
        private static readonly MethodInfo PackagerBought = RequiredMethod(typeof(Home_Bunker), "HugePackager_Bought");
        private static readonly MethodInfo CompostBought = RequiredMethod(typeof(Home_Bunker), "CompostMachine_Bought");
        private static readonly MethodInfo FriesPressBought = RequiredMethod(typeof(Home_Bunker), "FrenchFriesPress_Bought");

        private sealed class Offer
        {
            public readonly DealerMachineKind Kind;
            public readonly ulong Entity;
            public readonly string SpawnKey;
            public readonly ItemForSale_Dealer Display;
            public readonly GameObject? Stack;
            public bool Planter => Stack != null;
            public Offer(DealerMachineKind kind, ItemForSale_Dealer display, GameObject? stack = null)
            {
                Kind = kind; Stack = stack;
                Entity = DealerMachineState.EntityFor(kind);
                SpawnKey = RuntimeProbe.CanonicalPath(display.transform) + "/dealer-machine-upgrade-v1";
                Display = display;
            }
        }

        private readonly struct MachinePresentation
        {
            public readonly bool PackagerBig, PackagerHuge, Compost, FriesPress;
            public MachinePresentation(bool packagerBig, bool packagerHuge, bool compost, bool friesPress)
            { PackagerBig = packagerBig; PackagerHuge = packagerHuge; Compost = compost; FriesPress = friesPress; }
        }

        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly NativeEntityRegistry identities;
        private readonly bool host;
        private readonly Cash_Manager? cash;
        private readonly ToDoList_Manager? todo;
        private readonly Home_Bunker bunker;
        private readonly GameObject packagerBig;
        private readonly GameObject packagerHuge;
        private readonly GameObject compostMachine;
        private readonly GameObject friesPress;
        private readonly MachinePresentation originalMachine;
        private readonly Dictionary<DealerMachineKind, Offer> offers = new Dictionary<DealerMachineKind, Offer>();
        private readonly Dictionary<DealerMachineKind, bool> originalDisplays = new Dictionary<DealerMachineKind, bool>();
        private readonly Dictionary<DealerMachineKind, bool> originalStacks = new Dictionary<DealerMachineKind, bool>();
        private readonly Dictionary<ulong, DealerMachineKind> kindsByEntity = new Dictionary<ulong, DealerMachineKind>();
        private readonly Dictionary<DealerMachineKind, DealerMachineState> state = new Dictionary<DealerMachineKind, DealerMachineState>();
        private readonly Dictionary<(Guid Profile, Guid Operation), DealerMachinePurchaseReply> results =
            new Dictionary<(Guid, Guid), DealerMachinePurchaseReply>();
        private readonly Dictionary<Guid, (ulong Entity, float Deadline)> pending = new Dictionary<Guid, (ulong, float)>();
        private readonly Dictionary<Guid, ulong> timedOut = new Dictionary<Guid, ulong>();
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private NetworkSession? session;
        private WorldChannel? channel;
        private ItemForSale_Dealer? executingNative;
        private bool disposed, uncertainOutcome;
        private float nextCapture;

        internal static SharedDealerMachines? Active { get; private set; }
        internal bool NewPurchasesBlocked { get; set; }
        internal Func<Guid, Guid, ulong, bool>? DurableClaim { get; set; }
        internal bool ProductionSettled => !host || !NativeSaveProduction.Pending(bunker, "Coroutine_OutdoorPlanter_Bought");
        internal bool TransactionsSettled => !uncertainOutcome && pending.Count == 0 && timedOut.Count == 0 && ProductionSettled;
        internal bool SettleForSave()
        {
            if (!host || disposed) return true;
            // Preserve and drain the exact paid native multipack iterator. Replaying the
            // purchase would debit again or duplicate planters that already spawned.
            if (!ProductionSettled) NativeSaveProduction.Finish(bunker, "Coroutine_OutdoorPlanter_Bought");
            foreach (var offer in offers.Values) Publish(offer);
            return executingNative == null && ProductionSettled;
        }

#if MONMP_DEVELOPMENT
        internal int TestNativePurchases { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal DealerMachinePurchaseReply? TestLastReply { get; private set; }


        internal ItemForSale_Dealer? TestDisplay(DealerMachineKind kind) => offers.TryGetValue(kind, out var offer) ? offer.Display : null;


        internal DealerMachineState TestState(DealerMachineKind kind) => state[kind];
#endif


        internal SharedDealerMachines(Gameplay gameplay, bool host, SharedItems items, string diagnostics,
            NativeEntityRegistry identities)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
            cash = Object.FindObjectOfType<Cash_Manager>();
            todo = Object.FindObjectOfType<ToDoList_Manager>();
            bunker = gameplay.home != null ? gameplay.home.homeBunker : null!;
            if (cash == null || todo == null || bunker == null)
                throw new InvalidOperationException("Dealer machine purchase managers or the bunker are unavailable.");

            packagerBig = ReadGameObject(PackagerBigField, bunker, "large packager");
            packagerHuge = ReadGameObject(PackagerHugeField, bunker, "huge packager");
            compostMachine = ReadComponentGameObject(CompostMachineField, bunker, "compost machine");
            friesPress = ReadGameObject(FriesPressField, bunker, "French fries press");
            originalMachine = CaptureMachine();

            var planters = new List<ItemForSale_Dealer>();
            var displays = Object.FindObjectsOfType<ItemForSale_Dealer>(true)
                .Where(display => display != null && display.gameObject.scene.IsValid()).ToArray();
            foreach (var display in displays)
            {
                bool planter = ReadFlag(OutdoorPlanterFlag, display);
                var kinds = new List<DealerMachineKind>(3);
                if (ReadFlag(HugePackagerFlag, display)) kinds.Add(DealerMachineKind.HugePackager);
                if (ReadFlag(CompostMachineFlag, display)) kinds.Add(DealerMachineKind.CompostMachine);
                if (ReadFlag(FriesPressFlag, display)) kinds.Add(DealerMachineKind.FrenchFriesPress);
                if (planter) { planters.Add(display); continue; }
                if (kinds.Count == 0) continue;
                if (kinds.Count != 1)
                    throw new InvalidOperationException("Dealer machine listing has conflicting upgrade flags: " + RuntimeProbe.CanonicalPath(display.transform));
                var kind = kinds[0];
                if (offers.ContainsKey(kind))
                    throw new InvalidOperationException("Duplicate dealer machine listing: " + kind);
                offers.Add(kind, new Offer(kind, display));
            }

            DealerMachineKind[] required = { DealerMachineKind.HugePackager, DealerMachineKind.CompostMachine, DealerMachineKind.FrenchFriesPress };
            if (required.Any(kind => !offers.ContainsKey(kind)))
                throw new InvalidOperationException("The scene does not contain all three supported dealer machine listings.");
            var planterKinds = new[] { DealerMachineKind.OutdoorPlanter1, DealerMachineKind.OutdoorPlanter2,
                DealerMachineKind.OutdoorPlanter3, DealerMachineKind.OutdoorPlanter4 };
            var orderedPlanters = planters.OrderBy(display => RuntimeProbe.CanonicalPath(display.transform), StringComparer.Ordinal).ToArray();
            if (orderedPlanters.Length > planterKinds.Length)
                Debug.LogWarning("MonMP dealer: more outdoor planter listings than supported; extra stacks stay host-only.");
            for (int i = 0; i < Math.Min(orderedPlanters.Length, planterKinds.Length); i++)
                if (PlanterStackField.GetValue(orderedPlanters[i]) is GameObject stack && stack != null)
                    offers.Add(planterKinds[i], new Offer(planterKinds[i], orderedPlanters[i], stack));

            foreach (var offer in offers.Values)
            {
                var registered = identities.Bind(offer.Entity, DealerMachineState.Adapter, offer.Display.gameObject,
                    offer.SpawnKey, true, EntityPersistence.SessionOnly);
                if (registered.gameObject != offer.Display.gameObject || registered.AdapterType != DealerMachineState.Adapter ||
                    registered.SpawnKey != offer.SpawnKey)
                    throw new InvalidOperationException("The dealer machine listing did not register as its exact static display.");
                kindsByEntity.Add(offer.Entity, offer.Kind);
                state.Add(offer.Kind, Capture(offer));
                originalDisplays.Add(offer.Kind, offer.Display.gameObject.activeSelf);
                if (offer.Stack != null) originalStacks.Add(offer.Kind, offer.Stack.activeSelf);
            }


#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-dealer-machines.tsv"),
                new[] { "kind\tentity\tprice\tpurchased\tactive\tpath" }.Concat(offers.Values
                    .OrderBy(offer => offer.Kind)
                    .Select(offer => offer.Kind + "\t" + offer.Entity.ToString("X16") + "\t" + Price(offer.Display).ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "\t" + (offer.Planter ? !offer.Stack!.activeSelf : todo.isDone(PurchaseLine(offer.Kind))) + "\t" + offer.Display.gameObject.activeSelf + "\t" + RuntimeProbe.CanonicalPath(offer.Display.transform))));
#endif

            Active = this;
        }

        private static FieldInfo RequiredField(Type type, string name) => AccessTools.Field(type, name) ??
            throw new InvalidOperationException("Dealer machine native field is missing: " + type.Name + "." + name);
        private static MethodInfo RequiredMethod(Type type, string name) => AccessTools.Method(type, name, Type.EmptyTypes) ??
            throw new InvalidOperationException("Dealer machine native activation method is missing: " + type.Name + "." + name);
        private static GameObject ReadGameObject(FieldInfo field, object owner, string label) => field.GetValue(owner) as GameObject ??
            throw new InvalidOperationException("The bunker " + label + " object is missing.");
        private static GameObject ReadComponentGameObject(FieldInfo field, object owner, string label) =>
            (field.GetValue(owner) as Component)?.gameObject ?? throw new InvalidOperationException("The bunker " + label + " component is missing.");
        private static bool ReadFlag(FieldInfo field, object owner) => field.GetValue(owner) is bool value && value;
        private static float Price(ItemForSale_Dealer display) => ListingPrice.GetValue(display) is float value ? value : float.NaN;
        private static ToDoList_Line PurchaseLine(DealerMachineKind kind) => kind switch
        {
            DealerMachineKind.HugePackager => ToDoList_Line.Dealer_HugePackagerBought,
            DealerMachineKind.CompostMachine => ToDoList_Line.Dealer_CompostMachineBought,
            DealerMachineKind.FrenchFriesPress => ToDoList_Line.Dealer_FrenchFriesPressBought,
            _ => throw new InvalidDataException("Unknown dealer machine kind.")
        };

        public IEnumerable<WorldRecord> InitialRecords()
        {
            foreach (var offer in offers.Values)
                yield return new WorldRecord(offer.Entity, 1, DealerMachineState.Adapter, Capture(offer).Encode());
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        // True means the local native handler must be suppressed.
        internal bool InterceptMouseDown(ItemForSale_Dealer display)
        {
            if (disposed || session?.IsRunning != true || !TryGetOffer(display, out var offer)) return false;
            if (host)
            {
                if (ReferenceEquals(executingNative, display)) return false;
                if (!NewPurchasesBlocked) return false;
                Report("Wait for the multiplayer save to finish before buying this machine.");
                return true;
            }
            if (NewPurchasesBlocked)
            {
                Report("Wait for the multiplayer save to finish before buying this machine.");
                return true;
            }
            if (channel?.FullySynchronized != true || !IsExactRegisteredDisplay(offer))
            {
                Report("This dealer machine is unavailable until the host world is synchronized.");
                return true;
            }
            if (pending.Count + timedOut.Count >= 8)
            {
                Report("Wait for the pending dealer machine purchase to finish.");
                return true;
            }
            int price = PriceCents(Price(display));
            if (price <= 0)
            {
                Report("The dealer machine price is invalid; ask the host to check the listing.");
                return true;
            }
            if (cash != null && cash.GetCash < price)
            {
                Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_NotEnoughCash"));
                return true;
            }
            var request = new DealerMachinePurchaseRequest(offer.Kind, Guid.NewGuid(), price);
            pending.Add(request.Operation, (offer.Entity, Time.unscaledTime + CommandTimeout));
            channel!.Request(offer.Entity, request.Encode());
            return true;
        }

        internal void AfterNativeMouseDown(ItemForSale_Dealer display)
        {
            if (host && !disposed && session?.IsRunning == true && executingNative == null && TryGetOffer(display, out var offer))
                Publish(offer);
        }

        private bool TryGetOffer(ItemForSale_Dealer display, out Offer offer)
        {
            offer = null!;
            if (display == null) return false;
            DealerMachineKind? kind = null;
            if (ReadFlag(HugePackagerFlag, display)) kind = DealerMachineKind.HugePackager;
            if (ReadFlag(CompostMachineFlag, display))
            {
                if (kind.HasValue) return false;
                kind = DealerMachineKind.CompostMachine;
            }
            if (ReadFlag(FriesPressFlag, display))
            {
                if (kind.HasValue) return false;
                kind = DealerMachineKind.FrenchFriesPress;
            }
            if (ReadFlag(OutdoorPlanterFlag, display))
            {
                if (kind.HasValue) return false;
                offer = offers.Values.FirstOrDefault(candidate => candidate.Planter && ReferenceEquals(candidate.Display, display))!;
                return offer != null && IsExactRegisteredDisplay(offer);
            }
            if (!kind.HasValue || !offers.TryGetValue(kind.Value, out offer!)) return false;
            return ReferenceEquals(offer.Display, display) && IsExactRegisteredDisplay(offer);
        }

        private bool IsExactRegisteredDisplay(Offer offer)
        {
            var identity = identities.Resolve(offer.Entity);
            return identity != null && identity.gameObject == offer.Display.gameObject &&
                identity.AdapterType == DealerMachineState.Adapter && identity.SpawnKey == offer.SpawnKey;
        }

        private static int PriceCents(float price)
        {
            if (float.IsNaN(price) || float.IsInfinity(price) || price <= 0 || price > 10000000f) return -1;
            // A float listing price is only near a whole cent: 25400.69f is 2540068.945 cents, which an
            // exact-cent check rejected, so every guest purchase of the Huge Packager failed (#232).
            return (int)Math.Round((double)price * 100d, MidpointRounding.AwayFromZero);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var entity in kindsByEntity.Keys)
                if (records.TryGetValue(entity, out var record)) Apply(record);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == DealerMachineState.Adapter) Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            if (!kindsByEntity.TryGetValue(record.Id, out var expected) || record.Adapter != DealerMachineState.Adapter)
                throw new InvalidDataException("Unknown dealer machine presentation record.");
            var next = DealerMachineState.Decode(record.Payload);
            if (next.Kind != expected) throw new InvalidDataException("Dealer machine presentation kind does not match its display.");
            state[expected] = next;
            if (host) return;
            if (offers.TryGetValue(expected, out var offer) && offer.Display.gameObject.activeSelf != next.DisplayActive)
                offer.Display.gameObject.SetActive(next.DisplayActive);
            ApplyMachine(next);
        }

        private DealerMachineState Capture(Offer offer)
        {
            if (offer.Planter)
                return new DealerMachineState(offer.Kind, !offer.Stack!.activeSelf, offer.Display.gameObject.activeSelf, offer.Stack.activeSelf);
            MachinePresentation machine = CaptureMachine();
            bool primary, secondary = false;
            switch (offer.Kind)
            {
                case DealerMachineKind.HugePackager: primary = machine.PackagerBig; secondary = machine.PackagerHuge; break;
                case DealerMachineKind.CompostMachine: primary = machine.Compost; break;
                case DealerMachineKind.FrenchFriesPress: primary = machine.FriesPress; break;
                default: throw new InvalidDataException("Unknown dealer machine kind.");
            }
            return new DealerMachineState(offer.Kind, todo != null && todo.isDone(PurchaseLine(offer.Kind)),
                offer.Display.gameObject.activeSelf, primary, secondary);
        }

        private MachinePresentation CaptureMachine() =>
            new MachinePresentation(packagerBig.activeSelf, packagerHuge.activeSelf, compostMachine.activeSelf, friesPress.activeSelf);

        private bool MachineMatches(DealerMachineState expected)
        {
            if (DealerMachineState.IsPlanter(expected.Kind))
                return !offers.TryGetValue(expected.Kind, out var planter) || planter.Stack!.activeSelf == expected.MachinePrimaryActive;
            var actual = CaptureMachine();
            return expected.Kind switch
            {
                DealerMachineKind.HugePackager => actual.PackagerBig == expected.MachinePrimaryActive &&
                    actual.PackagerHuge == expected.MachineSecondaryActive,
                DealerMachineKind.CompostMachine => actual.Compost == expected.MachinePrimaryActive,
                DealerMachineKind.FrenchFriesPress => actual.FriesPress == expected.MachinePrimaryActive,
                _ => false
            };
        }

        private bool MachineMatchesPurchased(DealerMachineKind kind)
        {
            var actual = CaptureMachine();
            return kind switch
            {
                DealerMachineKind.HugePackager => !actual.PackagerBig && actual.PackagerHuge,
                DealerMachineKind.CompostMachine => actual.Compost,
                DealerMachineKind.FrenchFriesPress => actual.FriesPress,
                _ => false
            };
        }

        private void ApplyMachine(DealerMachineState expected)
        {
            if (MachineMatches(expected)) return;
            if (DealerMachineState.IsPlanter(expected.Kind))
            { offers[expected.Kind].Stack!.SetActive(expected.MachinePrimaryActive); return; }
            switch (expected.Kind)
            {
                case DealerMachineKind.HugePackager:
                    if (!expected.MachinePrimaryActive && expected.MachineSecondaryActive)
                        PackagerBought.Invoke(bunker, null);
                    else
                    {
                        packagerBig.SetActive(expected.MachinePrimaryActive);
                        packagerHuge.SetActive(expected.MachineSecondaryActive);
                    }
                    break;
                case DealerMachineKind.CompostMachine:
                    if (expected.MachinePrimaryActive) CompostBought.Invoke(bunker, null);
                    else compostMachine.SetActive(false);
                    break;
                case DealerMachineKind.FrenchFriesPress:
                    if (expected.MachinePrimaryActive) FriesPressBought.Invoke(bunker, null);
                    else friesPress.SetActive(false);
                    break;
            }
        }

        private void Rejected(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, true);
        [MonMP.Core.World.WorldCommandAdapters(DealerMachineState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, false);

        private void PurchaseCommand(uint peer, ulong entity, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || !DealerMachinePurchaseRequest.Is(payload) || session == null || channel == null || cash == null || todo == null)
                return;
            var request = DealerMachinePurchaseRequest.Decode(payload);
            if (!offers.TryGetValue(request.Kind, out var offer) || entity != offer.Entity ||
                !kindsByEntity.TryGetValue(entity, out var kind) || kind != request.Kind || !IsExactRegisteredDisplay(offer)) return;
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);
            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var previous))
                {

#if MONMP_DEVELOPMENT
                    TestDuplicateReplays++;
#endif

                    channel.Reply(peer, entity, previous.Encode());
                }
                else channel.Reply(peer, entity, new DealerMachinePurchaseReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }

            void Finish(PurchaseStatus status)
            {
                var reply = new DealerMachinePurchaseReply(request.Operation, status);

#if MONMP_DEVELOPMENT
                TestLastReply = reply;
#endif

                results[key] = reply;
                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, entity, reply.Encode());
            }

            int hostPrice = PriceCents(Price(offer.Display));
            bool exactDisplay = IsExactRegisteredDisplay(offer);
            bool displayActive = offer.Display.gameObject.scene.IsValid() && offer.Display.gameObject.activeInHierarchy;
            var store = ListingStore.GetValue(offer.Display) as Stores;
            bool storeOpen = store != null && store.isStoreOpen();
            bool bunkerBuilt = todo.isDone(ToDoList_Line.Dealer_BunkerBuilt);
            bool alreadyPurchased = offer.Planter ? !offer.Stack!.activeSelf : todo.isDone(PurchaseLine(request.Kind));
            bool inRange = items.PurchaseInRange(peer, offer.Display.transform.position);
            float nativePrice = Price(offer.Display);
            bool canAfford = hostPrice > 0 && cash.GetCash >= hostPrice && cash.isCanAfford(nativePrice);
            PurchaseStatus decision = DealerMachinePurchasePolicy.ValidateHostRequest(stale,
                channel.FullySynchronized, NewPurchasesBlocked, results.Count < PurchaseClaims.Capacity,
                exactDisplay, displayActive, storeOpen, bunkerBuilt, alreadyPurchased,
                hostPrice > 0 && request.PriceCents == hostPrice, inRange, canAfford);
            if (decision != PurchaseStatus.Placed) { Finish(decision); return; }

            // These native upgrades bypass the ordinary store purchase adapter. Persist
            // the operation before MouseDown can debit cash or mutate progression.
            try
            {
                if (DurableClaim == null || !DurableClaim(buyer.ProfileId, request.Operation, entity))
                { Finish(PurchaseStatus.Uncertain); return; }
            }
            catch (TransactionReceiptCapacityException)
            { Finish(PurchaseStatus.Capacity); return; }
            catch (Exception ex)
            {
                Finish(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
                return;
            }

            int cashBefore = cash.GetCash;
            bool boughtBefore = alreadyPurchased;
            MachinePresentation machineBefore = CaptureMachine();
            bool displayBefore = offer.Display.gameObject.activeSelf;
            bool stackBefore = offer.Stack != null && offer.Stack.activeSelf;
            try
            {
                executingNative = offer.Display;
                using (Patches.RemoteActionScope.Enter()) offer.Display.MouseDown();
                executingNative = null;

                bool boughtAfter = offer.Planter ? !offer.Stack!.activeSelf : todo.isDone(PurchaseLine(request.Kind));
                int cashAfter = cash.GetCash;
                bool exactDebit = cashAfter == cashBefore - hostPrice;
                // A planter pack's planters spawn over the next frames and share as ordinary items.
                bool exactActivation = offer.Planter || MachineMatchesPurchased(request.Kind);
                if (!boughtBefore && boughtAfter && exactDebit && exactActivation)
                {

#if MONMP_DEVELOPMENT
                    TestNativePurchases++;
#endif

                    Publish(offer);
                    Finish(PurchaseStatus.Placed);
                }
                else if (cashAfter == cashBefore && boughtAfter == boughtBefore &&
                    SamePresentation(machineBefore, CaptureMachine()) && offer.Display.gameObject.activeSelf == displayBefore &&
                    (offer.Stack == null || offer.Stack.activeSelf == stackBefore))
                    Finish(PurchaseStatus.NativeRejected);
                else
                {
                    uncertainOutcome = true;
                    Publish(offer);
                    Finish(PurchaseStatus.Uncertain);
                }
            }
            catch (Exception ex)
            {
                executingNative = null;
                uncertainOutcome = true;
                Publish(offer);
                Finish(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
            }
            finally { executingNative = null; }
        }

        private static bool SamePresentation(MachinePresentation a, MachinePresentation b) =>
            a.PackagerBig == b.PackagerBig && a.PackagerHuge == b.PackagerHuge && a.Compost == b.Compost && a.FriesPress == b.FriesPress;

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || !kindsByEntity.ContainsKey(entity) || !DealerMachinePurchaseReply.Is(payload)) return;
            var reply = DealerMachinePurchaseReply.Decode(payload);
            if (pending.TryGetValue(reply.Operation, out var request))
            {
                if (request.Entity != entity) return;
                pending.Remove(reply.Operation);
            }
            else
            {
                if (!timedOut.TryGetValue(reply.Operation, out ulong timedEntity) || timedEntity != entity) return;
                timedOut.Remove(reply.Operation);
            }

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == PurchaseStatus.Uncertain) uncertainOutcome = true;
            // The dealer's own line for the buyer, which the host's native purchase no longer shows on its screen.
            if (reply.Status == PurchaseStatus.Placed)
            {
                if (offers.TryGetValue(kindsByEntity[entity], out var bought))
                {
                    bool planter = bought.Planter;
                    Singleton<UI_EventMessage>.i?.Show(TEXT.GetText(planter ? "DEALER_PacksOfOutdoorPlanterBought" : "Bunker_MachineBought"),
                        TEXT.GetText("Dealers_Name"), planter ? 7f : 10f, UI_EventMessage.EventIconType.Talk);
                }
                return;
            }
            Report(reply.Status switch
            {
                PurchaseStatus.Closed => "The dealer store is closed.",
                PurchaseStatus.Funds => "Not enough shared cash.",
                PurchaseStatus.TooFar => "Move closer to the dealer machine display.",
                PurchaseStatus.PriceChanged => "The dealer machine listing changed. Check it and try again.",
                PurchaseStatus.Uncertain => "The purchase outcome is uncertain. Check shared cash and the machine before retrying.",
                PurchaseStatus.Capacity => "This session cannot accept more dealer purchases.",
                _ => "The host declined the dealer machine purchase. Check bunker access and purchase progress."
            });
        }

        private void Expire(float now)
        {
            foreach (var entry in pending.Where(pair => now >= pair.Value.Deadline).ToArray())
            {
                pending.Remove(entry.Key);
                timedOut[entry.Key] = entry.Value.Entity;
                Report("Dealer purchase confirmation is delayed. Check shared cash and the machine before retrying.");
            }
        }

        private void Publish(Offer offer)
        {
            if (!host || channel == null || disposed) return;
            var next = Capture(offer);
            byte[] payload = next.Encode();
            if (state[offer.Kind].Encode().SequenceEqual(payload)) return;
            state[offer.Kind] = next;
            channel.Publish(offer.Entity, payload);
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true || channel == null) return;
            Expire(Time.unscaledTime);
            if (Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            if (host)
            {
                foreach (var offer in offers.Values) Publish(offer);
            }
            else
            {
                foreach (var offer in offers.Values)
                {
                    if (!state.TryGetValue(offer.Kind, out var current)) continue;
                    if (offer.Display.gameObject.activeSelf != current.DisplayActive || !MachineMatches(current))
                        ApplyPresentation(current, offer);
                }
            }
        }

        private void ApplyPresentation(DealerMachineState current, Offer offer)
        {
            if (offer.Display.gameObject.activeSelf != current.DisplayActive)
                offer.Display.gameObject.SetActive(current.DisplayActive);
            ApplyMachine(current);
        }

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
                        if (offer.Display != null && offer.Display.gameObject != null && originalDisplays.TryGetValue(offer.Kind, out bool active))
                            offer.Display.gameObject.SetActive(active);
                        if (offer.Stack != null && originalStacks.TryGetValue(offer.Kind, out bool stocked)) offer.Stack.SetActive(stocked);
                    }
                    if (packagerBig != null) packagerBig.SetActive(originalMachine.PackagerBig);
                    if (packagerHuge != null) packagerHuge.SetActive(originalMachine.PackagerHuge);
                    if (compostMachine != null) compostMachine.SetActive(originalMachine.Compost);
                    if (friesPress != null) friesPress.SetActive(originalMachine.FriesPress);
                }
            }
            finally
            {
                if (ReferenceEquals(Active, this)) Active = null;
                pending.Clear();
                timedOut.Clear();
                session = null;
                channel = null;
            }
        }
    }
}
