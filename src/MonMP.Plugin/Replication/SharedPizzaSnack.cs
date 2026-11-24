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
    // PizzaSnack's physical boxes still use SharedItems. This adapter owns only
    // the event progression: guests request door/start/delivery actions while the
    // host runs native effects and publishes the selected route and door state.
    internal sealed class SharedPizzaSnack : IDisposable
    {
        private static readonly FieldInfo StoreOpen = RequiredField(typeof(Stores), "isOpen");
        private static readonly FieldInfo DoorOpen = RequiredField(typeof(Event_PizzaSnack), "isTakeOutDoorOpen");
        private static readonly FieldInfo PlayerDetector = RequiredField(typeof(Event_PizzaSnack), "PlayerDetector");
        private static readonly FieldInfo DropOffCube = RequiredField(typeof(Event_PizzaSnack), "DropOffCube");
        private static readonly FieldInfo Door = RequiredField(typeof(Event_PizzaSnack), "TakeOutDoor");
        private static readonly FieldInfo DoorOriginal = RequiredField(typeof(Event_PizzaSnack), "TakeOutDoorOriginalPosition");
        private static readonly FieldInfo DoorUp = RequiredField(typeof(Event_PizzaSnack), "TakeOutDoorUpPosition");
        private static readonly FieldInfo DropOffPoints = RequiredField(typeof(Event_PizzaSnack), "DropOffPoints");
        private static readonly FieldInfo CurrentDropOff = RequiredField(typeof(Event_PizzaSnack), "CurrentDropOff");
        private static readonly FieldInfo EventRunning = RequiredField(typeof(Event_PizzaSnack), "EventIsRunning");
        private static readonly FieldInfo CurrentTarget = RequiredField(typeof(Event_PizzaSnack), "Current_DropOffLookingAt");
        private static readonly FieldInfo CanvasArrow = RequiredField(typeof(Event_PizzaSnack), "Canvas_Arrow");
        private static readonly FieldInfo ArrowTarget = RequiredField(typeof(Canvas_3D_Arrow), "Target");
        private static readonly FieldInfo ArrowVisual = RequiredField(typeof(Canvas_3D_Arrow), "Arrow");
        private static readonly MethodInfo StartEventMethod = RequiredMethod(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.StartEvent));
        private static readonly MethodInfo PlayerStepInMethod = RequiredMethod(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.PlayerStepIn));
        private static readonly MethodInfo PizzaDeliveredMethod = RequiredMethod(typeof(Event_PizzaSnack), nameof(Event_PizzaSnack.PizzaDelivered));

        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly Event_PizzaSnack? pizzaSnack;
        private NativeBaseline? original;
        private sealed class NativeBaseline
        {
            internal bool StoreOpen, DoorOpen, Running;
            internal Transform[] Route = Array.Empty<Transform>();
            internal Transform? Target;
            internal object? ArrowReference;
            internal Canvas_3D_Arrow? Arrow;
            internal Transform? ArrowTarget;
            internal Quaternion ArrowRotation;
            internal readonly Dictionary<GameObject, bool> Active = new Dictionary<GameObject, bool>();
            internal readonly Dictionary<Transform, Vector3> Positions = new Dictionary<Transform, Vector3>();
            internal Furniture_Door[] OpenDoors = Array.Empty<Furniture_Door>();
        }
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<Guid, (PizzaSnackCommand Command, float Time)> pending = new Dictionary<Guid, (PizzaSnackCommand, float)>();
        private readonly Dictionary<ulong, (Event_PizzaSnack_PizzaBox Box, uint Generation, float LastSend, float Expires)> deliveryCandidates =
            new Dictionary<ulong, (Event_PizzaSnack_PizzaBox, uint, float, float)>();
        private readonly HashSet<(ulong Box, uint Generation)> deliveredBoxes = new HashSet<(ulong, uint)>();
        private readonly PizzaSnackClaims claims = new PizzaSnackClaims();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed, applying, handlingRequest;
        private uint nativeDeliveryPeer;
        private float lastDeliveryTime = float.NegativeInfinity;
        private Integration.ToolAimSample lastPickupAim;
        private float lastPickupRayTime = float.NegativeInfinity;
        private Event_PizzaSnack_PizzaBox? lastPickupBox;

        internal bool Ready => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true;
        internal bool GuestRunning => !host && !disposed && session?.IsRunning == true;
        internal bool NewActionsBlocked { get; set; }

#if MONMP_DEVELOPMENT
        internal PizzaSnackResult? TestLastResult { get; private set; }


        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal int TestDuplicates { get; private set; }
