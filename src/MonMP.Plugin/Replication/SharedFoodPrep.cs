using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The host runs the native cutter, packager and fryer callbacks. Guests receive stock
    // and activity state, while every guest mutation entry point is fail-closed until a
    // validated interaction command path is implemented.
    internal sealed class SharedFoodPrep : IDisposable
    {
        private static readonly FieldInfo CurdsField = AccessTools.Field(typeof(Furniture_Fryer), "FromageCurdsInMachine");
        private static readonly FieldInfo WorkingField = AccessTools.Field(typeof(Furniture_Fryer), "Coroutine_BuildingRunning");
        private static readonly FieldInfo LastAddedField = AccessTools.Field(typeof(Furniture_Fryer), "LastTimeAddedCurds");
        private static readonly FieldInfo AnimatorField = AccessTools.Field(typeof(Furniture_Fryer), "animator");
        private static readonly FieldInfo SteamField = AccessTools.Field(typeof(Furniture_Fryer), "vapeur");
        private static readonly FieldInfo SoundField = AccessTools.Field(typeof(Furniture_Fryer), "SOUND_Furniture_Fryer");
        private static readonly FieldInfo BasketField = AccessTools.Field(typeof(Furniture_Fryer), "FryingBasket");
        private static readonly FieldInfo PressSourceField = AccessTools.Field(typeof(SOUND_Furniture_Fryer), "Press");
        private static readonly FieldInfo BoilingSourceField = AccessTools.Field(typeof(SOUND_Furniture_Fryer), "BoilingOil");
        private static readonly FieldInfo OneShotSourceField = AccessTools.Field(typeof(SOUND_Furniture_Fryer), "OneShot");
        private readonly Dictionary<Furniture_Fryer, float> receivedAt = new Dictionary<Furniture_Fryer, float>();
        private readonly Dictionary<Furniture_Fryer, float> receivedScaledAt = new Dictionary<Furniture_Fryer, float>();
        private readonly Dictionary<Furniture_Fryer, float> pressUntil = new Dictionary<Furniture_Fryer, float>();
        private readonly Dictionary<Furniture_Fryer, byte> seekPending = new Dictionary<Furniture_Fryer, byte>();
        private readonly Dictionary<Furniture_Fryer, Queue<PresentationEvent>> pendingCues = new Dictionary<Furniture_Fryer, Queue<PresentationEvent>>();
        private readonly int[] cueCounts = new int[4];
        private readonly bool[] cueSounds = new bool[4];
        private const byte InputCurds = 1, InputFries = 2;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<Furniture_Fryer, float> lastGuestInput = new Dictionary<Furniture_Fryer, float>();
        private const float CaptureInterval = 0.15f;

        private readonly bool host;
        private readonly Dictionary<ulong, Furniture_Fryer> fryers = new Dictionary<ulong, Furniture_Fryer>();
        private readonly Dictionary<Furniture_Fryer, FoodPrepState> originals = new Dictionary<Furniture_Fryer, FoodPrepState>();
        private readonly Dictionary<Furniture_Fryer, FoodPrepState> deferredOriginalAnimation = new Dictionary<Furniture_Fryer, FoodPrepState>();
        private readonly Dictionary<Furniture_Fryer, FryerTransformPose[]> originalPoses = new Dictionary<Furniture_Fryer, FryerTransformPose[]>();
        private readonly Dictionary<Furniture_Fryer, FryerAnimationRestore> waitingRestores = new Dictionary<Furniture_Fryer, FryerAnimationRestore>();
        private readonly HashSet<Furniture_Fryer> applying = new HashSet<Furniture_Fryer>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextCapture;
        private bool disposed;
        private bool reportedUnavailable;

        internal int Count => fryers.Count;
        internal int CutterCount { get; }
        internal int PackagerCount { get; }
        internal bool GuestSession => !host && !disposed && session?.IsRunning == true;

#if MONMP_DEVELOPMENT
        internal FoodPrepState TestState(Furniture_Fryer fryer) => Capture(fryer);


        internal FoodPrepState TestOriginal(Furniture_Fryer fryer) => OriginalState(fryer);


        internal FoodPrepState TestCanonicalState(Furniture_Fryer fryer) => stateById[fryers.First(pair => pair.Value == fryer).Key];


        internal int TestCueCount(byte variant) => variant < cueCounts.Length ? cueCounts[variant] : 0;


        internal bool TestCueSoundObserved(byte variant) => variant < cueSounds.Length && cueSounds[variant];


        internal Furniture_Fryer? TestFryer => fryers.Values.FirstOrDefault();


        internal int TestCutterCount => CutterCount;


        internal int TestPackagerCount => PackagerCount;


        internal bool TestGuestMutationBlocked => GuestSession && !host;


        internal int TestPressCount { get; private set; }


        internal bool TestPressSoundObserved { get; private set; }


        internal bool TestPressStockPreserved { get; private set; } = true;


        internal static bool TestBasketVisible(Furniture_Fryer fryer) => (BasketField.GetValue(fryer) as GameObject)?.activeSelf == true;


        internal static void TestSetBasketVisible(Furniture_Fryer fryer, bool visible) => ApplyBasketVisibility(fryer, visible);
#endif


        internal void NativePress(Furniture_Fryer fryer)
        {
            if (!host || disposed || session?.IsRunning != true || SharedPresentationEvents.Replaying) return;
            ulong id = fryers.FirstOrDefault(pair => pair.Value == fryer).Key;
            if (id != 0) { ObserveNativeCue(fryer, 0); PresentationReplays.Post(PresentationKind.FryerPress, 0, id, fryer.transform.position); }
        }

        internal void NativeCue(SOUND_Furniture_Fryer sound, byte variant)
        {
            if (!host || disposed || variant < 1 || variant > 3 || session?.IsRunning != true || SharedPresentationEvents.Replaying) return;
            var fryer = sound.GetComponent<Furniture_Fryer>();
            ulong id = fryers.FirstOrDefault(pair => pair.Value == fryer).Key;
            if (id != 0) { ObserveNativeCue(fryer, variant); PresentationReplays.Post(PresentationKind.FryerPress, variant, id, fryer.transform.position); }
        }

        private void ObserveNativeCue(Furniture_Fryer fryer, byte variant)
        {

#if MONMP_DEVELOPMENT
            if (MonMPPlugin.Instance?.TestOptions.Enabled != true) return;
#endif

            cueCounts[variant]++;
            var sound = SoundField.GetValue(fryer) as SOUND_Furniture_Fryer;
            var source = sound == null ? null : (variant == 0 ? PressSourceField : OneShotSourceField).GetValue(sound) as AudioSource;
            cueSounds[variant] |= source?.isPlaying == true;
        }

        internal void PlayPress(PresentationEvent e)
        {
            if (disposed || e.Variant > 3 || !fryers.TryGetValue(e.Target, out var fryer) || fryer == null) return;
            var sound = SoundField.GetValue(fryer) as SOUND_Furniture_Fryer;
            var source = sound == null ? null : (e.Variant == 0 ? PressSourceField : OneShotSourceField).GetValue(sound) as AudioSource;
            if (source == null)
            {
                if (!pendingCues.TryGetValue(fryer, out var waiting)) pendingCues[fryer] = waiting = new Queue<PresentationEvent>();
                if (waiting.Count < 8) waiting.Enqueue(e);
                return;
            }
#if MONMP_DEVELOPMENT
            bool probe = MonMPPlugin.Instance?.TestOptions.Enabled == true;
            var before = probe ? Capture(fryer) : default;
#endif
            switch (e.Variant)
            {
                case 0:
                    // Give the native trigger a frame to enter its state before authoritative seeks.
                    pressUntil[fryer] = Time.unscaledTime + CaptureInterval + 0.1f;
                    fryer.PressPotato(); break;
                case 1: sound!.Close(); break;
                case 2: sound!.Open(); break;
                case 3: sound!.BuildPoutine(); break;
            }
#if MONMP_DEVELOPMENT
            if (!probe) return;
            cueCounts[e.Variant]++;
            cueSounds[e.Variant] |= source.isPlaying;
            var after = Capture(fryer);

#if MONMP_DEVELOPMENT
            TestPressStockPreserved &= after.FilledPercent == before.FilledPercent && after.Curds == before.Curds && after.Working == before.Working;
#endif

            if (e.Variant == 0) {
#if MONMP_DEVELOPMENT
TestPressCount++;

 TestPressSoundObserved |= source.isPlaying;
#endif
 }
#endif
        }

        internal SharedFoodPrep(bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.host = host;
            var sceneFryers = Object.FindObjectsOfType<Furniture_Fryer>(true)
                .Where(f => f != null && f.gameObject.scene.IsValid()).ToArray();
            CutterCount = Object.FindObjectsOfType<Furniture_FrenchFryCutter>(true)
                .Count(c => c != null && c.gameObject.scene.IsValid());
            PackagerCount = Object.FindObjectsOfType<Furniture_FrenchFryPackager>(true)
                .Count(p => p != null && p.gameObject.scene.IsValid());

            foreach (var fryer in sceneFryers)
            {
                var animator = AnimatorField.GetValue(fryer) as Animator;
                FoodPrepState? pendingAnimation = null;
                if (animator != null)
                {
                    // A previous disconnect may have left a deferred restoration on this inactive
                    // animator. Let it finish before any new authoritative seek, retaining its baseline.
                    foreach (var pending in animator.GetComponents<FryerAnimationRestore>())
                    {
                        if (!pending.enabled) continue;
                        pending.TryRestore();
                        if (pending.enabled) { pendingAnimation = pending.State; waitingRestores[fryer] = pending; }
                    }
                    originalPoses[fryer] = animator.GetComponentsInChildren<Transform>(true).Select(t => new FryerTransformPose(t)).ToArray();
                }
                string identity = RuntimeProbe.CanonicalPath(fryer.transform) + "/food-prep-v1";
                ulong id = StaticId.Hash(identity);
                var original = Capture(fryer);
                if (pendingAnimation.HasValue)
                {
                    var animation = pendingAnimation.Value;
                    original = new FoodPrepState(original.FilledPercent, original.Curds, original.Working, original.BasketVisible,
                        animation.AnimatorLayers, original.SteamPlaying, original.BoilingPlaying);
                }
                _ = original.Encode();
                identities.Bind(id, FoodPrepState.Adapter, fryer.gameObject, identity, true, EntityPersistence.SessionOnly);
                fryers.Add(id, fryer);
                originals.Add(fryer, original);
                stateById.Add(id, original);
            }

            string[] rows = fryers.Select(pair => pair.Key.ToString("X16") + "\t" +
                RuntimeProbe.CanonicalPath(pair.Value.transform) + "\t" + Describe(Capture(pair.Value))).ToArray();

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-food-prep.tsv"),
                new[] { "id\tidentity\tstate", "counts\tfryers=" + fryers.Count + "\tcutters=" + CutterCount + "\tpackagers=" + PackagerCount }.Concat(rows));
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            foreach (var pair in fryers)
                yield return new WorldRecord(pair.Key, 1, FoodPrepState.Adapter, Capture(pair.Value).Encode());
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            session.PoseReceived += OnPose;
            session.PeerLeft += OnLeft;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        // Furniture_Fryer.OnTriggerEnter on a guest: its own fries or curds, its own facing (#20).
        internal void GuestInput(Furniture_Fryer fryer, Collider other)
        {
            var items = MonMPPlugin.Instance?.Items;
            if (other == null || items == null || channel?.Ready != true || session == null) return;
            ulong fryerId = fryers.FirstOrDefault(pair => pair.Value == fryer).Key;
            if (fryerId == 0) return;
            var curds = other.GetComponent<Item_CurdsCheese>();
            var fries = curds == null ? other.GetComponent<Item_FrenchFries>() : null;
            InteractableItems? item = curds != null ? curds : fries;
            if (item == null) return;
            uint? owner = items.LeaseOwnerOf(item);
            if (owner == null || (owner != session.LocalPeerId && owner != ItemLease.Unowned)) return;
            if (lastGuestInput.TryGetValue(fryer, out float last) && Time.realtimeSinceStartup - last < 1f) return;
            if (fries != null)
            {
                var player = Singleton<Gameplay>.i.Player;
                if ((bool)WorkingField.GetValue(fryer) || GameManager.Get_Friendship[12] < 50 ||
                    Vector3.Dot(player.transform.forward, fryer.transform.right) < 0.35f) return;
                if (fryer.Get_PercentFiller >= 100)
                {
                    lastGuestInput[fryer] = Time.realtimeSinceStartup;
                    Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_FrenchFryCutter_FryerBasketIsFull"));
                    return;
                }
            }
            lastGuestInput[fryer] = Time.realtimeSinceStartup;
            var payload = new byte[9];
            payload[0] = curds != null ? InputCurds : InputFries;
            BitConverter.GetBytes(items.EntityOf(item)).CopyTo(payload, 1);
            channel.Request(fryerId, payload);
        }

        // The host's replica of an item a guest holds does not feed the fryer by the host's facing.
        internal bool HostAllowsNativeInput(Collider other)
        {
            if (!host || disposed || session?.IsRunning != true || other == null) return true;
            var item = (InteractableItems?)other.GetComponent<Item_FrenchFries>() ?? other.GetComponent<Item_CurdsCheese>();
            if (item == null) return true;
            uint? owner = MonMPPlugin.Instance?.Items?.LeaseOwnerOf(item);
            return owner == null || owner == 0 || owner == ItemLease.Unowned;
        }

        [MonMP.Core.World.WorldCommandAdapters(FoodPrepState.Adapter)]

        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || peer == 0 || !fryers.TryGetValue(entity, out var fryer) || fryer == null || payload.Length != 9) return;
            var items = MonMPPlugin.Instance?.Items;
            if (items == null || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            if (!presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 3f) return;
            var at = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
            if ((at - fryer.transform.position).sqrMagnitude > 4f * 4f) return;
            ulong itemId = BitConverter.ToUInt64(payload, 1);
            var item = items.ItemOf(itemId);
            if (item == null || (item.transform.position - fryer.transform.position).sqrMagnitude > 2.5f * 2.5f) return;
            if ((float)LastAddedField.GetValue(fryer) + 1f >= Time.realtimeSinceStartup) return;
            if (payload[0] == InputCurds && item is Item_CurdsCheese)
            {
                if (!items.TakeFrom(peer, itemId, typeof(Item_CurdsCheese))) return;
                LastAddedField.SetValue(fryer, Time.realtimeSinceStartup);
                CurdsField.SetValue(fryer, (int)CurdsField.GetValue(fryer) + 50);
            }
            else if (payload[0] == InputFries && item is Item_FrenchFries)
            {
                var forward = Quaternion.Euler(0f, seen.Pose.Yaw, 0f) * Vector3.forward;
                if ((bool)WorkingField.GetValue(fryer) || GameManager.Get_Friendship[12] < 50 ||
                    Vector3.Dot(forward, fryer.transform.right) < 0.35f || fryer.Get_PercentFiller >= 100) return;
                if (!items.TakeFrom(peer, itemId, typeof(Item_FrenchFries))) return;
                LastAddedField.SetValue(fryer, Time.realtimeSinceStartup);
                fryer.Get_PercentFiller = 100;
            }
            else return;
            MonMPPlugin.Instance?.World?.Events?.HostAnnounce(PresentationKind.PartInstalled, 0, entity, fryer.transform.position, 0, 0, true);
            nextCapture = 0;
        }

        // Guest inputs remain blocked even if a machine was not found during discovery. This
        // prevents an unsupported prefab variant from mutating its local save by falling back
        // to vanilla behavior.
        internal bool BlocksGuestMutation => GuestSession;
        internal bool AllowGuestPercentSetter(Furniture_Fryer fryer) =>
            !GuestSession || (fryer != null && applying.Contains(fryer));

        internal void ReportUnavailable()
        {
            if (reportedUnavailable) return;
            reportedUnavailable = true;
            MonMPPlugin.Instance?.ReportItemUseUnavailable("This fry machine is not registered in the shared world.");
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true || channel?.Ready != true) return;
            if (!host)
            {
                // An inactive fryer's native Start can run after its snapshot and hide the basket.
                // Reapply the host's explicit visibility without invoking unlock effects or stock changes.
                foreach (var pair in fryers)
                    if (pair.Value != null && stateById.TryGetValue(pair.Key, out var state))
                    {
                        ApplyBasketVisibility(pair.Value, state.BasketVisible);
                        ApplyPresentation(pair.Value, state);
                        DrainCues(pair.Value);
                    }
                return;
            }
            if (Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            foreach (var pair in fryers)
            {
                FoodPrepState next = Capture(pair.Value);
                if (!stateById.TryGetValue(pair.Key, out var previous))
                { stateById[pair.Key] = next; continue; }
                if (next.Equals(previous)) continue;
                stateById[pair.Key] = next;
                channel.Publish(pair.Key, next.Encode());
            }
        }

        private readonly Dictionary<ulong, FoodPrepState> stateById = new Dictionary<ulong, FoodPrepState>();

        private static FoodPrepState Capture(Furniture_Fryer fryer)
        {
            var layers = new List<FoodPrepAnimatorLayer>();
            var animator = AnimatorField.GetValue(fryer) as Animator;
            if (AnimatorSampleReady(animator))
            {
                for (int index = 0; index < Math.Min(animator!.layerCount, FoodPrepState.MaxAnimatorLayers); index++)
                {
                    var info = animator.GetCurrentAnimatorStateInfo(index);
                    if (animator.IsInTransition(index))
                    {
                        var next = animator.GetNextAnimatorStateInfo(index);
                        if (next.fullPathHash != 0 && animator.HasState(index, next.fullPathHash)) info = next;
                    }
                    float weight = animator.GetLayerWeight(index);
                    if (!float.IsNaN(info.normalizedTime) && !float.IsInfinity(info.normalizedTime) && !float.IsNaN(weight) && !float.IsInfinity(weight))
                    {
                        float time = info.fullPathHash == 0 ? 0 : info.loop ? Mathf.Repeat(info.normalizedTime, 1f) : Mathf.Clamp01(info.normalizedTime);
                        layers.Add(new FoodPrepAnimatorLayer((byte)index, info.fullPathHash, time, info.fullPathHash != 0 && info.loop, Mathf.Clamp01(weight)));
                    }
                }
            }
            var sound = SoundField.GetValue(fryer) as SOUND_Furniture_Fryer;
            var boil = sound == null ? null : BoilingSourceField.GetValue(sound) as AudioSource;
            return new FoodPrepState(fryer.Get_PercentFiller, (int)CurdsField.GetValue(fryer), (bool)WorkingField.GetValue(fryer),
                (BasketField.GetValue(fryer) as GameObject)?.activeSelf == true, layers,
                (SteamField.GetValue(fryer) as ParticleSystem)?.isEmitting == true, boil?.isPlaying == true);
        }

        private static string Describe(FoodPrepState state) =>
            "filled=" + state.FilledPercent + "\tcurds=" + state.Curds + "\tworking=" + state.Working + "\tbasket=" + state.BasketVisible;

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var pair in fryers)
                if (records.TryGetValue(pair.Key, out var record)) Apply(record, forceSeek: true);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == FoodPrepState.Adapter) Apply(record, forceSeek: false);
        }

        private void Apply(WorldRecord record, bool forceSeek)
        {
            if (record.Adapter != FoodPrepState.Adapter || !fryers.TryGetValue(record.Id, out var fryer))
                throw new InvalidDataException("Unknown food preparation machine record.");
            var state = FoodPrepState.Decode(record.Payload);
            // The host's fryer spends 20% filler and 10 curds per poutine, granting this natively.
            if (!forceSeek && stateById.TryGetValue(record.Id, out var previous) &&
                previous.FilledPercent - state.FilledPercent >= 20 && previous.Curds - state.Curds >= 10)
                GuestAchievements.Award(AchievementType.MakeHomemadePoutine);
            stateById[record.Id] = state;
            receivedAt[fryer] = Time.unscaledTime;
            receivedScaledAt[fryer] = Time.time;
            if (forceSeek)
            {
                byte mask = 0;
                foreach (var layer in state.AnimatorLayers) mask |= (byte)(1 << layer.LayerIndex);
                seekPending[fryer] = mask;
            }
            Apply(fryer, state);
            ApplyPresentation(fryer, state);
        }

        private static bool AnimatorReady(Animator? animator) => animator != null && animator.isActiveAndEnabled &&
            animator.runtimeAnimatorController != null && animator.isInitialized && animator.layerCount > 0;
        private static bool AnimatorSampleReady(Animator? animator) => animator != null &&
            animator.runtimeAnimatorController != null && animator.isInitialized && animator.layerCount > 0;

        private void DrainCues(Furniture_Fryer fryer)
        {
            if (!pendingCues.TryGetValue(fryer, out var waiting)) return;
            var sound = SoundField.GetValue(fryer) as SOUND_Furniture_Fryer;
            if (sound == null) return;
            while (waiting.Count > 0)
            {
                var e = waiting.Peek();
                if ((e.Variant == 0 ? PressSourceField : OneShotSourceField).GetValue(sound) is not AudioSource) break;
                waiting.Dequeue(); PlayPress(e);
            }
            if (waiting.Count == 0) pendingCues.Remove(fryer);
        }

        // Native phase state is independent of Working: steam remains on while the host scoops, but
        // boiling has already stopped. Late snapshots reproduce that state without old one-shots.
        private void ApplyPresentation(Furniture_Fryer fryer, FoodPrepState state, bool restore = false)
        {
            var steam = SteamField.GetValue(fryer) as ParticleSystem;
            var sound = SoundField.GetValue(fryer) as SOUND_Furniture_Fryer;
            if (steam != null && steam.isEmitting != state.SteamPlaying)
            {
                if (state.SteamPlaying) steam.Play(); else steam.Stop();
            }
            var boil = sound == null ? null : BoilingSourceField.GetValue(sound) as AudioSource;
            // SOUND_Furniture_Fryer.Start initializes its sources after the fryer; retry until ready.
            // Native BoilingStart always replays, so only invoke it when the loop really is stopped.
            if (sound != null && boil != null && boil.isPlaying != state.BoilingPlaying)
            {
                if (state.BoilingPlaying) sound.BoilingStart(); else sound.BoilingStop();
            }
            var animator = AnimatorField.GetValue(fryer) as Animator;
            if (!AnimatorReady(animator)) return;
            if (!restore && waitingRestores.TryGetValue(fryer, out var pendingRestore))
            {
                if (pendingRestore != null && pendingRestore.enabled)
                { pendingRestore.TryRestore(); if (pendingRestore.enabled) return; }
                waitingRestores.Remove(fryer);
            }
            // Inactive native animators may not have a state at discovery. Keep their first local
            // initialized pose before any host seek so disconnect can restore a real native state.
            if (!host && !restore && originals.TryGetValue(fryer, out var original) && original.AnimatorLayers.Count == 0 &&
                !deferredOriginalAnimation.ContainsKey(fryer))
            {
                var local = Capture(fryer);
                if (local.AnimatorLayers.Count > 0) deferredOriginalAnimation[fryer] = local;
            }
            if (restore)
            {
                animator!.ResetTrigger("MakePoutine"); animator.ResetTrigger("PressPotato");
                if (state.AnimatorLayers.Count == 0) { animator.Rebind(); animator.Update(0); return; }
            }
            seekPending.TryGetValue(fryer, out byte pendingMask);
            foreach (var layer in state.AnimatorLayers)
            {
                int index = layer.LayerIndex;
                if (index >= animator!.layerCount) continue;
                if (Mathf.Abs(animator.GetLayerWeight(index) - layer.Weight) > 1f / 65535f) animator.SetLayerWeight(index, layer.Weight);
                if (layer.StateHash == 0 || !animator.HasState(index, layer.StateHash)) continue;
                // Native press is layer 0 (FrenchPress); its grace must not delay layer 1's fryer cook.
                if (!restore && index == 0 && pressUntil.TryGetValue(fryer, out float until) && Time.unscaledTime < until)
                    continue;
                var current = animator.GetCurrentAnimatorStateInfo(index);
                if (animator.IsInTransition(index))
                {
                    var next = animator.GetNextAnimatorStateInfo(index);
                    if (next.fullPathHash == layer.StateHash) current = next;
                }
                float expected = layer.NormalizedTime;
                bool unscaled = animator.updateMode == AnimatorUpdateMode.UnscaledTime;
                var timestamps = unscaled ? receivedAt : receivedScaledAt;
                if (!restore && timestamps.TryGetValue(fryer, out float received) && current.fullPathHash == layer.StateHash && current.length > 0)
                    expected += ((unscaled ? Time.unscaledTime : Time.time) - received) * animator.speed * current.speed * current.speedMultiplier / current.length;
                if (float.IsNaN(expected) || float.IsInfinity(expected)) expected = layer.NormalizedTime;
                expected = layer.Loop ? Mathf.Repeat(expected, 1f) : Mathf.Clamp01(expected);
                bool invalidTime = float.IsNaN(current.normalizedTime) || float.IsInfinity(current.normalizedTime);
                float actual = invalidTime ? 0 : layer.Loop ? Mathf.Repeat(current.normalizedTime, 1f) : Mathf.Clamp01(current.normalizedTime);
                float drift = layer.Loop ? Mathf.Abs(Mathf.DeltaAngle(actual * 360f, expected * 360f)) / 360f : Mathf.Abs(actual - expected);
                if (restore || (pendingMask & (1 << index)) != 0 || current.fullPathHash != layer.StateHash || invalidTime || drift > 0.12f)
                    animator.Play(layer.StateHash, index, expected);
                pendingMask &= (byte)~(1 << index);
            }
            if (restore) animator!.Update(0);
            if (pendingMask == 0) seekPending.Remove(fryer); else seekPending[fryer] = pendingMask;
        }

        private void Apply(Furniture_Fryer fryer, FoodPrepState state)
        {
            applying.Add(fryer);
            try
            {
                ApplyBasketVisibility(fryer, state.BasketVisible);
                fryer.Get_PercentFiller = state.FilledPercent;
                CurdsField.SetValue(fryer, state.Curds);
                WorkingField.SetValue(fryer, state.Working);
            }
            finally { applying.Remove(fryer); }
        }

        private static void ApplyBasketVisibility(Furniture_Fryer fryer, bool visible)
        {
            var basket = BasketField.GetValue(fryer) as GameObject;
            if (basket != null && basket.activeSelf != visible) basket.SetActive(visible);
        }

        private FoodPrepState OriginalState(Furniture_Fryer fryer)
        {
            var original = originals[fryer];
            if (deferredOriginalAnimation.TryGetValue(fryer, out var animation))
                original = new FoodPrepState(original.FilledPercent, original.Curds, original.Working, original.BasketVisible,
                    animation.AnimatorLayers, original.SteamPlaying, original.BoilingPlaying);
            return original;
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
            }
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            presence.Clear(); lastGuestInput.Clear();
            if (!host)
                foreach (var pair in originals)
                    if (pair.Key != null)
                    {
                        var original = OriginalState(pair.Key);
                        Apply(pair.Key, original); ApplyPresentation(pair.Key, original, restore: true);
                        var animator = AnimatorField.GetValue(pair.Key) as Animator;
                        if (animator != null && !AnimatorReady(animator) && originalPoses.TryGetValue(pair.Key, out var poses))
                        {
                            foreach (var pose in poses) pose.Restore();
                            foreach (var pending in animator.GetComponents<FryerAnimationRestore>())
                            { pending.enabled = false; Object.Destroy(pending); }
                            animator.gameObject.AddComponent<FryerAnimationRestore>().Configure(animator, original, poses);
                        }
                    }
            receivedAt.Clear(); receivedScaledAt.Clear(); pressUntil.Clear(); seekPending.Clear(); pendingCues.Clear();
            deferredOriginalAnimation.Clear(); originalPoses.Clear(); waitingRestores.Clear();
            session = null;
            channel = null;
            applying.Clear();
        }
    }

    internal readonly struct FryerTransformPose
    {
        private readonly Transform transform;
        private readonly Vector3 position, scale;
        private readonly Quaternion rotation;
        internal FryerTransformPose(Transform transform)
        { this.transform = transform; position = transform.localPosition; rotation = transform.localRotation; scale = transform.localScale; }
        internal void Restore()
        {
            if (transform == null) return;
            transform.localPosition = position; transform.localRotation = rotation; transform.localScale = scale;
        }
    }

    // Does no machine work: an inactive animator cannot safely be sought at disconnect. Restore
    // its saved native state when Unity initializes it again, then remove this temporary helper.
    internal sealed class FryerAnimationRestore : MonoBehaviour
    {
        private Animator? animator;
        private FoodPrepState state;
        private FryerTransformPose[] poses = Array.Empty<FryerTransformPose>();
        internal FoodPrepState State => state;
        internal void Configure(Animator animator, FoodPrepState state, FryerTransformPose[] poses)
        { this.animator = animator; this.state = state; this.poses = poses; TryRestore(); }
        private void OnEnable() => TryRestore();
        private void Update() => TryRestore();
        internal void TryRestore()
        {
            if (!enabled) return;
            if (animator == null || !animator.isActiveAndEnabled || !animator.isInitialized || animator.runtimeAnimatorController == null || animator.layerCount == 0) return;
            animator.ResetTrigger("MakePoutine"); animator.ResetTrigger("PressPotato");
            if (state.AnimatorLayers.Count == 0) animator.Rebind();
            else foreach (var layer in state.AnimatorLayers)
            {
                if (layer.LayerIndex >= animator.layerCount) continue;
                animator.SetLayerWeight(layer.LayerIndex, layer.Weight);
                if (layer.StateHash != 0 && animator.HasState(layer.LayerIndex, layer.StateHash))
                    animator.Play(layer.StateHash, layer.LayerIndex, layer.NormalizedTime);
            }
            animator.Update(0);
            if (state.AnimatorLayers.Count == 0) foreach (var pose in poses) pose.Restore();
            enabled = false; Destroy(this);
        }
    }
}
