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
    // The host executes the native one-time purchase methods after validating the exact registered
    // sign, shared funds, unpaid-ticket state, and (for the fishing boat) host save fish counts.
    internal sealed class SharedBoatSale : IDisposable
    {
        private const float CaptureInterval = 0.1f;
        private const float CommandTimeout = 10f;
        private static readonly FieldInfo FishNeeded = AccessTools.Field(typeof(BoatFishing_SellSign), "FishNeeded");
        private static readonly FieldInfo ToDisableWhenBought = AccessTools.Field(typeof(BoatFishing_SellSign), "ToDisableWhenBought");
        private static readonly FieldInfo FuelCapPart = AccessTools.Field(typeof(Boat_SmallCruiser_FuelCap), "fuelCap");

        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly bool host;
        private readonly Cash_Manager? cash;
        private readonly ToDoList_Manager? todo;
        private readonly SmallCruiser_SellSign? smallSign;
        private readonly BoatFishing_SellSign? fishingSign;
        private readonly GameObject[] fishingObjects;
        private readonly Boat_SmallCruiser_FuelCap? smallFuelCap;
        private readonly BoxCollider? fuelGateCollider;
        private readonly Part_FuelTankCap? fuelPart;
        private readonly BoxCollider? fuelPartCollider;
        private readonly Dictionary<BoatSaleKind, BoatSaleState> state = new Dictionary<BoatSaleKind, BoatSaleState>();
        private readonly Dictionary<(Guid Profile, Guid Operation), BoatSaleReply> results = new Dictionary<(Guid, Guid), BoatSaleReply>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly HashSet<Guid> timedOut = new HashSet<Guid>();
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private NetworkSession? session;
        private WorldChannel? channel;
        private Interactable? executingNative;
        private bool disposed, initializedSmall, initializedFishing, uncertainOutcome;
        private float nextCapture;

        internal static SharedBoatSale? Active { get; private set; }
        public bool NewPurchasesBlocked { get; set; }
        public bool TransactionsSettled => !uncertainOutcome && pending.Count == 0 && timedOut.Count == 0;

#if MONMP_DEVELOPMENT
        internal SmallCruiser_SellSign? TestSmallSign => smallSign;


        internal BoatFishing_SellSign? TestFishingSign => fishingSign;


        internal int TestNativePurchases { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal BoatSaleReply? TestLastReply { get; private set; }


        internal BoatSaleState TestState(BoatSaleKind kind) => state[kind];
#endif


        private sealed class PreviousPresentation
        {
            public readonly bool SignActive;
            public readonly bool[] FishingObjectActive;
            public readonly bool GateColliderEnabled, FuelCapControllerEnabled, FuelPartEnabled, FuelPartColliderEnabled;
            public PreviousPresentation(bool signActive, bool[] fishingObjectActive = null!, bool gateColliderEnabled = false,
                bool fuelCapControllerEnabled = false, bool fuelPartEnabled = false, bool fuelPartColliderEnabled = false)
            {
                SignActive = signActive; FishingObjectActive = fishingObjectActive ?? Array.Empty<bool>();
                GateColliderEnabled = gateColliderEnabled; FuelCapControllerEnabled = fuelCapControllerEnabled;
                FuelPartEnabled = fuelPartEnabled; FuelPartColliderEnabled = fuelPartColliderEnabled;
            }
        }
        private readonly PreviousPresentation? originalSmallPresentation;
        private readonly PreviousPresentation? originalFishingPresentation;

        public SharedBoatSale(Gameplay gameplay, bool host, SharedItems items, string diagnostics)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            cash = Object.FindObjectOfType<Cash_Manager>();
            todo = Object.FindObjectOfType<ToDoList_Manager>();

            var smallSigns = Object.FindObjectsOfType<SmallCruiser_SellSign>(true).Where(s => s.gameObject.scene.IsValid()).ToArray();
            var fishingSigns = Object.FindObjectsOfType<BoatFishing_SellSign>(true).Where(s => s.gameObject.scene.IsValid()).ToArray();
            if (smallSigns.Length > 1 || fishingSigns.Length > 1)
                throw new InvalidOperationException("Boat sale signs are ambiguous.");
            smallSign = smallSigns.SingleOrDefault();
            fishingSign = fishingSigns.SingleOrDefault();

            if (smallSign != null)
            {
                var caps = Object.FindObjectsOfType<Boat_SmallCruiser_FuelCap>(true).Where(c => c.gameObject.scene.IsValid()).ToArray();
                if (caps.Length != 1 || todo == null || cash == null)
                    throw new InvalidOperationException("The small cruiser sign, fuel-cap gate, or purchase managers are incomplete.");
                smallFuelCap = caps[0];
                fuelGateCollider = smallFuelCap.GetComponent<BoxCollider>();
                fuelPart = FuelCapPart.GetValue(smallFuelCap) as Part_FuelTankCap;
                fuelPartCollider = fuelPart == null ? null : fuelPart.GetComponent<BoxCollider>();
                if (fuelGateCollider == null || fuelPart == null || fuelPartCollider == null)
                    throw new InvalidOperationException("The small cruiser fuel-cap presentation is incomplete.");
                var snapshot = CaptureSmallPresentation();
                originalSmallPresentation = snapshot;
                var initial = Capture(BoatSaleKind.SmallCruiser);
                state.Add(BoatSaleKind.SmallCruiser, initial);
            }

            if (fishingSign != null)
            {
                if (todo == null || cash == null) throw new InvalidOperationException("The fishing boat purchase managers are unavailable.");
                fishingObjects = ToDisableWhenBought.GetValue(fishingSign) as GameObject[] ??
                    throw new InvalidOperationException("The fishing boat purchased-object list is missing.");
                if (fishingObjects.Length > BoatSaleState.MaxDisabledObjects || fishingObjects.Any(o => o == null) ||
                    fishingObjects.Distinct().Count() != fishingObjects.Length)
                    throw new InvalidOperationException("The fishing boat purchased-object list is unsupported or ambiguous.");
                originalFishingPresentation = CaptureFishingPresentation();
                var initial = Capture(BoatSaleKind.FishingBoat);
                state.Add(BoatSaleKind.FishingBoat, initial);
            }
            else fishingObjects = Array.Empty<GameObject>();

            if (Mathf.RoundToInt(GameManager.PRICE_SmallCruiser * 100f) != BoatSalePolicy.ExpectedPriceCents(BoatSaleKind.SmallCruiser) ||
                Mathf.RoundToInt(GameManager.PRICE_FishingBoat * 100f) != BoatSalePolicy.ExpectedPriceCents(BoatSaleKind.FishingBoat))
                throw new InvalidOperationException("Native boat sale prices differ from the supported purchase protocol.");


#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-boat-sale.txt"),
                "BOAT_SALE_REGISTERED\nSmallCruiser=" + (smallSign != null) + "\nFishingBoat=" + (fishingSign != null) +
                "\nFishingDisabledObjects=" + fishingObjects.Length + "\nSmallCruiserPurchased=" +
                (state.TryGetValue(BoatSaleKind.SmallCruiser, out var small) && small.Purchased) + "\nFishingBoatPurchased=" +
                (state.TryGetValue(BoatSaleKind.FishingBoat, out var fishing) && fishing.Purchased));
#endif

            Active = this;
        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            foreach (var pair in state)
                yield return new WorldRecord(BoatSaleState.EntityFor(pair.Key), 1, BoatSaleState.Adapter, pair.Value.Encode());
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

        internal bool IsPriceQuoteFor(Interactable candidate) => !host && !disposed && session?.IsRunning == true &&
            channel?.FullySynchronized == true && (ReferenceEquals(candidate, smallSign) || ReferenceEquals(candidate, fishingSign));

        internal bool TryGetQuotePrice(Interactable candidate, out float price)
        {
            if (ReferenceEquals(candidate, smallSign)) { price = GameManager.PRICE_SmallCruiser; return true; }
            if (ReferenceEquals(candidate, fishingSign)) { price = GameManager.PRICE_FishingBoat; return true; }
            price = 0;
            return false;
        }

        internal bool IsExecutingNative(Interactable candidate) => host && ReferenceEquals(candidate, executingNative);

        // True means the native mouse handler must be suppressed.
        internal bool Purchase(BoatSaleKind kind, Interactable clicked)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (host)
            {
                if (IsExecutingNative(clicked)) return false;
                if (!NewPurchasesBlocked) return false;
                Report("Wait for the multiplayer save to finish before buying a boat.");
                return true;
            }
            if (NewPurchasesBlocked)
            {
                Report("Wait for the multiplayer save to finish before buying a boat.");
                return true;
            }
            if (!IsRegisteredSign(kind, clicked) || channel?.FullySynchronized != true)
            {
                Report("The boat purchase is unavailable until the host world is synchronized.");
                return true;
            }
            if (pending.Count + timedOut.Count >= 8)
            {
                Report("Wait for the pending boat purchase to finish.");
                return true;
            }
            Send(new BoatSaleRequest(kind, Guid.NewGuid(), BoatSalePolicy.ExpectedPriceCents(kind)), true);
            return true;
        }

        private bool IsRegisteredSign(BoatSaleKind kind, Interactable candidate) => kind switch
        {
            BoatSaleKind.SmallCruiser => smallSign != null && ReferenceEquals(candidate, smallSign),
            BoatSaleKind.FishingBoat => fishingSign != null && ReferenceEquals(candidate, fishingSign),
            _ => false
        };

        private void Send(BoatSaleRequest request, bool track)
        {
            if (channel == null || !state.ContainsKey(request.Kind)) return;
            if (track && !pending.ContainsKey(request.Operation) && !timedOut.Contains(request.Operation))
                pending.Add(request.Operation, Time.unscaledTime + CommandTimeout);
            channel.Request(BoatSaleState.EntityFor(request.Kind), request.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            if (records.TryGetValue(BoatSaleState.SmallCruiserEntity, out var small)) Apply(small);
            if (records.TryGetValue(BoatSaleState.FishingBoatEntity, out var fishing)) Apply(fishing);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != BoatSaleState.Adapter) return;
            Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            BoatSaleKind kind = record.Id == BoatSaleState.SmallCruiserEntity ? BoatSaleKind.SmallCruiser :
                record.Id == BoatSaleState.FishingBoatEntity ? BoatSaleKind.FishingBoat : (BoatSaleKind)0;
            if (kind == 0 || record.Adapter != BoatSaleState.Adapter || !state.ContainsKey(kind))
                throw new InvalidDataException("Unknown boat sale presentation record.");
            var next = BoatSaleState.Decode(record.Payload);
            ValidateLayout(next, kind);
            state[kind] = next;
            ApplyPresentation(next);
            if (kind == BoatSaleKind.SmallCruiser) initializedSmall = true; else initializedFishing = true;
        }

        private void ValidateLayout(BoatSaleState value, BoatSaleKind expected)
        {
            if (value.Kind != expected || value.DisabledObjectsActive.Count != (expected == BoatSaleKind.FishingBoat ? fishingObjects.Length : 0))
                throw new InvalidDataException("Boat sale presentation layout differs from the native scene.");
        }

        private BoatSaleState Capture(BoatSaleKind kind)
        {
            if (todo == null) throw new InvalidOperationException("The ToDo manager is unavailable.");
            if (kind == BoatSaleKind.SmallCruiser)
            {
                if (smallSign == null || smallFuelCap == null || fuelGateCollider == null || fuelPart == null || fuelPartCollider == null)
                    throw new InvalidOperationException("The small cruiser sale is not registered.");
                return new BoatSaleState(kind, todo.isDone(ToDoList_Line.Boat_SmallCruiserBought), smallSign.gameObject.activeSelf,
                    fuelCapRootColliderEnabled: fuelGateCollider.enabled, fuelCapControllerEnabled: smallFuelCap.enabled,
                    fuelCapPartEnabled: fuelPart.enabled, fuelCapPartColliderEnabled: fuelPartCollider.enabled);
            }
            if (kind == BoatSaleKind.FishingBoat)
            {
                if (fishingSign == null) throw new InvalidOperationException("The fishing boat sale is not registered.");
                return new BoatSaleState(kind, todo.isDone(ToDoList_Line.Boat_Fishing_Bought), fishingSign.gameObject.activeSelf,
                    fishingObjects.Select(o => o.activeSelf));
            }
            throw new InvalidDataException("Unknown boat sale kind.");
        }

        private void ApplyPresentation(BoatSaleState next)
        {
            if (next.Kind == BoatSaleKind.SmallCruiser)
            {
                if (smallSign == null || smallFuelCap == null || fuelGateCollider == null || fuelPart == null || fuelPartCollider == null) return;
                if (smallSign.gameObject.activeSelf != next.SignVisible) smallSign.gameObject.SetActive(next.SignVisible);
                fuelGateCollider.enabled = next.FuelCapRootColliderEnabled;
                smallFuelCap.enabled = next.FuelCapControllerEnabled;
                fuelPart.enabled = next.FuelCapPartEnabled;
                fuelPartCollider.enabled = next.FuelCapPartColliderEnabled;
            }
            else
            {
                if (fishingSign == null) return;
                if (fishingSign.gameObject.activeSelf != next.SignVisible) fishingSign.gameObject.SetActive(next.SignVisible);
                for (int i = 0; i < fishingObjects.Length; i++)
                    if (fishingObjects[i].activeSelf != next.DisabledObjectsActive[i]) fishingObjects[i].SetActive(next.DisabledObjectsActive[i]);
            }
        }

        private PreviousPresentation CaptureSmallPresentation() => new PreviousPresentation(smallSign!.gameObject.activeSelf,
            gateColliderEnabled: fuelGateCollider!.enabled, fuelCapControllerEnabled: smallFuelCap!.enabled,
            fuelPartEnabled: fuelPart!.enabled, fuelPartColliderEnabled: fuelPartCollider!.enabled);

        private PreviousPresentation CaptureFishingPresentation() => new PreviousPresentation(fishingSign!.gameObject.activeSelf,
            fishingObjects.Select(o => o.activeSelf).ToArray());

        private void RestoreOriginalPresentation()
        {
            if (smallSign != null && smallFuelCap != null && fuelGateCollider != null && fuelPart != null && fuelPartCollider != null && originalSmallPresentation != null)
            {
                smallSign.gameObject.SetActive(originalSmallPresentation.SignActive);
                fuelGateCollider.enabled = originalSmallPresentation.GateColliderEnabled;
                smallFuelCap.enabled = originalSmallPresentation.FuelCapControllerEnabled;
                fuelPart.enabled = originalSmallPresentation.FuelPartEnabled;
                fuelPartCollider.enabled = originalSmallPresentation.FuelPartColliderEnabled;
            }
            if (fishingSign != null && originalFishingPresentation != null)
            {
                fishingSign.gameObject.SetActive(originalFishingPresentation.SignActive);
                for (int i = 0; i < fishingObjects.Length; i++) fishingObjects[i].SetActive(originalFishingPresentation.FishingObjectActive[i]);
            }
        }

        private bool HostFishRequirementsMet()
        {
            if (fishingSign == null) return false;
            var requirements = FishNeeded.GetValue(fishingSign) as BoatFishing_SellSign.Fish_Needed[];
            int[] amounts = GameManager.Get_Fishing_Amount;
            if (requirements == null || amounts == null) return false;
            foreach (var requirement in requirements)
            {
                if (requirement == null || !Enum.IsDefined(typeof(Fish_Type), requirement.FishType) || requirement.AmountNeeded < 0)
                    return false;
                int index = (int)requirement.FishType;
                if (index < 0 || index >= amounts.Length || amounts[index] < requirement.AmountNeeded) return false;
            }
            return true;
        }

        private void Rejected(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, true);
        [MonMP.Core.World.WorldCommandAdapters(BoatSaleState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, false);

        private void PurchaseCommand(uint peer, ulong entity, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || !BoatSaleRequest.Is(payload) || session == null || channel == null || cash == null || todo == null) return;
            var request = BoatSaleRequest.Decode(payload);
            if (entity != BoatSaleState.EntityFor(request.Kind) || !state.ContainsKey(request.Kind)) return;
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
                else channel.Reply(peer, entity, new BoatSaleReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }

            void Finish(PurchaseStatus status)
            {
                var reply = new BoatSaleReply(request.Operation, status);

#if MONMP_DEVELOPMENT
                TestLastReply = reply;
#endif

                results[key] = reply;
                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, entity, reply.Encode());
            }

            int expectedPrice = BoatSalePolicy.ExpectedPriceCents(request.Kind);
            Interactable? sign = request.Kind == BoatSaleKind.SmallCruiser ? smallSign : fishingSign;
            bool exactSign = sign != null && IsRegisteredSign(request.Kind, sign) && entity == BoatSaleState.EntityFor(request.Kind);
            bool purchased = todo.isDone(request.Kind == BoatSaleKind.SmallCruiser ?
                ToDoList_Line.Boat_SmallCruiserBought : ToDoList_Line.Boat_Fishing_Bought);
            bool signActive = sign != null && sign.gameObject.scene.IsValid() && sign.gameObject.activeInHierarchy;
            bool inRange = sign != null && items.PurchaseInRange(peer, sign.transform.position);
            bool enough = cash.GetCash >= expectedPrice && cash.isCanAfford(expectedPrice / 100f);
            bool ticketClear = gameplay.Store_PoliceInterceptor != null && !gameplay.Store_PoliceInterceptor.Get_HasCriminalRecord;
            bool fishMet = request.Kind != BoatSaleKind.FishingBoat || HostFishRequirementsMet();
            PurchaseStatus decision = BoatSalePolicy.ValidateHostRequest(stale, channel.FullySynchronized,
                NewPurchasesBlocked, results.Count < PurchaseClaims.Capacity, exactSign, signActive, purchased,
                request.PriceCents == expectedPrice, inRange, enough, ticketClear, fishMet);
            if (decision != PurchaseStatus.Placed) { Finish(decision); return; }

            // The operation is permanently claimed before the native code can debit or complete ToDos.
            int cashBefore = cash.GetCash;
            BoatSaleKind kind = request.Kind;
            try
            {
                executingNative = sign;
                sign!.MouseDown();
                executingNative = null;

                bool purchasedAfter = todo.isDone(kind == BoatSaleKind.SmallCruiser ?
                    ToDoList_Line.Boat_SmallCruiserBought : ToDoList_Line.Boat_Fishing_Bought);
                int cashAfter = cash.GetCash;
                bool exactCommit = purchasedAfter && !purchased && cashAfter == cashBefore - expectedPrice;
                if (exactCommit)
                {
                    if (kind == BoatSaleKind.SmallCruiser) smallFuelCap!.MouseDown();

#if MONMP_DEVELOPMENT
                    TestNativePurchases++;
#endif

                    Publish(kind);
                    Finish(PurchaseStatus.Placed);
                }
                else if (cashAfter == cashBefore && purchasedAfter == purchased && sign!.gameObject.activeSelf == signActive)
                    Finish(PurchaseStatus.NativeRejected);
                else
                {
                    uncertainOutcome = true;
                    Publish(kind);
                    Finish(PurchaseStatus.Uncertain);
                }
            }
            catch (Exception ex)
            {
                executingNative = null;
                uncertainOutcome = true;
                Publish(kind);
                Finish(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
            }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || (entity != BoatSaleState.SmallCruiserEntity && entity != BoatSaleState.FishingBoatEntity) || !BoatSaleReply.Is(payload)) return;
            var reply = BoatSaleReply.Decode(payload);
            if (!pending.Remove(reply.Operation) && !timedOut.Remove(reply.Operation)) return;

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == PurchaseStatus.Uncertain) uncertainOutcome = true;
            if (reply.Status != PurchaseStatus.Placed) Report(Message(reply.Status));
        }

        private void Expire(float now)
        {
            foreach (var entry in pending.Where(p => now >= p.Value).ToArray())
            {
                pending.Remove(entry.Key);
                timedOut.Add(entry.Key);
                Report("Boat purchase confirmation is delayed. Check shared cash and the boat signs before retrying.");
            }
        }

        private static string Message(PurchaseStatus status) => status switch
        {
            PurchaseStatus.Unavailable => "This boat is no longer for sale.",
            PurchaseStatus.PriceChanged => "The boat price or world state changed. Check the sign and try again.",
            PurchaseStatus.TooFar => "Move closer to the boat sale sign before buying it.",
            PurchaseStatus.Funds => "Not enough shared cash.",
            PurchaseStatus.Uncertain => "Boat purchase outcome is uncertain. Check shared cash and ownership before retrying.",
            PurchaseStatus.Capacity => "This session cannot accept more boat purchase requests.",
            _ => "The host declined the boat purchase. Check unpaid tickets and fishing requirements."
        };

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        private void Publish(BoatSaleKind kind)
        {
            if (!host || channel == null || disposed || !state.ContainsKey(kind)) return;
            var next = Capture(kind);
            byte[] payload = next.Encode();
            if (state[kind].Encode().SequenceEqual(payload)) return;
            state[kind] = next;
            channel.Publish(BoatSaleState.EntityFor(kind), payload);
        }

        public void Update()
        {
            if (disposed || session?.IsRunning != true || channel == null) return;
            Expire(Time.unscaledTime);
            if (Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            if (host)
            {
                foreach (var kind in state.Keys.ToArray()) Publish(kind);
            }
            else
            {
                if (initializedSmall && state.ContainsKey(BoatSaleKind.SmallCruiser) &&
                    !Capture(BoatSaleKind.SmallCruiser).Encode().SequenceEqual(state[BoatSaleKind.SmallCruiser].Encode()))
                    ApplyPresentation(state[BoatSaleKind.SmallCruiser]);
                if (initializedFishing && state.ContainsKey(BoatSaleKind.FishingBoat) &&
                    !Capture(BoatSaleKind.FishingBoat).Encode().SequenceEqual(state[BoatSaleKind.FishingBoat].Encode()))
                    ApplyPresentation(state[BoatSaleKind.FishingBoat]);
            }
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest(Guid operation, BoatSaleKind kind, int priceCents) =>
            Send(new BoatSaleRequest(kind, operation, priceCents), true);
#endif


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
            if (!host) RestoreOriginalPresentation();
            if (ReferenceEquals(Active, this)) Active = null;
            pending.Clear();
            timedOut.Clear();
            session = null;
            channel = null;
        }
    }
}
