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
    // The native six-bay garage purchase belongs to the host save. The host executes
    // EntreposageGarage.BuyThisGarage only after validating a guest request against the exact sign.
    internal sealed class SharedStorageGarageSale : IDisposable
    {
        private const float CommandTimeout = 10f;
        private const float CaptureInterval = 0.15f;
        private static readonly FieldInfo IsSixBayField = AccessTools.Field(typeof(EntreposageGarage), "isSixBayGarage");
        private static readonly FieldInfo GarageSignField = AccessTools.Field(typeof(EntreposageGarage), "Pancarte_ForSale");
        private static readonly FieldInfo StorageTextField = AccessTools.Field(typeof(EntreposageGarage), "Text_VehiclesInStorage");
        private static readonly FieldInfo SignGarageField = AccessTools.Field(typeof(EntreposageGarage_PancarteForSale), "entreposageGarage");

        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly bool host;
        private readonly Cash_Manager? cash;
        private readonly ToDoList_Manager? todo;
        private readonly EntreposageGarage? garage;
        private readonly EntreposageGarage_PancarteForSale? sign;
        private readonly Component? storageText;
        private readonly GameObject? storageTextRoot;
        private readonly bool originalSignActive;
        private readonly bool originalStorageTextActive;
        private readonly string? originalStorageText;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), StorageGarageSaleReply> results =
            new Dictionary<(Guid, Guid), StorageGarageSaleReply>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly HashSet<Guid> timedOut = new HashSet<Guid>();

        private NetworkSession? session;
        private WorldChannel? channel;
        private StorageGarageSaleState state;
        private EntreposageGarage? executingGarage;
        private uint? executingPeer;
        private int localCashBefore;
        private bool localPurchaseBefore, localSignBefore;
        private bool initialized, disposed, uncertainOutcome, nativeProgressionPresentationApplied;
        private float nextCapture;

        internal static SharedStorageGarageSale? Active { get; private set; }
        internal bool NewPurchasesBlocked { get; set; }
        internal bool TransactionsSettled => !uncertainOutcome && pending.Count == 0 && timedOut.Count == 0 && executingGarage == null;

#if MONMP_DEVELOPMENT
        internal StorageGarageSaleState TestState => state;


        internal StorageGarageSaleReply? TestLastReply { get; private set; }


        internal int TestNativePurchases { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal bool TestRestored => sign == null || (sign.gameObject.activeSelf == originalSignActive &&
            (storageTextRoot == null || storageTextRoot.activeSelf == originalStorageTextActive) &&
            (originalStorageText == null || AccessTools.Property(storageText!.GetType(), "text")?.GetValue(storageText) as string == originalStorageText));
#endif


        internal SharedStorageGarageSale(Gameplay gameplay, bool host, SharedItems items, string diagnostics)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            cash = Object.FindObjectOfType<Cash_Manager>();
            todo = Object.FindObjectOfType<ToDoList_Manager>();

            var sixBayGarages = Object.FindObjectsOfType<EntreposageGarage>(true)
                .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid() &&
                    IsSixBayField?.GetValue(candidate) is bool isSixBay && isSixBay).ToArray();
            if (sixBayGarages.Length > 1)
                throw new InvalidOperationException("More than one six-bay storage garage was found.");
            garage = sixBayGarages.SingleOrDefault();
            if (garage != null)
            {
                sign = GarageSignField?.GetValue(garage) as EntreposageGarage_PancarteForSale;
                if (sign != null && (!sign.gameObject.scene.IsValid() || !ReferenceEquals(SignGarageField?.GetValue(sign), garage)))
                    throw new InvalidOperationException("The six-bay garage sale sign does not reference its exact garage.");
                storageText = StorageTextField?.GetValue(garage) as Component;
                storageTextRoot = storageText?.transform.parent?.gameObject;
                originalSignActive = sign != null && sign.gameObject.activeSelf;
                originalStorageTextActive = storageTextRoot != null && storageTextRoot.activeSelf;
                originalStorageText = storageText == null ? null : AccessTools.Property(storageText.GetType(), "text")?.GetValue(storageText) as string;
                if (sign != null && todo != null)
                {
                    state = Capture();
                }
            }


