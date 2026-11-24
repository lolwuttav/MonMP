using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Small host-owned metadata stream for rear toe controls and dashboard odometer display.
    // The odometer is projected onto the gauge only; guest Vehicle.TotalKM stays personal.
    internal sealed class SharedVehicleUi : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id; public VehicleController Vehicle = null!; public Vehicle_ToeManager? Toe;
            public VehicleUiState Original, Applied; public bool HasApplied, Purchased;
            public readonly VehicleToeEditQueue Edits = new VehicleToeEditQueue();
        }
        private const float TickSeconds = 0.5f, PoseMaxAge = 2f, RangeSquared = 25f;
        private readonly bool host;
        private readonly SharedVehicles vehicles;
        private readonly Gameplay gameplay;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<VehicleController, Binding> byVehicle = new Dictionary<VehicleController, Binding>();
        private readonly Dictionary<Vehicle_ToeManager, Binding> byToe = new Dictionary<Vehicle_ToeManager, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session; private WorldChannel? channel; private float nextTick;
        private bool applying, disposed;
        internal static SharedVehicleUi? Active { get; private set; }
        internal bool Ready => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true &&
            MonMPPlugin.Instance?.World?.SaveGate.Active != true;
        internal IEnumerable<ulong> RequiredDynamicCatalogEntityIds => bindings.Values.Where(b => !b.Purchased).Select(b => b.Id);
        internal bool TransactionsSettled => bindings.Values.All(binding => !binding.Edits.HasPending);

        internal SharedVehicleUi(bool host, SharedVehicles vehicles)
        {
            this.host = host; this.vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>() ?? throw new InvalidOperationException("Gameplay is unavailable for vehicle UI replication.");
            Scan(); Active = this;
        }
        internal static ulong RecordId(ulong vehicle) => WorldRecord.DynamicIdStart |
            Math.Max(1UL, StaticId.Hash(vehicle.ToString("X16") + "/vehicle-ui-v1") & 0x7FFFFFFFFFFFFFFFUL);
        private static VehicleUiState Capture(VehicleController vehicle)
        {
            var parts = vehicle.vehicle_PartsManager;
            var toeManager = parts == null ? null : parts.Vehicle_ToeManager;
            int left = 0, right = 0;
            if (toeManager != null && parts != null)
            {
                int[] toe = parts.Get_ToeAngle;
                if (toe != null && toe.Length >= 4) { left = toe[2]; right = toe[3]; }
                else toeManager = null;
            }
            float km = vehicle.TotalKM;
            if (float.IsNaN(km) || float.IsInfinity(km)) km = 0;
            return new VehicleUiState(left, right, Mathf.Clamp(km, 0, 1000000000f), toeManager != null);
        }
        private void Scan()
        {
            var currentlyBound = new HashSet<VehicleController>(vehicles.BoundControllers.Where(vehicle => vehicle != null));
            foreach (var stale in bindings.Values.Where(binding => binding.Vehicle == null || !currentlyBound.Contains(binding.Vehicle)).ToArray())
            {
                RestoreToe(stale);
                bindings.Remove(stale.Id); byVehicle.Remove(stale.Vehicle);
                if (stale.Toe != null) byToe.Remove(stale.Toe);
            }
            foreach (var vehicle in currentlyBound)
            {
                if (vehicle == null) continue;
                ulong entity = vehicles.EntityOf(vehicle); if (entity == 0) continue;
                ulong id = RecordId(entity);
                if (bindings.TryGetValue(id, out var old) && old.Vehicle == vehicle) continue;
                if (old != null)
                {
                    RestoreToe(old); byVehicle.Remove(old.Vehicle);
                    if (old.Toe != null) byToe.Remove(old.Toe);
                }
                var state = Capture(vehicle);
                var binding = new Binding { Id = id, Vehicle = vehicle,
                    Purchased = SharedVehicleSales.Active?.OwnsSaleClone(vehicle.transform) == true,
                    Toe = state.HasRearToe ? vehicle.vehicle_PartsManager.Vehicle_ToeManager : null,
                    Original = state, Applied = state };
                bindings[id] = binding; byVehicle[vehicle] = binding;
                if (binding.Toe != null) byToe[binding.Toe] = binding;
            }
        }
        internal void RefreshPurchasedBindings()
        {
            if (disposed) return;
            Scan();
            if (channel == null) return;
            foreach (var binding in bindings.Values.Where(b => b.Purchased))
            {
                if (host && !channel.Records.ContainsKey(binding.Id))
                    channel.Register(new WorldRecord(binding.Id, 1, VehicleUiState.Adapter, Capture(binding.Vehicle).Encode()));
                else if (!host && channel.Records.TryGetValue(binding.Id, out var record)) Apply(record);
            }
        }
        private void RestoreToe(Binding binding)
        {
            if (host || binding.Vehicle == null || binding.Vehicle.vehicle_PartsManager == null || binding.Toe == null || !binding.Original.HasRearToe) return;
            int[] angles = binding.Vehicle.vehicle_PartsManager.Get_ToeAngle;
            if (angles == null || angles.Length < 4) return;
            angles[2] = binding.Original.RearLeft; angles[3] = binding.Original.RearRight;
            binding.Vehicle.vehicle_PartsManager.Get_ToeAngle = angles; binding.Toe.UpdateVisual();
        }
        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, VehicleUiState.Adapter,
            (host ? Capture(b.Vehicle) : b.Original).Encode()));
        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += Pose; session.PeerLeft += PeerLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
        }
        private void Pose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void PeerLeft(uint peer) => presence.Remove(peer);
        private bool PeerPosition(uint peer, out Vector3 position)
        {
            position = default;
            if (peer == 0)
            {
                if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.PlayerWalking == null) return false;
                position = gameplay.PlayerWalking.transform.position; return true;
            }
            if (session?.Peers.ContainsKey(peer) != true || !presence.TryGetValue(peer, out var seen)) return false;
            float age = Time.unscaledTime - seen.Time;
            if (age < 0 || age > PoseMaxAge || seen.Pose.State != (byte)PlayerState.Walking ||
                seen.Pose.Rest != PlayerRest.None || seen.Pose.Spectating) return false;
            position = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z); return true;
        }
        internal bool RequestToe(Vehicle_ToeManager toe, VehicleToeSide side, int direction)
        {
            if (!Ready || applying || host || (direction != -1 && direction != 1) || !byToe.TryGetValue(toe, out var binding)) return false;
            if (!binding.Edits.Enqueue(side, direction))
            { Report("Wait for the pending rear toe adjustments to finish."); return true; }
            FlushToe(binding);
            return true;
        }

        private void FlushToe(Binding binding)
        {
            if (!Ready || channel == null || !channel.Records.TryGetValue(binding.Id, out var record) ||
                !VehicleUiState.TryDecode(record.Payload, out var current)) return;
            if (binding.Edits.Prepare(current, record.Revision, Time.unscaledTime, out var command))
            {
                try { channel.Request(binding.Id, command.Encode(), useLease: true); }
                catch (InvalidDataException) { binding.Edits.RequestFailed(); }
            }
        }

        private static void Report(string message) => Singleton<UI_ErrorMessage>.i?.Show(message);

        [WorldCommandAdapters(VehicleUiState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload) => HandleToe(peer, id, payload, false);
        private void Rejected(uint peer, ulong id, byte[] payload) => HandleToe(peer, id, payload, true);

        private void HandleToe(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || channel == null || !channel.Records.ContainsKey(id) || !bindings.TryGetValue(id, out var binding) ||
                binding.Vehicle == null || binding.Toe == null) return;
            VehicleToeCommand command;
            try { command = VehicleToeCommand.Decode(payload); } catch (InvalidDataException) { return; }
            var current = Capture(binding.Vehicle);
            // An explicit reply distinguishes acceptance from another player's edit.
            // Publish native state first so stale steps can rebase to this exact record.
            channel.Publish(id, current.Encode());
            byte result = VehicleToeEditQueue.Rejected;
            if (Ready && MonMPPlugin.Instance?.World?.Vehicles.TransfersBlocked != true && PeerPosition(peer, out var at))
            {
                bool inRange = (at - binding.Toe.transform.position).sqrMagnitude <= RangeSquared;
                int value = command.Side == VehicleToeSide.RearLeft ? current.RearLeft : current.RearRight;
                if (inRange && current.HasRearToe && (stale || command.ExpectedValue != value)) result = VehicleToeEditQueue.Stale;
                else if (inRange && ApplyToe(binding, current, command)) result = VehicleToeEditQueue.Accepted;
            }
            if (peer != 0) channel.Reply(peer, id, VehicleToeEditQueue.EncodeReply(command, result, channel.Records[id].Revision));
        }

        private bool ApplyToe(Binding binding, VehicleUiState current, VehicleToeCommand command)
        {
            bool saveGate = MonMPPlugin.Instance?.World?.SaveGate.Active == true;
            if (!VehicleToeAdjustmentPolicy.TryApply(host, Ready, saveGate, true,
                true, current, command, out var next)) return false;
            if (!next.HasRearToe || binding.Vehicle.vehicle_PartsManager == null || binding.Toe == null) return false;
            int[] angles = binding.Vehicle.vehicle_PartsManager.Get_ToeAngle;
            angles[command.Side == VehicleToeSide.RearLeft ? 2 : 3] = command.Side == VehicleToeSide.RearLeft ? next.RearLeft : next.RearRight;
            binding.Vehicle.vehicle_PartsManager.Get_ToeAngle = angles;
            applying = true;
            try { binding.Toe.UpdateVisual(); }
            finally { applying = false; }
            channel!.Publish(binding.Id, Capture(binding.Vehicle).Encode());
            return true;
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || !bindings.TryGetValue(id, out var binding) ||
                !VehicleToeEditQueue.TryDecodeReply(payload, out _, out byte status, out _) || !binding.Edits.Reply(payload)) return;
            if (status == VehicleToeEditQueue.Rejected) Report("The host couldn't apply the rear toe adjustment. Move closer and try again.");
            FlushToe(binding);
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (host) return; foreach (var record in records.Values) if (record.Adapter == VehicleUiState.Adapter) Apply(record); }
        private void Changed(WorldRecord record) { if (!host && record.Adapter == VehicleUiState.Adapter) Apply(record); }
        private void Apply(WorldRecord record)
        {
            if (!bindings.TryGetValue(record.Id, out var b) || b.Vehicle == null || !VehicleUiState.TryDecode(record.Payload, out var state)) return;
            try
            {
                if (!state.HasRearToe || b.Toe == null || b.Vehicle.vehicle_PartsManager == null)
                { b.Applied = state; b.HasApplied = true; return; }
                int[] angles = b.Vehicle.vehicle_PartsManager.Get_ToeAngle;
                if (angles == null || angles.Length < 4) return;
                bool toeChanged = !b.HasApplied || angles[2] != state.RearLeft || angles[3] != state.RearRight;
                if (toeChanged)
                {
                    angles[2] = state.RearLeft; angles[3] = state.RearRight;
                    b.Vehicle.vehicle_PartsManager.Get_ToeAngle = angles;
                    applying = true; b.Toe.UpdateVisual();
                }
                b.Applied = state; b.HasApplied = true;
            }
            finally { applying = false; }
        }
        internal bool TryGetOdometer(VehicleController vehicle, out float km)
        {
            km = 0;
            if (host || !Ready) return false;
            if (vehicle == null || !byVehicle.TryGetValue(vehicle, out var b) || !b.HasApplied) return false;
            km = b.Applied.TotalKm; return true;
        }
        public void Update()
        {
            if (disposed || session?.IsRunning != true || channel?.Ready != true) return;
            if (!host)
                foreach (var binding in bindings.Values)
                {
                    if (binding.Edits.Expire(Time.unscaledTime)) Report("The rear toe adjustment timed out. Try again.");
                    FlushToe(binding);
                }
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + TickSeconds; Scan();
            foreach (var b in bindings.Values)
            {
                if (b.Vehicle == null) continue;
                var local = Capture(b.Vehicle);
                if (host)
                {
                    if (!channel.Records.ContainsKey(b.Id)) channel.Register(new WorldRecord(b.Id, 1, VehicleUiState.Adapter, local.Encode()));
                    else if (!VehicleUiState.TryDecode(channel.Records[b.Id].Payload, out var published) || !published.Equals(local)) channel.Publish(b.Id, local.Encode());
                }
                else if (!b.HasApplied && channel.Records.TryGetValue(b.Id, out var record)) Apply(record);
                else if (!host && b.HasApplied && b.Applied.HasRearToe && b.Toe != null && b.Vehicle.vehicle_PartsManager != null &&
                    channel.Records.TryGetValue(b.Id, out var authoritative) &&
                    VehicleUiState.TryDecode(authoritative.Payload, out var state) && state.HasRearToe)
                {
                    int[] toe = b.Vehicle.vehicle_PartsManager.Get_ToeAngle;
                    if (toe != null && toe.Length >= 4 && (toe[2] != state.RearLeft || toe[3] != state.RearRight)) Apply(authoritative);
                }
            }
        }
        internal static bool IsApplying => Active?.applying == true;
