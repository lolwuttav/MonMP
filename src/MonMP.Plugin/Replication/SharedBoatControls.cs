using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH;
using NWH.DWP2.ShipController;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Host-owned controls for the two stock boats. Boat transforms, steering and engine input are
    // intentionally outside this adapter; unsupported/malformed native shapes never get patched
    // into multiplayer control.
    internal sealed class SharedBoatControls : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public string Identity = "";
            public BoatControlState State, Original;
            public AdvancedShipController Ship = null!;
            public Boat_Anchor? Anchor;
            public Vehicle_IgnitionSwitch? Ignition;
            public Boat_SmallCruiser_FuelCap? CapWrapper;
            public Part_FuelTankCap? FuelCap;
            public BoxCollider? WrapperCollider, CapCollider;
            public bool OriginalWrapperEnabled, OriginalWrapperColliderEnabled;
            public bool OriginalFuelCapEnabled, OriginalCapColliderEnabled;
        }

        private const float InteractionRangeSquared = 25f;
        private static readonly FieldInfo AnchorShip = AccessTools.Field(typeof(Boat_Anchor), "shipController");
        private static readonly FieldInfo AnchorVisual = AccessTools.Field(typeof(Boat_Anchor), "Visual_AnchorIsUp");
        private static readonly MethodInfo AnchorToggle = AccessTools.Method(typeof(Boat_Anchor), "Toggle_Anchor");
        private static readonly FieldInfo IgnitionVehicle = AccessTools.Field(typeof(Vehicle_IgnitionSwitch), "vehicleController");
        private static readonly FieldInfo IgnitionSound = AccessTools.Field(typeof(Vehicle_IgnitionSwitch), "sound_vehicle_Switch");
        private static readonly FieldInfo FuelCapTarget = AccessTools.Field(typeof(Boat_SmallCruiser_FuelCap), "fuelCap");
        private static readonly MethodInfo FuelCapEnable = AccessTools.Method(typeof(Boat_SmallCruiser_FuelCap), "EnableCap");
        private static readonly MethodInfo FuelCapLoad = AccessTools.Method(typeof(Boat_SmallCruiser_FuelCap), "onLoadSaveSystem");

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly NativeEntityRegistry identities;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Anchor, Binding> byAnchor = new Dictionary<Anchor, Binding>();
        private readonly Dictionary<Vehicle, Binding> byIgnitionVehicle = new Dictionary<Vehicle, Binding>();
        private readonly Dictionary<Vehicle_IgnitionSwitch, Binding> byIgnition = new Dictionary<Vehicle_IgnitionSwitch, Binding>();
        private readonly Dictionary<Boat_SmallCruiser_FuelCap, Binding> byFuelCapWrapper = new Dictionary<Boat_SmallCruiser_FuelCap, Binding>();
        private readonly Dictionary<Vehicle_IgnitionSwitch, uint> ignitionOwners = new Dictionary<Vehicle_IgnitionSwitch, uint>();
        private readonly Dictionary<AdvancedShipController, (uint Operation, uint Generation, bool Confirmed)> disconnectAnchors =
            new Dictionary<AdvancedShipController, (uint, uint, bool)>();
        private uint nextDisconnectOperation;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;

        internal int Count => bindings.Count;
        internal bool Ready => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true &&
            MonMPPlugin.Instance?.World?.PersonalProfileReady == true && MonMPPlugin.Instance?.World?.SaveGate.Active != true;
        private bool SessionRunning => !disposed && session?.IsRunning == true;

        internal SharedBoatControls(Gameplay gameplay, bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay;
            this.host = host;
            this.identities = identities;
            var lines = new List<string> { "id\tkind\tboat\tidentity\tstate" };
            var boats = UnityEngine.Object.FindObjectsOfType<AdvancedShipController>(true)
                .Where(IsSupportedBoat)
                .OrderBy(boat => SharedBuildingDoors.StablePath(boat.transform), StringComparer.Ordinal).ToArray();

            foreach (var boat in boats)
            {
                if (boat.Anchor == null || boat.vehicleRigidbody == null || boat.Anchor.GetComponentInParent<AdvancedShipController>() != boat)
                {
                    lines.Add("-\tboat\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(boat.transform) + "\tmissing-anchor-or-rigidbody");
                    continue;
                }

                var anchors = boat.GetComponentsInChildren<Boat_Anchor>(true)
                    .Where(anchor => anchor != null && AnchorShip?.GetValue(anchor) as AdvancedShipController == boat)
                    .ToArray();
                string anchorReason = "expected-one-bound-anchor-found-" + anchors.Length;
                if (anchors.Length != 1 || !SupportedAnchor(anchors.FirstOrDefault(), boat, out anchorReason))
                {
                    lines.Add("-\tanchor\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(boat.transform) + "\t" +
                        (anchors.Length == 1 ? anchorReason : "expected-one-bound-anchor-found-" + anchors.Length));
                }
                // An unsupported anchor must not keep the ignition or fuel cap from binding.
                else Add(anchors[0].gameObject, boat, BoatControlKind.Anchor, anchors[0], null, null, null, null,
                    boat.Anchor.Dropped ? (byte)1 : (byte)0, "anchor-v1", lines);

                if (boat.vehicle_type == Vehicle_Type.FishingBoat)
                {
                    var switches = boat.GetComponentsInChildren<Vehicle_IgnitionSwitch_FishingBoat>(true)
                        .Where(ignition => ignition != null && IgnitionVehicle?.GetValue(ignition) as Vehicle == boat)
                        .ToArray();
                    string ignitionReason = "expected-one-bound-fishing-boat-ignition-found-" + switches.Length;
                    if (switches.Length != 1 || !SupportedIgnition(switches.FirstOrDefault(), boat, BoatControlKind.FishingBoatIgnition, out ignitionReason))
                    {
                        lines.Add("-\tignition\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(boat.transform) + "\t" +
                            (switches.Length == 1 ? ignitionReason : "expected-one-bound-fishing-boat-ignition-found-" + switches.Length));
                        continue;
                    }
                    var ignition = switches[0];
                    Add(ignition.gameObject, boat, BoatControlKind.FishingBoatIgnition, null, ignition, null, null, null,
                        checked((byte)boat.Get_IgnitionPosition), "fishing-boat-ignition-v1", lines);
                }
                else
                {
                    var switches = boat.GetComponentsInChildren<Vehicle_IgnitionSwitch>(true)
                        .Where(ignition => ignition != null && ignition.GetType() == typeof(Vehicle_IgnitionSwitch) &&
                            IgnitionVehicle?.GetValue(ignition) as Vehicle == boat)
                        .ToArray();
                    string ignitionReason = "expected-one-bound-small-cruiser-ignition-found-" + switches.Length;
                    if (switches.Length != 1 || !SupportedIgnition(switches.FirstOrDefault(), boat, BoatControlKind.SmallCruiserIgnition, out ignitionReason))
                    {
                        lines.Add("-\tignition\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(boat.transform) + "\t" +
                            (switches.Length == 1 ? ignitionReason : "expected-one-bound-small-cruiser-ignition-found-" + switches.Length));
                    }
                    else
                    {
                        var ignition = switches[0];
                        Add(ignition.gameObject, boat, BoatControlKind.SmallCruiserIgnition, null, ignition, null, null, null,
                            checked((byte)boat.Get_IgnitionPosition), "small-cruiser-ignition-v1", lines);
                    }

                    var wrappers = boat.GetComponentsInChildren<Boat_SmallCruiser_FuelCap>(true)
                        .Where(wrapper => wrapper != null && FuelCapTarget?.GetValue(wrapper) is Part_FuelTankCap cap &&
                            cap.GetComponentInParent<AdvancedShipController>() == boat)
                        .ToArray();
                    string capReason = "native-fuel-cap-api-or-topology";
                if (wrappers.Length > 1)
                {
                    lines.Add("-\tfuel-cap\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(boat.transform) + "\tambiguous-wrapper-count-" + wrappers.Length);
                }
                else if (wrappers.Length == 1 && SupportedFuelCap(wrappers[0], boat, out var cap, out capReason))
                {
                    var wrapperCollider = wrappers[0].GetComponent<BoxCollider>();
                    var capCollider = cap.GetComponent<BoxCollider>();
                    bool active = !wrappers[0].enabled && wrapperCollider != null && !wrapperCollider.enabled &&
                        cap.enabled && capCollider != null && capCollider.enabled;
                    bool inactive = wrappers[0].enabled && wrapperCollider != null && wrapperCollider.enabled &&
                        !cap.enabled && capCollider != null && !capCollider.enabled;
                    bool purchased = BoughtSmallCruiser();
                    if (!active && !inactive)
                    {
                        lines.Add("-\tfuel-cap\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(wrappers[0].transform) +
                            "\tincoherent-native-cap-state");
                    }
                    else if (active != purchased)
                    {
                        lines.Add("-\tfuel-cap\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(wrappers[0].transform) +
                            "\tlocal-purchase-cap-state-mismatch");
                    }
                    else Add(wrappers[0].gameObject, boat, BoatControlKind.SmallCruiserFuelCap, null, null, wrappers[0], cap,
                        wrapperCollider, active ? (byte)1 : (byte)0, "small-cruiser-fuel-cap-v1", lines);
                    }
                    else if (wrappers.Length == 1)
                    {
                        lines.Add("-\tfuel-cap\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(wrappers[0].transform) + "\t" + capReason);
                    }
                }
            }


#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-boat-controls.tsv"), lines);
#endif


            void Add(GameObject target, AdvancedShipController boat, BoatControlKind kind, Boat_Anchor? anchor,
                Vehicle_IgnitionSwitch? ignition, Boat_SmallCruiser_FuelCap? wrapper,
                Part_FuelTankCap? cap, BoxCollider? wrapperCollider, byte value, string suffix, List<string> report)
            {
                // The Small Cruiser's wrapper rides on its fuel cap, which native reparents when open.
                string identity = (wrapper != null && cap != null && wrapper.transform.IsChildOf(cap.transform)
                    ? SharedBuildingDoors.StablePath(SharedFuel.CapHome(cap)) + "/" + StaticId.Segment(cap.gameObject.name, 0) + "/" + StaticId.Segment(wrapper.gameObject.name, 0)
                    : SharedBuildingDoors.StablePath(target.transform)) + "/" + suffix;
                ulong id = StaticId.Hash(identity);
                if (id == 0 || bindings.ContainsKey(id) || identities.Resolve(id) != null)
                {
                    report.Add("-\t" + kind + "\t" + boat.vehicle_type + "\t" + identity + "\tidentity-collision");
                    return;
                }
                var state = new BoatControlState(kind, value);
                var binding = new Binding { Id = id, Identity = identity, Ship = boat, State = state, Original = state,
                    Anchor = anchor, Ignition = ignition, CapWrapper = wrapper, FuelCap = cap, WrapperCollider = wrapperCollider,
                    CapCollider = cap?.GetComponent<BoxCollider>(), OriginalWrapperEnabled = wrapper != null && wrapper.enabled,
                    OriginalWrapperColliderEnabled = wrapperCollider != null && wrapperCollider.enabled,
                    OriginalFuelCapEnabled = cap != null && cap.enabled,
                    OriginalCapColliderEnabled = cap != null && cap.GetComponent<BoxCollider>().enabled };
                identities.Bind(id, BoatControlState.Adapter, target, identity, true, EntityPersistence.SessionOnly);
                bindings.Add(id, binding);
                if (anchor != null) byAnchor.Add(boat.Anchor, binding);
                if (ignition != null) { byIgnition.Add(ignition, binding); byIgnitionVehicle.Add(boat, binding); }
                if (wrapper != null) byFuelCapWrapper.Add(wrapper, binding);
                report.Add(id.ToString("X16") + "\t" + kind + "\t" + boat.vehicle_type + "\t" + identity + "\t" + value);
            }
        }

        private static bool IsSupportedBoat(AdvancedShipController boat) => boat != null && boat.gameObject.scene.IsValid() &&
            boat.gameObject.scene.isLoaded && (boat.vehicle_type == Vehicle_Type.FishingBoat || boat.vehicle_type == Vehicle_Type.SmallCruiser);

        private static bool SupportedAnchor(Boat_Anchor? anchor, AdvancedShipController boat, out string reason)
        {
            if (anchor == null || AnchorShip == null || AnchorShip.FieldType != typeof(AdvancedShipController) ||
                AnchorToggle == null || AnchorToggle.DeclaringType != typeof(Boat_Anchor) || AnchorToggle.ReturnType != typeof(void) ||
                AnchorToggle.GetParameters().Length != 0 || !AnchorToggle.IsPrivate || AnchorVisual == null ||
                AnchorVisual.FieldType != typeof(GameObject) || !AnchorVisual.IsPrivate || AnchorShip.GetValue(anchor) as AdvancedShipController != boat ||
                anchor.GetComponent<SOUND_Boat_Anchor>() == null || boat.Anchor == null ||
                anchor.GetComponentInParent<AdvancedShipController>() != boat ||
                (boat.vehicle_type == Vehicle_Type.FishingBoat) != (anchor is Boat_Anchor_FishingBoat))
            { reason = "native-anchor-api-or-topology"; return false; }
            reason = "supported"; return true;
        }

        private static bool SupportedIgnition(Vehicle_IgnitionSwitch? ignition, AdvancedShipController boat,
            BoatControlKind kind, out string reason)
        {
            bool supportedType = kind == BoatControlKind.FishingBoatIgnition
                ? ignition is Vehicle_IgnitionSwitch_FishingBoat && boat.vehicle_type == Vehicle_Type.FishingBoat
                : kind == BoatControlKind.SmallCruiserIgnition && ignition?.GetType() == typeof(Vehicle_IgnitionSwitch) &&
                    boat.vehicle_type == Vehicle_Type.SmallCruiser;
            if (ignition == null || IgnitionVehicle == null || IgnitionVehicle.FieldType != typeof(Vehicle) ||
                !supportedType || IgnitionVehicle.GetValue(ignition) as Vehicle != boat || IgnitionSound == null ||
                IgnitionSound.FieldType != typeof(SOUND_Vehicle_Switch) || IgnitionSound.GetValue(ignition) as SOUND_Vehicle_Switch == null ||
                ignition.GetComponentInParent<AdvancedShipController>() != boat || boat.Get_IgnitionPosition < 0 || boat.Get_IgnitionPosition > 2)
            { reason = "native-ignition-api-or-topology"; return false; }
            reason = "supported"; return true;
        }

        private static bool SupportedFuelCap(Boat_SmallCruiser_FuelCap? wrapper, AdvancedShipController boat,
            out Part_FuelTankCap cap, out string reason)
        {
            cap = FuelCapTarget?.GetValue(wrapper) as Part_FuelTankCap ?? null!;
            var wrapperCollider = wrapper == null ? null : wrapper.GetComponent<BoxCollider>();
            var capCollider = cap == null ? null : cap.GetComponent<BoxCollider>();
            if (wrapper == null || boat.vehicle_type != Vehicle_Type.SmallCruiser || FuelCapTarget == null ||
                FuelCapTarget.FieldType != typeof(Part_FuelTankCap) || !FuelCapTarget.IsPrivate ||
                FuelCapEnable == null || FuelCapEnable.DeclaringType != typeof(Boat_SmallCruiser_FuelCap) ||
                FuelCapEnable.ReturnType != typeof(void) || FuelCapEnable.GetParameters().Length != 0 || !FuelCapEnable.IsPrivate ||
                FuelCapLoad == null || FuelCapLoad.DeclaringType != typeof(Boat_SmallCruiser_FuelCap) ||
                FuelCapLoad.ReturnType != typeof(void) || FuelCapLoad.GetParameters().Length != 0 || !FuelCapLoad.IsPrivate ||
                cap == null || wrapperCollider == null || capCollider == null || cap.GetComponentInParent<AdvancedShipController>() != boat ||
                wrapper.GetComponentInParent<AdvancedShipController>() != boat || cap == wrapper.GetComponent<Part_FuelTankCap>())
            { reason = "native-fuel-cap-api-or-topology"; return false; }
            reason = "supported"; return true;
        }

        private static bool BoughtFishingBoat() => Singleton<ToDoList_Manager>.i != null &&
            Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Boat_Fishing_Bought);
        private static bool BoughtSmallCruiser() => Singleton<ToDoList_Manager>.i != null &&
            Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Boat_SmallCruiserBought);

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(binding =>
            new WorldRecord(binding.Id, 1, BoatControlState.Adapter, binding.State.Encode()));

        internal void Update()
        {
            if (!host) return;
            if (!Ready && ignitionOwners.Count != 0) ReleaseHeldIgnitions();
            if (!Ready) return;
            foreach (var binding in bindings.Values) PublishNative(binding);
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += Pose; session.PeerLeft += Left;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.ReplyReceived += DisconnectAnchorReply;
        }

        private void Pose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void Left(uint peer)
        {
            presence.Remove(peer);
            if (!host) return;
            ReleaseHeldIgnitions(peer);
        }
        private void ReleaseHeldIgnitions(uint? peer = null)
        {
            foreach (var pair in ignitionOwners.Where(pair => !peer.HasValue || pair.Value == peer.Value).ToArray())
            {
                applying = true;
                try { if (pair.Key != null) pair.Key.MouseUp(); }
                finally { applying = false; ignitionOwners.Remove(pair.Key); }
            }
        }
        private bool PeerPosition(uint peer, Binding binding, out Vector3 position)
        {
            position = default;
            if (peer == 0)
            {
                if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.PlayerWalking == null) return false;
                position = gameplay.PlayerWalking.transform.position; return true;
            }
            if (!presence.TryGetValue(peer, out var sample) || Time.unscaledTime - sample.Time > 2f) return false;
            // Native controls remain clickable from the driver seat. A seated pose alone
            // is not authority: the host must have leased this exact boat to the sender.
            bool walking = sample.Pose.State == (byte)PlayerState.Walking;
            bool drivingThisBoat = sample.Pose.State == (byte)PlayerState.InVehicle_Driver &&
                MonMPPlugin.Instance?.World?.BoatMotion.VehicleForPeer(peer) == binding.Ship;
            if (!walking && !drivingThisBoat) return false;
            position = new Vector3(sample.Pose.X, sample.Pose.Y, sample.Pose.Z); return true;
        }
        private static bool Near(Vector3 position, Transform target) => (position - target.position).sqrMagnitude <= InteractionRangeSquared;

        internal bool AnchorInput(Anchor anchor, bool dropped)
        {
            if (!SessionRunning || applying) return true;
            if (!byAnchor.TryGetValue(anchor, out var binding))
            {
                var boat = anchor.GetComponentInParent<AdvancedShipController>();
                return boat == null || !IsSupportedBoat(boat);
            }
            if (host) return true;
            Request(binding, new BoatControlCommand(BoatControlKind.Anchor, BoatControlAction.SetAnchor, dropped ? (byte)1 : (byte)0));
            return false;
        }

        internal void AnchorChanged(Anchor anchor)
        {
            if (!host || applying || !Ready || !byAnchor.TryGetValue(anchor, out var binding)) return;
            PublishNative(binding);
        }

        // Session shutdown requires a fresh correlated driver-generation reply,
        // even if the local anchor snapshot already looks lowered.
        internal bool RequestDisconnectAnchor(AdvancedShipController ship, uint generation)
        {
            if (!SessionRunning || channel?.Ready != true || !byAnchor.TryGetValue(ship.Anchor, out var binding)) return false;
            if (host) return DropAnchorOnDisconnect(ship);
            if (!disconnectAnchors.TryGetValue(ship, out var pending) || pending.Generation != generation)
            {
                if (++nextDisconnectOperation == 0) nextDisconnectOperation = 1;
                pending = (nextDisconnectOperation, generation, false);
                disconnectAnchors[ship] = pending;
            }
            channel.Request(binding.Id, new BoatDisconnectAnchorCommand(pending.Operation, generation).Encode(), useLease: true);
            return true;
        }

        internal bool DisconnectAnchorConfirmed(AdvancedShipController ship, uint generation) =>
            ship.Anchor.Dropped && disconnectAnchors.TryGetValue(ship, out var pending) &&
            pending.Generation == generation && pending.Confirmed;

        internal void CancelDisconnectAnchor(AdvancedShipController ship) => disconnectAnchors.Remove(ship);

        private void DisconnectAnchorReply(ulong entity, byte[] payload)
        {
            if (host || !BoatDisconnectAnchorCommand.HasMarker(payload)) return;
            var binding = bindings.TryGetValue(entity, out var found) ? found : null;
            if (binding == null || binding.Anchor == null || !disconnectAnchors.TryGetValue(binding.Ship, out var pending)) return;
            BoatDisconnectAnchorCommand reply; bool dropped;
            try { reply = BoatDisconnectAnchorCommand.DecodeReply(payload, out dropped); }
            catch (InvalidDataException) { return; }
            if (!dropped || reply.Operation != pending.Operation || reply.DriverGeneration != pending.Generation) return;
            Apply(binding, new BoatControlState(BoatControlKind.Anchor, 1));
            disconnectAnchors[binding.Ship] = (pending.Operation, pending.Generation, true);
        }

        internal bool DropAnchorOnDisconnect(AdvancedShipController ship)
        {
            if (!host || ship == null || ship.Anchor == null) return false;
            if (!byAnchor.TryGetValue(ship.Anchor, out var binding) || binding.Anchor == null) return false;
            if (!ship.Anchor.Dropped)
            {
                applying = true;
                try { AnchorToggle.Invoke(binding.Anchor, null); }
                finally { applying = false; }
            }
            if (channel?.Ready == true) PublishNative(binding);
            return ship.Anchor.Dropped;
        }

        internal bool AnchorClick(Boat_Anchor anchor)
        {
            if (!SessionRunning || applying) return true;
            // The clicked Boat_Anchor need not carry the Anchor component itself; match the bound one.
            var binding = anchor == null ? null : bindings.Values.FirstOrDefault(value => value.Anchor == anchor);
            if (binding == null) return false;
            if (host) return true;
            Request(binding, new BoatControlCommand(BoatControlKind.Anchor, BoatControlAction.SetAnchor,
                binding.State.Value == 0 ? (byte)1 : (byte)0));
            return false;
        }

        internal bool IgnitionInput(Vehicle_IgnitionSwitch ignition, bool down)
        {
            if (!SessionRunning || applying) return true;
            if (!byIgnition.TryGetValue(ignition, out var binding))
            {
                var boat = ignition.GetComponentInParent<AdvancedShipController>();
                return host || boat == null || !IsSupportedBoat(boat);
            }
            if (host) return true;
            // Aye, Captain!: buying the cruiser happens once per world, so a guest who joined later earns it at its ignition.
            if (down && binding.State.Kind == BoatControlKind.SmallCruiserIgnition && Singleton<ToDoList_Manager>.i?.isDone(ToDoList_Line.Boat_SmallCruiserBought) == true)
                GuestAchievements.AwardToDo(ToDoList_Line.Boat_SmallCruiserBought);
            Request(binding, new BoatControlCommand(binding.State.Kind,
                down ? BoatControlAction.IgnitionMouseDown : BoatControlAction.IgnitionMouseUp));
            return false;
        }

        internal bool CanSetIgnition(Vehicle vehicle)
        {
            if (!SessionRunning || applying) return true;
            if (vehicle == null || (vehicle.vehicle_type != Vehicle_Type.FishingBoat && vehicle.vehicle_type != Vehicle_Type.SmallCruiser)) return true;
            if (!host) return false;
            if (!byIgnitionVehicle.TryGetValue(vehicle, out var binding))
                return vehicle.vehicle_type == Vehicle_Type.SmallCruiser && Ready && MonMPPlugin.Instance?.World?.SaveGate.Active != true;
            return Ready && !MonMPPlugin.Instance!.World!.SaveGate.Active;
        }

        internal void IgnitionChanged(Vehicle vehicle)
        {
            if (!host || applying || !Ready || !byIgnitionVehicle.TryGetValue(vehicle, out var binding)) return;
            PublishNative(binding);
        }

        internal bool FuelCapClick(Boat_SmallCruiser_FuelCap wrapper)
        {
            if (!SessionRunning || applying) return true;
            if (!byFuelCapWrapper.TryGetValue(wrapper, out var binding)) return false;
            if (host) return true;
            Request(binding, new BoatControlCommand(BoatControlKind.SmallCruiserFuelCap, BoatControlAction.ActivateSmallCruiserFuelCap));
            return false;
        }

        internal void FuelCapActivated(Boat_SmallCruiser_FuelCap wrapper)
        {
            if (!host || applying || !Ready || !byFuelCapWrapper.TryGetValue(wrapper, out var binding)) return;
            PublishNative(binding);
        }

        internal void FuelCapLoaded(Boat_SmallCruiser_FuelCap wrapper)
        {
            if (!SessionRunning || applying || !byFuelCapWrapper.TryGetValue(wrapper, out var binding)) return;
            if (host)
            {
                if (Ready) PublishNative(binding);
            }
            else if (channel?.Records.TryGetValue(binding.Id, out var record) == true) Apply(binding, BoatControlState.Decode(record.Payload));
        }

        private void Request(Binding binding, BoatControlCommand command)
        {
            if (!Ready || channel?.Records.TryGetValue(binding.Id, out var record) != true || record.Adapter != BoatControlState.Adapter) return;
            // A press can mutate the record before its matching release reaches this client.
            // These bounded state/action commands are revalidated by the host, so do not make a
            // quick release depend on receiving that intermediate delta first.
            channel.Request(binding.Id, command.Encode(), useLease: true);
        }

        private bool Usable(Binding binding)
        {
            var identity = identities.Resolve(binding.Id);
            return identity != null && identity.IsStaticSceneEntity && identity.AdapterType == BoatControlState.Adapter &&
                identity.SpawnKey == binding.Identity && identity.Persistence == EntityPersistence.SessionOnly &&
                binding.Ship != null && binding.Ship.gameObject.scene.IsValid() && binding.Ship.gameObject.scene.isLoaded &&
                channel?.Records.TryGetValue(binding.Id, out var record) == true && record.Adapter == BoatControlState.Adapter;
        }

        [MonMP.Core.World.WorldCommandAdapters(BoatControlState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !SessionRunning || channel?.Ready != true || !bindings.TryGetValue(id, out var binding) || !Usable(binding)) return;
            if (BoatDisconnectAnchorCommand.HasMarker(payload))
            {
                BoatDisconnectAnchorCommand request;
                try { request = BoatDisconnectAnchorCommand.Decode(payload); }
                catch (InvalidDataException) { return; }
                if (binding.State.Kind != BoatControlKind.Anchor ||
                    MonMPPlugin.Instance?.World?.BoatMotion.PrepareDriverDisconnect(binding.Ship, peer, request.DriverGeneration) != true) return;
                channel.Reply(peer, id, request.Reply(DropAnchorOnDisconnect(binding.Ship)));
                return;
            }
            BoatControlCommand command;
            try { command = BoatControlCommand.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (command.Kind != binding.State.Kind) return;
            if (!Ready || !PeerPosition(peer, binding, out var at)) return;
            Component target = (Component?)binding.Anchor ?? binding.Ignition ?? (Component?)binding.CapWrapper!;
            if (target == null || !Near(at, target.transform)) return;
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;

            applying = true;
            try
            {
                switch (command.Action)
                {
                    case BoatControlAction.SetAnchor:
                        if (binding.Anchor == null || (binding.Ship.vehicle_type == Vehicle_Type.FishingBoat && !BoughtFishingBoat()) ||
                            binding.Ship.Anchor.Dropped == (command.Value != 0)) return;
                        binding.Anchor.MouseDown();
                        if (binding.Ship.Anchor.Dropped != (command.Value != 0)) return;
                        break;
                    case BoatControlAction.ActivateSmallCruiserFuelCap:
                        if (binding.CapWrapper == null || binding.State.Value != 0 || !BoughtSmallCruiser()) return;
                        FuelCapEnable.Invoke(binding.CapWrapper, null);
                        if (!CapActivated(binding)) return;
                        break;
                    case BoatControlAction.IgnitionMouseDown:
                        if (binding.Ignition == null || !IgnitionPurchaseAllowed(binding) || ignitionOwners.ContainsKey(binding.Ignition)) return;
                        binding.Ignition.MouseDown();
                        ignitionOwners.Add(binding.Ignition, peer);
                        break;
                    case BoatControlAction.IgnitionMouseUp:
                        if (binding.Ignition == null || !IgnitionPurchaseAllowed(binding) ||
                            !ignitionOwners.TryGetValue(binding.Ignition, out var owner) || owner != peer) return;
                        binding.Ignition.MouseUp();
                        ignitionOwners.Remove(binding.Ignition);
                        break;
                    default: return;
                }
            }
            finally { applying = false; }
            PublishNative(binding);
        }

        private static bool IgnitionPurchaseAllowed(Binding binding) => binding.State.Kind switch
        {
            BoatControlKind.FishingBoatIgnition => BoughtFishingBoat(),
            // The native small-cruiser key has no purchase check; retain its local behavior.
            BoatControlKind.SmallCruiserIgnition => true,
            _ => false
        };

        private static bool CapActivated(Binding binding) => binding.CapWrapper != null && binding.WrapperCollider != null &&
            binding.FuelCap != null && binding.CapCollider != null && !binding.CapWrapper.enabled && !binding.WrapperCollider.enabled &&
            binding.FuelCap.enabled && binding.CapCollider.enabled;

        private BoatControlState Capture(Binding binding)
        {
            byte value = binding.State.Kind switch
            {
                BoatControlKind.Anchor => binding.Ship.Anchor.Dropped ? (byte)1 : (byte)0,
                BoatControlKind.FishingBoatIgnition or BoatControlKind.SmallCruiserIgnition => checked((byte)binding.Ship.Get_IgnitionPosition),
                BoatControlKind.SmallCruiserFuelCap => CapActivated(binding) ? (byte)1 : (byte)0,
                _ => throw new InvalidDataException("Unknown boat control kind.")
            };
            return new BoatControlState(binding.State.Kind, value);
        }

        private void PublishNative(Binding binding)
        {
            BoatControlState next;
            try { next = Capture(binding); }
            catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException) { return; }
            if (next == binding.State) return;
            binding.State = next;
            channel!.Publish(binding.Id, next.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var staged = records.Values.Where(record => record.Adapter == BoatControlState.Adapter)
                .Select(record => (Binding: bindings.TryGetValue(record.Id, out var binding) ? binding : throw new InvalidDataException("Unknown boat-control record."),
                    State: BoatControlState.Decode(record.Payload))).ToArray();
            foreach (var item in staged) Apply(item.Binding, item.State);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == BoatControlState.Adapter)
                Apply(bindings.TryGetValue(record.Id, out var binding) ? binding : throw new InvalidDataException("Unknown boat-control record."),
                    BoatControlState.Decode(record.Payload));
        }

        private void Apply(Binding binding, BoatControlState state)
        {
            if (state.Kind != binding.State.Kind) throw new InvalidDataException("Boat-control record kind mismatch.");
            applying = true;
            try
            {
                switch (state.Kind)
                {
                    case BoatControlKind.Anchor:
                        if (binding.Anchor == null) throw new InvalidDataException("Boat anchor binding disappeared.");
                        if (binding.Ship.Anchor.Dropped != (state.Value != 0)) AnchorToggle.Invoke(binding.Anchor, null);
                        break;
                    case BoatControlKind.FishingBoatIgnition:
                    case BoatControlKind.SmallCruiserIgnition:
                        if (binding.Ignition == null) throw new InvalidDataException("Fishing-boat ignition binding disappeared.");
                        if (binding.Ship.Get_IgnitionPosition != state.Value) binding.Ignition.LoadFromSave(state.Value);
                        break;
                    case BoatControlKind.SmallCruiserFuelCap:
                        ApplyCapActivation(binding, state.Value != 0);
                        break;
                }
            }
            finally { applying = false; }
            binding.State = state;
        }

        private static void ApplyCapActivation(Binding binding, bool active)
        {
            if (binding.CapWrapper == null || binding.WrapperCollider == null || binding.FuelCap == null || binding.CapCollider == null)
                throw new InvalidDataException("Small-cruiser fuel-cap binding disappeared.");
            binding.CapWrapper.enabled = !active;
            binding.WrapperCollider.enabled = !active;
            binding.FuelCap.enabled = active;
            binding.CapCollider.enabled = active;
        }


