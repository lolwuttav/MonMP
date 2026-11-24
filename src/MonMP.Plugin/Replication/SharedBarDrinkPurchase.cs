using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The wine cup is a host-owned bar resource, while the alcohol effect belongs to the buyer.
    // The host charges and updates the bar presentation; the buyer runs the native drinking
    // coroutine locally after an acknowledged commit.
    internal sealed class SharedBarDrinkPurchase : IDisposable
    {
        private const float Range = 7f;
        // The bar has one native cup. Players who order while it is in use get their own glass
        // poured on the counter in front of them, so a whole group can drink together (#113).
        private const int MaxConcurrentDrinks = 8;
        private const float PoseMaxAge = 2f;
        private const float RequestTimeout = 10f;
        private const float CaptureInterval = 0.15f;
        private static readonly FieldInfo? Price = AccessTools.Field(typeof(ItemForSale_BarDrink), "Price");
        private static readonly FieldInfo? StoreField = AccessTools.Field(typeof(ItemForSale_BarDrink), "Store");
        private static readonly FieldInfo? CaroleField = AccessTools.Field(typeof(ItemForSale_BarDrink), "ActorCarole");
        private static readonly FieldInfo? BottlePrefab = AccessTools.Field(typeof(ItemForSale_BarDrink), "Prefab_WineBottle");
        private static readonly FieldInfo? BottleList = AccessTools.Field(typeof(ItemForSale_BarDrink), "WineBottles");
        private static readonly FieldInfo? ActiveBottle = AccessTools.Field(typeof(ItemForSale_BarDrink), "ActiveWineBottle");
        private static readonly FieldInfo? WineVolume = AccessTools.Field(typeof(ItemForSale_BarDrink), "WineInBottle");
        private static readonly FieldInfo? NativeDrinking = AccessTools.Field(typeof(ItemForSale_BarDrink), "Coroutine_IsDrinking");
        private static readonly FieldInfo? WineCup = AccessTools.Field(typeof(ItemForSale_BarDrink), "WineCup");
        private static readonly FieldInfo? WineCupVisual = AccessTools.Field(typeof(ItemForSale_BarDrink), "WineCup_Visual");
        private static readonly FieldInfo? WineCupCollider = AccessTools.Field(typeof(ItemForSale_BarDrink), "WineCupCollider");
        private static readonly MethodInfo? SpawnBottle = AccessTools.Method(typeof(ItemForSale_BarDrink), "SpawnNewWineBottle");
        private static readonly MethodInfo? ResetBottle = AccessTools.Method(typeof(ItemForSale_BarDrink), "ResetDrinks");
        private static readonly MethodInfo? DrinkRoutine = AccessTools.Method(typeof(ItemForSale_BarDrink), "Drinking");

        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly Func<Guid, PlayerProfileState?> profileLookup;
        private readonly Cash_Manager? cash;
        private readonly ItemForSale_BarDrink? listing;
        private readonly Store_Bar? store;
        private readonly Actor_Carole? carole;
        private readonly ulong entity;
        private readonly float originalPrice;
        private readonly BarDrinkState original;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), BarDrinkReply> results =
            new Dictionary<(Guid, Guid), BarDrinkReply>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly HashSet<Guid> timedOut = new HashSet<Guid>();
        private readonly Dictionary<Guid, float> peerCooldowns = new Dictionary<Guid, float>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private BarDrinkState state;
        private bool initialized, blocked, disposed, localPlayback, remotePlayback, localNativeClick, scopedEconomyDebit;
        private ItemForSale_BarDrink? hoverListing;
        private bool hasUncertainOutcome;
        private float remoteBusyUntil, nextCapture;
        // Host: buyers whose drink is still being poured or drunk.
        private readonly Dictionary<Guid, float> drinkingUntil = new Dictionary<Guid, float>();
        private bool localExtraDrink;
        private IEnumerator? localExtraRoutine, localExtraDriver;
        private bool settlingLocalExtra, localExtraFailed;

        internal static SharedBarDrinkPurchase? Active { get; private set; }
        internal bool NewActionsBlocked { get => blocked; set => blocked = value; }
        internal bool AllowNativeEconomyMutation(string methodName) => host && session?.IsRunning == true &&
            methodName == nameof(Cash_Manager.Event_Spent) && (localNativeClick || scopedEconomyDebit);
        internal bool AllowNativeAffordabilityQuote(float price) => session?.IsRunning == true && listing != null &&
            ((host && (localNativeClick || scopedEconomyDebit)) ||
                (ReferenceEquals(hoverListing, listing) && channel?.FullySynchronized == true && (host || initialized))) &&
            Price != null && Mathf.Approximately((float)Price.GetValue(listing)!, price);
        internal ItemForSale_BarDrink? HoverListing { get => hoverListing; set => hoverListing = value; }
        // The host's extra glass has its own paid pour before Fill_Alchool schedules the
        // native personal effect. Recovery must not bypass that production boundary.
        internal bool LocalProductionSettled => !localExtraDrink && !localExtraFailed;
        internal bool TransactionsSettled => pending.Count == 0 && timedOut.Count == 0 &&
            !state.Drinking && !localPlayback && !remotePlayback && LocalProductionSettled && !hasUncertainOutcome;

        internal bool SettleForSave()
        {
            if (!host || disposed || !localExtraDrink) return LocalProductionSettled;
            var routine = localExtraRoutine;
            if (listing == null || routine == null || localExtraDriver == null)
            { localExtraFailed = true; return false; }
            try
            {
                // Unity owns only the forwarding driver. Stopping it cannot dispose the
                // paid inner iterator before its one Fill_Alchool call has executed.
                listing.StopCoroutine(localExtraDriver);
                settlingLocalExtra = true;
                for (int step = 0; step < 16; step++)
                {
                    if (!routine.MoveNext()) return LocalProductionSettled;
                    if (routine.Current != null && routine.Current is not WaitForSeconds)
                        throw new InvalidOperationException("The extra drink gained an unsupported save dependency.");
                }
                throw new InvalidOperationException("The extra drink did not finish its bounded save settlement.");
            }
            catch (Exception ex)
            {
                localExtraFailed = true;
                Debug.LogWarning("MonMP could not finish the paid host drink for save: " + ex.Message);
                return false;
            }
            finally { settlingLocalExtra = false; }
        }

