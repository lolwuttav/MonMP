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
    // Cottage ownership is a single host-side transaction. Guests send a quoted one-time purchase
    // request; the host runs Cottage.TryToBuy. The host also owns the live contents record, while
    // guest presentation is corrected from that record and restored when the session ends.
    internal sealed class SharedCottagePurchase : IDisposable
    {
        private readonly struct WheelbarrowPhysicsState
        {
            public readonly Rigidbody Body;
            public readonly bool IsKinematic;
            public readonly Vector3 Velocity;
            public readonly Vector3 AngularVelocity;
            public WheelbarrowPhysicsState(Rigidbody body)
            { Body = body; IsKinematic = body.isKinematic; Velocity = body.velocity; AngularVelocity = body.angularVelocity; }
        }

        public const float PurchasePrice = 5299.99f;
        public const int PurchasePriceCents = 529999;
        private const float CaptureInterval = 0.05f;
        private const float CommandTimeout = 10f;

        private static readonly FieldInfo SignCottage = AccessTools.Field(typeof(Cottage_SellSign), "Cottage");
        private static readonly FieldInfo CottageGate = AccessTools.Field(typeof(Cottage), "CottageGate");
        private static readonly FieldInfo CottageSign = AccessTools.Field(typeof(Cottage), "SellSign");

        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly bool host;
        private readonly Cottage? cottage;
        private readonly Cottage_SellSign? sellSign;
        private readonly Transform? gate;
        private readonly Transform? sign;
        private readonly Bunker_CompostMachine? compostMachine;
        private readonly Wheelbarrow? cottageWheelbarrow;
        private readonly WheelbarrowPhysicsState[] wheelbarrowPhysics = Array.Empty<WheelbarrowPhysicsState>();
        private readonly CoffeeSeedAutoSpawn? coffeeSeedAutoSpawn;
        private readonly Item_CoffeePercolator? coffeePercolator;
        private readonly Cash_Manager? cash;
        private readonly ToDoList_Manager? todo;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), CottagePurchaseReply> results =
            new Dictionary<(Guid, Guid), CottagePurchaseReply>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly HashSet<Guid> timedOut = new HashSet<Guid>();

        private NetworkSession? session;
        private WorldChannel? channel;
        private CottagePurchaseState state, original;
        private CottageContentsState contents, originalContents;
        private bool initialized, contentsInitialized, applyingContents, wheelbarrowPhysicsLocked, disposed, uncertainOutcome;
        private bool contentsPurchasePublished;
        // Set while the host runs a guest's native purchase; its gate animation starts a frame later.
        internal static bool PurchasingForGuest { get; private set; }
        private float nextCapture;

        internal static SharedCottagePurchase? Active { get; private set; }
        public bool NewPurchasesBlocked { get; set; }
        public bool TransactionsSettled => !uncertainOutcome && pending.Count == 0 && timedOut.Count == 0 && pendingUse.Count == 0 &&
            (coffeePercolator == null || !(bool)PercolatorBrewing.GetValue(coffeePercolator)) &&
            (compostMachine == null || !(bool)CompostWorking.GetValue(compostMachine));

#if MONMP_DEVELOPMENT
        internal Cottage? TestCottage => cottage;


        internal Cottage_SellSign? TestSign => sellSign;


        internal Transform? TestGate => gate;


        internal Item_CoffeePercolator? TestPercolator => coffeePercolator;
#endif

        internal bool IsPriceQuoteFor(Cottage_SellSign candidate) => !host && !disposed && session?.IsRunning == true &&
            channel?.FullySynchronized == true && candidate == sellSign;

#if MONMP_DEVELOPMENT
        internal CottagePurchaseState TestState => state;


        internal CottageContentsState TestContentsState => contents;


        internal bool TestContentsMatch => ContentsClose(CaptureContents(contents.Purchased), contents);


        internal bool TestContentsRestored => ContentsClose(CaptureContents(todo != null && todo.isDone(ToDoList_Line.Intermediate_CottageBought)), originalContents);


        internal bool TestWheelbarrowPhysicsRestored => wheelbarrowPhysics.All(previous => previous.Body == null ||
            (previous.Body.isKinematic == previous.IsKinematic && (previous.IsKinematic ||
                Vector3.Distance(previous.Body.velocity, previous.Velocity) < 0.01f &&
                Vector3.Distance(previous.Body.angularVelocity, previous.AngularVelocity) < 0.01f)));


        internal CottagePurchaseReply? TestLastReply { get; private set; }


        internal int TestNativePurchases { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal int TestFundsRejections { get; private set; }


        internal int TestRangeRejections { get; private set; }


        internal int TestPriceRejections { get; private set; }


        internal bool TestRestored
        {
            get
            {
                var actual = Capture();
                return actual.Purchased == original.Purchased && actual.SignVisible == original.SignVisible &&
                    Close(actual.Gate, original.Gate) && Close(actual.Sign, original.Sign) && TestContentsRestored && TestWheelbarrowPhysicsRestored;
            }
        }
#endif

        private static bool Close(ItemPose a, ItemPose b) =>
            Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z)) < 0.01f &&
            Quaternion.Angle(new Quaternion(a.Qx, a.Qy, a.Qz, a.Qw), new Quaternion(b.Qx, b.Qy, b.Qz, b.Qw)) < 0.1f;
        private static bool ContentsClose(CottageContentsState a, CottageContentsState b) =>
            a.Purchased == b.Purchased && Mathf.Abs(a.Compost - b.Compost) < 0.0051f &&
            Mathf.Abs(a.Fertilizer - b.Fertilizer) < 0.0051f && Close(a.Wheelbarrow, b.Wheelbarrow) &&
            a.CoffeeSeedsActive == b.CoffeeSeedsActive && a.PercolatorCoffeeAmount == b.PercolatorCoffeeAmount;