#if MONMP_DEVELOPMENT
        internal ulong TestId(BoatControlKind kind) => bindings.Values.Where(binding => binding.State.Kind == kind)
            .OrderBy(binding => binding.Id).Select(binding => binding.Id).FirstOrDefault();


        internal BoatControlState TestState(ulong id) => bindings[id].State;


        internal BoatControlState TestOriginal(ulong id) => bindings[id].Original;


        internal Anchor? TestAnchor(ulong id) => bindings[id].Ship.Anchor;


        internal Vehicle_IgnitionSwitch? TestIgnition(ulong id) => bindings[id].Ignition;


        internal Boat_SmallCruiser_FuelCap? TestFuelCapWrapper(ulong id) => bindings[id].CapWrapper;


        internal void TestRequest(ulong id, BoatControlCommand command) => channel!.Request(id, command.Encode());
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= Left; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.ReplyReceived -= DisconnectAnchorReply; }
            disconnectAnchors.Clear();
            if (host) ReleaseHeldIgnitions();
            if (!host)
            {
                applying = true;
                try
                {
                    foreach (var binding in bindings.Values)
                    {
                        if (binding.Anchor != null && binding.Ship.Anchor.Dropped != (binding.Original.Value != 0)) AnchorToggle.Invoke(binding.Anchor, null);
                        else if (binding.Ignition != null && binding.Ship.Get_IgnitionPosition != binding.Original.Value)
                            binding.Ignition.LoadFromSave(binding.Original.Value);
                        else if (binding.CapWrapper != null) ApplyOriginalCap(binding);
                    }
                }
                finally { applying = false; }
            }
            session = null; channel = null;
        }

        private static void ApplyOriginalCap(Binding binding)
        {
            if (binding.CapWrapper == null || binding.WrapperCollider == null || binding.FuelCap == null || binding.CapCollider == null) return;
            binding.CapWrapper.enabled = binding.OriginalWrapperEnabled;
            binding.WrapperCollider.enabled = binding.OriginalWrapperColliderEnabled;
            binding.FuelCap.enabled = binding.OriginalFuelCapEnabled;
            binding.CapCollider.enabled = binding.OriginalCapColliderEnabled;
        }
    }
}