#if MONMP_DEVELOPMENT
        internal BarDrinkState TestState => state;


        internal BarDrinkReply? TestLastReply { get; private set; }


        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal bool TestRestored => !host && (listing == null || Capture().Encode().SequenceEqual(original.Encode()));
#endif

        internal bool SuppressLocalCaroleResponse => !host && session?.IsRunning == true && localPlayback;

        internal SharedBarDrinkPurchase(Gameplay gameplay, bool host, SharedItems items,
            Func<Guid, PlayerProfileState?> profileLookup)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            this.profileLookup = profileLookup ?? throw new ArgumentNullException(nameof(profileLookup));
            cash = Object.FindObjectOfType<Cash_Manager>();

            var candidates = Object.FindObjectsOfType<ItemForSale_BarDrink>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            if (candidates.Length > 1) throw new InvalidOperationException("More than one native bar drink listing was found.");
            listing = candidates.SingleOrDefault();
            if (listing != null)
            {
                if (Price == null || StoreField == null || CaroleField == null || BottlePrefab == null || BottleList == null ||
                    ActiveBottle == null || WineVolume == null || NativeDrinking == null || WineCup == null || WineCupVisual == null ||
                    WineCupCollider == null || SpawnBottle == null || ResetBottle == null || DrinkRoutine == null)
                    throw new InvalidOperationException("The native bar drink component layout is unsupported.");
                store = StoreField.GetValue(listing) as Store_Bar;
                carole = CaroleField.GetValue(listing) as Actor_Carole;
                var prefab = BottlePrefab.GetValue(listing) as ItemForSale_BarDrink_WineBottle;
                if (listing.GetType() != typeof(ItemForSale_BarDrink) || store == null || carole == null ||
                    prefab == null || BottleList.GetValue(listing) is not IList || (float)Price.GetValue(listing) < 0 ||
                    !Finite((float)Price.GetValue(listing)))
                    throw new InvalidOperationException("The native bar drink listing or wine bottle factory is unsupported.");
                originalPrice = (float)Price.GetValue(listing);
                entity = StaticId.Hash("monmp/bar-drink-purchase/v1/" + SharedBuildingDoors.StablePath(listing.transform));
                original = state = Capture();
            }
            else
            {
                originalPrice = 0f;
                original = state = default;
            }
            if (Active != null && Active != this) throw new InvalidOperationException("A bar drink purchase adapter is already active.");
            Active = this;
        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            if (listing != null) yield return new WorldRecord(entity, 1, BarDrinkState.Adapter, original.Encode());
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            if (listing == null) return;
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        // Harmony returns true to suppress the native handler. Offline behavior is untouched;
        // the host continues using the game's exact MouseDown and drinking routine.
        internal bool InterceptClick(ItemForSale_BarDrink candidate)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (host)
            {
                if (!ReferenceEquals(candidate, listing)) return false;
                if (blocked)
                {
                    Report("Wait for the multiplayer save to finish before ordering a drink.");
                    return true;
                }
                if ((remotePlayback || Time.unscaledTime < remoteBusyUntil) && !localExtraDrink && NativeDrinking!.GetValue(listing) is true)
                    return HostExtraDrink();
                localNativeClick = true;
                return false;
            }
            if (candidate == null || !ReferenceEquals(candidate, listing) || listing == null || channel?.FullySynchronized != true || !initialized)
            {
                Report("Bar drinks are unavailable until the host world is synchronized.");
                return true;
            }
            if (blocked)
            {
                Report("Wait for the multiplayer save to finish before ordering a drink.");
                return true;
            }
            if (NativeDrinking!.GetValue(listing) is true || localPlayback)
            {
                Report("You are already drinking.");
                return true;
            }
            var needs = Object.FindObjectOfType<BasicNeeds_Manager>();
            if (needs == null || !needs.Get_CanDrinkAlcool) return true;
            listing.MouseOut();
            if (gameplay.Get_PlayerState != PlayerState.InFurnitureChair)
            {
                Report(Message(BarDrinkStatus.NotSeated));
                return true;
            }
            var request = new BarDrinkRequest(Guid.NewGuid(), entity, state.PriceCents);
            if (pending.Count + timedOut.Count >= 16)
            {
                Report("Wait for your previous drink order to finish.");
                return true;
            }
            pending[request.Operation] = Time.unscaledTime + RequestTimeout;
            channel!.Request(entity, request.Encode());
            return true;
        }

        internal void OnNativeHostClick(ItemForSale_BarDrink candidate)
        {
            try
            {
                if (!localNativeClick || disposed || !host || session?.IsRunning != true || !ReferenceEquals(candidate, listing)) return;
                Publish();
            }
            finally
            {
                if (ReferenceEquals(candidate, listing)) localNativeClick = false;
            }
        }

        internal void OnStoreLifecycle(Store_Bar candidate)
        {
            if (!host || disposed || session?.IsRunning != true || !ReferenceEquals(candidate, store)) return;
            Publish();
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnPeerLeft(uint peer)
        {
            presence.Remove(peer);
            if (session != null && session.Peers.TryGetValue(peer, out var info)) peerCooldowns.Remove(info.ProfileId);
        }

        private BarDrinkState Capture()
        {
            if (listing == null || store == null || Price == null || BottleList == null || WineVolume == null || NativeDrinking == null)
                throw new InvalidOperationException("The native bar drink listing is unavailable.");
            var bottles = BottleList.GetValue(listing) as IList ?? throw new InvalidOperationException("The native wine bottle list is unavailable.");
            float volume = (float)WineVolume.GetValue(listing);
            byte units = (byte)Mathf.Clamp(Mathf.RoundToInt(volume * 5f), 0, 5);
            bool busy = (bool)NativeDrinking.GetValue(listing)! || remotePlayback || Time.unscaledTime < remoteBusyUntil;
            return new BarDrinkState(entity, Mathf.RoundToInt((float)Price.GetValue(listing) * 100f),
                store.isStoreOpen(), busy, (byte)Math.Min(byte.MaxValue, bottles.Count), units);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || listing == null || !records.TryGetValue(entity, out var record)) return;
            Apply(record);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != entity || record.Adapter != BarDrinkState.Adapter) return;
            Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            if (record.Id != entity || record.Adapter != BarDrinkState.Adapter || listing == null || store == null || Price == null)
                throw new InvalidDataException("Unknown bar drink state record.");
            var next = BarDrinkState.Decode(record.Payload);
            if (next.Listing != entity) throw new InvalidDataException("Bar drink listing identity changed.");
            state = next;
            Price.SetValue(listing, next.PriceCents / 100f);
            store.isOpen = next.StoreOpen;
            initialized = true;
            if (!next.Drinking && !localPlayback) ApplyBottlePresentation(next);
        }

        [MonMP.Core.World.WorldCommandAdapters(BarDrinkState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload) => Purchase(peer, id, payload, stale: false);
        private void Rejected(uint peer, ulong id, byte[] payload) => Purchase(peer, id, payload, stale: true);

        private void Purchase(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || listing == null || store == null || cash == null || session == null || channel == null ||
                !BarDrinkRequest.Is(payload) || id != entity) return;
            BarDrinkRequest request;
            try { request = BarDrinkRequest.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);
            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var prior)) channel.Reply(peer, entity, prior.Encode());
                else channel.Reply(peer, entity, new BarDrinkReply(request.Operation, BarDrinkStatus.Capacity).Encode());
                return;
            }

            void Finish(BarDrinkStatus status, bool committed = false)
            {
                if (status == BarDrinkStatus.Uncertain) hasUncertainOutcome = true;
                var reply = new BarDrinkReply(request.Operation, status, committed);
                results[key] = reply;

#if MONMP_DEVELOPMENT
                TestLastReply = reply;
#endif

                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, entity, reply.Encode());
            }

            var current = Capture();
            var liveProfile = profileLookup(buyer.ProfileId);
            bool freshPose = presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= PoseMaxAge;
            // The reliable chair release can arrive before the next unreliable walking pose.
            // Require the host's current chair claim as well as the recent seated observation.
            var world = MonMPPlugin.Instance?.World;
            var seatedAt = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
            bool seated = freshPose && seen.Pose.State == (byte)PlayerState.InFurnitureChair &&
                (world?.FurnitureSeats.PeerSeatedNear(peer, seatedAt, 1.6f) == true ||
                    world?.ExtraSeats.PeerSeatedNear(peer, seatedAt, 1.6f) == true);
            bool inRange = freshPose && (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - listing.transform.position).sqrMagnitude <= Range * Range;
            bool exactListing = listing.gameObject.scene.IsValid() && listing.GetType() == typeof(ItemForSale_BarDrink) &&
                ReferenceEquals(StoreField?.GetValue(listing), store) && store.GetType() == typeof(Store_Bar);
            foreach (var done in drinkingUntil.Where(pair => Time.unscaledTime >= pair.Value).Select(pair => pair.Key).ToArray())
                drinkingUntil.Remove(done);
            bool nativeBusy = drinkingUntil.ContainsKey(buyer.ProfileId) || drinkingUntil.Count >= MaxConcurrentDrinks;
            bool canDrink = liveProfile != null && liveProfile.Counters.Count > 5 && liveProfile.Counters[5] == 0;
            bool cooldownElapsed = !peerCooldowns.TryGetValue(buyer.ProfileId, out float nextDrink) || Time.timeSinceLevelLoad >= nextDrink;
            int priceCents = current.PriceCents;
            BarDrinkStatus decision = BarDrinkPurchasePolicy.ValidateHostRequest(stale, channel.FullySynchronized,
                blocked, results.Count < PurchaseClaims.Capacity, id == entity && request.Listing == entity,
                exactListing, request.PriceCents == priceCents, current.StoreOpen, inRange, seated,
                !items.PeerHolding(peer), canDrink, cooldownElapsed, nativeBusy, cash.GetCash >= (float)priceCents);
            if (decision != BarDrinkStatus.Accepted)
            {

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Finish(decision);
                return;
            }

            int cashBefore = cash.GetCash;
            try
            {
                scopedEconomyDebit = true;
                try { cash.Event_Spent(priceCents / 100f); }
                finally { scopedEconomyDebit = false; }
                bool debitExact = cash.GetCash == cashBefore - priceCents;
                if (SharedCupFree) BeginRemoteDrink(current);
                else BeginExtraDrink(current, new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z), seen.Pose.Yaw, local: false);
                drinkingUntil[buyer.ProfileId] = Time.unscaledTime + 6.2f + WineAudioLength();
                peerCooldowns[buyer.ProfileId] = Time.timeSinceLevelLoad + WineAudioLength();
                Publish();