#if MONMP_DEVELOPMENT
        internal string TestRestoreDetails
        {
            get
            {
                var actual = Capture();
                var actualContents = CaptureContents(todo != null && todo.isDone(ToDoList_Line.Intermediate_CottageBought));
                return $"original purchased={original.Purchased}, sign={original.SignVisible}; actual purchased={actual.Purchased}, sign={actual.SignVisible}; gate distance={Distance(original.Gate, actual.Gate)}, gate angle={Angle(original.Gate, actual.Gate)}, sign distance={Distance(original.Sign, actual.Sign)}, sign angle={Angle(original.Sign, actual.Sign)}; compost={originalContents.Compost}/{actualContents.Compost}, fertilizer={originalContents.Fertilizer}/{actualContents.Fertilizer}, coffee seeds={originalContents.CoffeeSeedsActive}/{actualContents.CoffeeSeedsActive}, percolator={originalContents.PercolatorCoffeeAmount}/{actualContents.PercolatorCoffeeAmount}, wheelbarrow distance={Distance(originalContents.Wheelbarrow, actualContents.Wheelbarrow)}, wheelbarrow angle={Angle(originalContents.Wheelbarrow, actualContents.Wheelbarrow)}, wheelbarrow physics restored={TestWheelbarrowPhysicsRestored}";
            }
        }
#endif


        // Guest interactions that can change cottage contents are deliberately host-only in this
        // preview. The host's live record is the source of truth; these predicates are shared by
        // Harmony guards and the paired diagnostic.
        internal bool BlocksGuest(Bunker_CompostMachine candidate) => IsGuestSession && candidate == compostMachine;
        internal bool HandlesCompost(Bunker_CompostMachine candidate) => candidate != null && candidate == compostMachine;
        internal bool BlocksGuest(Wheelbarrow candidate) => IsGuestSession && candidate == cottageWheelbarrow && !guestDriving;
        internal bool BlocksGuest(CoffeeSeedAutoSpawn candidate) => IsGuestSession && candidate == coffeeSeedAutoSpawn;
        private bool IsGuestSession => !host && !applyingContents && !disposed && session?.IsRunning == true;


#if MONMP_DEVELOPMENT
        internal string TestGuestMutationGuards()
        {
            if (host || !IsGuestSession || !contentsInitialized || !contents.Purchased)
                return "guest cottage contents are not active";
            if (!BlocksGuest(compostMachine!) || !BlocksGuest(cottageWheelbarrow!) ||
                !BlocksGuest(coffeeSeedAutoSpawn!))
                return "a cottage component was not recognized as guest-owned state";

            var before = CaptureContents(contents.Purchased);
            compostMachine!.Button_MakeFertilizer();
            if (!ContentsClose(CaptureContents(contents.Purchased), before)) return "fertilizer button changed guest contents";
            AccessTools.Method(typeof(Bunker_CompostMachine), "HourChanged")!.Invoke(compostMachine, null);
            if (!ContentsClose(CaptureContents(contents.Purchased), before)) return "compost hour tick changed guest contents";

            coffeeSeedAutoSpawn!.MouseDown();
            if (!ContentsClose(CaptureContents(contents.Purchased), before)) return "coffee-seed interaction changed guest contents";
            coffeeSeedAutoSpawn.LoadFromSave(false);
            AccessTools.Method(typeof(CoffeeSeedAutoSpawn), "NewDay", new[] { typeof(int) })!
                .Invoke(coffeeSeedAutoSpawn, new object[] { 0 });
            var afterSeedReset = CaptureContents(contents.Purchased);
            if (afterSeedReset.CoffeeSeedsActive)
            {
                coffeeSeedAutoSpawn.LoadFromSave(before.CoffeeSeedsActive);
                return "coffee-seed daily tick changed guest contents";
            }
            coffeeSeedAutoSpawn.LoadFromSave(before.CoffeeSeedsActive);

            // Guest percolator clicks are supported host commands and are exercised by the
            // paired purchase/contents fixture after it synchronizes the host's brewed coffee.

            // Pushing the wheelbarrow is a host-claimed drive; while nobody drives, the native reset and
            // tick must leave the replicated pose alone.
            cottageWheelbarrow!.ResetLocation();
            AccessTools.Method(typeof(Wheelbarrow), "Update")!.Invoke(cottageWheelbarrow, null);
            if (!ContentsClose(CaptureContents(contents.Purchased), before)) return "wheelbarrow native update changed guest pose";
            return string.Empty;
        }


        internal CottageContentsState TestAdvanceHostContents()
        {
            if (!host || cottage == null || todo?.isDone(ToDoList_Line.Intermediate_CottageBought) != true)
                throw new InvalidOperationException("The host must own the cottage before advancing its contents test fixture.");
            if (compostMachine == null || cottageWheelbarrow == null || coffeeSeedAutoSpawn == null || coffeePercolator == null)
                throw new InvalidOperationException("The native cottage contents are not available.");
            compostMachine.LoadFromSave(0.49f, 0.38f);
            cottageWheelbarrow.transform.SetPositionAndRotation(cottageWheelbarrow.transform.position + Vector3.right * 0.37f,
                cottageWheelbarrow.transform.rotation * Quaternion.Euler(0f, 17f, 0f));
            coffeeSeedAutoSpawn.LoadFromSave(false);
            coffeePercolator.LoadFromSave(1);
            return CaptureContents(true);
        }


        internal void TestPerturbGuestContents()
        {
            if (host || !IsGuestSession || !contentsInitialized || !contents.Purchased)
                throw new InvalidOperationException("Guest cottage contents are not active for a repair test.");
            if (compostMachine == null || cottageWheelbarrow == null || coffeeSeedAutoSpawn == null || coffeePercolator == null)
                throw new InvalidOperationException("The native cottage contents are not available.");
            compostMachine.LoadFromSave(0.12f, 0.91f);
            cottageWheelbarrow.transform.SetPositionAndRotation(cottageWheelbarrow.transform.position + Vector3.up * 1.25f,
                cottageWheelbarrow.transform.rotation * Quaternion.Euler(0f, 71f, 0f));
            coffeeSeedAutoSpawn.LoadFromSave(true);
            coffeePercolator.LoadFromSave(3);
        }