#if MONMP_DEVELOPMENT
        internal ulong TestPurchasedUiId(ulong entity, GameObject instance)
        {
            var vehicle = instance.GetComponent<VehicleController>();
            ulong id = RecordId(entity);
            if (vehicle == null || !bindings.TryGetValue(id, out var binding) || !binding.Purchased || binding.Vehicle != vehicle ||
                !byVehicle.TryGetValue(vehicle, out var direct) || direct != binding || channel == null ||
                !channel.Records.TryGetValue(id, out var record) || record.Adapter != VehicleUiState.Adapter ||
                !VehicleUiState.TryDecode(record.Payload, out var state) ||
                (host ? !Capture(vehicle).Equals(state) : !binding.HasApplied || !binding.Applied.Equals(state)) ||
                RequiredDynamicCatalogEntityIds.Contains(id))
                throw new InvalidOperationException("Purchased truck dashboard binding/state is not applied: " + id.ToString("X16"));
            return id;
        }
#endif
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= PeerLeft; }
            if (channel != null)
            {
                channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
                channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply;
            }
            if (!host) foreach (var b in bindings.Values) RestoreToe(b);
            presence.Clear(); bindings.Clear(); byVehicle.Clear(); byToe.Clear(); session = null; channel = null;
            if (ReferenceEquals(Active, this)) Active = null;
        }
    }
}