#endif

        internal int PendingCount => pending.Count;

        private readonly AimSegmentTest checkSegment;

        internal SharedPizzaSnack(AimSegmentTest checkSegment, Gameplay gameplay, bool host, SharedItems items)
        {
            this.checkSegment = checkSegment;
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            var candidates = Object.FindObjectsOfType<Event_PizzaSnack>(true)
                .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid()).ToArray();
            if (candidates.Length == 1) pizzaSnack = candidates[0];
            ValidateNativeLayout();
            if (!host && pizzaSnack != null) original = CaptureBaseline(pizzaSnack);
        }

        private static NativeBaseline CaptureBaseline(Event_PizzaSnack native)
        {
            var baseline = new NativeBaseline
            {
                StoreOpen = StoreOpen.GetValue(native) is true,
                DoorOpen = DoorOpen.GetValue(native) is true,
                Running = EventRunning.GetValue(native) is true,
                Route = Route(native).ToArray(), Target = CurrentTarget.GetValue(native) as Transform,
                ArrowReference = CanvasArrow.GetValue(native), OpenDoors = native.StoreDoorsStillOpen.ToArray()
            };
            baseline.Arrow = baseline.ArrowReference as Canvas_3D_Arrow ?? Object.FindObjectOfType<Canvas_3D_Arrow>();
            void CaptureActive(GameObject? value) { if (value != null && !baseline.Active.ContainsKey(value)) baseline.Active.Add(value, value.activeSelf); }
            void CapturePosition(Transform? value) { if (value != null) baseline.Positions[value] = value.position; }
            CaptureActive(PlayerDetector.GetValue(native) as GameObject);
            var cube = DropOffCube.GetValue(native) as GameObject;
            CaptureActive(cube); CapturePosition(cube == null ? null : cube.transform);
            CapturePosition(Door.GetValue(native) as Transform);
            foreach (var visual in native.EnableWhenStoreIsOpen ?? Array.Empty<GameObject>()) CaptureActive(visual);
            foreach (var visual in native.EnableWhenStoreIsClose ?? Array.Empty<GameObject>()) CaptureActive(visual);
            foreach (var listing in native.ItemForSale) if (listing != null) CaptureActive(listing.gameObject);
            if (baseline.Arrow != null)
            {
                baseline.ArrowTarget = ArrowTarget.GetValue(baseline.Arrow) as Transform;
                var visual = ArrowVisual.GetValue(baseline.Arrow) as GameObject;
                CaptureActive(visual);
                if (visual != null) baseline.ArrowRotation = visual.transform.localRotation;
            }
            return baseline;
        }

        private void RestoreBaseline()
        {
            var baseline = original;
            original = null;
            if (baseline == null || pizzaSnack == null) return;
            StoreOpen.SetValue(pizzaSnack, baseline.StoreOpen); DoorOpen.SetValue(pizzaSnack, baseline.DoorOpen);
            EventRunning.SetValue(pizzaSnack, baseline.Running);
            var route = Route(pizzaSnack); route.Clear(); route.AddRange(baseline.Route);
            CurrentTarget.SetValue(pizzaSnack, baseline.Target);
            pizzaSnack.StoreDoorsStillOpen.Clear(); pizzaSnack.StoreDoorsStillOpen.AddRange(baseline.OpenDoors);
            if (baseline.Arrow != null)
            {
                baseline.Arrow.Set_Target = baseline.ArrowTarget;
                if (ArrowVisual.GetValue(baseline.Arrow) is GameObject visual && visual != null)
                    visual.transform.localRotation = baseline.ArrowRotation;
            }
            CanvasArrow.SetValue(pizzaSnack, baseline.ArrowReference);
            foreach (var pair in baseline.Positions) if (pair.Key != null) pair.Key.position = pair.Value;
            foreach (var pair in baseline.Active) if (pair.Key != null) pair.Key.SetActive(pair.Value);
        }

        private static FieldInfo RequiredField(Type type, string name) => AccessTools.Field(type, name) ??
            throw new MissingFieldException(type.FullName, name);
        private static MethodInfo RequiredMethod(Type type, string name) => AccessTools.Method(type, name, Type.EmptyTypes) ??
            throw new MissingMethodException(type.FullName, name);

        private static void ValidateNativeLayout()
        {
            if (typeof(InteractableItems).IsAssignableFrom(typeof(Event_PizzaSnack_PizzaBox)) != true ||
                AccessTools.Method(typeof(Event_PizzaSnack_PizzaBox), nameof(Event_PizzaSnack_PizzaBox.Grab), Type.EmptyTypes) == null ||
                AccessTools.Method(typeof(Event_PizzaSnack_DropOffCube), "OnTriggerEnter", new[] { typeof(Collider) }) == null)
                throw new InvalidOperationException("Unsupported PizzaSnack native interaction layout.");
        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            yield return new WorldRecord(PizzaSnackCommand.Entity, 1, PizzaSnackCommand.Adapter, Array.Empty<byte>());
            yield return new WorldRecord(PizzaSnackCommand.StateEntity, 1, PizzaSnackCommand.Adapter,
                (pizzaSnack == null ? EmptyState() : Capture(pizzaSnack)).Encode());
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += PoseReceived; session.PeerLeft += PeerLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRequested += Command; channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
        }

        // Called by a small grab prefix while the crosshair is still on the pizza box.
        // SharedItems replays the vanilla grab after its host lease is granted.
        internal void CapturePickupRay(PlayerInteractable player, InteractableItems? item)
        {
            if (!GuestRunning || item is not Event_PizzaSnack_PizzaBox || player?.mainCamera == null) return;
            var camera = player.mainCamera;
            lastPickupAim = Integration.LocalInteraction.SampleTool(camera);
            lastPickupRayTime = Time.unscaledTime;
            lastPickupBox = (Event_PizzaSnack_PizzaBox)item;
        }

        // Guest prefix for the native trigger volume. The pickup lease and the
        // camera ray captured before SharedItems' delayed grab authorize StartEvent.
        internal bool StartEvent(Event_PizzaSnack native)
        {
            if (!GuestRunning) return false;
            if (!ReferenceEquals(native, pizzaSnack) || !Ready || NewActionsBlocked ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true || pending.Count >= 64)
                return true;
            var held = gameplay.Player.Get_HoldingItem as Event_PizzaSnack_PizzaBox;
            if (held == null || !ReferenceEquals(held, lastPickupBox) || Time.unscaledTime - lastPickupRayTime < 0 || Time.unscaledTime - lastPickupRayTime > 5f ||
                !TryLocalLease(held, out ulong id, out uint generation)) return true;
            var request = new PizzaSnackCommand(Guid.NewGuid(), PizzaSnackAction.StartEvent, id, generation,
                lastPickupAim.Origin.x, lastPickupAim.Origin.y, lastPickupAim.Origin.z, lastPickupAim.Yaw, lastPickupAim.Pitch, lastPickupAim.Independent);
            Send(request);
            return true;
        }

        // Guest prefix for the store's player detector. It prevents local box
        // instantiation; host-owned pizza boxes arrive through SharedItems instead.
        internal bool PlayerStepIn(Event_PizzaSnack native)
        {
            if (!GuestRunning) return false;
            if (!ReferenceEquals(native, pizzaSnack) || !Ready || NewActionsBlocked ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true || pending.Values.Any(p => p.Command.Action == PizzaSnackAction.StepIn))
                return true;
            if (StateOf(native).TakeOutDoorOpen) return true;
            Send(new PizzaSnackCommand(Guid.NewGuid(), PizzaSnackAction.StepIn, 0, 0));
            return true;
        }

        // Guest prefix for DropOffCube.OnTriggerEnter. Host validates overlap using
        // its own item replica and only then destroys the item and runs PizzaDelivered.
        internal bool DropOffTrigger(Event_PizzaSnack_DropOffCube cube, Collider other)
        {
            if (!GuestRunning) return false;
            if (!Ready || NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true || other == null) return true;
            var box = other.GetComponentInParent<Event_PizzaSnack_PizzaBox>();
            if (box == null || !ReferenceEquals(cube.event_PizzaSnack, pizzaSnack) || !TryLocalLease(box, out ulong id, out uint generation)) return true;
            deliveryCandidates[id] = (box, generation, float.NegativeInfinity, Time.unscaledTime + 6f);
            SendDelivery(id, box, generation);
            return true;
        }

        // Host prefix: allow only an active, leased box actually overlapping the
        // authoritative target to reach vanilla delivery. Offline is left native.
        internal bool AllowNativeDropOff(Event_PizzaSnack_DropOffCube cube, Collider other)
        {
            if (!host || session?.IsRunning != true) return true;
            var box = other == null ? null : other.GetComponentInParent<Event_PizzaSnack_PizzaBox>();
            if (box == null || pizzaSnack == null || !ReferenceEquals(cube.event_PizzaSnack, pizzaSnack) ||
                !TryHostLease(box, out ulong id, out uint owner, out uint generation) ||
                !IsRunning(pizzaSnack) || !BoxOverlapsTarget(pizzaSnack, box)) return false;
            if (owner != ItemLease.Unowned && !items.NearPeer(owner, CurrentTargetPosition(pizzaSnack), 4.5f)) return false;
            if (Time.realtimeSinceStartup - lastDeliveryTime <= 1f) return false;
            if (!deliveredBoxes.Add((id, generation))) return false;
            lastDeliveryTime = Time.realtimeSinceStartup;
            nativeDeliveryPeer = owner;
            return true;
        }

        internal void NativePizzaDelivered(Event_PizzaSnack native)
        {
            if (!host || disposed || applying || handlingRequest || !ReferenceEquals(native, pizzaSnack)) return;
            FinalizeNativeDelivery(nativeDeliveryPeer);
            nativeDeliveryPeer = 0;
        }

        internal void NativeEventChanged(Event_PizzaSnack native)
        {
            if (disposed || applying || handlingRequest || !ReferenceEquals(native, pizzaSnack)) return;
            if (host) PublishState();
        }

        internal bool NativeEventRunning(Event_PizzaSnack native) => ReferenceEquals(native, pizzaSnack) && IsRunning(native);

        internal void NativeEventCompleted(Event_PizzaSnack native, bool wasRunning)
        {
            if (!host || disposed || applying || !wasRunning || !ReferenceEquals(native, pizzaSnack) || IsRunning(native)) return;
            PresentationReplays.Events?.Post(PresentationKind.PizzaSnackOutcome, 0, PizzaSnackCommand.StateEntity, native.transform.position);
        }

        internal void NativeStoreClosed(Event_PizzaSnack native, bool wasRunning)
        {
            if (!host || disposed || applying || !wasRunning || !ReferenceEquals(native, pizzaSnack) || IsRunning(native)) return;
            PresentationReplays.Events?.Post(PresentationKind.PizzaSnackOutcome, 1, PizzaSnackCommand.StateEntity, native.transform.position);
        }

        internal void PlayOutcome(PresentationEvent outcome)
        {
            if (host || disposed || outcome.Target != PizzaSnackCommand.StateEntity || outcome.Variant > 1) return;
            Singleton<UI_EventMessage>.i?.Show(TEXT.GetText(outcome.Variant == 0 ? "EVENT_PIZZASNACK_Completed" : "EVENT_PIZZASNACK_TimesUP"),
                TEXT.GetText("PizzaSnack_Name"), 6f);
        }

        internal bool SuppressGuestEventMutation(Event_PizzaSnack native)
        {
            if (!GuestRunning) return false;
            return ReferenceEquals(native, pizzaSnack);
        }

        private void PoseReceived(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void PeerLeft(uint peer) => presence.Remove(peer);

        private void Send(PizzaSnackCommand request)
        {
            if (channel == null) return;
            pending[request.Operation] = (request, Time.unscaledTime);
            try { channel.Request(PizzaSnackCommand.Entity, request.Encode(), useLease: true); }
            catch { pending.Remove(request.Operation); throw; }
        }

        private bool TryLocalLease(Event_PizzaSnack_PizzaBox box, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (host || session == null || !items.Ready || items.TransfersBlocked || box == null) return false;
            id = items.EntityOf(box);
            var state = id == 0 ? null : items.StateOf(id);
            if (state == null || !state.Exists || state.Lease.Owner != session.LocalPeerId || state.Lease.Generation == 0) return false;
            generation = state.Lease.Generation;
            return true;
        }

        private bool TryHostLease(Event_PizzaSnack_PizzaBox box, out ulong id, out uint owner, out uint generation)
        {
            id = 0; owner = ItemLease.Unowned; generation = 0;
            if (!host || box == null) return false;
            id = items.EntityOf(box);
            var state = id == 0 ? null : items.StateOf(id);
            if (state == null || !state.Exists || !ReferenceEquals(items.ItemOf(id), box)) return false;
            owner = state.Lease.Owner; generation = state.Lease.Generation;
            return generation != 0;
        }

        [MonMP.Core.World.WorldCommandAdapters(PizzaSnackCommand.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != PizzaSnackCommand.Entity || peer == 0 || channel == null || session?.Peers.ContainsKey(peer) != true ||
                payload == null || payload.Length == 0 || !PizzaSnackCommand.HasMarker(payload)) return;
            if (!PizzaSnackCommand.Is(payload)) { Debug.LogWarning("MonMP malformed PizzaSnack command."); return; }
            PizzaSnackCommand command;
            try { command = PizzaSnackCommand.Decode(payload); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid PizzaSnack request: " + ex.Message); return; }

            var claim = claims.Claim(peer, command.Operation);
            if (claim != PizzaSnackClaimResult.Claimed)
            {
                var result = claim == PizzaSnackClaimResult.Duplicate ? PizzaSnackResult.Duplicate : PizzaSnackResult.Capacity;
                if (result == PizzaSnackResult.Duplicate)
#if MONMP_DEVELOPMENT
TestDuplicates++;
#else
{}
#endif

                ReplyTo(peer, command.Operation, result); return;
            }
            var decision = ApplyHostRequest(peer, command);

#if MONMP_DEVELOPMENT
            TestLastResult = decision;
#endif

            if (decision == PizzaSnackResult.Applied)
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

            ReplyTo(peer, command.Operation, decision);
        }

        private PizzaSnackResult ApplyHostRequest(uint peer, PizzaSnackCommand command)
        {
            var native = pizzaSnack;
            if (native == null || NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true || !Ready)
                return PizzaSnackResult.StoreClosed;
            bool fresh = presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time >= 0 && Time.unscaledTime - seen.Time <= 2f;
            PlayerPose pose = fresh ? seen.Pose : default;
            if (command.Action == PizzaSnackAction.StepIn)
            {
                var detector = PlayerDetector.GetValue(native) as GameObject;
                bool near = fresh && detector != null && items.NearPeer(peer, detector.transform.position, 2.5f);
                var result = PizzaSnackPolicy.ValidateStepIn(fresh, fresh ? pose.State : byte.MaxValue,
                    IsStoreOpen(native), DoorOpen.GetValue(native) is true, near);
                if (result != PizzaSnackResult.Applied) return result;
                handlingRequest = true;
                try { PlayerStepInMethod.Invoke(native, null); }
                catch (Exception ex) { Debug.LogError("MonMP PizzaSnack door effect failed: " + ex); return PizzaSnackResult.NativeEffectFailed; }
                finally { handlingRequest = false; }
                PublishState(); return PizzaSnackResult.Applied;
            }

            if (!TryHostLease(command.Box, command.Generation, peer, out var box)) return PizzaSnackResult.InvalidLease;
            if (command.Action == PizzaSnackAction.StartEvent)
            {
                pose = pose.WithIndependentAim(command.IndependentAim);
                bool originFits = fresh && ToolAimPolicy.CameraOriginFits(pose, command.CameraX, command.CameraY, command.CameraZ, clear: checkSegment);
                bool aimFits = fresh && ToolAimPolicy.CameraAimFits(pose, command.CameraYaw, command.CameraPitch);
                bool hitBox = originFits && aimFits && HostRayHitsBox(command, box!);
                bool nearPickup = items.HostItemInRange(peer, command.Box, 4.5f);
                var result = PizzaSnackPolicy.ValidateStart(true, fresh, fresh ? pose.State : byte.MaxValue,
                    originFits, aimFits, hitBox, nearPickup, IsStoreOpen(native) && DoorOpen.GetValue(native) is true, IsRunning(native));
                if (result != PizzaSnackResult.Applied) return result;
                handlingRequest = true;
                try
                {
                    StartEventMethod.Invoke(native, null);
                    SelectClosestForPeer(native, pose);
                }
                catch (Exception ex) { Debug.LogError("MonMP PizzaSnack route start failed: " + ex); return PizzaSnackResult.NativeEffectFailed; }
                finally { handlingRequest = false; }
                PublishState(); return PizzaSnackResult.Applied;
            }

            if (command.Action == PizzaSnackAction.Deliver)
            {
                bool nearTarget = fresh && items.NearPeer(peer, CurrentTargetPosition(native), 4.5f);
                // The host's replica trails the guest's hand; the guest's latest pose counts too.
                bool overlaps = IsRunning(native) && (BoxOverlapsTarget(native, box!) ||
                    (items.TryOwnerPosition(command.Box, out var ownerAt) && Vector3.Distance(ownerAt, CurrentTargetPosition(native)) <= 1.5f));
                var result = PizzaSnackPolicy.ValidateDelivery(true, fresh, fresh ? pose.State : byte.MaxValue,
                    IsRunning(native), nearTarget, overlaps);
                if (result != PizzaSnackResult.Applied) return result;
                if (Time.realtimeSinceStartup - lastDeliveryTime <= 1f) return PizzaSnackResult.BoxNotAtDropOff;
                if (deliveredBoxes.Contains((command.Box, command.Generation))) return PizzaSnackResult.Duplicate;
                return DeliverHostBox(native, box!, command.Box, command.Generation, peer)
                    ? PizzaSnackResult.Applied : PizzaSnackResult.NativeEffectFailed;
            }
            return PizzaSnackResult.WrongTarget;
        }

        private bool TryHostLease(ulong id, uint generation, uint peer, out Event_PizzaSnack_PizzaBox? box)
        {
            box = null;
            var state = items.StateOf(id);
            if (state == null || !state.Exists || state.Lease.Owner != peer || state.Lease.Generation != generation) return false;
            box = items.ItemOf(id) as Event_PizzaSnack_PizzaBox;
            return box != null && ReferenceEquals(box, items.ItemOf(id));
        }

        private bool HostRayHitsBox(PizzaSnackCommand command, Event_PizzaSnack_PizzaBox expected)
        {
            var origin = new Vector3(command.CameraX, command.CameraY, command.CameraZ);
            var direction = Quaternion.Euler(command.CameraPitch, command.CameraYaw, 0) * Vector3.forward;
            return Physics.Raycast(new Ray(origin, direction), out var hit, PizzaSnackCommand.Reach) &&
                ReferenceEquals(hit.collider.GetComponentInParent<Event_PizzaSnack_PizzaBox>(), expected);
        }

        private static bool IsStoreOpen(Event_PizzaSnack native) => StoreOpen.GetValue(native) is true;
        private static bool IsRunning(Event_PizzaSnack native) => EventRunning.GetValue(native) is true;

        private static Transform[] Points(Event_PizzaSnack native) => DropOffPoints.GetValue(native) as Transform[] ?? Array.Empty<Transform>();
        private static List<Transform> Route(Event_PizzaSnack native) => CurrentDropOff.GetValue(native) as List<Transform> ?? new List<Transform>();
        private static Vector3 CurrentTargetPosition(Event_PizzaSnack native) => (CurrentTarget.GetValue(native) as Transform)?.position ?? native.transform.position;

        private static bool BoxOverlapsTarget(Event_PizzaSnack native, Event_PizzaSnack_PizzaBox box)
        {
            if (box.itemCollider == null || !box.itemCollider.enabled || !box.gameObject.activeInHierarchy) return false;
            var cube = DropOffCube.GetValue(native) as GameObject;
            if (cube == null || !cube.activeInHierarchy) return false;
            var targetCollider = cube.GetComponent<Collider>();
            if (targetCollider != null && targetCollider.enabled && box.itemCollider.bounds.Intersects(targetCollider.bounds)) return true;
            return Vector3.Distance(box.itemCollider.bounds.center, CurrentTargetPosition(native)) <= 1.25f;
        }

        private void DestroyHostBox(Event_PizzaSnack_PizzaBox box)
        {
            try { gameplay.Player.CheckIfHoldingItemToCancel(box); } catch { }
            box.gameObject.SetActive(false); Object.Destroy(box.gameObject);
        }

        private bool DeliverHostBox(Event_PizzaSnack native, Event_PizzaSnack_PizzaBox box, ulong id, uint generation, uint owner)
        {
            if (Time.realtimeSinceStartup - lastDeliveryTime <= 1f) return false;
            if (!deliveredBoxes.Add((id, generation))) return false;
            lastDeliveryTime = Time.realtimeSinceStartup;
            DestroyHostBox(box);
            nativeDeliveryPeer = owner;
            handlingRequest = true;
            try { PizzaDeliveredMethod.Invoke(native, null); }
            catch (Exception ex)
            {
                deliveredBoxes.Remove((id, generation));
                Debug.LogError("MonMP PizzaSnack delivery effect failed: " + ex);
                return false;
            }
            finally { handlingRequest = false; }
            FinalizeNativeDelivery(owner); nativeDeliveryPeer = 0;
            return true;
        }

        private void HostPollDeliveries()
        {
            var native = pizzaSnack;
            if (native == null || !IsRunning(native) || Time.realtimeSinceStartup - lastDeliveryTime <= 1f) return;
            foreach (var (id, item) in items.LiveItems.ToArray())
            {
                if (!(item is Event_PizzaSnack_PizzaBox box) || !BoxOverlapsTarget(native, box) ||
                    !TryHostLease(box, out ulong foundId, out uint owner, out uint generation) || foundId != id) continue;
                if (owner != ItemLease.Unowned && !items.NearPeer(owner, CurrentTargetPosition(native), 4.5f)) continue;
                DeliverHostBox(native, box, id, generation, owner);
            }
        }

        private void SendDelivery(ulong id, Event_PizzaSnack_PizzaBox box, uint generation)
        {
            var request = new PizzaSnackCommand(Guid.NewGuid(), PizzaSnackAction.Deliver, id, generation);
            if (deliveryCandidates.TryGetValue(id, out var candidate))
                deliveryCandidates[id] = (box, generation, Time.unscaledTime, candidate.Expires);
            Send(request);
        }

        private void SelectClosestForPeer(Event_PizzaSnack native, PlayerPose pose)
        {
            var route = Route(native);
            if (route.Count == 0) return;
            var player = new Vector3(pose.X, pose.Y, pose.Z);
            Transform best = route[0]; float distance = (best.position - player).sqrMagnitude;
            foreach (var candidate in route.Skip(1))
            {
                float next = (candidate.position - player).sqrMagnitude;
                if (next < distance) { distance = next; best = candidate; }
            }
            CurrentTarget.SetValue(native, best);
            ApplyTarget(native, best, running: true);
        }

        private void FinalizeNativeDelivery(uint peer)
        {
            var native = pizzaSnack;
            if (native == null) return;
            if (peer != 0 && presence.TryGetValue(peer, out var seen)) SelectClosestForPeer(native, seen.Pose);
            else if (peer == 0) { /* Native host routing already uses the host player position. */ }
            PublishState();
        }

        private void ReplyTo(uint peer, Guid operation, PizzaSnackResult result)
        {
            if (session?.Peers.ContainsKey(peer) == true)
                channel?.Reply(peer, PizzaSnackCommand.Entity, new PizzaSnackReply(operation, result).Encode());
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != PizzaSnackCommand.Entity) return;
            PizzaSnackReply reply;
            try { reply = PizzaSnackReply.Decode(payload); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid PizzaSnack reply: " + ex.Message); return; }
            if (!pending.Remove(reply.Operation, out var pendingRequest)) return;

#if MONMP_DEVELOPMENT
            TestLastResult = reply.Result;
#endif

            if (pendingRequest.Command.Action == PizzaSnackAction.Deliver)
            {
                if (reply.Result == PizzaSnackResult.Applied || reply.Result == PizzaSnackResult.Duplicate || reply.Result == PizzaSnackResult.InvalidLease)
                    deliveryCandidates.Remove(pendingRequest.Command.Box);
                else if (deliveryCandidates.TryGetValue(pendingRequest.Command.Box, out var candidate))
                    deliveryCandidates[pendingRequest.Command.Box] = (candidate.Box, candidate.Generation, pendingRequest.Time, Time.unscaledTime + 6f);
            }
            if (reply.Result != PizzaSnackResult.Applied && reply.Result != PizzaSnackResult.Duplicate)
                Debug.LogWarning("MonMP PizzaSnack action rejected: " + reply.Result);
        }

        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (host || peer != session?.LocalPeerId || entity != PizzaSnackCommand.Entity || !PizzaSnackCommand.Is(payload)) return;
            try
            {
                var request = PizzaSnackCommand.Decode(payload);
                if (pending.Remove(request.Operation))
#if MONMP_DEVELOPMENT
TestLastResult = PizzaSnackResult.StalePlayer;
#else
{}
#endif

            }
            catch (InvalidDataException) { }
        }

        internal void Update()
        {
            if (disposed) return;
            float now = Time.unscaledTime;
            if (pending.Count > 0) foreach (var request in pending.Where(pair => now - pair.Value.Time > 5f).ToArray())
            {
                pending.Remove(request.Key);
#if MONMP_DEVELOPMENT
TestLastResult = PizzaSnackResult.StalePlayer;
#endif

                if (request.Value.Command.Action == PizzaSnackAction.Deliver && deliveryCandidates.TryGetValue(request.Value.Command.Box, out var candidate))
                    deliveryCandidates[request.Value.Command.Box] = (candidate.Box, candidate.Generation, candidate.LastSend, now + 1f);
            }
            if (host && Ready) HostPollDeliveries();
            if (!GuestRunning || !Ready || pizzaSnack == null) return;
            foreach (var pair in deliveryCandidates.ToArray())
            {
                var (box, generation, lastSend, expires) = pair.Value;
                if (box == null || !box.gameObject.activeInHierarchy || now > expires ||
                    !TryLocalLease(box, out ulong currentId, out uint currentGeneration) || currentId != pair.Key || currentGeneration != generation)
                { deliveryCandidates.Remove(pair.Key); continue; }
                if (now - lastSend < 0.35f || pending.Values.Any(p => p.Command.Action == PizzaSnackAction.Deliver && p.Command.Box == pair.Key) ||
                    !BoxOverlapsTarget(pizzaSnack, box)) continue;
                SendDelivery(pair.Key, box, generation);
            }
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || !records.TryGetValue(PizzaSnackCommand.StateEntity, out var record)) return;
            ApplyState(PizzaSnackState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != PizzaSnackCommand.StateEntity || record.Adapter != PizzaSnackCommand.Adapter) return;
            ApplyState(PizzaSnackState.Decode(record.Payload));
        }

        private PizzaSnackState StateOf(Event_PizzaSnack native)
        {
            if (!host && channel != null && channel.Records.TryGetValue(PizzaSnackCommand.StateEntity, out var record))
                return PizzaSnackState.Decode(record.Payload);
            return Capture(native);
        }

        private static PizzaSnackState EmptyState() => new PizzaSnackState(false, false, false);

        private PizzaSnackState Capture(Event_PizzaSnack native)
        {
            bool open = IsStoreOpen(native);
            bool doorOpen = DoorOpen.GetValue(native) is true;
            bool running = IsRunning(native);
            if (!running) return new PizzaSnackState(open, doorOpen, false);
            Transform[] points = Points(native); var route = Route(native);
            if (points.Length > ushort.MaxValue || route.Count == 0 || route.Count > 3)
                throw new InvalidDataException("Unsupported native PizzaSnack route shape.");
            var indices = new ushort[route.Count];
            for (int i = 0; i < route.Count; i++)
            {
                int index = Array.IndexOf(points, route[i]);
                if (index < 0 || index >= ushort.MaxValue) throw new InvalidDataException("PizzaSnack route names an unknown scene point.");
                indices[i] = (ushort)index;
            }
            int target = route.FindIndex(candidate => ReferenceEquals(candidate, CurrentTarget.GetValue(native) as Transform));
            if (target < 0 || target > byte.MaxValue) throw new InvalidDataException("PizzaSnack current target is not on the route.");
            return new PizzaSnackState(open, doorOpen, true, indices, (byte)target);
        }

        private void PublishState()
        {
            if (!host || channel?.Ready != true || pizzaSnack == null) return;
            try { channel.Publish(PizzaSnackCommand.StateEntity, Capture(pizzaSnack).Encode()); }
            catch (Exception ex) { Debug.LogError("MonMP could not publish PizzaSnack state: " + ex); }
        }

        private void ApplyState(PizzaSnackState state)
        {
            if (host || pizzaSnack == null || applying) return;
            Transform[] points = Points(pizzaSnack);
            if (state.Route.Any(index => index >= points.Length || points[index] == null))
                throw new InvalidDataException("PizzaSnack state names a missing scene point.");
            applying = true;
            try
            {
                StoreOpen.SetValue(pizzaSnack, state.StoreOpen);
                DoorOpen.SetValue(pizzaSnack, state.TakeOutDoorOpen);
                EventRunning.SetValue(pizzaSnack, state.EventRunning);
                var route = Route(pizzaSnack); route.Clear();
                foreach (ushort index in state.Route) route.Add(points[index]);
                Transform? target = state.EventRunning ? route[state.TargetIndex] : null;
                CurrentTarget.SetValue(pizzaSnack, target);
                var detector = PlayerDetector.GetValue(pizzaSnack) as GameObject;
                detector?.SetActive(state.StoreOpen && !state.TakeOutDoorOpen);
                foreach (var visual in pizzaSnack.EnableWhenStoreIsOpen ?? Array.Empty<GameObject>())
                    if (visual != null) visual.SetActive(state.StoreOpen);
                foreach (var visual in pizzaSnack.EnableWhenStoreIsClose ?? Array.Empty<GameObject>())
                    if (visual != null) visual.SetActive(!state.StoreOpen);
                foreach (var listing in pizzaSnack.ItemForSale)
                    if (listing != null) listing.gameObject.SetActive(state.StoreOpen);
                pizzaSnack.StoreDoorsStillOpen.Clear();
                if (!state.StoreOpen) pizzaSnack.StoreDoorsStillOpen.AddRange(pizzaSnack.StoreDoors);
                var door = Door.GetValue(pizzaSnack) as Transform;
                var doorPosition = (state.TakeOutDoorOpen ? DoorUp.GetValue(pizzaSnack) : DoorOriginal.GetValue(pizzaSnack)) as Transform;
                if (door != null && doorPosition != null) door.position = doorPosition.position;
                ApplyTarget(pizzaSnack, target, state.EventRunning);
            }
            finally { applying = false; }
        }

        private static void ApplyTarget(Event_PizzaSnack native, Transform? target, bool running)
        {
            var arrow = CanvasArrow.GetValue(native) as Canvas_3D_Arrow;
            if (arrow == null) { arrow = Object.FindObjectOfType<Canvas_3D_Arrow>(); CanvasArrow.SetValue(native, arrow); }
            if (arrow != null) arrow.Set_Target = running ? target : null;
            var cube = DropOffCube.GetValue(native) as GameObject;
            if (cube == null) return;
            cube.SetActive(running);
            if (running && target != null) cube.transform.position = target.position;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
                channel.CommandRequested -= Command; channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply;
            }
            if (session != null) { session.PoseReceived -= PoseReceived; session.PeerLeft -= PeerLeft; }
            if (!host) RestoreBaseline();
            pending.Clear(); deliveryCandidates.Clear(); presence.Clear(); deliveredBoxes.Clear(); claims.Clear();
            channel = null; session = null;
        }
    }
}