#if MONMP_DEVELOPMENT
                TestAccepted++;
#endif

                Finish(debitExact ? BarDrinkStatus.Accepted : BarDrinkStatus.Uncertain, committed: true);
            }
            catch (Exception ex)
            {
                scopedEconomyDebit = false;

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                hasUncertainOutcome = true;
                Publish();
                Finish(BarDrinkStatus.Uncertain, committed: cash.GetCash != cashBefore);
                Debug.LogException(ex);
            }
        }

        private void BeginRemoteDrink(BarDrinkState current)
        {
            if (listing == null || WineVolume == null || ActiveBottle == null || WineCupVisual == null || NativeDrinking == null ||
                WineCupCollider == null || BottleList == null || SpawnBottle == null)
                throw new InvalidOperationException("The native bar drink presentation is unsupported.");
            float volume = current.WineUnits / 5f;
            bool newBottle = current.BottleCount == 0 || volume <= 0.05f;
            if (newBottle) SpawnBottle.Invoke(listing, null);
            var active = ActiveBottle.GetValue(listing) as ItemForSale_BarDrink_WineBottle ??
                throw new InvalidOperationException("The bar did not create its wine bottle.");
            volume = (float)WineVolume.GetValue(listing);
            ((Collider)WineCupCollider.GetValue(listing)!).enabled = false;
            ((Transform)WineCupVisual.GetValue(listing)!).gameObject.SetActive(true);
            float before = volume;
            volume -= 0.2f;
            WineVolume.SetValue(listing, volume);
            remotePlayback = true;
            NativeDrinking.SetValue(listing, true);
            float duration = 4.7f + WineAudioLength() + (newBottle ? 1.5f : 0f);
            remoteBusyUntil = Time.unscaledTime + duration;
            listing.StartCoroutine(RemotePresentation(active, before, volume, duration));
        }

        private IEnumerator RemotePresentation(ItemForSale_BarDrink_WineBottle bottle, float before, float after, float duration)
        {
            try
            {
                if (listing == null || WineCupVisual == null || WineCup == null || WineCupCollider == null || NativeDrinking == null) yield break;
                var cupAt = ((GameObject)WineCup.GetValue(listing)!).transform.position;
                var sound = Singleton<SOUND_Manager>.i;
                using (SpatialSounds.At(cupAt)) sound?.PlayOneShot2D(sound.WineBottle_PourOut, _randomPitch: true, 1.2f);
                float elapsed = 0;
                float pour = 2.2f;
                while (elapsed < pour)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / pour);
                    float scale = Mathf.Lerp(0.33f, 1f, t);
                    ((Transform)WineCupVisual.GetValue(listing)!).localScale = new Vector3(scale, t, scale);
                    bottle.UpdateVisual(Mathf.Lerp(before, after + 0.01f, t));
                    yield return null;
                }
                bottle.UpdateVisual(after);
                yield return new WaitForSeconds(1f);
                // The other player's gulp, as their own copy plays it (#113).
                using (SpatialSounds.At(cupAt)) sound?.PlayOneShot2D(sound.BasicNeeds_Wine, _randomPitch: true);
                ((GameObject)WineCup.GetValue(listing)!).SetActive(false);
                ((Transform)WineCupVisual.GetValue(listing)!).gameObject.SetActive(false);
                float remaining = Mathf.Max(0, duration - pour - 1f);
                yield return new WaitForSeconds(remaining);
            }
            finally
            {
                try
                {
                    if (listing != null && WineCup != null && WineCupCollider != null && NativeDrinking != null)
                    {
                        ((GameObject)WineCup.GetValue(listing)!).SetActive(true);
                        ((Collider)WineCupCollider.GetValue(listing)!).enabled = true;
                        NativeDrinking.SetValue(listing, false);
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
                remotePlayback = false;
                remoteBusyUntil = 0;
                Publish();
            }
        }

        private bool SharedCupFree => listing != null && NativeDrinking!.GetValue(listing) is false && !remotePlayback &&
            Time.unscaledTime >= remoteBusyUntil;

        // The host orders while a guest's drink occupies the native cup: the same checks and charge
        // as native MouseDown, then the host's own glass.
        private bool HostExtraDrink()
        {
            var needs = Singleton<BasicNeeds_Manager>.i;
            if (listing == null || store == null || cash == null || Price == null || needs == null || !needs.Get_CanDrinkAlcool) return true;
            listing.MouseOut();
            float price = (float)Price.GetValue(listing);
            string? error = !store.isStoreOpen() ? "ERROR_StoreClosed" : cash.GetCash < Mathf.RoundToInt(price * 100f) ? "ERROR_NotEnoughCash" :
                gameplay.Player.IsHoldingAnObject ? "ERROR_AlreadyHaveAItemInHand" :
                gameplay.Get_PlayerState != PlayerState.InFurnitureChair ? "ERROR_DrinkAtBar_NeedToSitDown" : null;
            if (error != null) { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText(error)); return true; }
            scopedEconomyDebit = true;
            try { cash.Event_Spent(price); }
            finally { scopedEconomyDebit = false; }
            var eye = gameplay.Player.mainCamera != null ? gameplay.Player.mainCamera.transform : gameplay.PlayerWalking.transform;
            BeginExtraDrink(Capture(), gameplay.PlayerWalking.transform.position, eye.eulerAngles.y, local: true);
            return true;
        }

        private void BeginExtraDrink(BarDrinkState current, Vector3 drinker, float yaw, bool local)
        {
            if (listing == null || WineVolume == null || ActiveBottle == null || BottleList == null || SpawnBottle == null || WineCup == null || WineCupVisual == null)
                throw new InvalidOperationException("The native bar drink presentation is unsupported.");
            float volume = (float)WineVolume.GetValue(listing);
            if (ActiveBottle.GetValue(listing) == null || volume <= 0.05f) SpawnBottle.Invoke(listing, null);
            var bottle = ActiveBottle.GetValue(listing) as ItemForSale_BarDrink_WineBottle;
            volume = Mathf.Max(0f, (float)WineVolume.GetValue(listing) - 0.2f);
            WineVolume.SetValue(listing, volume);
            bottle?.UpdateVisual(volume);
            var routine = ExtraPresentation(PourGlass(drinker, yaw), local);
            if (local)
            {
                localExtraDrink = true;
                localExtraRoutine = routine;
                localExtraDriver = DriveExtraPresentation(routine);
                listing.StartCoroutine(localExtraDriver);
            }
            else listing.StartCoroutine(routine);
        }

        private static IEnumerator DriveExtraPresentation(IEnumerator routine)
        {
            while (routine.MoveNext()) yield return routine.Current;
        }

        // A copy of the bar's cup with every behaviour and collider stripped, set on the counter
        // in front of the drinker (or beside the native cup when the counter isn't found there).
        private (GameObject Cup, Transform? Wine)? PourGlass(Vector3 drinker, float yaw)
        {
            try
            {
                var source = (GameObject)WineCup!.GetValue(listing)!;
                var sourceWine = (Transform)WineCupVisual!.GetValue(listing)!;
                string? winePath = sourceWine.IsChildOf(source.transform) && sourceWine != source.transform ? PathFrom(source.transform, sourceWine) : null;
                var holder = new GameObject("MonMP bar glass");
                holder.SetActive(false);
                var cup = Object.Instantiate(source, holder.transform);
                foreach (var behaviour in cup.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(behaviour);
                foreach (var collider in cup.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
                foreach (var body in cup.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(body);
                var wine = winePath != null ? cup.transform.Find(winePath) : null;
                float counter = source.transform.position.y;
                var forward = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                var at = new Vector3(drinker.x, counter, drinker.z) + forward * 0.5f;
                if (Physics.Raycast(at + Vector3.up * 0.3f, Vector3.down, out var hit, 0.6f, ~0, QueryTriggerInteraction.Ignore)) at = hit.point;
                else
                {
                    var toCup = Vector3.ProjectOnPlane(source.transform.position - drinker, Vector3.up);
                    at = new Vector3(drinker.x, counter, drinker.z) + (toCup.sqrMagnitude > 0.01f ? toCup.normalized : forward) * 0.5f;
                }
                cup.transform.SetParent(null, false);
                cup.transform.SetPositionAndRotation(at, source.transform.rotation);
                cup.transform.localScale = source.transform.lossyScale;
                Object.Destroy(holder);
                cup.SetActive(true);
                if (wine != null) wine.gameObject.SetActive(true);
                return (cup, wine);
            }
            catch (Exception ex) { Debug.LogException(ex); return null; }
        }

        private static string PathFrom(Transform root, Transform child)
        {
            var parts = new List<string>();
            for (var t = child; t != null && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private IEnumerator ExtraPresentation((GameObject Cup, Transform? Wine)? glass, bool local)
        {
            bool completed = false;
            try
            {
                var sound = Singleton<SOUND_Manager>.i;
                Vector3 at = glass?.Cup != null ? glass.Value.Cup.transform.position : listing!.transform.position;
                using (local ? SpatialSounds.Local() : SpatialSounds.At(at))
                    sound?.PlayOneShot2D(sound.WineBottle_PourOut, _randomPitch: true, 1.2f);
                // A bartender's quick pour: the native cup's slow fill kept players waiting (#113).
                float elapsed = 0, pour = 1.1f;
                while (elapsed < pour)
                {
                    elapsed += local && settlingLocalExtra ? pour : Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / pour), scale = Mathf.Lerp(0.33f, 1f, t);
                    if (glass?.Wine != null) glass.Value.Wine!.localScale = new Vector3(scale, t, scale);
                    yield return null;
                }
                yield return new WaitForSeconds(0.3f);
                if (!local) using (SpatialSounds.At(at)) sound?.PlayOneShot2D(sound.BasicNeeds_Wine, _randomPitch: true);
                if (glass?.Cup != null) Object.Destroy(glass.Value.Cup);
                glass = null;
                if (local)
                {
                    var needs = Singleton<BasicNeeds_Manager>.i;
                    float seconds = needs != null ? needs.Fill_Alchool(5f, 8f, 15f, _drinkingACup: true) : 0f;
                    yield return new WaitForSeconds(seconds);
                }
                completed = true;
            }
            finally
            {
                if (glass?.Cup != null) Object.Destroy(glass.Value.Cup);
                if (local)
                {
                    localExtraDrink = false;
                    localExtraRoutine = localExtraDriver = null;
                    if (!completed) localExtraFailed = true;
                }
                Publish();
            }
        }

        private float WineAudioLength()
        {
            var audio = Singleton<SOUND_Manager>.i?.BasicNeeds_Wine;
            return audio != null ? audio.length : 5f;
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || id != entity || !BarDrinkReply.Is(payload)) return;
            var reply = BarDrinkReply.Decode(payload);
            if (!pending.Remove(reply.Operation) && !timedOut.Remove(reply.Operation)) return;

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == BarDrinkStatus.Uncertain) hasUncertainOutcome = true;
            if (reply.Committed && (reply.Status == BarDrinkStatus.Accepted || reply.Status == BarDrinkStatus.Uncertain))
                StartGuestDrink();
            else Report(Message(reply.Status));
        }

        private void StartGuestDrink()
        {
            if (listing == null || DrinkRoutine == null || localPlayback || (bool)NativeDrinking!.GetValue(listing)!)
            {
                hasUncertainOutcome = true;
                Report("The drink was charged, but its local presentation could not start. Check shared cash and your needs.");
                return;
            }
            localPlayback = true;
            try { listing.StartCoroutine(GuestDrink()); }
            catch (Exception ex)
            {
                localPlayback = false;
                hasUncertainOutcome = true;
                Report("The drink was charged, but its local presentation could not start. Check shared cash and your needs.");
                Debug.LogException(ex);
            }
        }

        private IEnumerator GuestDrink()
        {
            IEnumerator? native = null;
            try
            {
                native = (IEnumerator)DrinkRoutine!.Invoke(listing, null)!;
                while (native.MoveNext()) yield return native.Current;
            }
            finally
            {
                localPlayback = false;
                if (!host && !disposed && !state.Drinking) ApplyBottlePresentation(state);
            }
        }

        private void ApplyBottlePresentation(BarDrinkState target, bool teardown = false)
        {
            if (listing == null || BottlePrefab == null || BottleList == null || ActiveBottle == null || WineVolume == null ||
                ResetBottle == null || WineCupVisual == null) return;
            if (teardown)
            {
                var cup = WineCupVisual.GetValue(listing) as Transform;
                var source = BottlePrefab.GetValue(listing) as ItemForSale_BarDrink_WineBottle;
                var bottles = BottleList.GetValue(listing) as IList;
                if (cup == null || bottles == null || (target.BottleCount > 0 && source == null)) return;
                if (target.BottleCount > 0 &&
                    (AccessTools.Field(typeof(ItemForSale_BarDrink_WineBottle), "WineBottle_Visual")?.GetValue(source) as Transform == null ||
                     AccessTools.Field(typeof(ItemForSale_BarDrink_WineBottle), "WineBottle_Cap")?.GetValue(source) as Transform == null)) return;
                // Native ResetDrinks dereferences each bottle's gameObject before removing it.
                for (int i = bottles.Count - 1; i >= 0; i--)
                    if (bottles[i] as ItemForSale_BarDrink_WineBottle == null) bottles.RemoveAt(i);
            }
            ResetBottle.Invoke(listing, null);
            var prefab = (ItemForSale_BarDrink_WineBottle)BottlePrefab.GetValue(listing)!;
            var list = (IList)BottleList.GetValue(listing)!;
            ItemForSale_BarDrink_WineBottle? active = null;
            for (int i = 0; i < target.BottleCount; i++)
            {
                foreach (ItemForSale_BarDrink_WineBottle prior in list)
                {
                    Vector3 local = prior.transform.localPosition;
                    local.z -= 0.11f;
                    prior.transform.localPosition = local;
                }
                var bottle = Object.Instantiate(prefab, prefab.transform.position, prefab.transform.rotation);
                bottle.transform.SetParent(prefab.transform.parent);
                bottle.gameObject.SetActive(true);
                bottle.PopCap();
                bottle.UpdateVisual(i == target.BottleCount - 1 ? target.WineUnits / 5f : 0f);
                list.Add(bottle);
                active = bottle;
            }
            ActiveBottle.SetValue(listing, active);
            WineVolume.SetValue(listing, target.BottleCount == 0 ? 0f : target.WineUnits / 5f);
            ((Transform)WineCupVisual.GetValue(listing)!).gameObject.SetActive(false);
        }

        private void Publish()
        {
            if (!host || disposed || listing == null || channel == null) return;
            var next = Capture();
            if (state.Encode().SequenceEqual(next.Encode())) return;
            state = next;
            channel.Publish(entity, next.Encode());
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true || listing == null || channel == null) return;
            if (pending.Count > 0) foreach (var operation in pending.Where(pair => Time.unscaledTime >= pair.Value).Select(pair => pair.Key).ToArray())
            {
                pending.Remove(operation);
                timedOut.Add(operation);
                // Transport delay remains represented by timedOut until a definitive
                // receipt arrives. Only a genuinely uncertain native mutation is sticky.
                Report("Drink purchase confirmation is delayed. Check shared cash and the bar before ordering again.");
            }
            if (host)
            {
                if (peerCooldowns.Count > 0) foreach (var profile in peerCooldowns.Where(pair => Time.timeSinceLevelLoad >= pair.Value).Select(pair => pair.Key).ToArray())
                    peerCooldowns.Remove(profile);
                if (Time.unscaledTime >= nextCapture)
                {
                    nextCapture = Time.unscaledTime + CaptureInterval;
                    Publish();
                }
            }
            else if (initialized && !state.Drinking && !localPlayback && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + CaptureInterval;
                var local = Capture();
                if (local.BottleCount != state.BottleCount || local.WineUnits != state.WineUnits || local.StoreOpen != state.StoreOpen)
                    ApplyBottlePresentation(state);
            }
        }

        private static string Message(BarDrinkStatus status) => status switch
        {
            BarDrinkStatus.Unavailable => "The bar drink is unavailable in the host's world.",
            BarDrinkStatus.Closed => "The bar is closed.",
            BarDrinkStatus.PriceChanged => "The drink price changed. Check the listing and try again.",
            BarDrinkStatus.NotSeated => "Sit down at the bar before ordering a drink.",
            BarDrinkStatus.TooFar => "Move closer to the bar before ordering.",
            BarDrinkStatus.HandsBusy => "Put down your current item before ordering a drink.",
            BarDrinkStatus.Funds => "Not enough shared cash.",
            BarDrinkStatus.Cooldown => "You are not ready to drink alcohol yet.",
            BarDrinkStatus.Busy => "Wait for the current bar drink to finish.",
            BarDrinkStatus.Capacity => "Wait for the previous drink order to finish.",
            BarDrinkStatus.Uncertain => "The drink purchase outcome is uncertain. Check shared cash and the bar before retrying.",
            _ => "The host declined the drink purchase."
        };

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnPeerLeft; }
                if (channel != null)
                {
                    channel.SnapshotApplied -= Snapshot;
                    channel.Changed -= Changed;
                    channel.CommandRequested -= Command;
                    channel.CommandRejected -= Rejected;
                    channel.ReplyReceived -= Reply;
                }
                if (!host && listing != null && !localPlayback)
                {
                    Price?.SetValue(listing, originalPrice);
                    if (store != null) store.isOpen = original.StoreOpen;
                    ApplyBottlePresentation(original, teardown: true);
                }
            }
            finally
            {
                pending.Clear();
                timedOut.Clear();
                drinkingUntil.Clear();
                peerCooldowns.Clear();
                presence.Clear();
                if (Active == this) Active = null;
                session = null;
                channel = null;
            }
        }
    }
}