#endif

        private static float Distance(ItemPose a, ItemPose b) => Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z));
        private static float Angle(ItemPose a, ItemPose b) => Quaternion.Angle(new Quaternion(a.Qx, a.Qy, a.Qz, a.Qw), new Quaternion(b.Qx, b.Qy, b.Qz, b.Qw));

        public SharedCottagePurchase(Gameplay gameplay, bool host, SharedItems items, string diagnostics)
        {
            this.gameplay = gameplay;
            this.host = host;
            this.items = items;
            cash = Object.FindObjectOfType<Cash_Manager>();
            todo = Object.FindObjectOfType<ToDoList_Manager>();

            var cottages = Object.FindObjectsOfType<Cottage>(true).Where(c => c.gameObject.scene.IsValid()).ToArray();
            if (cottages.Length > 1) throw new InvalidOperationException("More than one native cottage was found.");
            cottage = cottages.SingleOrDefault();
            if (cottage != null)
            {
                var gates = CottageGate.GetValue(cottage) as Transform;
                var signs = CottageSign.GetValue(cottage) as Transform;
                var matches = Object.FindObjectsOfType<Cottage_SellSign>(true)
                    .Where(s => s.gameObject.scene.IsValid() && ReferenceEquals(SignCottage.GetValue(s), cottage)).ToArray();
                if (gates == null || signs == null || matches.Length != 1)
                    throw new InvalidOperationException("The native cottage gate or sale sign is missing or ambiguous.");
                if (cottage.compostMachine == null || cottage.Cottage_Wheelbarrow == null ||
                    cottage.CoffeeSeedAutoSpawn == null || cottage.CoffeePercolator == null)
                    throw new InvalidOperationException("The native cottage contents are missing or ambiguous.");
                gate = gates;
                sign = signs;
                sellSign = matches[0];
                compostMachine = cottage.compostMachine;
                cottageWheelbarrow = cottage.Cottage_Wheelbarrow;
                wheelbarrowPhysics = cottageWheelbarrow.GetComponentsInChildren<Rigidbody>(true)
                    .Select(body => new WheelbarrowPhysicsState(body)).ToArray();
                if (wheelbarrowPhysics.Length == 0)
                    Debug.LogWarning("MonMP: Cottage_Wheelbarrow has no Rigidbody in its hierarchy; guest pose sync will rely on transform correction.");
                coffeeSeedAutoSpawn = cottage.CoffeeSeedAutoSpawn;
                coffeePercolator = cottage.CoffeePercolator;
                state = original = Capture();
                contents = originalContents = CaptureContents(state.Purchased);
                contentsPurchasePublished = state.Purchased;
                contentsInitialized = host;
            }


#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-cottage-purchase.txt"),
                cottage == null ? "COTTAGE_NOT_FOUND\n" : "COTTAGE_PURCHASE_REGISTERED\nEntity=" + CottagePurchaseState.Entity.ToString("X16") +
                    "\nPurchased=" + state.Purchased + "\nSignVisible=" + state.SignVisible +
                    "\nWheelbarrowRigidbodies=" + wheelbarrowPhysics.Length);
