using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Tire and wheel objects still use SharedItems. This adapter owns the native machine's
    // private wheel slots, routes guest callbacks to the host, and replays approved native
    // cycles locally so the stock handle/turntable animations stay visible to every peer.
    internal sealed class SharedTireChanger : IDisposable
    {
        private const float PoseMaxAge = 2f;
        private const float InteractionRange = 5f;
        private const float HostOverlapMargin = 0.6f;
        // A carried part sits in front of the camera, a hand's width short of where the native trigger reaches (#201).
        private const float GuestOverlapMargin = 0.45f;
        private const float BudgetWindow = 3f;
        private const int RequestsPerWindow = 12;
        private const int ReplayCapacity = 2048;

        private sealed class Machine
        {
            public ulong Id;
            public string Path = "";
            public Furniture_TireChanger_Manual Native = null!;
            public Furniture_TireChanger_WheelDetection[] DeRimDetectors = Array.Empty<Furniture_TireChanger_WheelDetection>();
            public Furniture_TireChanger_WheelDetection[] RimDetectors = Array.Empty<Furniture_TireChanger_WheelDetection>();
            public TireChangerState State = TireChangerState.Initial;
            public uint ReplayedOperationSequence;
            public Replay? ActiveReplay;
        }

        private sealed class Replay
        {
            public TireChangerOperation Operation;
            public Part_Driveline_Wheel Wheel = null!;
            public Item_Driveline_Tires? Tire;
            public bool WheelRemoved, TireRemoved;
            public Transform Handle = null!;
            public Transform? HandleParent;
            public Vector3 HandlePosition, RotatingPosition;
            public Quaternion HandleRotation;
        }

        private static readonly FieldInfo HandleManualField = RequiredField(typeof(Furniture_TireChanger_Handle), "TireChanger_Manual");
        private static readonly FieldInfo DetectorManualField = RequiredField(typeof(Furniture_TireChanger_WheelDetection), "TireChanger_Manual");
        private static readonly FieldInfo DetectorDeRimField = RequiredField(typeof(Furniture_TireChanger_WheelDetection), "isDeRim");
        private static readonly FieldInfo PayantField = RequiredField(typeof(Furniture_TireChanger_Manual), "Payant");
        private static readonly FieldInfo StoreField = RequiredField(typeof(Furniture_TireChanger_Manual), "StoreParentIfPayant");
        private static readonly FieldInfo DeRimWheelField = RequiredField(typeof(Furniture_TireChanger_Manual), "Wheel_DeRim");
        private static readonly FieldInfo RimWheelField = RequiredField(typeof(Furniture_TireChanger_Manual), "Wheel_Rim");
        private static readonly FieldInfo RimRotationField = RequiredField(typeof(Furniture_TireChanger_Manual), "RotatingWhileAddingTire");
        private static readonly FieldInfo HandleField = RequiredField(typeof(Furniture_TireChanger_Manual), "Handle");
        private static readonly FieldInfo WorkingField = RequiredField(typeof(Furniture_TireChanger_Manual), "MachineIsWorking");
        private static readonly FieldInfo CooldownField = RequiredField(typeof(Furniture_TireChanger_Manual), "CooldownBeforePlacingAnotherWheel");
        private static readonly MethodInfo RemoveWheelMethod = RequiredMethod(typeof(Furniture_TireChanger_Manual), "RemoveWheelFromMachine", typeof(Part_Driveline_Wheel));
        private static readonly MethodInfo TriggerMethod = RequiredMethod(typeof(Furniture_TireChanger_WheelDetection), "OnTriggerEnter", typeof(Collider));

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly Dictionary<ulong, Machine> machines = new Dictionary<ulong, Machine>();
        private readonly Dictionary<Furniture_TireChanger_Manual, Machine> byMachine = new Dictionary<Furniture_TireChanger_Manual, Machine>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Started, int Count)> budgets = new Dictionary<uint, (float, int)>();
        private readonly HashSet<(uint Peer, Guid Operation)> replayed = new HashSet<(uint, Guid)>();
        private readonly Queue<(uint Peer, Guid Operation)> replayOrder = new Queue<(uint, Guid)>();
        private readonly Dictionary<(ulong Machine, TireChangerAction Action, ulong Item), float> pending =
            new Dictionary<(ulong, TireChangerAction, ulong), float>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextCapture;
        private bool disposed;

        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => machines.Values.All(machine => !machine.Native.Get_MachineIsWorking);
        internal bool SettleForSave()
        {
            if (!host || disposed) return true;
            bool complete = true;
            foreach (var machine in machines.Values)
            {
                if (machine.Native == null) continue;
                if (machine.Native.Get_MachineIsWorking)
                {
                    // Only one native cycle can run on a machine. Do not start another cycle:
                    // the original iterator retains the paid operation and the consumed tire.
                    bool finished = NativeSaveProduction.Finish(machine.Native, "MachineRim") ||
                        NativeSaveProduction.Finish(machine.Native, "MachineDeRim");
                    complete &= finished && !machine.Native.Get_MachineIsWorking;
                }
                ObserveNative(machine);
            }
            items.DiscoverSoon();
            return complete;
        }

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }
#endif


        // Why each request went through or not, for shared-tire-changer.tsv: the machine has no paired
        // suite, and three retests of #201 gave no evidence of which check refused a held part.
        private readonly string? diagnostics;
        private readonly List<string> trace = new List<string> { "time\tside\tevent" };
        private string lastTrace = "";
        private void Trace(string text)
        {
            if (diagnostics == null || text == lastTrace) return;
            lastTrace = text;
            trace.Add(Time.unscaledTime.ToString("F1") + "\t" + (host ? "host" : "guest") + "\t" + text);
            if (trace.Count > 500) trace.RemoveAt(1);
            try {
#if MONMP_DEVELOPMENT
File.WriteAllLines(Path.Combine(diagnostics, "shared-tire-changer.tsv"), trace);
#endif
 }
            catch { /* diagnostics only */ }
        }
        private void Reject(string why) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 Trace("rejected " + why); }

        internal SharedTireChanger(Gameplay gameplay, bool host, SharedItems items, string? diagnostics = null)
        {
            this.diagnostics = diagnostics;
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            ValidateNativeLayout();

            var nativeMachines = Object.FindObjectsOfType<Furniture_TireChanger_Manual>(true)
                .Where(machine => machine != null && machine.gameObject.scene.IsValid() && machine.gameObject.scene.isLoaded)
                .Select(machine => new KeyValuePair<string, Furniture_TireChanger_Manual>(
                    ScenePath(machine.gameObject.scene) + "/" + StablePath(machine.transform), machine))
                .ToArray();
            var unique = StaticIdentityResolver.UniqueByPath(nativeMachines, out var ambiguous);
            if (ambiguous.Length != 0)
                throw new InvalidOperationException("Ambiguous tire-changer scene paths: " + string.Join(", ", ambiguous));

            var detectors = Object.FindObjectsOfType<Furniture_TireChanger_WheelDetection>(true)
                .Where(detector => detector != null && detector.gameObject.scene.IsValid() && detector.gameObject.scene.isLoaded)
                .ToArray();
            foreach (var pair in unique.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var machine = new Machine
                {
                    Id = TireChangerIdentity.Entity(ScenePath(pair.Value.gameObject.scene), StablePath(pair.Value.transform)),
                    Path = pair.Key,
                    Native = pair.Value,
                    DeRimDetectors = detectors.Where(detector =>
                        ReferenceEquals(DetectorManualField.GetValue(detector), pair.Value) && IsDeRim(detector)).ToArray(),
                    RimDetectors = detectors.Where(detector =>
                        ReferenceEquals(DetectorManualField.GetValue(detector), pair.Value) && !IsDeRim(detector)).ToArray()
                };
                if (machines.ContainsKey(machine.Id))
                    throw new InvalidOperationException("Tire-changer stable identity collision at " + pair.Key);
                if (machine.DeRimDetectors.Length == 0 || machine.RimDetectors.Length == 0)
                    throw new InvalidOperationException("Tire changer is missing a native wheel detector: " + pair.Key);
                machine.State = host ? InitialHostState(machine) : TireChangerState.Initial;
                machines.Add(machine.Id, machine);
                byMachine.Add(machine.Native, machine);
            }
        }

        private static FieldInfo RequiredField(Type owner, string name) => AccessTools.Field(owner, name) ??
            throw new InvalidOperationException("Unsupported native tire-changer field: " + owner.Name + "." + name);
        private static MethodInfo RequiredMethod(Type owner, string name, params Type[] args) => AccessTools.Method(owner, name, args) ??
            throw new InvalidOperationException("Unsupported native tire-changer method: " + owner.Name + "." + name);
        private static void ValidateNativeLayout()
        {
            if (HandleManualField.FieldType != typeof(Furniture_TireChanger_Manual) ||
                DetectorManualField.FieldType != typeof(Furniture_TireChanger_Manual) || DetectorDeRimField.FieldType != typeof(bool) ||
                PayantField.FieldType != typeof(bool) || DeRimWheelField.FieldType != typeof(Part_Driveline_Wheel) ||
                RimWheelField.FieldType != typeof(Part_Driveline_Wheel) || RimRotationField.FieldType != typeof(Transform) ||
                HandleField.FieldType != typeof(Transform) || WorkingField.FieldType != typeof(bool) || CooldownField.FieldType != typeof(float))
                throw new InvalidOperationException("Unsupported native tire-changer state layout.");
        }

        private static string StablePath(Transform transform)
        {
            var segments = new List<string>();
            // Ordinal among same-named siblings: save progress inserts or reorders other children
            // (a menu-joining guest registers from a fresh world), which shifts raw sibling indices.
            for (var current = transform; current != null; current = current.parent)
                segments.Add(StaticId.Segment(current.name, SameNameOrdinal(current)));
            segments.Reverse();
            return string.Join("/", segments);
        }

        internal string? PathOf(ulong id) => machines.TryGetValue(id, out var machine) ? machine.Path : null;

        private static int SameNameOrdinal(Transform transform)
        {
            int ordinal = 0;
            if (transform.parent != null)
            {
                for (int i = 0; i < transform.GetSiblingIndex(); i++)
                    if (transform.parent.GetChild(i).name == transform.name) ordinal++;
            }
            else
            {
                foreach (var root in transform.gameObject.scene.GetRootGameObjects())
                {
                    if (root.transform == transform) break;
                    if (root.name == transform.name) ordinal++;
                }
            }
            return ordinal;
        }

        private static string ScenePath(UnityEngine.SceneManagement.Scene scene) =>
            scene.path.Length == 0 ? scene.name : scene.path;

        private static bool IsDeRim(Furniture_TireChanger_WheelDetection detector) => (bool)DetectorDeRimField.GetValue(detector);
        private static bool Payant(Furniture_TireChanger_Manual manual) => (bool)PayantField.GetValue(manual);
        // The garage's paid machines charge the shared wallet $15 for a de-rim cycle or a tire mount,
        // exactly as native Use/AddTireToMachine do, and only while the garage is open (#122).
        private const float PaidCyclePrice = 15f;
        private static bool CanPay(Furniture_TireChanger_Manual manual, bool showError)
        {
            if (!Payant(manual)) return true;
            string? error = null;
            if (StoreField.GetValue(manual) is Stores store && store != null && !store.isStoreOpen()) error = "ERROR_StoreClosed";
            // A guest's native affordability check is held false to block local purchases; the shared
            // wallet's balance is still current on every machine (#201).
            else if (Singleton<Cash_Manager>.i.GetCash < PaidCyclePrice * 100f) error = "ERROR_NotEnoughCash";
            if (error != null && showError) Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText(error));
            return error == null;
        }
        private static void SetWheel(Furniture_TireChanger_Manual manual, bool deRim, Part_Driveline_Wheel? wheel) =>
            (deRim ? DeRimWheelField : RimWheelField).SetValue(manual, wheel);

        private TireChangerState InitialHostState(Machine machine)
        {
            var deRim = machine.Native.Get_Wheel(true);
            var rim = machine.Native.Get_Wheel(false);
            ulong deRimId = deRim == null ? 0 : items.EntityOf(deRim);
            ulong rimId = rim == null ? 0 : items.EntityOf(rim);
            if ((deRim != null && deRimId == 0) || (rim != null && rimId == 0))
                throw new InvalidOperationException("An initially mounted tire-changer wheel is not registered as a shared item: " + machine.Path);
            if (machine.Native.Get_MachineIsWorking)
                throw new InvalidOperationException("Wait for the tire changer to finish before hosting or joining.");
            return new TireChangerState(deRimId, rimId, 0, 0, TireChangerOperation.None, false);
        }

        internal IEnumerable<WorldRecord> InitialRecords() => machines.Values.OrderBy(machine => machine.Id)
            .Select(machine => new WorldRecord(machine.Id, 1, TireChangerIdentity.Adapter, machine.State.Encode()));

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected ?? throw new ArgumentNullException(nameof(connected));
            channel = world ?? throw new ArgumentNullException(nameof(world));
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
        }

        private bool Running => !disposed && session?.IsRunning == true;
        private bool Ready => Running && channel?.FullySynchronized == true;
        private bool Guest => Running && !host;

        internal void InterceptHandle(Furniture_TireChanger_Handle handle)
        {
            if (!Guest) return;
            if (HandleManualField.GetValue(handle) is not Furniture_TireChanger_Manual manual || !byMachine.TryGetValue(manual, out var machine))
            { Report("This tire changer is not registered in the shared world."); return; }
            if (!MayRequest(machine, TireChangerAction.RunCycle, 0, handsFree: !PeerHasSharedItems(session!.LocalPeerId),
                itemLeaseValid: true, itemValid: true, atTarget: true)) return;
            var wheel = manual.Get_Wheel(true);
            bool pressureAllowed = wheel != null && wheel.IntValue1 > 0 && wheel.Value1 <= 5f;
            if (!TireChangerAuthorityPolicy.ValidCycleStart(!manual.Get_MachineIsWorking, wheel != null,
                !PeerHasSharedItems(session!.LocalPeerId), pressureAllowed))
            { Report("The tire changer needs an unpressurized tire and an empty hand before it can start."); return; }
            if (!CanPay(manual, showError: true)) return;
            Send(machine, new TireChangerCommand(Guid.NewGuid(), TireChangerAction.RunCycle, false), 0);
        }

        internal void InterceptRemoval(Furniture_TireChanger_WheelDetection detector)
        {
            if (!Guest) return;
            if (!TryMachine(detector, out var machine)) return;
            bool deRim = IsDeRim(detector);
            var wheel = machine.Native.Get_Wheel(deRim);
            ulong itemId = wheel == null ? 0 : items.EntityOf(wheel);
            var itemState = itemId == 0 ? null : items.StateOf(itemId);
            bool unowned = itemState != null && itemState.Exists && itemState.Lease.Owner == ItemLease.Unowned;
            bool handsFree = !PeerHasSharedItems(session!.LocalPeerId);
            if (!MayRequest(machine, TireChangerAction.RemoveWheel, itemId, handsFree, unowned, wheel != null, atTarget: true)) return;
            if (!TireChangerAuthorityPolicy.ValidWheelRemoval(!machine.Native.Get_MachineIsWorking,
                wheel != null, handsFree, unowned))
            { Report("Put down your item before removing a wheel from the tire changer."); return; }
            Send(machine, new TireChangerCommand(Guid.NewGuid(), TireChangerAction.RemoveWheel, deRim), itemId);
        }

        internal void InterceptTrigger(Furniture_TireChanger_WheelDetection detector, Collider other, InteractableItems? offered = null)
        {
            if (!Guest || detector == null || other == null || !TryMachine(detector, out var machine)) return;
            bool deRimStation = IsDeRim(detector);
            // A held item's collider can sit on a child; the offer names the item itself.
            if ((offered as Part_Driveline_Wheel ?? other.GetComponent<Part_Driveline_Wheel>()) is Part_Driveline_Wheel wheel && wheel != null)
            {
                bool hasTire = wheel.IntValue1 > 0;
                if (deRimStation != hasTire || !items.TryGetLocalLease(wheel, out ulong id, out uint generation)) return;
                bool empty = machine.Native.Get_Wheel(true) == null && machine.Native.Get_Wheel(false) == null;
                bool ownedOnly = true;
                bool atTarget = Overlaps(machine, deRimStation, wheel, GuestOverlapMargin);
                if (!MayRequest(machine, TireChangerAction.MountWheel, id, ownedOnly, true, true, atTarget) ||
                    !TireChangerAuthorityPolicy.ValidWheelMount(!machine.Native.Get_MachineIsWorking, empty,
                        ownedOnly, deRimStation == hasTire, atTarget)) return;
                Send(machine, new TireChangerCommand(Guid.NewGuid(), TireChangerAction.MountWheel, deRimStation, id, generation), id);
                return;
            }

            if (!deRimStation && (offered as Item_Driveline_Tires ?? other.GetComponent<Item_Driveline_Tires>()) is Item_Driveline_Tires tire && tire != null &&
                items.TryGetLocalLease(tire, out ulong tireId, out uint tireGeneration))
            {
                bool rimWheel = machine.Native.Get_Wheel(false) != null;
                bool ownedOnly = true;
                bool atTarget = Overlaps(machine, deRim: false, tire, GuestOverlapMargin);
                if (!MayRequest(machine, TireChangerAction.MountTire, tireId, ownedOnly, true, true, atTarget) ||
                    !TireChangerAuthorityPolicy.ValidTireMount(!machine.Native.Get_MachineIsWorking,
                        rimWheel, ownedOnly, tire != null, atTarget) || !CanPay(machine.Native, showError: true)) return;
                Send(machine, new TireChangerCommand(Guid.NewGuid(), TireChangerAction.MountTire, false, tireId, tireGeneration), tireId);
            }
        }

        internal bool HostMayMount(Collider other)
        {
            if (!host || !Running || other == null) return true;
            InteractableItems? item = other.GetComponent<Part_Driveline_Wheel>();
            if (item == null) item = other.GetComponent<Item_Driveline_Tires>();
            ulong id = item == null ? 0 : items.EntityOf(item);
            var state = id == 0 ? null : items.StateOf(id);
            return state == null || state.Lease.Owner == ItemLease.Unowned || state.Lease.Owner == session!.LocalPeerId;
        }

        private bool MayRequest(Machine machine, TireChangerAction action, ulong itemId, bool handsFree,
            bool itemLeaseValid, bool itemValid, bool atTarget)
        {
            if (!TireChangerAuthorityPolicy.AllowGuestRequest(Running, host, Ready, NewActionsBlocked,
                gameplay.Get_PlayerState == PlayerState.Walking, true,
                NearLocal(machine.Native.transform.position, InteractionRange), !machine.Native.Get_MachineIsWorking,
                handsFree, itemLeaseValid, itemValid, atTarget))
            {
                Trace("local refusal " + action + ": ready=" + Ready + " blocked=" + NewActionsBlocked + " state=" + gameplay.Get_PlayerState +
                    " near=" + NearLocal(machine.Native.transform.position, InteractionRange) + " idle=" + !machine.Native.Get_MachineIsWorking +
                    " handsFree=" + handsFree + " lease=" + itemLeaseValid + " item=" + itemValid + " atTarget=" + atTarget);
                Report(!Ready || NewActionsBlocked ? "Wait for the shared world and save to finish before using the tire changer." :
                    "Move closer to the tire changer and try again.");
                return false;
            }
            var key = (machine.Id, action, itemId);
            float now = Time.unscaledTime;
            foreach (var expired in pending.Where(pair => now - pair.Value > 2f).Select(pair => pair.Key).ToArray()) pending.Remove(expired);
            if (pending.TryGetValue(key, out var sent) && now - sent <= 0.75f) return false;
            return true;
        }

        private void Send(Machine machine, TireChangerCommand command, ulong pendingItemId)
        {
            if (!Ready || channel == null) return;
            try
            {
                channel.Request(machine.Id, command.Encode(), useLease: true);
                // Local eligibility checks can pass before the slot, pressure or payment
                // checks do. Only a sent command should start the retry throttle.
                pending[(machine.Id, command.Action, pendingItemId)] = Time.unscaledTime;
            }
            catch (InvalidDataException ex) { Report("The tire-changer request could not be encoded: " + ex.Message); }
        }

        private bool TryMachine(Furniture_TireChanger_WheelDetection detector, out Machine machine)
        {
            machine = null!;
            if (detector == null || DetectorManualField.GetValue(detector) is not Furniture_TireChanger_Manual manual ||
                !byMachine.TryGetValue(manual, out machine))
            { Report("This tire-changer detector is not registered in the shared world."); return false; }
            return true;
        }

        private bool Overlaps(Machine machine, bool deRim, InteractableItems item, float margin = 0f)
        {
            var collider = item == null ? null : item.itemCollider;
            if (collider == null || !collider.gameObject.activeInHierarchy) return false;
            // A carried item's collider can be switched off in the hand; its mesh still shows where it is.
            Bounds bounds;
            if (collider.enabled) bounds = collider.bounds;
            else
            {
                var renderers = item!.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) return false;
                bounds = renderers[0].bounds;
                foreach (var renderer in renderers) if (renderer != null && renderer.enabled) bounds.Encapsulate(renderer.bounds);
            }
            if (margin > 0) bounds.Expand(margin * 2f);
            var detectors = deRim ? machine.DeRimDetectors : machine.RimDetectors;
            return detectors.Any(detector =>
            {
                var target = detector == null ? null : detector.GetComponent<Collider>();
                return target != null && target.enabled && target.gameObject.activeInHierarchy && bounds.Intersects(target.bounds);
            });
        }

        private string PoseNote(uint peer, Vector3 target)
        {
            if (!presence.TryGetValue(peer, out var seen)) return "pose=none";
            var at = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
            return "pose=" + (PlayerState)seen.Pose.State + " age=" + (Time.unscaledTime - seen.Time).ToString("F1") + " dist=" + Vector3.Distance(at, target).ToString("F1");
        }
        private static string CooldownNote(Machine machine) =>
            ((float)CooldownField.GetValue(machine.Native) - Time.realtimeSinceStartup).ToString("F1") + "s";

        // The native machine takes a part only when it falls into the trigger; a carried one never
        // entered it, for the host either (#201). The host's own held wheel or tire is handed to the
        // native trigger once it overlaps, when the machine would accept it (so no refusal message
        // repeats while a part is carried past a busy machine).
        private float nextHostOffer;
        private void OfferHostHeldItem()
        {
            if (Time.unscaledTime < nextHostOffer) return;
            nextHostOffer = Time.unscaledTime + 0.25f;
            var held = gameplay.Player?.Get_HoldingItem;
            if (held == null || !(held is Part_Driveline_Wheel || held is Item_Driveline_Tires) || held.itemCollider == null) return;
            foreach (var machine in machines.Values)
            {
                var native = machine.Native;
                if (native == null || native.Get_MachineIsWorking || !NearLocal(native.transform.position, InteractionRange) ||
                    (float)CooldownField.GetValue(native) > Time.realtimeSinceStartup) continue;
                foreach (var detector in machine.DeRimDetectors.Concat(machine.RimDetectors))
                {
                    if (detector == null) continue;
                    bool deRim = IsDeRim(detector);
                    bool accepts = held is Part_Driveline_Wheel wheel
                        ? native.Get_Wheel(true) == null && native.Get_Wheel(false) == null && (wheel.IntValue1 > 0) == deRim
                        : !deRim && native.CanRemoveThisTire(false) && CanPay(native, showError: false);
                    if (!accepts || !Overlaps(machine, deRim, held, GuestOverlapMargin)) continue;
                    Trace("host offers held " + held.GetType().Name + " deRim=" + deRim);
                    try { TriggerMethod.Invoke(detector, new object[] { held.itemCollider }); }
                    catch (Exception ex) { Debug.LogWarning("MonMP tire changer held offer failed: " + (ex.InnerException ?? ex).Message); }
                    return;
                }
            }
        }

        // #201: natively a wheel or tire carried into the machine's trigger is taken at once. The guest's
        // trigger fires only on entry, often before it counts as at the target, so keep offering it.
        private float nextOffer;
        private void OfferHeldItem()
        {
            if (Time.unscaledTime < nextOffer || !Ready) return;
            nextOffer = Time.unscaledTime + 0.25f;
            var held = gameplay.Player?.Get_HoldingItem;
            if (held == null || !(held is Part_Driveline_Wheel || held is Item_Driveline_Tires) || held.itemCollider == null) return;
            foreach (var machine in machines.Values)
            {
                if (machine.Native == null || !NearLocal(machine.Native.transform.position, InteractionRange)) continue;
                foreach (var detector in machine.DeRimDetectors.Concat(machine.RimDetectors))
                    // Only the station that takes this part: the de-rim detector is checked first and sits
                    // beside the rim one, so a bare rim or a tire offered there was refused and never
                    // reached the rim side (#201).
                    if (detector != null && IsDeRim(detector) == (held is Part_Driveline_Wheel w && w.IntValue1 > 0) &&
                        Overlaps(machine, IsDeRim(detector), held, GuestOverlapMargin))
                    {
                        Trace("guest offers held " + held.GetType().Name + " deRim=" + IsDeRim(detector));
                        InterceptTrigger(detector, held.itemCollider, held);
                        return;
                    }
            }
        }

        [MonMP.Core.World.WorldCommandAdapters(TireChangerIdentity.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!TireChangerCommand.HasMarker(payload) || !host || session?.IsHost != true || channel == null ||
                !machines.TryGetValue(entity, out var machine)) return;
            TireChangerCommand command;
            try { command = TireChangerCommand.Decode(payload); }
            catch (InvalidDataException) { Reject("undecodable command from " + peer); return; }
            if (peer == 0 || !Remember(peer, command.OperationId) || !TakeBudget(peer)) { Reject(command.Action + " duplicate or over budget from " + peer); return; }
            if (!session.Peers.ContainsKey(peer) || !HasFreshWalkingPose(peer, machine.Native.transform.position))
            { Reject(command.Action + " from " + peer + ": " + PoseNote(peer, machine.Native.transform.position)); return; }
            if (!Ready || NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { Reject(command.Action + ": world not ready or saving"); return; }
            Trace("request " + command.Action + " deRim=" + command.DeRimSide + " item=" + command.ItemEntity.ToString("X16") + " from " + peer);

            bool idle = !machine.Native.Get_MachineIsWorking;
            // The native calls below report their failures on this screen; they belong to the guest (#201).
            using var quiet = Patches.RemoteActionScope.Enter();
            switch (command.Action)
            {
                case TireChangerAction.MountWheel:
                    AcceptWheel(peer, machine, command, idle);
                    break;
                case TireChangerAction.MountTire:
                    AcceptTire(peer, machine, command, idle);
                    break;
                case TireChangerAction.RunCycle:
                    AcceptCycle(peer, machine, command, idle);
                    break;
                case TireChangerAction.RemoveWheel:
                    AcceptRemoval(peer, machine, command, idle);
                    break;
                default:
                    Reject("unknown action " + command.Action);
                    break;
            }
        }

        private void AcceptWheel(uint peer, Machine machine, TireChangerCommand command, bool idle)
        {
            // The Accept* checks judge a guest's request, so its "is host" input is the requester's: the
            // host's own flag there refused every guest mount, cycle and removal (#201).
            var item = items.ItemOf(command.ItemEntity) as Part_Driveline_Wheel;
            var state = items.StateOf(command.ItemEntity);
            bool owned = state != null && state.Exists && state.Lease.Owner == peer && state.Lease.Generation == command.Generation;
            bool rightType = item != null && (item.IntValue1 > 0) == command.DeRimSide;
            bool empty = machine.Native.Get_Wheel(true) == null && machine.Native.Get_Wheel(false) == null;
            // The host's copy trails the guest's hand by a network tick; the guest measured the exact overlap (#201).
            bool overlap = item != null && (Overlaps(machine, command.DeRimSide, item, HostOverlapMargin) ||
                owned && NearDetector(peer, machine, command.DeRimSide));
            bool onlyItem = owned || items.PeerHoldsOnly(peer, command.ItemEntity);
            if (!TireChangerAuthorityPolicy.AllowGuestRequest(Running, false, Ready, NewActionsBlocked,
                    HasFreshWalkingPose(peer), HasFreshWalkingPose(peer), NearPeer(peer, machine.Native.transform.position, InteractionRange),
                    idle, onlyItem, owned, item != null && items.Handles(item), overlap) ||
                !TireChangerAuthorityPolicy.ValidWheelMount(idle, empty, owned, rightType, overlap))
            { Reject("wheel: item=" + (item != null) + " owned=" + owned + " lease=" + (state == null ? "none" : state.Lease.Owner + "/" + state.Lease.Generation) +
                " sent=" + command.Generation + " rightType=" + rightType + " empty=" + empty + " idle=" + idle + " overlap=" + overlap + " onlyItem=" + onlyItem +
                " " + PoseNote(peer, machine.Native.transform.position)); return; }

            machine.Native.PlaceWheelOnMachine(item!, command.DeRimSide);
            if (machine.Native.Get_Wheel(command.DeRimSide) != item)
            { Reject("wheel: native placement refused (cooldown " + CooldownNote(machine) + ")"); return; }
            if (!items.HostReleaseTireChangerItem(item!, peer, command.Generation))
            { Reject("wheel: lease release failed"); return; }
            ObserveNative(machine);

#if MONMP_DEVELOPMENT
            TestAccepted++;
#endif

        }

        private void AcceptTire(uint peer, Machine machine, TireChangerCommand command, bool idle)
        {
            var tire = items.ItemOf(command.ItemEntity) as Item_Driveline_Tires;
            var itemState = items.StateOf(command.ItemEntity);
            bool owned = itemState != null && itemState.Exists && itemState.Lease.Owner == peer && itemState.Lease.Generation == command.Generation;
            bool rimWheel = machine.Native.Get_Wheel(false) != null;
            bool overlap = tire != null && (Overlaps(machine, false, tire, HostOverlapMargin) || owned && NearDetector(peer, machine, false));
            bool onlyItem = owned || items.PeerHoldsOnly(peer, command.ItemEntity);
            if (!TireChangerAuthorityPolicy.AllowGuestRequest(Running, false, Ready, NewActionsBlocked,
                    HasFreshWalkingPose(peer), HasFreshWalkingPose(peer), NearPeer(peer, machine.Native.transform.position, InteractionRange),
                    idle, onlyItem, owned, tire != null && items.Handles(tire), overlap) ||
                !TireChangerAuthorityPolicy.ValidTireMount(idle, rimWheel, owned, tire != null, overlap) ||
                !CanPay(machine.Native, showError: false))
            { Reject("tire: item=" + (tire != null) + " owned=" + owned + " rimWheel=" + rimWheel + " idle=" + idle + " overlap=" + overlap +
                " onlyItem=" + onlyItem + " pay=" + CanPay(machine.Native, showError: false) + " " + PoseNote(peer, machine.Native.transform.position)); return; }

            machine.Native.AddTireToMachine(tire!);
            if (!machine.Native.Get_MachineIsWorking) { Reject("tire: native mount refused"); return; }
            if (!items.HostReleaseTireChangerItem(tire!, peer, command.Generation)) { Reject("tire: lease release failed"); return; }
            SetOperation(machine, TireChangerOperation.Rim, command.ItemEntity);

#if MONMP_DEVELOPMENT
            TestAccepted++;
#endif

        }

        private void AcceptCycle(uint peer, Machine machine, TireChangerCommand command, bool idle)
        {
            if (command.DeRimSide || command.ItemEntity != 0 || command.Generation != 0)
            { Reject("cycle: malformed"); return; }
            var wheel = machine.Native.Get_Wheel(true);
            bool pressureAllowed = wheel != null && wheel.IntValue1 > 0 && wheel.Value1 <= 5f;
            bool handsFree = !PeerHasSharedItems(peer);
            if (!TireChangerAuthorityPolicy.AllowGuestRequest(Running, false, Ready, NewActionsBlocked,
                    HasFreshWalkingPose(peer), HasFreshWalkingPose(peer),
                    NearPeer(peer, machine.Native.transform.position, InteractionRange), idle, handsFree,
                    itemLeaseValid: true, itemValid: true, atTarget: true) ||
                !TireChangerAuthorityPolicy.ValidCycleStart(idle, wheel != null, handsFree, pressureAllowed) ||
                !CanPay(machine.Native, showError: false))
            { Reject("cycle: wheel=" + (wheel != null) + " tire=" + (wheel != null && wheel.IntValue1 > 0) + " pressure=" + (wheel == null ? -1f : wheel.Value1).ToString("F1") +
                " handsFree=" + handsFree + " idle=" + idle + " pay=" + CanPay(machine.Native, showError: false) + " " + PoseNote(peer, machine.Native.transform.position)); return; }

            machine.Native.Use();
            if (!machine.Native.Get_MachineIsWorking)
            { Reject("cycle: native start refused"); return; }
            SetOperation(machine, TireChangerOperation.DeRim, 0);

#if MONMP_DEVELOPMENT
            TestAccepted++;
#endif

        }

        private void AcceptRemoval(uint peer, Machine machine, TireChangerCommand command, bool idle)
        {
            var wheel = machine.Native.Get_Wheel(command.DeRimSide);
            ulong itemId = wheel == null ? 0 : items.EntityOf(wheel);
            var state = itemId == 0 ? null : items.StateOf(itemId);
            bool unowned = state != null && state.Exists && state.Lease.Owner == ItemLease.Unowned;
            bool handsFree = !PeerHasSharedItems(peer);
            if (!TireChangerAuthorityPolicy.AllowGuestRequest(Running, false, Ready, NewActionsBlocked,
                    HasFreshWalkingPose(peer), HasFreshWalkingPose(peer),
                    NearPeer(peer, machine.Native.transform.position, InteractionRange), idle, handsFree,
                    unowned, wheel != null && items.Handles(wheel), atTarget: true) ||
                !TireChangerAuthorityPolicy.ValidWheelRemoval(idle, wheel != null, handsFree, unowned) ||
                command.ItemEntity != 0 || command.Generation != 0)
            { Reject("removal: wheel=" + (wheel != null) + " unowned=" + unowned + " handsFree=" + handsFree + " idle=" + idle); return; }

            RemoveWheelForPeer(machine.Native, command.DeRimSide, wheel!);
            ObserveNative(machine);
            if (!items.HostGiveTireChangerItemToPeer(wheel!, peer))
                items.HostPublishItem(itemId);

#if MONMP_DEVELOPMENT
            TestAccepted++;
#endif

        }

        private static void RemoveWheelForPeer(Furniture_TireChanger_Manual manual, bool deRim, Part_Driveline_Wheel wheel)
        {
            RemoveWheelMethod.Invoke(manual, new object[] { wheel });
            SetWheel(manual, deRim, null);
            CooldownField.SetValue(manual, Time.realtimeSinceStartup + 0.4f);
            try { Singleton<SOUND_Manager>.i.PlaySound_PartRemoved(); }
            catch (Exception ex) { Debug.LogWarning("Tire-changer removal sound was unavailable: " + ex.Message); }
        }

        private void SetOperation(Machine machine, TireChangerOperation operation, ulong rimTire)
        {
            uint sequence = machine.State.OperationSequence == uint.MaxValue ? 1 : machine.State.OperationSequence + 1;
            machine.State = SnapshotNative(machine, operation, sequence, rimTire);
            Publish(machine);
        }

        private void ObserveNative(Machine machine)
        {
            bool working = machine.Native.Get_MachineIsWorking;
            TireChangerOperation operation = working ? machine.State.Operation : TireChangerOperation.None;
            uint sequence = machine.State.OperationSequence;
            if (working && operation == TireChangerOperation.None)
            {
                operation = InferOperation(machine);
                sequence = sequence == uint.MaxValue ? 1 : sequence + 1;
            }
            var next = SnapshotNative(machine, operation, sequence, machine.State.RimTire);
            if (next.Equals(machine.State)) return;
            machine.State = next;
            Publish(machine);
        }

        private TireChangerOperation InferOperation(Machine machine)
        {
            // A cycle first seen in its tail has already let go of its wheel; only a rim cycle keeps the rim slot.
            return machine.Native.Get_Wheel(false) != null ? TireChangerOperation.Rim : TireChangerOperation.DeRim;
        }

        private TireChangerState SnapshotNative(Machine machine, TireChangerOperation operation, uint sequence, ulong rimTire)
        {
            bool working = machine.Native.Get_MachineIsWorking;
            var deRim = machine.Native.Get_Wheel(true);
            var rim = machine.Native.Get_Wheel(false);
            ulong deRimId = deRim == null ? 0 : items.EntityOf(deRim);
            ulong rimId = rim == null ? 0 : items.EntityOf(rim);
            if ((deRim != null && deRimId == 0) || (rim != null && rimId == 0))
                throw new InvalidOperationException("A mounted tire-changer wheel is missing from SharedItems: " + machine.Path);
            if (!working) { operation = TireChangerOperation.None; rimTire = 0; }
            else if (operation == TireChangerOperation.Rim && rim == null) rimTire = 0; // the cycle's tail: tire fitted and consumed
            else if (operation == TireChangerOperation.Rim && rimTire == 0)
            {
                var tire = FindCurrentTire(machine.Native);
                rimTire = tire == null ? 0 : items.EntityOf(tire);
            }
            if (working && operation == TireChangerOperation.Rim && rim != null && rimTire == 0)
                throw new InvalidOperationException("The native tire-install cycle has no shared tire item: " + machine.Path);
            return new TireChangerState(deRimId, rimId, rimTire, sequence, operation, working);
        }

        private static Item_Driveline_Tires? FindCurrentTire(Furniture_TireChanger_Manual manual)
        {
            var rotating = RimRotationField.GetValue(manual) as Transform;
            return rotating == null ? null : rotating.GetComponentInChildren<Item_Driveline_Tires>(true);
        }

        private void Publish(Machine machine)
        {
            if (host && channel?.Ready == true)
                channel.Publish(machine.Id, machine.State.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == TireChangerIdentity.Adapter && machines.TryGetValue(record.Id, out var machine))
                    ApplyReplica(machine, TireChangerState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != TireChangerIdentity.Adapter || !machines.TryGetValue(record.Id, out var machine)) return;
            ApplyReplica(machine, TireChangerState.Decode(record.Payload));
        }

        private void ApplyReplica(Machine machine, TireChangerState next)
        {
            machine.State = next;
            ReconcileReplicaReplay(machine);
            ApplyWheel(machine, true, next.DeRimWheel);
            ApplyWheel(machine, false, next.RimWheel);
            TryReplayOperation(machine);
        }

        private void TryReplayOperation(Machine machine)
        {
            var next = machine.State;
            // Applying the record is not proof its native presentation started: its
            // wheel/tire can be represented later, or an older local cycle can finish later.
            if (!next.Working || next.OperationSequence == machine.ReplayedOperationSequence ||
                machine.Native.Get_MachineIsWorking) return;
            var wheel = items.ItemOf(next.Operation == TireChangerOperation.DeRim ? next.DeRimWheel : next.RimWheel) as Part_Driveline_Wheel;
            if (wheel == null || machine.Native.Get_Wheel(next.Operation == TireChangerOperation.DeRim) != wheel) return;
            var tire = next.Operation == TireChangerOperation.Rim ? items.ItemOf(next.RimTire) as Item_Driveline_Tires : null;
            if (next.Operation == TireChangerOperation.Rim && tire == null) return;
            var handle = (Transform)HandleField.GetValue(machine.Native)!;
            var replay = new Replay
            {
                Operation = next.Operation, Wheel = wheel, Tire = tire, Handle = handle,
                HandleParent = handle.parent, HandlePosition = handle.position, HandleRotation = handle.rotation,
                RotatingPosition = ((Transform)RimRotationField.GetValue(machine.Native)!).position
            };
            {
                // The host already charged the shared wallet; replaying a paid machine's native charge here
                // met the guest's blocked wallet, showed "not enough cash" and skipped the animation (#201).
                bool paid = Payant(machine.Native);
                PayantField.SetValue(machine.Native, false);
                using var quiet = Patches.RemoteActionScope.Enter();
                try
                {
                    if (next.Operation == TireChangerOperation.DeRim)
                    {
                        machine.Native.Use();
                    }
                    else if (next.Operation == TireChangerOperation.Rim)
                        machine.Native.AddTireToMachine(tire!);
                }
                finally
                {
                    PayantField.SetValue(machine.Native, paid);
                    if (machine.Native.Get_MachineIsWorking)
                    {
                        machine.ReplayedOperationSequence = next.OperationSequence;
                        machine.ActiveReplay = replay;
                    }
                }
            }
        }

        // SharedItems has accepted the tombstone and removed routing/identity, but native
        // MachineRim still reads its tire after the animation. Keep only this exact replay's
        // dependencies alive; their transaction is already complete on the host.
        internal bool ClaimRemovedItem(InteractableItems item)
        {
            if (host || disposed || item == null) return false;
            foreach (var machine in machines.Values)
            {
                var replay = machine.ActiveReplay;
                if (replay == null || !machine.Native.Get_MachineIsWorking) continue;
                if (ReferenceEquals(replay.Tire, item)) { replay.TireRemoved = true; return true; }
                if (ReferenceEquals(replay.Wheel, item)) { replay.WheelRemoved = true; return true; }
            }
            return false;
        }

        private void ReconcileReplicaReplay(Machine machine)
        {
            if (machine.ActiveReplay != null && (!machine.Native.Get_MachineIsWorking ||
                !machine.State.Working || machine.State.OperationSequence != machine.ReplayedOperationSequence))
                FinishReplicaReplay(machine, cancel: false);
        }

        private void FinishReplicaReplay(Machine machine, bool cancel)
        {
            var replay = machine.ActiveReplay;
            if (replay == null) return;
            string method = replay.Operation == TireChangerOperation.Rim ? "MachineRim" : "MachineDeRim";
            using var quiet = Patches.RemoteActionScope.Enter();
            // During disconnect cancel the presentation, with no producer/output work after
            // session teardown. While connected, finish the retained iterator on host idle.
            bool finished = !cancel && NativeSaveProduction.Finish(machine.Native, method) &&
                !machine.Native.Get_MachineIsWorking;
            if (!finished)
            {
                NativeSaveProduction.Cancel(machine.Native, method);
                if (machine.Native != null)
                {
                    foreach (bool deRim in new[] { true, false })
                    {
                        var wheel = machine.Native.Get_Wheel(deRim);
                        if (wheel != null) RemoveWheelMethod.Invoke(machine.Native, new object[] { wheel });
                        SetWheel(machine.Native, deRim, null);
                    }
                    if (replay.Handle != null)
                    {
                        replay.Handle.SetParent(replay.HandleParent);
                        replay.Handle.position = replay.HandlePosition;
                        replay.Handle.rotation = replay.HandleRotation;
                    }
                    if (RimRotationField.GetValue(machine.Native) is Transform rotating && rotating != null)
                        rotating.position = replay.RotatingPosition;
                    WorkingField.SetValue(machine.Native, false);
                }
                if (replay.Tire != null && !replay.TireRemoved)
                {
                    replay.Tire.UnGrab();
                    replay.Tire.itemCollider.enabled = true;
                    replay.Tire.transform.SetParent(null);
                }
            }
            // A completed rim iterator has already scheduled its own tire destruction.
            if (!finished && replay.TireRemoved && replay.Tire != null) Object.Destroy(replay.Tire.gameObject);
            if (replay.WheelRemoved && replay.Wheel != null) Object.Destroy(replay.Wheel.gameObject);
            machine.ActiveReplay = null;
        }

        private void ApplyWheel(Machine machine, bool deRim, ulong entity)
        {
            var current = machine.Native.Get_Wheel(deRim);
            var desired = entity == 0 ? null : items.ItemOf(entity) as Part_Driveline_Wheel;
            if (current == desired) return;
            // A delayed replica may still be using its wheel when the host's tail
            // empties/replaces the slot. Native coroutines own these dependencies
            // until they finish; the next update reconciles the latest host slots.
            if (machine.Native.Get_MachineIsWorking) return;
            if (current != null)
            {
                RemoveWheelMethod.Invoke(machine.Native, new object[] { current });
                SetWheel(machine.Native, deRim, null);
            }
            if (desired != null && !machine.Native.Get_MachineIsWorking &&
                machine.Native.Get_Wheel(true) == null && machine.Native.Get_Wheel(false) == null)
            {
                using var quiet = Patches.RemoteActionScope.Enter();
                machine.Native.PlaceWheelOnMachine(desired, deRim);
            }
        }

        private void UpdateReplicaSlots()
        {
            if (host) return;
            foreach (var machine in machines.Values)
            {
                ReconcileReplicaReplay(machine);
                ApplyWheel(machine, true, machine.State.DeRimWheel);
                ApplyWheel(machine, false, machine.State.RimWheel);
                TryReplayOperation(machine);
            }
        }

        internal void NativeCallbackCompleted(Furniture_TireChanger_Handle handle)
        {
            if (!host || !Running || handle == null || HandleManualField.GetValue(handle) is not Furniture_TireChanger_Manual manual ||
                !byMachine.TryGetValue(manual, out var machine)) return;
            ObserveNative(machine);
        }

        internal void NativeCallbackCompleted(Furniture_TireChanger_WheelDetection detector)
        {
            if (!host || !Running || !TryMachine(detector, out var machine)) return;
            ObserveNative(machine);
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnPeerLeft(uint peer)
        {
            presence.Remove(peer); budgets.Remove(peer);
            foreach (var key in replayed.Where(value => value.Peer == peer).ToArray()) replayed.Remove(key);
        }

        private bool HasFreshWalkingPose(uint peer) => presence.TryGetValue(peer, out var observed) &&
            Time.unscaledTime >= observed.Time && Time.unscaledTime - observed.Time <= PoseMaxAge &&
            observed.Pose.State == (byte)PlayerState.Walking;

        private bool HasFreshWalkingPose(uint peer, Vector3 target) => HasFreshWalkingPose(peer) &&
            NearPeer(peer, target, InteractionRange);

        private bool NearPeer(uint peer, Vector3 target, float range)
        {
            if (!presence.TryGetValue(peer, out var observed)) return false;
            var position = new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z);
            return (position - target).sqrMagnitude <= range * range;
        }

        // The host draws a guest's held wheel at its avatar's hand, not where the guest's camera holds it,
        // so a held item the guest measured at the detector is trusted while the guest stands beside it.
        private bool NearDetector(uint peer, Machine machine, bool deRim) =>
            (deRim ? machine.DeRimDetectors : machine.RimDetectors).Any(d => d != null && NearPeer(peer, d.transform.position, 2.5f));

        private bool NearLocal(Vector3 target, float range) => gameplay.Get_PlayerState == PlayerState.Walking &&
            (gameplay.PlayerWalking.transform.position - target).sqrMagnitude <= range * range;

        private bool PeerHasSharedItems(uint peer) => items.PeerHolding(peer);

        private bool TakeBudget(uint peer)
        {
            float now = Time.unscaledTime;
            var current = budgets.TryGetValue(peer, out var old) && now >= old.Started && now - old.Started < BudgetWindow
                ? old : (Started: now, Count: 0);
            if (current.Count >= RequestsPerWindow) { budgets[peer] = current; return false; }
            budgets[peer] = (current.Started, current.Count + 1);
            return true;
        }

        private bool Remember(uint peer, Guid operation)
        {
            if (!replayed.Add((peer, operation))) return false;
            replayOrder.Enqueue((peer, operation));
            while (replayOrder.Count > ReplayCapacity) replayed.Remove(replayOrder.Dequeue());
            return true;
        }

        internal void Update()
        {
            if (!Running) return;
            if (!host) { UpdateReplicaSlots(); OfferHeldItem(); return; }
            OfferHostHeldItem();
            if (Time.unscaledTime < nextCapture || channel?.Ready != true) return;
            nextCapture = Time.unscaledTime + 0.1f;
            foreach (var machine in machines.Values) ObserveNative(machine);
        }

        private static void Report(string text) => MonMPPlugin.Instance?.ReportItemUseUnavailable(text);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnPeerLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host)
                foreach (var machine in machines.Values) FinishReplicaReplay(machine, cancel: true);
            presence.Clear(); budgets.Clear(); pending.Clear(); replayed.Clear(); replayOrder.Clear();
            machines.Clear(); byMachine.Clear(); session = null; channel = null;
        }
    }
}
