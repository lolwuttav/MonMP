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
    // The Dealer bunker is a one-time, $40,000 native purchase that starts a seven-day
    // construction timer. The host runs the native transaction; this adapter replicates
    // the sign and construction presentation while SharedEconomy/SharedProgress carry
    // the cash and objective state.
    internal sealed class SharedDealerBunkerPurchase : IDisposable
    {
        public const float PurchasePrice = 40000f;
        private const float CommandTimeout = 10f;
        private const float CaptureInterval = 0.1f;
        private static readonly FieldInfo DayField = RequireField(typeof(Home), "Bunker_DaySinceBuildStarted", typeof(int));
        private static readonly FieldInfo SignDealerField = RequireField(typeof(Store_Dealer_BunkerSellSign), "store_Dealer", typeof(Store_Dealer));
        private static readonly FieldInfo StoreSignField = RequireField(typeof(Store_Dealer), "SellSignBunker", typeof(GameObject));
        private static readonly FieldInfo NormalBathtubField = RequireField(typeof(Home), "Bathtub_Normal", typeof(GameObject));
        private static readonly FieldInfo BrokenBathroomDoorField = RequireField(typeof(Home), "BathroomDoor_Broken", typeof(GameObject));
        private static readonly FieldInfo RepairedBathroomDoorField = RequireField(typeof(Home), "BathroomDoor_Repaired", typeof(GameObject));

        private readonly SharedItems items;
        private readonly bool host;
        private readonly Home home;
        private readonly Store_Dealer store;
        private readonly Store_Dealer_BunkerSellSign sign;
        private readonly Cash_Manager cash;
        private readonly ToDoList_Manager todo;
        private readonly GameObject[] constructionObjects;
        private readonly GameObject normalBathtub, brokenBathroomDoor, repairedBathroomDoor;
        private readonly DealerBunkerPurchaseState original;
        private readonly int originalDay;
        private readonly bool originalBunkerActive, originalBunkerBathtubActive;
        private readonly bool originalNormalBathtubActive, originalBrokenBathroomDoorActive, originalRepairedBathroomDoorActive;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), DealerBunkerPurchaseReply> results =
            new Dictionary<(Guid, Guid), DealerBunkerPurchaseReply>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly HashSet<Guid> timedOut = new HashSet<Guid>();

        private NetworkSession? session;
        private WorldChannel? channel;
        private DealerBunkerPurchaseState state;
        private bool initialized, disposed, executing, uncertainOutcome, applying;
        private float nextCapture;

        internal static SharedDealerBunkerPurchase? Active { get; private set; }
        internal bool NewPurchasesBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0 && timedOut.Count == 0 && !executing && !uncertainOutcome;
        internal bool IsGuestSession => !host && !disposed && session?.IsRunning == true && channel != null;
        // Native purchase code may debit the host's shared wallet only while servicing the
        // validated remote bunker command. Keep both the mutation target and quoted amount narrow.
        internal bool AllowNativeEconomyMutation(string methodName) =>
            host && session?.IsRunning == true && executing &&
            methodName == nameof(Cash_Manager.Event_Spent);
        internal bool AllowNativeAffordabilityQuote(float price) =>
            host && session?.IsRunning == true && executing && price == PurchasePrice;
        internal static bool BlocksNativeGuestOperation
        {
            get
            {
                var session = MonMPPlugin.Instance?.Session;
                if (session?.IsRunning != true || session.IsHost) return false;
                return Active == null || Active.BlocksNativeGuestPurchase;
            }
        }

#if MONMP_DEVELOPMENT
        internal DealerBunkerPurchaseState TestState => state;


        internal Store_Dealer_BunkerSellSign TestSign => sign;


        internal bool TestCanRestore => host || RestoreMatchesOriginal();


        internal int TestNativePurchases { get; private set; }


        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal DealerBunkerPurchaseReply? TestLastReply { get; private set; }
