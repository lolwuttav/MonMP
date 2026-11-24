using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // VP-02: complete the host's original native rim iterator while a guest's real
    // replay is live, reuse the machine for de-rim, then disconnect during another rim.
    // This invokes the save-settlement adapter; it does not claim disk/reload coverage.
    internal sealed class PairedTireReplayTest
    {
        private const byte Marker = 0x75, Domain = 0x54;
        private const float ControlRetryInterval = .5f, ControlTimeout = 60f;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly WorldChannel fixtureChannel;
        private readonly Vector3 originalPosition;
        private readonly Quaternion originalCamera;
        private readonly float[] originalStatistics;
        private readonly List<Action> restore = new List<Action>();
        private readonly List<InteractableItems> fixtures = new List<InteractableItems>();
        private readonly Dictionary<ulong, (Transform? Parent, Vector3 Position, Quaternion Rotation)> initialHandles = new Dictionary<ulong, (Transform?, Vector3, Quaternion)>();
        private readonly float started = Time.unscaledTime;
        private Furniture_TireChanger_Manual machine = null!;
        private Furniture_TireChanger_WheelDetection rimDetector = null!, deRimDetector = null!;
        private Furniture_TireChanger_Handle handle = null!;
        private Transform handleTransform = null!;
        private Transform? originalHandleParent;
        private Vector3 originalHandlePosition;
        private Quaternion originalHandleRotation;
        private Part_Driveline_Wheel wheel = null!;
        private Item_Driveline_Tires tire = null!;
        private HashSet<Item_Driveline_Tires> tiresBefore = new HashSet<Item_Driveline_Tires>();
        private ulong machineId, wheelId, tireId;
        private uint peer;
        private int phase, order, receivedOrder, ack, charge, cashBefore, controlRetries, signalRetries;
        private byte pendingSignal;
        private byte[]? pendingControl;
        private float controlStarted, nextControlRetry, signalStarted, nextSignalRetry;
        private float next, rimStarted, deRimHandleSettleStarted = -1f;
        private bool cleaned, selected, guestReady;
        internal string Status => "tire replay phase=" + phase + "; order=" + order + "; ack=" + ack + "; machine=" + machineId.ToString("X") +
            "; wheel=" + wheelId.ToString("X") + "; tire=" + tireId.ToString("X") + "; nativeWorking=" + (machine != null && machine.Get_MachineIsWorking) +
            "; guestReady=" + guestReady + "; receivedOrder=" + receivedOrder + "; pendingControl=" + (pendingControl?[2] ?? 0) +
            "; controlRetries=" + controlRetries + "; pendingSignal=" + pendingSignal + "; signalRetries=" + signalRetries;
        internal PairedTireReplayTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            originalPosition = gameplay.PlayerWalking.transform.position; originalCamera = gameplay.Player.mainCamera.transform.rotation;
            originalStatistics = (float[])GameManager.Get_PlayerStats.Clone();
            fixtureChannel = plugin.World!.Channel;
            fixtureChannel.CommandRequested += OnCommand; fixtureChannel.ReplyReceived += OnReply;
        }
        private void OnCommand(uint sender, ulong entity, byte[] payload)
        {
            if (!plugin.TestOptions.IsHost || sender == 0 || entity != ProfileRestoreChannel.Entity || payload.Length != 4 || payload[0] != Marker || payload[1] != Domain || payload[3] != 0 || (peer != 0 && sender != peer)) return;
            peer = sender;
            if (payload[2] == 99) { guestReady = true; return; }
            // Only the outstanding order can advance this phase. A delayed duplicate
            // ACK must not replace a newer ACK or acknowledge a later native action.
            if (payload[2] < 1 || payload[2] > 4 || pendingControl == null || payload[2] != pendingControl[2]) return;
            ack = payload[2];
            pendingControl = null;
        }
        private void OnReply(ulong entity, byte[] payload)
        {
            if (plugin.TestOptions.IsHost || entity != ClockState.Entity || payload.Length != 28 || payload[0] != Marker || payload[1] != Domain) return;
            byte incoming = payload[2];
            if (incoming < 1 || incoming > 5 || incoming < receivedOrder) return;
            ulong nextMachine = BitConverter.ToUInt64(payload, 4), nextWheel = BitConverter.ToUInt64(payload, 12), nextTire = BitConverter.ToUInt64(payload, 20);
            if (incoming == receivedOrder)
            {
                Require(machineId == nextMachine && wheelId == nextWheel && tireId == nextTire, "A repeated control changed its native fixture identities.");
                // A retry is transport bookkeeping, not another native action or a
                // reset of the guest's already completed order/handle wait.
                if (pendingSignal == incoming) SendPendingSignal();
                return;
            }
            Require(incoming == receivedOrder + 1 && nextMachine != 0 && nextWheel != 0 && nextTire != 0,
                "A native replay control skipped an order or omitted its fixture identities.");
            pendingSignal = 0;
            receivedOrder = order = incoming; machineId = nextMachine; wheelId = nextWheel; tireId = nextTire;
        }
        private void Tell(byte value)
        {
            var payload = new byte[28]; payload[0] = Marker; payload[1] = Domain; payload[2] = value;
            Array.Copy(BitConverter.GetBytes(machineId), 0, payload, 4, 8); Array.Copy(BitConverter.GetBytes(wheelId), 0, payload, 12, 8); Array.Copy(BitConverter.GetBytes(tireId), 0, payload, 20, 8);
            pendingControl = payload; controlStarted = Time.unscaledTime; controlRetries = 0;
            SendPendingControl();
        }
        private void SendPendingControl()
        {
            if (pendingControl == null) return;
            fixtureChannel.Reply(peer, ClockState.Entity, pendingControl);
            controlRetries++; nextControlRetry = Time.unscaledTime + ControlRetryInterval;
        }
        private void Signal(byte value)
        {
            pendingSignal = value; signalStarted = Time.unscaledTime; signalRetries = 0;
            SendPendingSignal();
        }
        private void SendPendingSignal()
        {
            if (pendingSignal == 0) return;
            // ProfileRestoreChannel's immutable command record avoids racing live
            // clock revisions. All repeats are namespaced, idempotent fixture ACKs.
            fixtureChannel.Request(ProfileRestoreChannel.Entity, new[] { Marker, Domain, pendingSignal, (byte)0 });
            signalRetries++; nextSignalRetry = Time.unscaledTime + ControlRetryInterval;
        }
        private void RetryControl()
        {
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0 && !guestReady) Require(Time.unscaledTime - started < ControlTimeout, "Guest readiness was not acknowledged: " + Status);
                if (phase == 2) Require(Time.unscaledTime - rimStarted < 7, "Host did not finish early enough to exercise the guest's live tire dependency.");
                if (pendingControl == null) return;
                Require(Time.unscaledTime - controlStarted < ControlTimeout, "Native replay control was not acknowledged: " + Status);
                if (Time.unscaledTime >= nextControlRetry) SendPendingControl();
            }
            else if (pendingSignal != 0)
            {
                Require(Time.unscaledTime - signalStarted < ControlTimeout, "Native replay signal did not advance the host: " + Status);
                if (Time.unscaledTime >= nextSignalRetry) SendPendingSignal();
            }
        }
        internal bool Tick()
        {
            Require(Time.unscaledTime - started < 150, "Timed out: " + Status);
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true || !plugin.World.PersonalProfileReady) return false;
            Require(ReferenceEquals(plugin.World.Channel, fixtureChannel), "The fixture channel changed during native replay.");
            RetryControl();
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }
        private bool Host()
        {
            var world = plugin.World!; var items = world.Items;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2 || !guestReady) return false;
                peer = plugin.Session.Peers.Values.Single(value => value.Id != 0).Id;
                SelectHostMachine();
                var cash = Object.FindObjectOfType<Cash_Manager>(); int previous = cash.GetCash;
                restore.Add(() => AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(cash, previous));
                cashBefore = Math.Max(previous, 100000); AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(cash, cashBefore);
                charge = Field<bool>(machine, "Payant") ? 1500 : 0;
                if (Field<Stores>(machine, "StoreParentIfPayant") is Stores store)
                { bool open = store.isOpen; restore.Add(() => store.isOpen = open); store.isOpen = true; }
                wheelId = items.TestSpawnInflationWheel(machine.transform.position + Vector3.up * 2);
                wheel = (Part_Driveline_Wheel)items.TestItem(wheelId)!; wheel.IntValue1 = 0; wheel.Value1 = 0;
                tire = Object.Instantiate(Singleton<GameAssets>.i.Get_Tires((TireType)1), machine.transform.position + Vector3.up * 3, Quaternion.identity).GetComponent<Item_Driveline_Tires>();
                tire.isSpawnFromTheSaveLoadSystem = true; tire.CreateBasics(); if (tire.rb != null) tire.rb.isKinematic = true;
                fixtures.Add(wheel); fixtures.Add(tire); items.DiscoverSoon(); phase = 1; next = Time.unscaledTime + .5f;
            }
            else if (phase == 1)
            {
                tireId = items.TestBoundId(tire); if (tireId == 0) { items.DiscoverSoon(); return false; }
                StartRim(); Require(Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - charge, "Initial native mount did not charge exactly once.");
                Tell(1); phase = 2;
            }
            else if (phase == 2 && ack == 1)
            {
                Require(Time.unscaledTime - rimStarted < 7, "Host did not finish early enough to exercise the guest's live tire dependency.");
                Require(machine.Get_MachineIsWorking && NativeSaveProduction.Pending(machine, "MachineRim"), "Host rim iterator finished before the early-settlement trigger.");
                Require(world.TireChanger.SettleForSave(), "Host native rim iterator did not settle.");
                Require(!machine.Get_MachineIsWorking && wheel.IntValue1 == 1 && wheel.Value1 == 0 && Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - charge, "Host settlement lost the fitted tire or charged the mount again.");
                Debug.Log("MONMP_TIRE_REPLAY_EARLY_SETTLEMENT: guest acknowledged active native replay; host retained iterator finished at " + (Time.unscaledTime - rimStarted).ToString("F2") + "s; native wheel type=1; charge=" + charge + "; no checkpoint write/reload in this suite");
                Tell(2); phase = 3; ack = 0;
            }
            else if (phase == 3 && ack == 2)
            {
                tiresBefore = new HashSet<Item_Driveline_Tires>(SceneTires());
                Trigger(deRimDetector, wheel); Require(machine.Get_Wheel(true) == wheel, "Native de-rim detector did not mount the released wheel.");
                handle.MouseDown();
                Require(machine.Get_MachineIsWorking && NativeSaveProduction.Pending(machine, "MachineDeRim"), "Machine could not start a second native cycle after early completion.");
                Require(Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - charge * 2, "Native de-rim did not charge exactly once.");
                Tell(3); phase = 4; ack = 0;
            }
            else if (phase == 4 && ack == 3) phase = 5;
            else if (phase == 5 && !machine.Get_MachineIsWorking)
            {
                var outputs = SceneTires().Where(value => !tiresBefore.Contains(value)).ToArray();
                if (outputs.Length == 0 || items.TestBoundId(outputs[0]) == 0) { items.DiscoverSoon(); return false; }
                Require(outputs.Length == 1 && wheel.IntValue1 == 0 && machine.Get_Wheel(true) == null && machine.Get_Wheel(false) == null, "Native de-rim did not release exactly one bare wheel and one tire.");
                tire = outputs[0]; tireId = items.TestBoundId(tire); fixtures.Add(tire);
                Tell(4); phase = 6; ack = 0;
            }
            else if (phase == 6 && ack == 4)
            {
                StartRim(); Require(Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - charge * 3, "Third native cycle did not charge once.");
                Tell(5); phase = 7;
            }
            else if (phase == 7 && plugin.Session!.Peers.Count == 1)
            {
                Require(world.TireChanger.SettleForSave() && !machine.Get_MachineIsWorking && wheel.IntValue1 == 1, "Guest departure stranded the host's already-paid rim operation.");
                Require(Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - charge * 3, "Replay/teardown changed the host's three native cycle charges.");
                Debug.Log("MONMP_TIRE_REPLAY_HOST: early completion, native machine reuse/de-rim output and disconnect during third rim completed; exact total debit=" + charge * 3);
                Cleanup(); return true;
            }
            return false;
        }
        private bool Guest()
        {
            if (!guestReady)
            {
                var initial = (IDictionary)AccessTools.Field(typeof(SharedTireChanger), "machines").GetValue(plugin.World!.TireChanger);
                foreach (DictionaryEntry entry in initial)
                {
                    var native = Field<Furniture_TireChanger_Manual>(entry.Value!, "Native");
                    Require(!native.Get_MachineIsWorking, "Guest baseline must be captured before any native replay begins.");
                    var nativeHandle = Field<Transform>(native, "Handle");
                    initialHandles[(ulong)entry.Key] = (nativeHandle.parent, nativeHandle.position, nativeHandle.rotation);
                }
                Signal(99); guestReady = true;
            }
            if (order == 0) return false;
            var world = plugin.World!; var items = world.Items;
            if (!selected) { SelectMachine(machineId); selected = true; }
            if (items.TestItem(wheelId) is not Part_Driveline_Wheel sharedWheel) return false;
            wheel = sharedWheel;
            if (order == 1 && machine.Get_MachineIsWorking && NativeSaveProduction.Pending(machine, "MachineRim"))
            {
                Require(items.TestState(tireId).Exists && items.TestItem(tireId) != null, "Native replay started without its authoritative tire dependency.");
                Signal(1); order = 0;
            }
            else if (order == 2 && !machine.Get_MachineIsWorking && !items.TestState(tireId).Exists)
            {
                Require(!NativeSaveProduction.Pending(machine, "MachineRim") && wheel.IntValue1 == 1 && wheel.Value1 == 0, "Host completion left the guest native replay pending or its fitted wheel incorrect.");
                VerifyHandleRestored(); Require(machine.Get_Wheel(true) == null && machine.Get_Wheel(false) == null, "Completed replay retained a native wheel slot.");
                Debug.Log("MONMP_TIRE_REPLAY_GUEST: host tombstone accepted while native rim replay was live; replay finished, native machine idle and handle restored");
                Signal(2); order = 0;
            }
            else if (order == 3 && machine.Get_MachineIsWorking && NativeSaveProduction.Pending(machine, "MachineDeRim"))
            { Signal(3); order = 0; }
            else if (order == 4 && !machine.Get_MachineIsWorking && wheel.IntValue1 == 0 && items.TestItem(tireId) is Item_Driveline_Tires)
            {
                Require(!NativeSaveProduction.Pending(machine, "MachineDeRim"), "Native de-rim replay remained pending after machine reuse.");
                // MachineDeRim finishes its two waits independently of the native Animator.
                // Host-idle reconciliation can finish those waits while the guest's DeRim
                // animation still owns the handle. Bound that presentation tail, then retain
                // the same original parent/pose assertion used for rim completion/teardown.
                if (!HandleRestored())
                {
                    if (deRimHandleSettleStarted < 0)
                    {
                        deRimHandleSettleStarted = Time.unscaledTime;
                        Debug.Log("MONMP_TIRE_REPLAY_DERIM_HANDLE_WAIT: " + HandleState());
                    }
                    if (Time.unscaledTime - deRimHandleSettleStarted < 3f) return false;
                }
                VerifyHandleRestored();
                if (deRimHandleSettleStarted >= 0)
                    Debug.Log("MONMP_TIRE_REPLAY_DERIM_HANDLE_SETTLED: waited=" + (Time.unscaledTime - deRimHandleSettleStarted).ToString("F3") + "s; " + HandleState());
                Signal(4); order = 0;
            }
            else if (order == 5 && machine.Get_MachineIsWorking && NativeSaveProduction.Pending(machine, "MachineRim"))
            {
                Require(items.TestState(tireId).Exists, "Disconnect case did not retain a live tire before teardown.");
                plugin.EndPairedTestSession();
                Require(!machine.Get_MachineIsWorking && machine.Get_Wheel(true) == null && machine.Get_Wheel(false) == null &&
                    !NativeSaveProduction.Pending(machine, "MachineRim") && !NativeSaveProduction.Pending(machine, "MachineDeRim"), "Disconnect did not cancel/reset the native replay and slots.");
                VerifyHandleRestored();
                Debug.Log("MONMP_TIRE_REPLAY_GUEST_DISCONNECT: native replay cancelled before shared input destruction; no pending iterator, working flag or slot remained; handle restored");
                Cleanup(); return true;
            }
            return false;
        }
        private void SelectHostMachine()
        {
            var map = (IDictionary)AccessTools.Field(typeof(SharedTireChanger), "machines").GetValue(plugin.World!.TireChanger);
            var candidates = new List<(ulong Id, Furniture_TireChanger_Manual Native)>();
            foreach (DictionaryEntry entry in map) candidates.Add(((ulong)entry.Key, Field<Furniture_TireChanger_Manual>(entry.Value!, "Native")));
            var available = candidates.Where(value => !value.Native.Get_MachineIsWorking && value.Native.Get_Wheel(true) == null && value.Native.Get_Wheel(false) == null && value.Native.gameObject.activeInHierarchy)
                .OrderBy(value => Field<bool>(value.Native, "Payant") ? 0 : 1).ThenBy(value => value.Id).ToArray();
            Require(available.Length > 0, "No idle empty registered native tire changer is available.");
            machineId = available[0].Id; SelectMachine(machineId); selected = true;
        }
        private void SelectMachine(ulong id)
        {
            var map = (IDictionary)AccessTools.Field(typeof(SharedTireChanger), "machines").GetValue(plugin.World!.TireChanger);
            Require(map.Contains(id), "Host-selected tire changer is missing from the guest catalog.");
            machine = Field<Furniture_TireChanger_Manual>(map[id]!, "Native");
            var detectors = Object.FindObjectsOfType<Furniture_TireChanger_WheelDetection>(true).Where(value => Field<Furniture_TireChanger_Manual>(value, "TireChanger_Manual") == machine).ToArray();
            rimDetector = detectors.First(value => !Field<bool>(value, "isDeRim")); deRimDetector = detectors.First(value => Field<bool>(value, "isDeRim"));
            handle = Object.FindObjectsOfType<Furniture_TireChanger_Handle>(true).First(value => Field<Furniture_TireChanger_Manual>(value, "TireChanger_Manual") == machine);
            handleTransform = Field<Transform>(machine, "Handle"); originalHandleParent = handleTransform.parent;
            originalHandlePosition = handleTransform.position; originalHandleRotation = handleTransform.rotation;
            if (!plugin.TestOptions.IsHost && initialHandles.TryGetValue(id, out var baseline))
            { originalHandleParent = baseline.Parent; originalHandlePosition = baseline.Position; originalHandleRotation = baseline.Rotation; }
        }
        private void StartRim()
        {
            Require(!machine.Get_MachineIsWorking && wheel.IntValue1 == 0 && tire != null && tire.IntValue1 == 1, "Native rim prerequisites changed before admission.");
            Trigger(rimDetector, wheel); Require(machine.Get_Wheel(false) == wheel, "Native rim detector did not mount the fixture bare wheel.");
            Trigger(rimDetector, tire!);
            Require(machine.Get_MachineIsWorking && NativeSaveProduction.Pending(machine, "MachineRim"), "Native rim detector did not start a retained producer.");
            rimStarted = Time.unscaledTime;
        }
        private static void Trigger(Furniture_TireChanger_WheelDetection detector, InteractableItems input)
        {
            var trigger = detector.GetComponent<Collider>();
            if (input.rb != null) { input.rb.isKinematic = true; input.rb.velocity = Vector3.zero; input.rb.angularVelocity = Vector3.zero; }
            input.transform.position = trigger.bounds.center; Physics.SyncTransforms();
            AccessTools.Method(typeof(Furniture_TireChanger_WheelDetection), "OnTriggerEnter").Invoke(detector, new object[] { input.itemCollider });
        }
        private bool HandleRestored() => handleTransform.parent == originalHandleParent && Vector3.Distance(handleTransform.position, originalHandlePosition) < .025f && Quaternion.Angle(handleTransform.rotation, originalHandleRotation) < .5f;
        private void VerifyHandleRestored() => Require(HandleRestored(), "Native handle parent/pose did not return to the original state. " + HandleState());
        private string HandleState()
        {
            var animator = Field<Animator>(machine, "animator");
            string animation = "animator=<null>";
            if (animator != null)
            {
                animation = "animatorEnabled=" + animator.enabled + "; layers=" + animator.layerCount;
                if (animator.layerCount > 0)
                {
                    var state = animator.GetCurrentAnimatorStateInfo(0);
                    bool transitioning = animator.IsInTransition(0);
                    animation += "; stateHash=" + state.fullPathHash + "; normalizedTime=" + state.normalizedTime.ToString("F4") +
                        "; stateLength=" + state.length.ToString("F4") + "; transitioning=" + transitioning;
                    if (transitioning)
                    {
                        var nextState = animator.GetNextAnimatorStateInfo(0);
                        animation += "; nextStateHash=" + nextState.fullPathHash + "; nextNormalizedTime=" + nextState.normalizedTime.ToString("F4");
                    }
                }
            }
            return "parent=" + TransformIdentity(handleTransform.parent) + "; expectedParent=" + TransformIdentity(originalHandleParent) +
                "; position=" + handleTransform.position.ToString("F4") + "; expectedPosition=" + originalHandlePosition.ToString("F4") +
                "; positionError=" + Vector3.Distance(handleTransform.position, originalHandlePosition).ToString("F4") +
                "; rotation=" + handleTransform.eulerAngles.ToString("F4") + "; expectedRotation=" + originalHandleRotation.eulerAngles.ToString("F4") +
                "; rotationError=" + Quaternion.Angle(handleTransform.rotation, originalHandleRotation).ToString("F4") +
                "; nativeOriginalParent=" + TransformIdentity(Field<Transform>(machine, "Handle_OriginalParent")) +
                "; nativeOriginalPosition=" + Field<Vector3>(machine, "Handle_OriginalPosition").ToString("F4") +
                "; nativeOriginalRotation=" + Field<Vector3>(machine, "Handle_OriginalRotation").ToString("F4") + "; " + animation;
        }
        private static string TransformIdentity(Transform? value) => value == null ? "<null>" : value.name + "#" + value.GetInstanceID();
        private static Item_Driveline_Tires[] SceneTires() => Object.FindObjectsOfType<Item_Driveline_Tires>(true).Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy).ToArray();
        private static T Field<T>(object target, string name) => (T)AccessTools.Field(target.GetType(), name).GetValue(target);
        internal void Cleanup()
        {
            if (cleaned) return; cleaned = true;
            fixtureChannel.CommandRequested -= OnCommand; fixtureChannel.ReplyReceived -= OnReply;
            pendingControl = null; pendingSignal = 0;
            if (plugin.TestOptions.IsHost)
            {
                plugin.World?.TireChanger.SettleForSave();
                if (machine != null)
                    foreach (bool deRim in new[] { true, false })
                        if (machine.Get_Wheel(deRim) is Part_Driveline_Wheel mounted && fixtures.Contains(mounted))
                        { AccessTools.Method(typeof(Furniture_TireChanger_Manual), "RemoveWheelFromMachine").Invoke(machine, new object[] { mounted }); AccessTools.Field(typeof(Furniture_TireChanger_Manual), deRim ? "Wheel_DeRim" : "Wheel_Rim").SetValue(machine, null); }
                foreach (var item in fixtures.Distinct()) if (item != null) Object.Destroy(item.gameObject);
                for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
                Array.Copy(originalStatistics, GameManager.Get_PlayerStats, originalStatistics.Length);
            }
            gameplay.PlayerWalking.transform.position = originalPosition; gameplay.Get_Player_Rigidbody.position = originalPosition; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
            gameplay.Player.mainCamera.transform.rotation = originalCamera;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Tire replay: " + reason); }
    }
}