#endif

            Active = this;
        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            if (cottage != null) yield return new WorldRecord(CottagePurchaseState.Entity, 1, CottagePurchaseState.Adapter, state.Encode());
        }

        public IEnumerable<WorldRecord> ContentsInitialRecords()
        {
            if (cottage != null) yield return new WorldRecord(CottageContentsState.Entity, 1, CottageContentsState.Adapter, contents.Encode());
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            // A guest's native wheelbarrow physics would otherwise keep fighting host pose updates.
            if (!host)
            {
                wheelbarrowPhysicsLocked = wheelbarrowPhysics.Any(body => body.Body != null);
                try
                {
                    foreach (var body in wheelbarrowPhysics) if (body.Body != null) body.Body.isKinematic = true;
                }
                catch
                {
                    if (wheelbarrowPhysicsLocked) RestoreWheelbarrowPhysics();
                    wheelbarrowPhysicsLocked = false;
                    throw;
                }
            }
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.Changed += ContentsChanged;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        // True means the guest attempt was handled and the native sign must not buy locally.
        internal bool Purchase(Cottage_SellSign clicked)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (host)
            {
                if (!NewPurchasesBlocked) return false;
                Report("Wait for the multiplayer save to finish before buying the cottage.");
                return true;
            }
            if (sellSign == null || clicked != sellSign || channel?.FullySynchronized != true)
            {
                Report("The cottage purchase is unavailable until the host world is synchronized.");
                return true;
            }
            if (NewPurchasesBlocked)
            {
                Report("Wait for the multiplayer save to finish before buying the cottage.");
                return true;
            }
            if (pending.Count + timedOut.Count >= 8)
            {
                Report("Wait for the pending cottage purchase to finish.");
                return true;
            }
            Send(new CottagePurchaseRequest(Guid.NewGuid(), PurchasePriceCents), track: true);
            return true;
        }

        private void Send(CottagePurchaseRequest request, bool track)
        {
            if (channel == null || cottage == null) return;
            if (track && !pending.ContainsKey(request.Operation) && !timedOut.Contains(request.Operation))
                pending.Add(request.Operation, Time.unscaledTime + CommandTimeout);
            // The gate and sign animate during purchase; host validation uses current native
            // eligibility, so their fast-changing record revision must not stale this request.
            channel.Request(CottagePurchaseState.Entity, request.Encode(), true);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || cottage == null) return;
            if (records.TryGetValue(CottagePurchaseState.Entity, out var record)) Apply(record);
            if (records.TryGetValue(CottageContentsState.Entity, out var contentsRecord)) ApplyContents(contentsRecord);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != CottagePurchaseState.Entity || record.Adapter != CottagePurchaseState.Adapter) return;
            Apply(record);
        }

        private void ContentsChanged(WorldRecord record)
        {
            if (host || record.Id != CottageContentsState.Entity || record.Adapter != CottageContentsState.Adapter) return;
            ApplyContents(record);
        }

        private void Apply(WorldRecord record)
        {
            if (record.Adapter != CottagePurchaseState.Adapter || record.Id != CottagePurchaseState.Entity)
                throw new InvalidDataException("Unknown cottage purchase record.");
            state = CottagePurchaseState.Decode(record.Payload);
            ApplyPresentation(state);
            initialized = true;
        }

        private void ApplyContents(WorldRecord record)
        {
            if (record.Adapter != CottageContentsState.Adapter || record.Id != CottageContentsState.Entity)
                throw new InvalidDataException("Unknown cottage contents record.");
            contents = CottageContentsState.Decode(record.Payload);
            contentsInitialized = true;
            // The host may publish an unowned baseline at join. It only becomes visible on the guest
            // once ownership is established, or when joining a world where the cottage is already owned.
            if (contents.Purchased) ApplyContentsPresentation(contents);
        }

        private CottagePurchaseState Capture()
        {
            if (gate == null || sign == null) throw new InvalidOperationException("The native cottage is not available.");
            bool purchased = todo != null && todo.isDone(ToDoList_Line.Intermediate_CottageBought);
            return new CottagePurchaseState(purchased, sign.gameObject.activeSelf, Pose(gate), Pose(sign));
        }

        private CottageContentsState CaptureContents(bool purchased)
        {
            if (compostMachine == null || cottageWheelbarrow == null || coffeeSeedAutoSpawn == null || coffeePercolator == null)
                throw new InvalidOperationException("The native cottage contents are not available.");
            return new CottageContentsState(purchased, compostMachine.Get_Compost, compostMachine.Get_Fertilizer,
                Pose(cottageWheelbarrow.transform), coffeeSeedAutoSpawn.Get_IsActive, coffeePercolator.Get_CoffeeAmount);
        }

        private void ApplyPresentation(CottagePurchaseState next)
        {
            if (gate == null || sign == null) return;
            SetPose(gate, next.Gate);
            SetPose(sign, next.Sign);
            if (sign.gameObject.activeSelf != next.SignVisible) sign.gameObject.SetActive(next.SignVisible);
        }

        private void ApplyContentsPresentation(CottageContentsState next)
        {
            if (compostMachine == null || cottageWheelbarrow == null || coffeeSeedAutoSpawn == null || coffeePercolator == null) return;
            // These native loaders only set saved values and refresh visuals. Keep gate/sign animation
            // under CottagePurchaseState; contents are reapplied on snapshots, deltas and local drift.
            applyingContents = true;
            try
            {
                compostMachine.LoadFromSave(next.Fertilizer, next.Compost);
                // The barrow this guest is pushing follows its hands, not the host's echo.
                if (!guestDriving) SetPose(cottageWheelbarrow.transform, next.Wheelbarrow);
                coffeeSeedAutoSpawn.LoadFromSave(next.CoffeeSeedsActive);
                coffeePercolator.LoadFromSave(next.PercolatorCoffeeAmount);
            }
            finally { applyingContents = false; }
        }

        private static ItemPose Pose(Transform transform)
        {
            Vector3 p = transform.position;
            Quaternion q = transform.rotation;
            return new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
        }

        private static void SetPose(Transform transform, ItemPose pose) =>
            transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw));

        private void Rejected(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, stale: true);
        [MonMP.Core.World.WorldCommandAdapters(CottagePurchaseState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (host && peer != 0 && entity == CottagePurchaseState.Entity && CottageUseRequest.Is(payload) && cottage != null && channel != null)
            {
                try { UseCommand(peer, CottageUseRequest.Decode(payload)); }
                catch (InvalidDataException) { }
                return;
            }
            PurchaseCommand(peer, entity, payload, stale: false);
        }

        private void PurchaseCommand(uint peer, ulong entity, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || entity != CottagePurchaseState.Entity || !CottagePurchaseRequest.Is(payload) ||
                session == null || channel == null || cottage == null || sellSign == null) return;
            var request = CottagePurchaseRequest.Decode(payload);
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);

            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var previous))
                {

#if MONMP_DEVELOPMENT
                    TestDuplicateReplays++;
#endif

                    channel.Reply(peer, CottagePurchaseState.Entity, previous.Encode());
                }
                else channel.Reply(peer, CottagePurchaseState.Entity,
                    new CottagePurchaseReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }

            void Finish(PurchaseStatus status)
            {
                var reply = new CottagePurchaseReply(request.Operation, status);

#if MONMP_DEVELOPMENT
                TestLastReply = reply;
#endif

                if (status == PurchaseStatus.Funds)
#if MONMP_DEVELOPMENT
TestFundsRejections++;
#else
{}
#endif

                else if (status == PurchaseStatus.TooFar)
#if MONMP_DEVELOPMENT
TestRangeRejections++;
#else
{}
#endif

                else if (status == PurchaseStatus.PriceChanged)
#if MONMP_DEVELOPMENT
TestPriceRejections++;
#else
{}
#endif

                results[key] = reply;
                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, CottagePurchaseState.Entity, reply.Encode());
            }

            if (stale || request.PriceCents != PurchasePriceCents) { Finish(PurchaseStatus.PriceChanged); return; }
            if (NewPurchasesBlocked) { Finish(PurchaseStatus.NativeRejected); return; }
            if (!sellSign.gameObject.activeInHierarchy || todo == null || todo.isDone(ToDoList_Line.Intermediate_CottageBought))
            { Finish(PurchaseStatus.Unavailable); return; }
            if (!items.PurchaseInRange(peer, sellSign.transform.position)) { Finish(PurchaseStatus.TooFar); return; }
            if (cash == null || !cash.isCanAfford(PurchasePrice)) { Finish(PurchaseStatus.Funds); return; }
            if (gameplay == null || gameplay.Store_PoliceInterceptor == null || gameplay.Store_PoliceInterceptor.Get_HasCriminalRecord)
            { Finish(PurchaseStatus.NativeRejected); return; }

            int cashBefore = cash.GetCash;
            bool purchasedBefore = todo.isDone(ToDoList_Line.Intermediate_CottageBought);
            try
            {
                PurchasingForGuest = true;
                try { cottage.TryToBuy(); }
                finally { PurchasingForGuest = false; }
                int expectedCash = cashBefore - PurchasePriceCents;
                bool purchasedAfter = todo.isDone(ToDoList_Line.Intermediate_CottageBought);
                if (purchasedAfter && !purchasedBefore && cash.GetCash == expectedCash)
                {

#if MONMP_DEVELOPMENT
                    TestNativePurchases++;
#endif

                    PublishContents();
                    Finish(PurchaseStatus.Placed);
                }
                else if (cash.GetCash == cashBefore && purchasedAfter == purchasedBefore)
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
            finally { Publish(); }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (!host && entity == CottagePurchaseState.Entity && CottageUseReply.Is(payload))
            {
                try { UseReply(CottageUseReply.Decode(payload)); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid cottage use reply: " + ex.Message); }
                return;
            }
            if (host || entity != CottagePurchaseState.Entity || !CottagePurchaseReply.Is(payload)) return;
            var reply = CottagePurchaseReply.Decode(payload);
            if (!pending.Remove(reply.Operation) && !timedOut.Remove(reply.Operation)) return;

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == PurchaseStatus.Uncertain) uncertainOutcome = true;
            // The buyer hears the gate open as the native purchase does; the host hears it at the gate.
            if (reply.Status == PurchaseStatus.Placed)
                try { Singleton<SOUND_Manager>.i.PlayOneShot2D(Singleton<SOUND_Manager>.i.FenceDoorOpenSqueak, _randomPitch: false); }
                catch (System.Exception) { }
            if (reply.Status != PurchaseStatus.Placed) Report(Message(reply.Status));
        }

        private void Expire(float now)
        {
            foreach (var entry in pending.Where(p => now >= p.Value).ToArray())
            {
                pending.Remove(entry.Key);
                timedOut.Add(entry.Key);
                Report("Cottage purchase confirmation is delayed. Check cash and the gate before trying again.");
            }
        }

        private static string Message(PurchaseStatus status) => status switch
        {
            PurchaseStatus.Unavailable => "The cottage is no longer for sale.",
            PurchaseStatus.PriceChanged => "The cottage price changed. Check the sign and try again.",
            PurchaseStatus.TooFar => "Move closer to the cottage sign before buying it.",
            PurchaseStatus.Funds => "Not enough shared cash.",
            PurchaseStatus.Uncertain => "Cottage purchase outcome is uncertain. Check your cash and the gate before retrying.",
            PurchaseStatus.Capacity => "This session cannot accept more cottage purchases.",
            _ => "The host declined the cottage purchase. Check that outstanding tickets are paid."
        };

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        private void Publish()
        {
            if (!host || cottage == null || channel == null || disposed) return;
            state = Capture();
            channel.Publish(CottagePurchaseState.Entity, state.Encode());
        }

        private void PublishContents()
        {
            if (!host || cottage == null || channel == null || disposed) return;
            contents = CaptureContents(todo != null && todo.isDone(ToDoList_Line.Intermediate_CottageBought));
            contentsInitialized = true;
            channel.Publish(CottageContentsState.Entity, contents.Encode());
            if (contents.Purchased) contentsPurchasePublished = true;
        }

        public void Update()
        {
            if (disposed || session?.IsRunning != true || cottage == null || channel == null) return;
            Expire(Time.unscaledTime);
            UpdateUse(Time.unscaledTime);
            if (host)
            {
                if (Time.unscaledTime < nextCapture) return;
                nextCapture = Time.unscaledTime + CaptureInterval;
                var current = Capture();
                if (current.Purchased && !contentsPurchasePublished) PublishContents();
                else if (!current.Purchased) contentsPurchasePublished = false;
                else
                {
                    var currentContents = CaptureContents(current.Purchased);
                    if (!ContentsClose(currentContents, contents)) PublishContents();
                }
                Publish();
            }
            else if (initialized && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + CaptureInterval;
                var actual = Capture();
                if (!actual.Encode().SequenceEqual(state.Encode())) ApplyPresentation(state);
                if (contentsInitialized && contents.Purchased && !ContentsClose(CaptureContents(contents.Purchased), guestDriving ? WithBarrow(contents) : contents))
                    ApplyContentsPresentation(contents);
            }
        }

        private CottageContentsState WithBarrow(CottageContentsState s) => new CottageContentsState(s.Purchased, s.Compost, s.Fertilizer,
            Pose(cottageWheelbarrow!.transform), s.CoffeeSeedsActive, s.PercolatorCoffeeAmount);

        // ---- Cottage use: guest wheelbarrow driving and percolator ---------------------------------
        private static readonly FieldInfo BarrowActive = AccessTools.Field(typeof(Wheelbarrow), "isActive");
        private static readonly FieldInfo BarrowHandle = AccessTools.Field(typeof(Wheelbarrow), "Handle_Position");
        private static readonly MethodInfo BarrowFreeze = AccessTools.Method(typeof(Wheelbarrow), "FreezeContent", new[] { typeof(bool) });
        private static readonly MethodInfo BarrowToGround = AccessTools.Method(typeof(Wheelbarrow), "BringBarrowBackToTheGround");
        private static readonly FieldInfo PercolatorBrewing = AccessTools.Field(typeof(Item_CoffeePercolator), "Coroutine_BrewingCoffee");
        private static readonly FieldInfo PercolatorFurnace = AccessTools.Field(typeof(Item_CoffeePercolator), "OverFurnace");
        private static readonly FieldInfo PercolatorSpawn = AccessTools.Field(typeof(Item_CoffeePercolator), "CoffeeSpawnLocation");
        private static readonly MethodInfo PercolatorBrewStart = AccessTools.Method(typeof(Item_CoffeePercolator), "BrewCoffee_Start");
        private static readonly MethodInfo PercolatorBrewing2 = AccessTools.Method(typeof(Item_CoffeePercolator), "BrewingCoffee");
        private static readonly FieldInfo CoffeeSeedSpawn = AccessTools.Field(typeof(CoffeeSeedAutoSpawn), "SeedsSpawn_Location");
        private static readonly FieldInfo CompostWorking = AccessTools.Field(typeof(Bunker_CompostMachine), "MachineWorking");
        private const float BarrowReach = 4.5f, BarrowSendInterval = 0.1f, BarrowDriverTimeout = 5f;

        private readonly Dictionary<uint, (MonMP.Core.Protocol.PlayerPose Pose, float Time)> presence = new Dictionary<uint, (MonMP.Core.Protocol.PlayerPose, float)>();
        // Host: the guest driving the wheelbarrow and its latest pose.
        private uint barrowDriver;
        private float barrowDriverSeen;
        private Vector3 barrowTargetPosition;
        private Quaternion barrowTargetRotation;
        // Guest: this player is pushing the wheelbarrow.
        private bool guestDriving;
        private WheelbarrowItemCollision? barrowCollision;
        private float nextBarrowSend;
        private readonly Dictionary<Guid, float> pendingUse = new Dictionary<Guid, float>();
        private readonly Dictionary<(uint Peer, Guid Operation), (CottageUseAction Action, CottageUseStatus Status)> useResults =
            new Dictionary<(uint, Guid), (CottageUseAction, CottageUseStatus)>();

        internal bool GuestCollectionClick(MonoBehaviour candidate)
        {
            bool seeds = candidate == coffeeSeedAutoSpawn;
            if (disposed || session?.IsRunning != true || (!seeds && candidate != compostMachine)) return false;
            if (NewPurchasesBlocked || items.TransfersBlocked) { Report("Wait for the multiplayer save to finish."); return true; }
            if (host) return false;
            if (channel?.FullySynchronized != true) { Report("Wait for the shared world to finish loading."); return true; }
            if (!Owned) { Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_General_NotOnYourLand")); return true; }
            if (seeds && (!coffeeSeedAutoSpawn!.Get_IsActive || gameplay.Player.IsHoldingAnObject))
            {
                if (gameplay.Player.IsHoldingAnObject) Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_AlreadyHaveAItemInHand"));
                return true;
            }
            if (pendingUse.Count != 0) return true;
            SendUse(seeds ? CottageUseAction.CoffeeSeeds : CottageUseAction.Fertilizer, Pose(candidate.transform));
            return true;
        }

        private void OnPose(uint peer, MonMP.Core.Protocol.PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnPeerLeft(uint peer)
        {
            presence.Remove(peer);
            if (host && barrowDriver == peer) HostReleaseBarrow(null);
        }
        private bool PeerNear(uint peer, Vector3 at, float range) =>
            presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2f &&
            Vector3.Distance(new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z), at) <= range;
        private bool Owned => todo != null && todo.isDone(ToDoList_Line.Intermediate_CottageBought);

        internal bool GuestDrivesBarrow => guestDriving;
        internal bool BarrowDrivenByGuest(Wheelbarrow candidate) => host && barrowDriver != 0 && candidate == cottageWheelbarrow;

        // Guest click on the cottage wheelbarrow (enter or exit). Returns true when handled here.
        internal bool GuestBarrowClick(Wheelbarrow candidate)
        {
            if (host || disposed || session?.IsRunning != true || candidate != cottageWheelbarrow) return false;
            var barrow = cottageWheelbarrow!;
            if (guestDriving) { GuestExitBarrow(true); return true; }
            if (channel?.FullySynchronized != true) { Report("Wait for the shared world to finish loading."); return true; }
            if (!Owned) { Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_General_NotOnYourLand")); return true; }
            if (!gameplay.EnterExitWheelbarrow(barrow, (Transform)BarrowHandle.GetValue(barrow))) return true;
            BarrowActive.SetValue(barrow, true);
            guestDriving = true;
            barrowCollision = new WheelbarrowItemCollision(barrow.transform);
            nextBarrowSend = Time.unscaledTime + BarrowSendInterval;
            SendUse(CottageUseAction.WheelbarrowGrab, Pose(barrow.transform));
            return true;
        }
        private void GuestExitBarrow(bool notifyHost)
        {
            var barrow = cottageWheelbarrow;
            guestDriving = false;
            barrowCollision = null;
            if (barrow == null) return;
            BarrowActive.SetValue(barrow, false);
            if (gameplay.Get_PlayerState == PlayerState.InWheelbarrow && gameplay.Get_CurrentWheelBarrow == barrow)
                gameplay.EnterExitWheelbarrow(barrow, (Transform)BarrowHandle.GetValue(barrow));
            if (barrow.transform.parent != null) barrow.transform.SetParent(null);
            // The host settles the barrow back onto its legs and replicates the result.
            if (notifyHost && channel?.FullySynchronized == true) SendUse(CottageUseAction.WheelbarrowRelease, Pose(barrow.transform));
        }

        // Guest click on the cottage percolator. Returns true when handled here.
        internal bool GuestPercolatorClick(Item_CoffeePercolator candidate)
        {
            if (disposed || session?.IsRunning != true || candidate != coffeePercolator) return false;
            // This patch also sees ordinary host clicks. No new input/output mutation
            // may start during save draining, while an existing native brew may finish.
            if (NewPurchasesBlocked || items.TransfersBlocked) { Report("Wait for the multiplayer save to finish."); return true; }
            if (host) return false;
            if ((bool)PercolatorBrewing.GetValue(candidate)) return true;
            if (channel?.FullySynchronized != true) { Report("Wait for the shared world to finish loading."); return true; }
            if (candidate.Get_CoffeeAmount > 0 && gameplay.Player.IsHoldingAnObject)
            { Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_AlreadyHaveAItemInHand")); return true; }
            if (pendingUse.Count > 0) return true;
            SendUse(CottageUseAction.Percolator, Pose(candidate.transform));
            candidate.MouseOut();
            return true;
        }

        private void SendUse(CottageUseAction action, ItemPose pose)
        {
            var request = new CottageUseRequest(Guid.NewGuid(), action, pose);
            if (action != CottageUseAction.WheelbarrowMove && action != CottageUseAction.WheelbarrowRelease)
                pendingUse[request.Operation] = Time.unscaledTime + CommandTimeout;
            channel!.Request(CottagePurchaseState.Entity, request.Encode(), true);
        }

        private void UseCommand(uint peer, CottageUseRequest request)
        {
            bool transaction = request.Action != CottageUseAction.WheelbarrowMove && request.Action != CottageUseAction.WheelbarrowRelease;
            var key = (Peer: peer, Operation: request.Operation);
            CottageUseStatus status;
            if (transaction && useResults.TryGetValue(key, out var previous))
                status = previous.Action == request.Action ? previous.Status : CottageUseStatus.NotReady;
            else
            {
                // Retain all transaction outcomes for the session. When full, fail closed;
                // evicting an old coffee operation would allow replay after weekly refill.
                status = CottageUseStatus.NotReady;
                if (!transaction || useResults.Count < 8192)
                {
                    if (transaction) useResults.Add(key, (request.Action, status));
                    try { status = HostUse(peer, request); }
                    catch (Exception ex) { Debug.LogError("MonMP host cottage use failed: " + ex); }
                    if (transaction) useResults[key] = (request.Action, status);
                }
            }
            if (request.Action == CottageUseAction.WheelbarrowMove && status == CottageUseStatus.Accepted) return;
            if (session?.Peers.ContainsKey(peer) == true)
                channel!.Reply(peer, CottagePurchaseState.Entity, new CottageUseReply(request.Operation, request.Action, status).Encode());
        }

        private CottageUseStatus HostUse(uint peer, CottageUseRequest request)
        {
            if (cottageWheelbarrow == null || coffeePercolator == null) return CottageUseStatus.NotReady;
            if (!Owned) return CottageUseStatus.NotOwned;
            // Keep movement/release available to drain an existing wheelbarrow hold.
            if ((NewPurchasesBlocked || items.TransfersBlocked) &&
                request.Action != CottageUseAction.WheelbarrowMove && request.Action != CottageUseAction.WheelbarrowRelease)
                return CottageUseStatus.NotReady;
            var barrow = cottageWheelbarrow;
            var pose = request.Pose;
            var at = new Vector3(pose.X, pose.Y, pose.Z);
            switch (request.Action)
            {
                case CottageUseAction.WheelbarrowGrab:
                    if (barrowDriver == peer) return CottageUseStatus.Accepted;
                    if (barrowDriver != 0 || (bool)BarrowActive.GetValue(barrow)) return CottageUseStatus.Busy;
                    if (!PeerNear(peer, barrow.transform.position, BarrowReach)) return CottageUseStatus.TooFar;
                    barrow.StopAllCoroutines();
                    barrowDriver = peer; barrowDriverSeen = Time.unscaledTime;
                    barrowTargetPosition = barrow.transform.position; barrowTargetRotation = barrow.transform.rotation;
                    SharedWheelbarrows.FreezeLoad(barrow, gameplay, false);
                    return CottageUseStatus.Accepted;
                case CottageUseAction.WheelbarrowMove:
                    if (barrowDriver != peer) return CottageUseStatus.Busy;
                    if (!PeerNear(peer, at, 6f)) return CottageUseStatus.Accepted;
                    barrowDriverSeen = Time.unscaledTime;
                    barrowTargetPosition = at; barrowTargetRotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
                    return CottageUseStatus.Accepted;
                case CottageUseAction.WheelbarrowRelease:
                    if (barrowDriver != peer) return CottageUseStatus.Busy;
                    HostReleaseBarrow(PeerNear(peer, at, 6f) ? pose : (ItemPose?)null);
                    return CottageUseStatus.Accepted;
                case CottageUseAction.Percolator:
                    return HostPercolator(peer);
                case CottageUseAction.CoffeeSeeds:
                    return HostCoffeeSeeds(peer);
                case CottageUseAction.Fertilizer:
                    return HostFertilizer(peer);
                default: return CottageUseStatus.NotReady;
            }
        }

        private void HostReleaseBarrow(ItemPose? finalPose)
        {
            var barrow = cottageWheelbarrow;
            barrowDriver = 0;
            if (barrow == null) return;
            if (finalPose is ItemPose pose) SetPose(barrow.transform, pose);
            else barrow.transform.SetPositionAndRotation(barrowTargetPosition, barrowTargetRotation);
            BarrowFreeze.Invoke(barrow, new object[] { false });
            if (BarrowToGround.Invoke(barrow, null) is System.Collections.IEnumerator settle) barrow.StartCoroutine(settle);
            PublishContents();
        }

        private CottageUseStatus HostPercolator(uint peer)
        {
            var percolator = coffeePercolator!;
            if (!PeerNear(peer, percolator.transform.position, 4f)) return CottageUseStatus.TooFar;
            if ((bool)PercolatorBrewing.GetValue(percolator)) return CottageUseStatus.Busy;
            if (percolator.Get_CoffeeAmount > 0)
            {
                if (items.PeerHolding(peer)) return CottageUseStatus.HandsBusy;
                var spawn = (Transform)PercolatorSpawn.GetValue(percolator);
                var asset = Singleton<GameAssets>.i?.HomemadeCoffeeMug;
                ulong id = asset == null ? 0 : items.HostSpawnLeasedItem(asset, spawn.position, Quaternion.identity, peer);
                if (id == 0) return CottageUseStatus.NotReady;
                Singleton<SOUND_Manager>.i?.PlayOneShotOnTransform(percolator.transform, Singleton<SOUND_Manager>.i.Coffee_Pourring);
                percolator.LoadFromSave(percolator.Get_CoffeeAmount - 1);
                PublishContents();
                return CottageUseStatus.Poured;
            }
            if (PercolatorFurnace.GetValue(percolator) is Cottage_Furnace furnace && furnace != null && furnace.Get_FirePower < 0.3f)
                return CottageUseStatus.TooCold;
            // The same basket search as the native brew, to tell the guest why it cannot start.
            Tool_HarvestBasket? basket = null;
            foreach (var collider in Physics.OverlapSphere(percolator.transform.position, 2f))
            {
                if (collider.tag == "Player") continue;
                var found = collider.GetComponent<Tool_HarvestBasket>();
                if (found == null) continue;
                if (basket == null) basket = found;
                if (found.IntValue1 == 3) { basket = found; if (found.Value1 >= 10f) break; }
            }
            if (basket == null) return CottageUseStatus.NoBasket;
            if (basket.Value1 <= 0f) return CottageUseStatus.BasketEmpty;
            if (basket.IntValue1 != 3) return CottageUseStatus.NotCoffee;
            if (basket.Value1 < 10f) return CottageUseStatus.NotEnoughCoffee;
            using (Patches.RemoteActionScope.Enter()) PercolatorBrewStart.Invoke(percolator, null);
            return (bool)PercolatorBrewing.GetValue(percolator) ? CottageUseStatus.Brewing : CottageUseStatus.NotReady;
        }

        private CottageUseStatus HostCoffeeSeeds(uint peer)
        {
            var seeds = coffeeSeedAutoSpawn;
            if (seeds == null || !seeds.isActiveAndEnabled) return CottageUseStatus.NotReady;
            if (!PeerNear(peer, seeds.transform.position, 4f)) return CottageUseStatus.TooFar;
            if (!seeds.Get_IsActive) return CottageUseStatus.SupplyEmpty;
            if (items.PeerHolding(peer)) return CottageUseStatus.HandsBusy;
            var prefab = Singleton<GameAssets>.i?.GetSeeds(SeedsType.Coffee);
            var spawn = CoffeeSeedSpawn.GetValue(seeds) as Transform;
            if (prefab == null || spawn == null || items.HostSpawnLeasedItem(prefab, spawn.position, spawn.rotation, peer) == 0)
                return CottageUseStatus.NotReady;
            // A failed catalog/spawn/delivery must leave the weekly supply available.
            seeds.LoadFromSave(false);
            PublishContents();
            return CottageUseStatus.SeedsCollected;
        }

        private CottageUseStatus HostFertilizer(uint peer)
        {
            var machine = compostMachine;
            if (machine == null || !machine.isActiveAndEnabled) return CottageUseStatus.NotReady;
            if (!PeerNear(peer, machine.transform.position, 4f)) return CottageUseStatus.TooFar;
            if ((bool)CompostWorking.GetValue(machine)) return CottageUseStatus.Busy;
            if (machine.Get_Fertilizer < 0.25f) return CottageUseStatus.NotEnoughFertilizer;
            using (Patches.RemoteActionScope.Enter())
            using (SpatialSounds.AtPeer(peer)) machine.Button_MakeFertilizer();
            PublishContents();
            items.DiscoverSoon();
            return (bool)CompostWorking.GetValue(machine) ? CottageUseStatus.FertilizerStarted : CottageUseStatus.NotReady;
        }

        private void UseReply(CottageUseReply reply)
        {
            if (!pendingUse.Remove(reply.Operation) && reply.Action != CottageUseAction.WheelbarrowMove) return;
            switch (reply.Status)
            {
                case CottageUseStatus.Accepted: return;
                case CottageUseStatus.SupplyEmpty: return;
                case CottageUseStatus.SeedsCollected:
                    Singleton<SOUND_Manager>.i?.PlayOneShot2D(Singleton<SOUND_Manager>.i.Pack_GrabPlastic, _randomPitch: true);
                    return;
                case CottageUseStatus.FertilizerStarted:
                    Singleton<SOUND_Manager>.i?.Play_General_TapePacking();
                    return;
                case CottageUseStatus.Poured:
                    if (coffeePercolator != null)
                        Singleton<SOUND_Manager>.i?.PlayOneShotOnTransform(coffeePercolator.transform, Singleton<SOUND_Manager>.i.Coffee_Pourring);
                    // The host pour skips native GrabHomemadeCoffee, so award its achievement to this guest.
                    try { Singleton<Steam_AchievementManager>.i?.AchievementDone(AchievementType.MakeHomemadeCoffee); }
                    catch (Exception ex) { Debug.LogWarning("MonMP homemade coffee achievement failed: " + ex.Message); }
                    return;
                case CottageUseStatus.Brewing:
                    // The brewing animation, sound and smoke; the host's record carries the coffee.
                    if (coffeePercolator != null && !(bool)PercolatorBrewing.GetValue(coffeePercolator) &&
                        PercolatorBrewing2.Invoke(coffeePercolator, null) is System.Collections.IEnumerator brew)
                        coffeePercolator.StartCoroutine(brew);
                    return;
            }
            if (reply.Action == CottageUseAction.WheelbarrowGrab || reply.Action == CottageUseAction.WheelbarrowMove)
            {
                if (!guestDriving) return;
                GuestExitBarrow(false);
            }
            string? key = reply.Status switch
            {
                CottageUseStatus.HandsBusy => "ERROR_AlreadyHaveAItemInHand",
                CottageUseStatus.TooCold => "CoffeePercolator_Error_TooColdCantOperateIfNoFire",
                CottageUseStatus.NoBasket => "ERROR_CoffeePercolator_NoBasketClose",
                CottageUseStatus.BasketEmpty => "ERROR_BasketIsEmpty",
                CottageUseStatus.NotCoffee => "ERROR_CoffeePercolator_BasketDoesNotContainCoffee",
                CottageUseStatus.NotEnoughCoffee => "ERROR_CoffeePercolator_NotEnoughtToCreate",
                CottageUseStatus.NotOwned => "ERROR_General_NotOnYourLand",
                CottageUseStatus.NotEnoughFertilizer => "BUNKER_CompostMachine_NotEnoughtFertilizer",
                _ => null
            };
            if (key != null) { Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText(key)); return; }
            Report(reply.Status switch
            {
                CottageUseStatus.Busy => reply.Action == CottageUseAction.Percolator ? "The percolator is already brewing." : "Another player is pushing the wheelbarrow.",
                CottageUseStatus.TooFar => "Move closer and try again.",
                _ => "The host could not complete that action."
            });
        }

        private void UpdateUse(float now)
        {
            foreach (var entry in pendingUse.Where(p => now >= p.Value).ToArray()) pendingUse.Remove(entry.Key);
            var barrow = cottageWheelbarrow;
            if (barrow == null) return;
            if (host)
            {
                if (barrowDriver == 0) return;
                if (session?.Peers.ContainsKey(barrowDriver) != true || now - barrowDriverSeen > BarrowDriverTimeout) { HostReleaseBarrow(null); return; }
                float blend = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
                barrow.transform.SetPositionAndRotation(Vector3.Lerp(barrow.transform.position, barrowTargetPosition, blend),
                    Quaternion.Slerp(barrow.transform.rotation, barrowTargetRotation, blend));
                return;
            }
            if (!guestDriving) return;
            // Left the barrow some other way (sleep, a cutscene, a teleport).
            if (gameplay.Get_PlayerState != PlayerState.InWheelbarrow || gameplay.Get_CurrentWheelBarrow != barrow) { GuestExitBarrow(true); return; }
            ConstrainBarrowMotion();
            if (now >= nextBarrowSend && channel?.FullySynchronized == true)
            {
                nextBarrowSend = now + BarrowSendInterval;
                SendUse(CottageUseAction.WheelbarrowMove, Pose(barrow.transform));
            }
        }

        internal void ConstrainBarrowMotion()
        {
            if (!host && guestDriving && gameplay.Get_CurrentWheelBarrow == cottageWheelbarrow && gameplay.PlayerWalking != null)
                barrowCollision?.Constrain(gameplay.PlayerWalking.transform);
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest(Guid operation, int priceCents) => Send(new CottagePurchaseRequest(operation, priceCents), track: true);


        internal void TestPublishContents() => PublishContents();
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnPeerLeft; }
            if (guestDriving) { try { GuestExitBarrow(false); } catch (Exception ex) { Debug.LogWarning("MonMP wheelbarrow exit failed: " + ex.Message); } }
            if (host && barrowDriver != 0) { try { HostReleaseBarrow(null); } catch (Exception ex) { Debug.LogWarning("MonMP wheelbarrow release failed: " + ex.Message); } }
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.Changed -= ContentsChanged;
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected;
                channel.ReplyReceived -= Reply;
            }
            if (!host)
            {
                try
                {
                    if (cottage != null)
                    {
                        ApplyPresentation(original);
                        ApplyContentsPresentation(originalContents);
                    }
                }
                finally { if (wheelbarrowPhysicsLocked) RestoreWheelbarrowPhysics(); }
            }
            if (ReferenceEquals(Active, this)) Active = null;
            pending.Clear();
            pendingUse.Clear(); useResults.Clear(); presence.Clear();
            timedOut.Clear();
            session = null;
            channel = null;
        }

        private void RestoreWheelbarrowPhysics()
        {
            foreach (var previous in wheelbarrowPhysics)
            {
                if (previous.Body == null) continue;
                previous.Body.isKinematic = previous.IsKinematic;
                if (!previous.IsKinematic)
                {
                    previous.Body.velocity = previous.Velocity;
                    previous.Body.angularVelocity = previous.AngularVelocity;
                }
            }
        }
    }
}