#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-storage-garage-sale.txt"),
                garage == null ? "SIX_BAY_GARAGE_NOT_FOUND\n" :
                    "SIX_BAY_GARAGE_REGISTERED\nEntity=" + StorageGarageSaleState.Entity.ToString("X16") +
                    "\nSign=" + (sign != null) + "\nPurchased=" + state.Purchased + "\nSignVisible=" + state.SignVisible);
#endif

            Active = this;
        }

        internal bool AllowNativePurchase(EntreposageGarage candidate) => host && !disposed && session?.IsRunning == true &&
            garage != null && ReferenceEquals(candidate, garage) && ReferenceEquals(executingGarage, garage);

        // True consumes the local click; false allows the exact host sign through its native callback.
        internal bool InterceptSignClick(EntreposageGarage_PancarteForSale clicked, out bool finishLocalClick)
        {
            finishLocalClick = false;
            if (disposed || session?.IsRunning != true) return false;
            if (garage == null || sign == null || !ReferenceEquals(clicked, sign))
            {
                Report("The six-bay garage purchase is not supported in this multiplayer session.");
                return true;
            }
            if (host)
            {
                if (executingGarage == garage) return false;
                if (NewPurchasesBlocked)
                {
                    Report("Wait for the multiplayer save to finish before buying the garage.");
                    return true;
                }
                if (!sign.gameObject.activeInHierarchy || IsPurchased())
                {
                    Report("The six-bay garage is no longer for sale.");
                    return true;
                }
                localCashBefore = cash?.GetCash ?? 0;
                localPurchaseBefore = IsPurchased();
                localSignBefore = sign.gameObject.activeSelf;
                executingGarage = garage;
                finishLocalClick = true;
                return false;
            }

            if (NewPurchasesBlocked)
            {
                Report("Wait for the multiplayer save to finish before buying the garage.");
                return true;
            }
            if (channel?.FullySynchronized != true || !initialized)
            {
                Report("The garage purchase is unavailable until the host world is synchronized.");
                return true;
            }
            if (pending.Count + timedOut.Count >= 8)
            {
                Report("Wait for the pending garage purchase to finish.");
                return true;
            }
            Send(new StorageGarageSaleRequest(Guid.NewGuid(), StorageGarageSaleState.PurchasePriceCents), track: true);
            return true;
        }

        internal bool IsExecutingPeer() => host && executingPeer.HasValue;

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            if (garage != null && sign != null && todo != null)
                yield return new WorldRecord(StorageGarageSaleState.Entity, 1, StorageGarageSaleState.Adapter, state.Encode());
        }

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

        private bool IsPurchased() => todo?.isDone(ToDoList_Line.Advanced_SixBayGarage) == true;

        // Host-local purchase without the sign (the debug cheats): the native call through the registered
        // path, published like a sign purchase. The session otherwise blocks it (#172).
        internal bool HostPurchase()
        {
            if (!host || disposed || garage == null || IsPurchased()) return IsPurchased();
            executingGarage = garage;
            try { garage.BuyThisGarage(); }
            finally { executingGarage = null; }
            if (IsPurchased()) Publish();
            return IsPurchased();
        }

        private StorageGarageSaleState Capture()
        {
            if (garage == null || sign == null) throw new InvalidOperationException("The six-bay garage sale is not registered.");
            return new StorageGarageSaleState(IsPurchased(), sign.gameObject.activeSelf);
        }

        private void Send(StorageGarageSaleRequest request, bool track)
        {
            if (channel == null || garage == null || sign == null) return;
            if (track && !pending.ContainsKey(request.Operation) && !timedOut.Contains(request.Operation))
                pending.Add(request.Operation, Time.unscaledTime + CommandTimeout);
            channel.Request(StorageGarageSaleState.Entity, request.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || garage == null || sign == null || !records.TryGetValue(StorageGarageSaleState.Entity, out var record)) return;
            Apply(record);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != StorageGarageSaleState.Entity || record.Adapter != StorageGarageSaleState.Adapter) return;
            Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            if (record.Id != StorageGarageSaleState.Entity || record.Adapter != StorageGarageSaleState.Adapter ||
                garage == null || sign == null) throw new InvalidDataException("Unknown six-bay garage sale state record.");
            state = StorageGarageSaleState.Decode(record.Payload);
            ApplyPresentation(state);
            initialized = true;
        }

        private void ApplyPresentation(StorageGarageSaleState next)
        {
            if (sign == null || garage == null) return;
            if (sign.gameObject.activeSelf != next.SignVisible) sign.gameObject.SetActive(next.SignVisible);
            if (storageTextRoot != null && storageTextRoot.activeSelf != next.Purchased)
                storageTextRoot.SetActive(next.Purchased);
            // SharedProgress applies the host's ToDo state too. Once it matches, let the native
            // garage rebuild its vehicle list and presentation from that authoritative unlock.
            bool progressionMatches = todo?.isDone(ToDoList_Line.Advanced_SixBayGarage) == next.Purchased;
            if (progressionMatches && !nativeProgressionPresentationApplied)
                garage.UpdateList();
            nativeProgressionPresentationApplied = progressionMatches;
        }

        private void Rejected(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, stale: true);
        [MonMP.Core.World.WorldCommandAdapters(StorageGarageSaleState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, stale: false);

        private void PurchaseCommand(uint peer, ulong entity, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || !StorageGarageSaleRequest.Is(payload) || session == null || channel == null ||
                garage == null || sign == null || cash == null || todo == null) return;
            var request = StorageGarageSaleRequest.Decode(payload);
            if (entity != StorageGarageSaleState.Entity || !session.Peers.TryGetValue(peer, out var buyer)) return;
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
                else channel.Reply(peer, entity, new StorageGarageSaleReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }

            void Finish(PurchaseStatus status)
            {
                var reply = new StorageGarageSaleReply(request.Operation, status);

#if MONMP_DEVELOPMENT
                TestLastReply = reply;
#endif

                results[key] = reply;
                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, entity, reply.Encode());
            }

            bool purchased = IsPurchased();
            bool signActive = ReferenceEquals(SignGarageField?.GetValue(sign), garage) && sign.gameObject.activeInHierarchy;
            bool inRange = items.PurchaseInRange(peer, sign.transform.position);
            bool enough = cash.GetCash >= StorageGarageSaleState.PurchasePriceCents &&
                cash.isCanAfford(StorageGarageSaleState.PurchasePriceCents / 100f);
            bool ticketClear = gameplay.Store_PoliceInterceptor != null && !gameplay.Store_PoliceInterceptor.Get_HasCriminalRecord;
            PurchaseStatus decision = StorageGarageSalePolicy.ValidateHostRequest(stale, channel.FullySynchronized,
                NewPurchasesBlocked, results.Count < PurchaseClaims.Capacity, ReferenceEquals(sign, GarageSignField?.GetValue(garage)),
                signActive, purchased, request.PriceCents == StorageGarageSaleState.PurchasePriceCents,
                inRange, enough, ticketClear);
            if (decision != PurchaseStatus.Placed) { Finish(decision); return; }

            int cashBefore = cash.GetCash;
            try
            {
                executingGarage = garage;
                executingPeer = peer;
                garage.BuyThisGarage();
                executingPeer = null;
                executingGarage = null;

                bool purchasedAfter = IsPurchased();
                int cashAfter = cash.GetCash;
                if (!purchased && purchasedAfter && cashAfter == cashBefore - StorageGarageSaleState.PurchasePriceCents)
                {

#if MONMP_DEVELOPMENT
                    TestNativePurchases++;
#endif

                    Publish();
                    Finish(PurchaseStatus.Placed);
                }
                else if (cashAfter == cashBefore && purchasedAfter == purchased && sign.gameObject.activeSelf == signActive)
                    Finish(PurchaseStatus.NativeRejected);
                else
                {
                    uncertainOutcome = true;
                    Publish();
                    Finish(PurchaseStatus.Uncertain);
                }
            }
            catch (Exception ex)
            {
                executingPeer = null;
                executingGarage = null;
                uncertainOutcome = true;
                Publish();
                Finish(PurchaseStatus.Uncertain);
                Debug.LogException(ex);
            }
        }

        internal void FinishLocalClick(EntreposageGarage_PancarteForSale clicked, Exception? exception)
        {
            if (!host || disposed || sign == null || garage == null || !ReferenceEquals(clicked, sign) ||
                !ReferenceEquals(executingGarage, garage)) return;
            executingGarage = null;
            if (exception != null) uncertainOutcome = true;
            else if (cash == null || todo == null ||
                !((!localPurchaseBefore && IsPurchased() && cash.GetCash == localCashBefore - StorageGarageSaleState.PurchasePriceCents) ||
                    (IsPurchased() == localPurchaseBefore && cash.GetCash == localCashBefore && sign.gameObject.activeSelf == localSignBefore)))
                uncertainOutcome = true;
            Publish();
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != StorageGarageSaleState.Entity || !StorageGarageSaleReply.Is(payload)) return;
            var reply = StorageGarageSaleReply.Decode(payload);
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
                Report("Garage purchase confirmation is delayed. Check shared cash and garage access before retrying.");
            }
        }

        private static string Message(PurchaseStatus status) => status switch
        {
            PurchaseStatus.Unavailable => "The six-bay garage is no longer for sale.",
            PurchaseStatus.PriceChanged => "The garage price or world state changed. Check the sign and try again.",
            PurchaseStatus.TooFar => "Move closer to the garage sale sign before buying it.",
            PurchaseStatus.Funds => "Not enough shared cash.",
            PurchaseStatus.Uncertain => "Garage purchase outcome is uncertain. Check shared cash and garage access before retrying.",
            PurchaseStatus.Capacity => "This session cannot accept more garage purchase requests.",
            _ => "The host declined the garage purchase. Check unpaid tickets and shared cash."
        };

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        private void Publish()
        {
            if (!host || channel == null || disposed || garage == null || sign == null || todo == null) return;
            var next = Capture();
            byte[] payload = next.Encode();
            if (state.Encode().SequenceEqual(payload)) return;
            state = next;
            channel.Publish(StorageGarageSaleState.Entity, payload);
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest(Guid operation, int priceCents) =>
            Send(new StorageGarageSaleRequest(operation, priceCents), track: true);
#endif


        public void Update()
        {
            if (disposed || session?.IsRunning != true || garage == null || sign == null || channel == null) return;
            Expire(Time.unscaledTime);
            if (Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            if (host) Publish();
            else if (initialized)
            {
                bool progressionMatches = todo?.isDone(ToDoList_Line.Advanced_SixBayGarage) == state.Purchased;
                if (sign.gameObject.activeSelf != state.SignVisible ||
                    (storageTextRoot != null && storageTextRoot.activeSelf != state.Purchased) ||
                    (progressionMatches && !nativeProgressionPresentationApplied))
                    ApplyPresentation(state);
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
            if (!host && sign != null)
            {
                sign.gameObject.SetActive(originalSignActive);
                if (storageTextRoot != null) storageTextRoot.SetActive(originalStorageTextActive);
                if (originalStorageText != null && storageText != null)
                    AccessTools.Property(storageText.GetType(), "text")?.SetValue(storageText, originalStorageText);
            }
            if (ReferenceEquals(Active, this)) Active = null;
            pending.Clear();
            timedOut.Clear();
            session = null;
            channel = null;
        }
    }
}
