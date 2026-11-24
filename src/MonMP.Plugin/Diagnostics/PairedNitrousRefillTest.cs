using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2.VehicleGUI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // One real native refill entered by moving only our clone's rigidbody through the station's
    // existing trigger over fixed physics steps, including its wallet debit and coroutine.
    // Only our spawned bottle's capacity is extended, giving the late join a 20-second window.
    internal sealed class PairedNitrousRefillTest
    {
        private const byte Marker = 242;
        private const float Capacity = 5f, Price = 69.99f;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly NitrousRefillStation station;
        private readonly AnalogGauge gauge;
        private readonly Collider trigger;
        private AudioSource loop = null!, oneShot = null!;
        private Cash_Manager cash = null!;
        private UI_ShowCash cashUi = null!;
        private readonly ushort bottlePrefab;
        private readonly ulong previousBottle;
        private float originalPressure, originalSpent;
        private readonly float originalCooldown;
        private AudioOriginal originalLoop = null!, originalOneShot = null!;
        private readonly bool originalWorking;
        private Quaternion originalNeedleRotation;
        private bool originalNeedleCaptured;
        private readonly bool originalStationEnabled, originalGaugeEnabled;
        private readonly List<GameObject> activatedObjects = new List<GameObject>();
        private readonly Dictionary<GameObject, bool> originalActivity = new Dictionary<GameObject, bool>();
        private readonly Dictionary<GameObject, bool> siblingActivity = new Dictionary<GameObject, bool>();
        private readonly HashSet<Transform> fixturePaths = new HashSet<Transform>();
        private readonly float deadline;
        private WorldChannel? subscribed;
        private Harmony? guard;
        private FieldInfo? iteratorStation, iteratorBottle;
        private IEnumerator? ownRefill;
        private IEnumerator? ownEntry;
        private Part_NitrousBottle? bottle;
        private static PairedNitrousRefillTest? nativeOwner;
        private ulong bottleId;
        private uint guestPeer, initialGuest, initialPeer;
        private int phase, acknowledgment, paritySamples, nativeSteps, guestNativeSteps, cashDebits;
        private int physicsSteps, triggerEntries, acceptedTriggerEntries;
        private float entryDeadline;
        private int originalCash;
        private float cashSpentBefore, firstPressure = -1, lastPressure, lastCharge, nextSample, next, mismatchSince = -1;
        private byte pendingStage;
        private bool stageAcknowledged, hostSawDisconnect, staged, cleaned, needleProgressObserved, initialized;
        private float retryAt;

        private sealed class AudioOriginal
        {
            private readonly bool playing;
            private readonly float time, volume, pitch;
            internal AudioOriginal(AudioSource source)
            { playing = source.isPlaying; time = source.time; volume = source.volume; pitch = source.pitch; }
            internal bool Playing => playing;
            internal void Restore(AudioSource source)
            {
                if (source == null) return;
                source.Stop(); source.volume = volume; source.pitch = pitch;
                if (source.clip != null) source.time = Mathf.Clamp(time, 0, source.clip.length);
                if (playing) source.Play();
            }
        }

        internal string Status => "nitrous refill phase=" + phase + "; ack=" + acknowledgment +
            "; pressure=" + (gauge == null ? "missing" : gauge.Value.ToString("F3")) +
            "; loop=" + (loop != null && loop.isPlaying) + "; bottle=" + bottleId.ToString("X16") +
            "; charge=" + lastCharge.ToString("F3") + "; nativeSteps=" + nativeSteps +
            "; guestNativeSteps=" + guestNativeSteps + "; cashDebits=" + cashDebits +
            "; physicsSteps=" + physicsSteps + "; triggerEntries=" + triggerEntries + "; acceptedTriggerEntries=" + acceptedTriggerEntries +
            "; parity=" + paritySamples + "; needleMoved=" + needleProgressObserved + "; departed=" + hostSawDisconnect +
            "; ready=" + initialized + "; activated=" + activatedObjects.Count + "; active=" + (station != null && station.gameObject.activeInHierarchy);

        internal PairedNitrousRefillTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; host = plugin.TestOptions.IsHost;
            station = plugin.World!.NitrousRefillStation.TestStation ?? throw new InvalidOperationException("Nitrous refill fixture station missing.");
            Require(station != null && station.gameObject.scene.IsValid(), "Native station is unavailable.");
            gauge = Field<AnalogGauge>(station, "NitrousPressureGauge");
            Require(gauge != null, "Native station serialized gauge is missing; activation cannot repair the scene reference.");
            originalWorking = Field<bool>(station, "isWorking"); originalCooldown = Field<float>(station, "CooldownTimer");
            Require(!originalWorking, "Fixture requires an idle native refill station.");
            originalStationEnabled = station.enabled; originalGaugeEnabled = gauge.enabled;
            // Capture before waking a dormant station: native Start resets its gauge to zero.
            originalPressure = gauge.Value;
            if (gauge.Needle != null)
            { originalNeedleRotation = gauge.Needle.transform.localRotation; originalNeedleCaptured = true; }
            var location = Field<Transform>(station, "NitrousBottleLocation");
            Require(location != null, "Native bottle mounting transform is missing.");
            // Native OnTriggerEnter calls other.GetComponent<Part_NitrousBottle>(); use the
            // station's original collider and the clone's same-object itemCollider unchanged.
            var triggers = station.GetComponents<Collider>().Where(c => c.isTrigger && c.enabled).ToArray();
            Require(triggers.Length == 1, "Native station must have one enabled same-object trigger; found " + triggers.Length + ".");
            trigger = triggers[0];
            foreach (var transform in Parents(station.transform).Concat(Parents(gauge.transform)).Concat(Parents(location)))
            {
                fixturePaths.Add(transform);
                if (!originalActivity.ContainsKey(transform.gameObject)) originalActivity.Add(transform.gameObject, transform.gameObject.activeSelf);
            }
            var prefabs = plugin.Items!.TestPrefabs(typeof(Part_NitrousBottle));
            Require(prefabs.Length > 0, "No shared native nitrous bottle prefab exists.");
            bottlePrefab = prefabs[0]; previousBottle = plugin.Items.TestNewestPrefab(bottlePrefab);
            new ItemValues(Capacity, 0, 0, 0).Validate();
            deadline = Time.unscaledTime + 50;
            // Readiness is asynchronous: an initially hidden Roger store has no native station
            // sound until its Start runs, and the added sound component needs a subsequent Start.
            // Keep all canonical paths unchanged, including through a new guest World binding.
            Debug.Log("MonMP NitrousRefill fixture readiness: path=" + RuntimeProbe.CanonicalPath(station.transform) +
                "; gauge=" + gauge.name + "; sound=" + (Field<SOUND_NitrousRefillStation>(station, "SOUND_NitrousRefillStation") != null) +
                "; hierarchy=" + string.Join(" / ", Parents(station.transform).Select(t => t.name + ":" + t.gameObject.activeSelf)));
        }

        private static IEnumerable<Transform> Parents(Transform child)
        { for (var current = child; current != null; current = current.parent) yield return current; }

        private void StageActivation(Transform child)
        {
            foreach (var transform in Parents(child).Reverse())
            {
                var go = transform.gameObject;
                if (go.activeSelf) continue;
                // Enable only the needed path through an originally hidden store state. Hide its
                // unrelated sibling branches while their parent is still inactive, preventing
                // dormant stock/actors/controllers from receiving Awake/Start as a side effect.
                if (!originalActivity[go] && transform != station.transform && transform != gauge.transform)
                    for (int i = 0; i < transform.childCount; i++)
                    {
                        var sibling = transform.GetChild(i);
                        if (fixturePaths.Contains(sibling)) continue;
                        if (!siblingActivity.ContainsKey(sibling.gameObject)) siblingActivity.Add(sibling.gameObject, sibling.gameObject.activeSelf);
                        if (sibling.gameObject.activeSelf) sibling.gameObject.SetActive(false);
                    }
                if (!activatedObjects.Contains(go)) activatedObjects.Add(go);
                go.SetActive(true);
            }
        }

        private void MaintainActivation()
        {
            StageActivation(station.transform); StageActivation(gauge.transform);
            if (!station.enabled) station.enabled = true;
            if (!gauge.enabled) gauge.enabled = true;
        }

        private float SharedSpent() =>
            plugin.World?.Channel.Records.TryGetValue(SharedStatisticsState.Entity, out var record) == true ? SharedStatisticsState.Decode(record.Payload).Values[4] : 0;
        private bool InitializeNative()
        {
            var sound = Field<SOUND_NitrousRefillStation>(station, "SOUND_NitrousRefillStation");
            if (sound == null) return false;
            var nativeLoop = Field<AudioSource>(sound, "NitrousFill");
            var nativeOneShot = Field<AudioSource>(sound, "OneShotAudio");
            if (nativeLoop == null || nativeOneShot == null || nativeLoop.clip == null || nativeOneShot.clip == null || gauge.Needle == null) return false;
            loop = nativeLoop; oneShot = nativeOneShot;
            Require(gauge.isActiveAndEnabled && gauge.Needle != null && gauge.Needle.activeInHierarchy &&
                !gauge.lockAtStart && !gauge.lockAtEnd && gauge.maxValue > 0 && gauge.needleSmoothing < 1 &&
                Math.Abs(gauge.startAngle - gauge.endAngle) > 1,
                "Native gauge needle must be active, unlocked and able to move.");
            if (!originalNeedleCaptured)
            { originalNeedleRotation = gauge.Needle.transform.localRotation; originalNeedleCaptured = true; }
            Require(!Field<bool>(station, "isWorking") && !loop.isPlaying && !oneShot.isPlaying, "Fixture requires an idle native refill station.");
            originalLoop = new AudioOriginal(loop); originalOneShot = new AudioOriginal(oneShot);
            cash = Object.FindObjectOfType<Cash_Manager>();
            Require(cash != null, "Native wallet is missing.");
            cashUi = Field<UI_ShowCash>(cash, "ui_ShowCash");
            Require(cashUi != null, "Native wallet display is missing.");
            originalSpent = GameManager.Get_PlayerStats[4];
            // The item protocol admits finite values up to 1,000,000; there is no prefab-capacity
            // clamp. A replica retains its ordinary capacity, but carries the host's literal charge.
            InstallGuards();
            initialized = true;
            Debug.Log("MonMP NitrousRefill native Start/source readiness established; " + Status);
            return true;
        }

        internal bool Tick()
        {
            if (cleaned) return true;
            Require(Time.unscaledTime < deadline, "Native refill/rejoin exceeded 50 seconds. " + Status);
            Require(station != null && gauge != null, "Native station/gauge were destroyed before preparation. " + Status);
            MaintainActivation();
            if (!initialized && !InitializeNative()) return false;
            Require(station != null && gauge != null && loop != null && oneShot != null && gameplay != null && cash != null,
                "Native fixture objects were destroyed. " + Status);
            Require(guestNativeSteps == 0 && (host || cashDebits == 0), "Guest ran native refill or wallet debit. " + Status);
            // Spending is a host-owned shared statistic: the guest follows the host's total, so only a
            // value above both its own baseline and that total would be a local guest debit.
            if (!host) Require(GameManager.Get_PlayerStats[4] <= Math.Max(originalSpent, SharedSpent()) + 0.001f, "Guest presentation changed native spending statistics.");
            if (!host && (phase == 2 || phase == 6))
            {
                if (Time.unscaledTime < next) return false;
                AssertOfflineRestoration();
                if (phase == 6) { Cleanup(); return true; }
                plugin.BeginPairedTestSession(); phase = 3; return false;
            }
            var world = plugin.World;
            if (world?.Channel.FullySynchronized != true || !world.PersonalProfileReady) return false;
            Attach(world.Channel); RetryStage();
            if (host) return Host(world.NitrousRefillStation);
            return Guest(world.NitrousRefillStation);
        }

        private bool Host(SharedNitrousRefillStation shared)
        {
            if (phase == 0 && acknowledgment >= 1)
            {
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                    "Fixture requires empty host hands.");
                Require(Time.timeScale >= 0.9f && Time.timeScale <= 1.1f, "Fixture requires normal native coroutine time scale.");
                Require(cash.isCanAfford(Price), "Host wallet cannot afford the native $69.99 refill; refusing to fund it synthetically.");
                if (Field<float>(station, "CooldownTimer") + 1.5f > Time.realtimeSinceStartup) return false;
                initialGuest = guestPeer;
                var spawned = plugin.Items!.TestSpawn(station.transform.position + Vector3.up * 10, bottlePrefab);
                bottle = spawned as Part_NitrousBottle;
                Require(bottle != null && bottle.gameObject.scene.IsValid(), "Spawned fixture is not a native bottle.");
                // Clone only: never modify the prefab or any pre-existing scene bottle.
                Set(bottle!, "MaxNitrousContent", Capacity); bottle!.Value1 = 0;
                Require(bottle.Value1_Max == Capacity && bottle.Value1 == 0, "Spawned clone capacity/empty charge staging failed.");
                plugin.Items.DiscoverSoon(); phase = 1;
            }
            else if (phase == 1)
            {
                Require(bottle != null, "Fixture bottle disappeared before native refill.");
                bottleId = plugin.Items!.TestBoundId(bottle!);
                if (bottleId == 0) return false;
                Require(bottleId > previousBottle && plugin.Items.TestState(bottleId).Lease.Owner == ItemLease.Unowned,
                    "Fixture bottle is not its own newly discovered unowned shared item.");
                if (ownEntry == null)
                {
                    Require(!Field<bool>(station, "isWorking"), "Another refill started before fixture entry.");
                    Require(trigger != null && trigger.enabled && trigger.gameObject.activeInHierarchy && trigger.bounds.size.sqrMagnitude > 0,
                        "Native station physics trigger is unavailable after activation.");
                    Require(bottle!.rb != null && bottle.itemCollider != null && bottle.itemCollider.enabled &&
                        bottle.itemCollider.GetComponent<Part_NitrousBottle>() == bottle,
                        "Native clone has no usable same-object rigidbody/bottle collider.");
                    Require(!Physics.GetIgnoreLayerCollision(trigger.gameObject.layer, bottle.gameObject.layer) &&
                        !Physics.GetIgnoreCollision(trigger, bottle.itemCollider), "Native trigger and bottle collision pair is ignored.");
                    originalCash = cash.GetCash; cashSpentBefore = GameManager.Get_PlayerStats[4]; staged = true;
                    entryDeadline = Time.unscaledTime + 6;
                    ownEntry = EnterThroughPhysics(); station.StartCoroutine(ownEntry);
                    return false;
                }
                Require(Time.unscaledTime < entryDeadline || Field<bool>(station, "isWorking"),
                    "Moving the clone through the native trigger did not start refill within 6 seconds. " + Status);
                if (!Field<bool>(station, "isWorking")) return false;
                Require(loop.isPlaying && ownRefill != null && nativeSteps > 0 && physicsSteps > 0 &&
                    triggerEntries > 0 && acceptedTriggerEntries == 1,
                    "Native physics trigger did not start exactly one real refill coroutine and loop. " + Status);
                Require(cashDebits == 1 && cash.GetCash == originalCash - 6999 && GameManager.Get_PlayerStats[4] == cashSpentBefore + 6999,
                    "Native refill did not debit exactly $69.99 once.");
                Require(shared.TestCueCount(0) == 1 && shared.TestCueSoundObserved(0), "Host native attach one-shot was not observed.");
                Debug.Log("MonMP NitrousRefill physics trigger started one native 20-second refill; bottle=" + bottleId.ToString("X16") + "; " + Status);
                phase = 2;
            }
            else if (phase == 2)
            {
                hostSawDisconnect |= plugin.Session!.Peers.Count == 1;
                Require(bottle != null && nativeSteps > 0, "Native fixture bottle/coroutine disappeared.");
                lastCharge = bottle!.Value1;
                Require(cashDebits == 1 && cash.GetCash == originalCash - 6999 && GameManager.Get_PlayerStats[4] == cashSpentBefore + 6999,
                    "Refill/reconnect duplicated or changed the native debit.");
                if (loop.isPlaying)
                {
                    Require(Field<bool>(station, "isWorking") && bottle.transform.parent == Field<Transform>(station, "NitrousBottleLocation") &&
                        Math.Abs(gauge.Value - bottle.Value1 / Capacity) < 0.005f,
                        "Host actual bottle/gauge diverged from the running native coroutine.");
                }
                if (acknowledgment >= 3)
                    Require(hostSawDisconnect && guestPeer != initialGuest, "Host did not observe actual departure and a new guest identity.");
                if (!Field<bool>(station, "isWorking"))
                {
                    Require(acknowledgment >= 3, "Native refill ended before the guest proved its late-join loop resumed.");
                    Require(lastCharge == Capacity && !loop.isPlaying && gauge.Value == 0 && bottle.transform.parent == null && bottle.itemCollider.enabled,
                        "Native completion did not fill/release the bottle and stop/reset its loop/gauge.");
                    Require(shared.TestCueCount(1) == 1 && shared.TestCueSoundObserved(1), "Host native detach one-shot was not observed.");
                    if (acknowledgment >= 4)
                    {
                        Require(acceptedTriggerEntries == 1, "Physics callback accepted a duplicate refill. " + Status);
                        Debug.Log("MonMP NitrousRefill host proved native physics entry, full charge, one debit, guest departure/rejoin and attach/detach. " + Status);
                        Cleanup(); return true;
                    }
                }
            }
            return false;
        }

        private IEnumerator EnterThroughPhysics()
        {
            var body = bottle!.rb;
            var collider = bottle.itemCollider;
            bool gravity = body.useGravity;
            var direction = station.transform.right.normalized;
            var bounds = trigger.bounds;
            var itemBounds = collider.bounds;
            // Place outside first, then sweep its actual collider centre into the original
            // trigger with MovePosition. No trigger/BottleEntered method is invoked by this test.
            float clearance = Vector3.Dot(bounds.extents + itemBounds.extents, new Vector3(
                Math.Abs(direction.x), Math.Abs(direction.y), Math.Abs(direction.z))) + 0.25f;
            var target = bounds.center - (itemBounds.center - body.position);
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true; body.useGravity = false;
            body.position = target + direction * clearance;
            Physics.SyncTransforms();
            Debug.Log("MonMP NitrousRefill entering original trigger through fixed-step clone physics; trigger=" +
                trigger.GetType().Name + "; bounds=" + bounds + "; outside=" + body.position + "; target=" + target);
            try
            {
                // Allow one full physics step to register the outside pose before crossing.
                yield return new WaitForFixedUpdate();
                while (bottle != null && !Field<bool>(station, "isWorking") && Time.unscaledTime < entryDeadline)
                {
                    body.MovePosition(Vector3.MoveTowards(body.position, target, 0.04f)); physicsSteps++;
                    yield return new WaitForFixedUpdate();
                }
            }
            finally { if (body != null) body.useGravity = gravity; }
        }

        private bool Guest(SharedNitrousRefillStation shared)
        {
            if (phase == 0) { initialPeer = plugin.Session!.LocalPeerId; Signal(1); phase = 1; }
            else if (phase == 1)
            {
                CheckParity(shared);
                if (loop.isPlaying && gauge.Value > 0.01f)
                {
                    if (firstPressure < 0) firstPressure = gauge.Value;
                    bottleId = plugin.Items!.TestNewestPrefab(bottlePrefab);
                    if (bottleId <= previousBottle || !plugin.Items.TestState(bottleId).Exists) return false;
                    if (paritySamples >= 5 && needleProgressObserved && gauge.Value > firstPressure + 0.025f && shared.TestCueCount(0) == 1)
                    {
                        Require(shared.TestCueSoundObserved(0), "Guest native attach one-shot was not observed.");
                        Require(plugin.Items.TestState(bottleId).Values.Value1 > 0, "Guest did not receive real host bottle charge progression.");
                        lastPressure = gauge.Value; Signal(2); phase = 10;
                    }
                }
            }
            else if (phase == 10 && stageAcknowledged)
            {
                CheckParity(shared); plugin.EndPairedTestSession(); Detach();
                Require(loop.isPlaying == originalLoop.Playing && Math.Abs(gauge.Value - originalPressure) < 0.0001f,
                    "Guest disconnect retained the running fixture loop/gauge.");
                phase = 2; next = Time.unscaledTime + 1;
            }
            else if (phase == 3)
            {
                Require(plugin.Session!.LocalPeerId != initialPeer, "Guest did not perform an actual reconnect.");
                var state = shared.TestCanonicalState(station);
                Require(state.Filling && state.PressureValue > lastPressure,
                    "Late-join snapshot did not capture the same still-running native refill.");
                Require(shared.TestCueCount(0) == 0 && shared.TestCueCount(1) == 0,
                    "Late-join replayed historical attach/detach one-shots.");
                CheckParity(shared);
                Require(loop.isPlaying && gauge.Value > lastPressure, "Late-join snapshot did not resume the actual native loop/gauge.");
                next = Time.unscaledTime + 1; phase = 11;
            }
            else if (phase == 11)
            {
                CheckParity(shared);
                Require(loop.isPlaying && shared.TestCueCount(0) == 0, "Late-join loop stopped or fabricated an attach cue.");
                if (Time.unscaledTime >= next)
                {
                    Require(gauge.Needle != null && Quaternion.Angle(originalNeedleRotation, gauge.Needle.transform.localRotation) > 0.5f,
                        "Late-join gauge value did not move the actual native needle.");
                    Debug.Log("MonMP NitrousRefill guest resumed actual loop/gauge from ongoing snapshot without attach history. " + Status); Signal(3); phase = 4;
                }
            }
            else if (phase == 4)
            {
                CheckParity(shared);
                if (!shared.TestCanonicalState(station).Filling && !loop.isPlaying && gauge.Value == 0 && shared.TestCueCount(1) == 1)
                {
                    Require(shared.TestCueCount(0) == 0 && shared.TestCueSoundObserved(1), "Guest future native detach cue was missing or historical attach replayed.");
                    var item = plugin.Items!.TestItem(bottleId) as Part_NitrousBottle;
                    if (item == null || item.Value1 < Capacity - 0.001f || plugin.Items.TestState(bottleId).Values.Value1 < Capacity - 0.001f) return false;
                    lastCharge = item.Value1; Signal(4); phase = 5;
                }
            }
            else if (phase == 5 && stageAcknowledged && !plugin.Items!.TestState(bottleId).Exists)
            {
                Debug.Log("MonMP NitrousRefill guest saw progression, late-join resumed loop, future detach and host full charge; fixture tombstone received. " + Status);
                plugin.EndPairedTestSession(); Detach(); phase = 6; next = Time.unscaledTime + 1;
            }
            return false;
        }

        private void CheckParity(SharedNitrousRefillStation shared)
        {
            var state = shared.TestCanonicalState(station);
            bool matches = state.Filling == loop.isPlaying && Math.Abs(state.PressureValue - gauge.Value) < 0.005f;
            if (!matches)
            {
                if (mismatchSince < 0) mismatchSince = Time.unscaledTime;
                Require(Time.unscaledTime - mismatchSince < 1, "Guest actual gauge/loop failed to converge to carried host state. " + Status);
            }
            else
            {
                mismatchSince = -1;
                if (state.Filling && Time.unscaledTime >= nextSample)
                {
                    Require(gauge.isActiveAndEnabled && gauge.Needle != null && gauge.Needle.activeInHierarchy, "Native gauge needle became inactive.");
                    needleProgressObserved |= Quaternion.Angle(originalNeedleRotation, gauge.Needle.transform.localRotation) > 0.5f;
                    paritySamples++; nextSample = Time.unscaledTime + 0.2f;
                }
            }
        }

        private void AssertOfflineRestoration()
        {
            Require(gauge != null && loop != null && oneShot != null && Math.Abs(gauge.Value - originalPressure) < 0.0001f &&
                loop.isPlaying == originalLoop.Playing && oneShot.isPlaying == originalOneShot.Playing,
                "Disconnect did not restore original native gauge/loop/one-shot after 1 second.");
            Require(gauge.Needle != null && Quaternion.Angle(originalNeedleRotation, gauge.Needle.transform.localRotation) < 2,
                "Disconnect did not return the actual native gauge needle to its original idle angle after 1 second.");
            Require(!Field<bool>(station, "isWorking") && guestNativeSteps == 0 && cashDebits == 0 && GameManager.Get_PlayerStats[4] == originalSpent,
                "Guest disconnect left native refill/wallet effects.");
        }

        private void Attach(WorldChannel channel)
        {
            if (subscribed == channel) return;
            Detach(); subscribed = channel;
            if (host) channel.CommandRequested += Command; else channel.ReplyReceived += Reply;
        }
        private void Detach()
        {
            if (subscribed != null) { subscribed.CommandRequested -= Command; subscribed.ReplyReceived -= Reply; }
            subscribed = null;
        }
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (peer == 0 || entity != ClockState.Entity || payload.Length != 2 || payload[0] != Marker || payload[1] < 1 || payload[1] > 4) return;
            guestPeer = peer; acknowledgment = Math.Max(acknowledgment, payload[1]);
            subscribed!.Reply(peer, entity, new[] { Marker, payload[1] });
        }
        private void Reply(ulong entity, byte[] payload)
        {
            if (entity == ClockState.Entity && payload.Length == 2 && payload[0] == Marker && payload[1] == pendingStage) stageAcknowledged = true;
        }
        private void Signal(byte stage) { pendingStage = stage; stageAcknowledged = false; retryAt = 0; RetryStage(); }
        private void RetryStage()
        {
            if (host || pendingStage == 0 || stageAcknowledged || subscribed == null || Time.unscaledTime < retryAt) return;
            subscribed.Request(ClockState.Entity, new[] { Marker, pendingStage }); retryAt = Time.unscaledTime + 0.5f;
        }

        private void InstallGuards()
        {
            Require(nativeOwner == null, "Another nitrous fixture is active."); nativeOwner = this;
            guard = new Harmony("MonMP.LocalTest.NitrousRefill");
            try
            {
                var factory = AccessTools.Method(typeof(NitrousRefillStation), "RefillBottle", new[] { typeof(Part_NitrousBottle) });
                var step = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
                Require(step != null && step.ReturnType == typeof(bool) && step.GetParameters().Length == 0,
                    "Native RefillBottle MoveNext target has an unsupported shape.");
                var fields = step!.DeclaringType!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                iteratorStation = fields.SingleOrDefault(field => field.FieldType == typeof(NitrousRefillStation));
                iteratorBottle = fields.SingleOrDefault(field => field.FieldType == typeof(Part_NitrousBottle));
                Require(iteratorStation != null && iteratorBottle != null, "Native refill iterator owner/bottle fields are missing or ambiguous.");
                var spent = AccessTools.Method(typeof(Cash_Manager), nameof(Cash_Manager.Event_Spent), new[] { typeof(float), typeof(bool) });
                Require(spent != null, "Native wallet debit target is missing.");
                var enter = AccessTools.Method(typeof(NitrousRefillStation), "OnTriggerEnter", new[] { typeof(Collider) });
                Require(enter != null && enter.ReturnType == typeof(void), "Native station physics trigger target is missing.");
                guard.Patch(step, prefix: new HarmonyMethod(typeof(PairedNitrousRefillTest), nameof(BeforeRefillStep)));
                guard.Patch(spent, postfix: new HarmonyMethod(typeof(PairedNitrousRefillTest), nameof(AfterSpent)));
                guard.Patch(enter, postfix: new HarmonyMethod(typeof(PairedNitrousRefillTest), nameof(AfterTriggerEntered)));
                PatchHealth.VerifyManualPatchInstalled(guard, step); PatchHealth.VerifyManualPatchInstalled(guard, spent!);
                PatchHealth.VerifyManualPatchInstalled(guard, enter!);
            }
            catch { guard.UnpatchSelf(); guard = null; nativeOwner = null; throw; }
        }
        private static void BeforeRefillStep(object __instance)
        {
            var owner = nativeOwner;
            if (owner == null || owner.station == null || (owner.iteratorStation?.GetValue(__instance) as NitrousRefillStation) != owner.station) return;
            if (!owner.host) { owner.guestNativeSteps++; return; }
            if (!owner.staged || owner.bottle == null || (owner.iteratorBottle?.GetValue(__instance) as Part_NitrousBottle) != owner.bottle) return;
            owner.ownRefill = (IEnumerator)__instance; owner.nativeSteps++;
        }
        private static void AfterSpent(Cash_Manager __instance, float __0)
        {
            var owner = nativeOwner;
            if (owner != null && owner.cash != null && __instance == owner.cash && Math.Abs(__0 - Price) < 0.001f) owner.cashDebits++;
        }
        private static void AfterTriggerEntered(NitrousRefillStation __instance, Collider __0)
        {
            var owner = nativeOwner;
            if (owner == null || !owner.host || !owner.staged || __instance != owner.station || owner.bottle == null ||
                __0 != owner.bottle.itemCollider) return;
            owner.triggerEntries++;
            if (owner.ownRefill != null && Field<bool>(__instance, "isWorking")) owner.acceptedTriggerEntries++;
        }

        internal void Cleanup()
        {
            if (cleaned) return;
            var failures = new List<Exception>();
            void Restore(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
            try
            {
                if (host)
                {
                    if (staged && station != null)
                    {
                        Restore(() => { if (ownRefill != null) station.StopCoroutine(ownRefill); });
                        Restore(() => { if (ownEntry != null) station.StopCoroutine(ownEntry); });
                        Restore(() => { Set(station, "isWorking", originalWorking); Set(station, "CooldownTimer", originalCooldown); });
                        Restore(() =>
                        {
                            if (cash == null) throw new InvalidOperationException("Native wallet destroyed before fixture restoration.");
                            Set(cash, "cash", originalCash); GameManager.Get_PlayerStats[4] = cashSpentBefore;
                            if (cashUi != null) cashUi.UpdateCash(originalCash);
                        });
                    }
                    if (bottle != null) Restore(() => Object.Destroy(bottle.gameObject));
                    Restore(() => plugin.Items?.DiscoverSoon());
                }
            }
            finally
            {
                try
                {
                    // Setup can fail after native Start has reset a previously hidden gauge.
                    // Restore this baseline even when no entry/refill was staged.
                    Restore(() => { if (gauge != null) gauge.Value = originalPressure; });
                    Restore(() => { if (gauge != null && gauge.Needle != null && originalNeedleCaptured)
                        gauge.Needle.transform.localRotation = originalNeedleRotation; });
                    Restore(() => { if (originalLoop != null) originalLoop.Restore(loop); });
                    Restore(() => { if (originalOneShot != null) originalOneShot.Restore(oneShot); });
                    // Hide the activated parent first, then restore sibling activeSelf flags:
                    // unrelated dormant objects must never wake during fixture teardown either.
                    for (int i = activatedObjects.Count - 1; i >= 0; i--)
                    {
                        var go = activatedObjects[i];
                        if (go != null) Restore(() => go.SetActive(originalActivity[go]));
                    }
                    foreach (var pair in siblingActivity)
                        if (pair.Key != null) Restore(() => pair.Key.SetActive(pair.Value));
                    Restore(() => { if (station != null) station.enabled = originalStationEnabled; });
                    Restore(() => { if (gauge != null) gauge.enabled = originalGaugeEnabled; });
                }
                finally
                {
                    Detach(); guard?.UnpatchSelf(); guard = null;
                    if (nativeOwner == this) nativeOwner = null;
                    cleaned = true;
                }
            }
            if (failures.Count > 0) throw new AggregateException("Nitrous refill fixture restoration failed.", failures);
        }

        private static T Field<T>(object target, string name) => (T)AccessTools.Field(target.GetType(), name).GetValue(target);
        private static void Set(object target, string name, object value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
        private static void Require([DoesNotReturnIf(false)] bool ok, string message)
        { if (!ok) throw new InvalidOperationException("Nitrous refill test: " + message); }
    }
}