#endif


        internal SharedDealerBunkerPurchase(Gameplay gameplay, bool host, SharedItems items)
        {
            if (gameplay == null) throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));

            home = UnityEngine.Object.FindObjectOfType<Home>() ?? throw new InvalidOperationException("The native home scene is unavailable.");
            cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>() ?? throw new InvalidOperationException("The native cash manager is unavailable.");
            todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>() ?? throw new InvalidOperationException("The native progression manager is unavailable.");
            var signs = UnityEngine.Object.FindObjectsOfType<Store_Dealer_BunkerSellSign>(true)
                .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid()).ToArray();
            if (signs.Length != 1) throw new InvalidOperationException("Expected one native Dealer bunker sell sign; found " + signs.Length + ".");
            sign = signs[0];
            store = SignDealerField.GetValue(sign) as Store_Dealer ?? throw new InvalidOperationException("The bunker sign has no Dealer store.");
            if (!ReferenceEquals(StoreSignField.GetValue(store), sign.gameObject))
                throw new InvalidOperationException("The Dealer store does not reference the discovered bunker sign.");

            constructionObjects = home.BunkerInConstruction_Objects;
            if (constructionObjects == null || constructionObjects.Length == 0 || constructionObjects.Any(value => value == null))
                throw new InvalidOperationException("The native bunker construction presentation is incomplete.");
            normalBathtub = NormalBathtubField.GetValue(home) as GameObject ?? throw new InvalidOperationException("The normal bathtub object is unavailable.");
            brokenBathroomDoor = BrokenBathroomDoorField.GetValue(home) as GameObject ?? throw new InvalidOperationException("The broken bathroom door is unavailable.");
            repairedBathroomDoor = RepairedBathroomDoorField.GetValue(home) as GameObject ?? throw new InvalidOperationException("The repaired bathroom door is unavailable.");
            if (home.homeBunker == null || home.bunker_Bathtub == null)
                throw new InvalidOperationException("The native bunker or its bathtub is unavailable.");

            originalDay = home.Get_Bunker_DaySinceBuildStarted;
            originalBunkerActive = home.homeBunker.gameObject.activeSelf;
            originalBunkerBathtubActive = home.bunker_Bathtub.gameObject.activeSelf;
            originalNormalBathtubActive = normalBathtub.activeSelf;
            originalBrokenBathroomDoorActive = brokenBathroomDoor.activeSelf;
            originalRepairedBathroomDoorActive = repairedBathroomDoor.activeSelf;
            original = Capture();
            state = original;
            Active = this;
        }

        private static FieldInfo RequireField(Type type, string name, Type fieldType)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.FieldType != fieldType)
                throw new InvalidOperationException("Unsupported native Dealer bunker field " + type.Name + "." + name + ".");
            return field;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => new[]
        { new WorldRecord(DealerBunkerPurchaseState.Entity, 1, DealerBunkerPurchaseState.Adapter, state.Encode()) };

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRequested += Command; channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
        }

        // Returns true when the multiplayer adapter owns the guest click and native code must stop.
        internal bool Purchase(Store_Dealer_BunkerSellSign candidate)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (candidate != sign) return !host;
            if (host)
            {
                if (!NewPurchasesBlocked) return false;
                Report("Wait for the multiplayer save to finish before buying the bunker.");
                return true;
            }
            if (!channel!.FullySynchronized)
            {
                Report("The bunker purchase is unavailable until the host world is synchronized.");
                return true;
            }
            if (NewPurchasesBlocked)
            {
                Report("Wait for the multiplayer save to finish before buying the bunker.");
                return true;
            }
            if (pending.Count + timedOut.Count >= 8)
            {
                Report("Wait for the pending bunker purchase to finish.");
                return true;
            }
            if (Singleton<Cash_Manager>.i != null && Singleton<Cash_Manager>.i.GetCash < DealerBunkerPurchaseRequest.PriceCents)
            {
                Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_NotEnoughCash"));
                return true;
            }
            var request = new DealerBunkerPurchaseRequest(Guid.NewGuid(), DealerBunkerPurchaseRequest.PriceCents);
            pending.Add(request.Operation, Time.unscaledTime + CommandTimeout);
            channel.Request(DealerBunkerPurchaseState.Entity, request.Encode());
            return true;
        }

        private bool snapshotReceived;

        internal bool BlocksNativeGuestPurchase => IsGuestSession && !applying;

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            // SnapshotApplied also fires for scoped repairs that carry unrelated records.
            if (!records.TryGetValue(DealerBunkerPurchaseState.Entity, out var record))
            {
                if (snapshotReceived) return;
                throw new InvalidDataException("Initial Dealer bunker purchase record is missing.");
            }
            snapshotReceived = true;
            Apply(record);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != DealerBunkerPurchaseState.Adapter) return;
            if (record.Id != DealerBunkerPurchaseState.Entity) throw new InvalidDataException("Unknown Dealer bunker purchase record.");
            Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            if (record.Id != DealerBunkerPurchaseState.Entity || record.Adapter != DealerBunkerPurchaseState.Adapter)
                throw new InvalidDataException("Unknown Dealer bunker state record.");
            state = DealerBunkerPurchaseState.Decode(record.Payload);
            ApplyPresentation(state);
            initialized = true;
        }

        private DealerBunkerPurchaseState Capture()
        {
            int day = home.Get_Bunker_DaySinceBuildStarted;
            bool purchased = todo.isDone(ToDoList_Line.Dealer_BunkerBought);
            bool built = todo.isDone(ToDoList_Line.Dealer_BunkerBuilt);
            bool constructionVisible = constructionObjects.Any(value => value.activeSelf);
            // A to-do line completed without Home.BunkerBought (old saves, the debug cheats) leaves the
            // clock behind the lines; publish the nearest consistent state instead of failing every tick.
            if (built && !purchased) purchased = true;
            // A guest can hold the host's build day before the bought line reaches its to-do list; a
            // started build clock only exists after a purchase.
            if (!purchased && day >= 0) purchased = true;
            if (purchased && day < 0) day = 0;
            if (built) { day = Math.Max(day, 7); constructionVisible = false; }
            return new DealerBunkerPurchaseState(purchased, sign.gameObject.activeSelf, constructionVisible, built, day);
        }

        private void ApplyPresentation(DealerBunkerPurchaseState next)
        {
            applying = true;
            try
            {
                DayField.SetValue(home, next.DaySinceBuildStarted);
                if (sign.gameObject.activeSelf != next.SignVisible) sign.gameObject.SetActive(next.SignVisible);
                foreach (var value in constructionObjects)
                    if (value.activeSelf != next.ConstructionVisible) value.SetActive(next.ConstructionVisible);
                SetActive(home.homeBunker.gameObject, next.Built);
                SetActive(home.bunker_Bathtub.gameObject, next.Built);
                SetActive(normalBathtub, !next.Built);
                SetActive(brokenBathroomDoor, !next.Built);
                SetActive(repairedBathroomDoor, next.Built);
            }
            finally { applying = false; }
        }

        private static void SetActive(GameObject target, bool active)
        { if (target != null && target.activeSelf != active) target.SetActive(active); }

        private void Rejected(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, stale: true);
        [MonMP.Core.World.WorldCommandAdapters(DealerBunkerPurchaseState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, stale: false);

        private void PurchaseCommand(uint peer, ulong entity, byte[] payload, bool stale)
        {
            if (!host || session?.IsHost != true || peer == 0 || entity != DealerBunkerPurchaseState.Entity ||
                !DealerBunkerPurchaseRequest.Is(payload) || channel == null) return;
            var request = DealerBunkerPurchaseRequest.Decode(payload);
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);
            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var previous)) channel.Reply(peer, DealerBunkerPurchaseState.Entity, previous.Encode());
                else channel.Reply(peer, DealerBunkerPurchaseState.Entity,
                    new DealerBunkerPurchaseReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }

            void Finish(PurchaseStatus status)
            {
                var reply = new DealerBunkerPurchaseReply(request.Operation, status);
                results[key] = reply;
#if MONMP_DEVELOPMENT
TestLastReply = reply;
#endif

                if (status == PurchaseStatus.Placed)
#if MONMP_DEVELOPMENT
TestAccepted++;
#else
{}
#endif
 else
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, DealerBunkerPurchaseState.Entity, reply.Encode());
            }

            bool dealerUnlocked = todo.isDone(ToDoList_Line.Dealer_Unlock);
            bool hydroCompleted = todo.isDone(ToDoList_Line.Intermediate_HydroCompleted);
            bool alreadyPurchased = todo.isDone(ToDoList_Line.Dealer_BunkerBought);
            bool inRange = items.PurchaseInRange(peer, sign.transform.position);
            if (stale)
            {
                Finish(PurchaseStatus.PriceChanged);
                return;
            }
            var decision = DealerBunkerPurchasePolicy.Validate(request.QuotedPriceCents,
                blocked: NewPurchasesBlocked,
                signVisible: sign.gameObject.activeInHierarchy,
                alreadyPurchased: alreadyPurchased,
                dealerUnlocked: dealerUnlocked,
                hydroCompleted: hydroCompleted,
                inRange: inRange,
                canAfford: cash.isCanAfford(PurchasePrice));
            if (decision != PurchaseStatus.Placed)
            {
                Finish(decision);
                return;
            }

            int cashBefore = cash.GetCash;
            int dayBefore = home.Get_Bunker_DaySinceBuildStarted;
            executing = true;
            try
            {
                // The dealer's build-start line is the buyer's; the guest shows it from its reply.
                using (Patches.RemoteActionScope.Enter()) store.TryToBuyBunker();
                int expectedCash = cashBefore - DealerBunkerPurchaseRequest.PriceCents;
                bool boughtAfter = todo.isDone(ToDoList_Line.Dealer_BunkerBought);
                int dayAfter = home.Get_Bunker_DaySinceBuildStarted;
                if (!alreadyPurchased && boughtAfter && dayAfter == 0 && cash.GetCash == expectedCash && !sign.gameObject.activeInHierarchy)
                {

#if MONMP_DEVELOPMENT
                    TestNativePurchases++;
#endif

                    Finish(PurchaseStatus.Placed);
                }
                else if (cash.GetCash == cashBefore && dayAfter == dayBefore && boughtAfter == alreadyPurchased)
                    Finish(PurchaseStatus.NativeRejected);
                else
                {
                    uncertainOutcome = true;
                    Finish(PurchaseStatus.Uncertain);
                }
            }
            catch (Exception ex)
            {
                uncertainOutcome = true;
                Finish(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
            }
            finally
            {
                executing = false;
                Publish();
            }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != DealerBunkerPurchaseState.Entity || !DealerBunkerPurchaseReply.Is(payload)) return;
            var reply = DealerBunkerPurchaseReply.Decode(payload);
            if (!pending.Remove(reply.Operation) && !timedOut.Remove(reply.Operation)) return;

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == PurchaseStatus.Uncertain) uncertainOutcome = true;
            if (reply.Status == PurchaseStatus.Placed)
                Singleton<UI_EventMessage>.i?.Show(TEXT.GetText("BUNKER_BuildStarted"), TEXT.GetText("Dealers_Name"), 15f, UI_EventMessage.EventIconType.Talk);
            else Report(Message(reply.Status));
        }

        private void Expire(float now)
        {
            foreach (var entry in pending.Where(value => now >= value.Value).ToArray())
            {
                pending.Remove(entry.Key); timedOut.Add(entry.Key);
                Report("Bunker purchase confirmation is delayed. Check shared cash and the bunker sign before retrying.");
            }
        }

        private static string Message(PurchaseStatus status) => status switch
        {
            PurchaseStatus.Unavailable => "The Dealer bunker is not currently available.",
            PurchaseStatus.PriceChanged => "The bunker price changed. Check the sign and try again.",
            PurchaseStatus.TooFar => "Move closer to the bunker sign before buying it.",
            PurchaseStatus.Funds => "Not enough shared cash.",
            PurchaseStatus.Uncertain => "Bunker purchase outcome is uncertain. Check shared cash and the construction sign before retrying.",
            PurchaseStatus.Capacity => "This session cannot accept more bunker purchase requests.",
            _ => "The Dealer bunker purchase was rejected by the host."
        };

        private void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        private void Publish()
        {
            if (!host || disposed || channel == null) return;
            var current = Capture();
            if (current.Equals(state)) return;
            state = current;
            channel.Publish(DealerBunkerPurchaseState.Entity, state.Encode());
        }

        public void Update()
        {
            if (disposed || session?.IsRunning != true || channel == null) return;
            Expire(Time.unscaledTime);
            if (!host)
            {
                if (initialized && Time.unscaledTime >= nextCapture)
                {
                    nextCapture = Time.unscaledTime + CaptureInterval;
                    var actual = Capture();
                    if (!actual.Equals(state)) ApplyPresentation(state);
                }
                return;
            }
            if (Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            Publish();
        }

        private bool RestoreMatchesOriginal()
        {
            if (home.Get_Bunker_DaySinceBuildStarted != originalDay || sign.gameObject.activeSelf != original.SignVisible ||
                home.homeBunker.gameObject.activeSelf != originalBunkerActive || home.bunker_Bathtub.gameObject.activeSelf != originalBunkerBathtubActive ||
                constructionObjects.Any(value => value.activeSelf != original.ConstructionVisible)) return false;
            return normalBathtub.activeSelf == originalNormalBathtubActive &&
                brokenBathroomDoor.activeSelf == originalBrokenBathroomDoorActive &&
                repairedBathroomDoor.activeSelf == originalRepairedBathroomDoorActive;
        }

        private void RestoreOriginalPresentation()
        {
            applying = true;
            try
            {
                // Unity's overloaded null check also detects native objects that were
                // destroyed during scene teardown. Restore each survivor independently.
                if (home != null) DayField.SetValue(home, originalDay);
                SetComponentActive(sign, original.SignVisible);
                foreach (var value in constructionObjects) SetActive(value, original.ConstructionVisible);
                if (home != null)
                {
                    SetComponentActive(home.homeBunker, originalBunkerActive);
                    SetComponentActive(home.bunker_Bathtub, originalBunkerBathtubActive);
                }
                SetActive(normalBathtub, originalNormalBathtubActive);
                SetActive(brokenBathroomDoor, originalBrokenBathroomDoorActive);
                SetActive(repairedBathroomDoor, originalRepairedBathroomDoorActive);
            }
            finally { applying = false; }
        }

        private static void SetComponentActive(Component value, bool active)
        {
            if (value != null) SetActive(value.gameObject, active);
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest(Guid operation, int priceCents) =>
            channel!.Request(DealerBunkerPurchaseState.Entity, new DealerBunkerPurchaseRequest(operation, priceCents).Encode());


        internal void TestPublish() => Publish();
#endif


        public void Dispose()
        {
            if (disposed) return;
            try
            {
                if (channel != null)
                {
                    channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
                    channel.CommandRequested -= Command; channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply;
                }
                if (!host && initialized) RestoreOriginalPresentation();
            }
            finally
            {
                disposed = true; pending.Clear(); timedOut.Clear(); session = null; channel = null;
                if (ReferenceEquals(Active, this)) Active = null;
            }
        }
    }
}
