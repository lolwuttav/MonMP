using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.NOS;

namespace MonMP.Plugin.Replication
{
    // The installed nitrous mount's arm switch and power button only changed the clicking peer's NOS module, so a
    // guest driving a car the host armed had no boost (and the host's save kept its own setting). The host
    // owns both controls: guests send their clicks, the host runs the native methods (which also write the
    // saved custom parameters) and every peer mirrors the armed flag, power level, switch and button.
    // The controls ride on the installed nitrous mount part (a clone that appears when the mount goes
    // in), so records are keyed by the vehicle's shared id and registered as the mount shows up.
    internal sealed class SharedNitrous : IDisposable
    {
        public const ushort Adapter = 102;
        internal const byte ArmOn = 1, ArmOff = 2, PowerToggle = 3;
        private const byte UseOn = 4, UseOff = 5;
        private const float CommandRange = 5f, ScanInterval = 2f;
        private static readonly AccessTools.FieldRef<Vehicle_Nitrous_Controls, int> PowerField = AccessTools.FieldRefAccess<Vehicle_Nitrous_Controls, int>("power");
        private static readonly AccessTools.FieldRef<Vehicle_Nitrous_Controls, Furniture_SwitchButton> SwitchField = AccessTools.FieldRefAccess<Vehicle_Nitrous_Controls, Furniture_SwitchButton>("Power_Switch");

        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_Nitrous_Controls Controls = null!;
            public byte[] Original = null!;
            public VehicleController Vehicle = null!;
            public NOSModuleWrapper? Nos;
            public uint UsagePeer;
            public float UsageAt;
            public bool RemoteUsing, SentUsing;
            public float NextUsage;
        }
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_Nitrous_Controls, Binding> byControls = new Dictionary<Vehicle_Nitrous_Controls, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly bool host;
        private readonly SharedVehicles vehicles;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool appliedReplica, applying;
        private float nextCapture, nextScan;
        public int Count => bindings.Count;

        public SharedNitrous(bool host, SharedVehicles vehicles) { this.host = host; this.vehicles = vehicles; }

        internal IEnumerable<WorldRecord> InitialRecords() => Enumerable.Empty<WorldRecord>();

        internal static ulong RecordId(ulong vehicle) =>
            WorldRecord.DynamicIdStart | Math.Max(1UL, StaticId.Hash(vehicle.ToString("X16") + "/nitrous-controls-v2") & 0x7FFFFFFFFFFFFFFFUL);

        private void Scan(bool periodic = false)
        {
            foreach (var controls in WorldScans.Find<Vehicle_Nitrous_Controls>(periodic, true))
            {
                if (controls == null || byControls.ContainsKey(controls) || !controls.gameObject.scene.IsValid() || SwitchField(controls) == null) continue;
                ulong vehicle = vehicles.EntityOf(controls.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>(true));
                if (vehicle == 0) continue;
                ulong id = RecordId(vehicle);
                if (bindings.TryGetValue(id, out var existing))
                {
                    // The mount was removed and installed again: the new part carries the vehicle's controls.
                    if (existing.Controls != null) continue;
                    byControls.Remove(existing.Controls!); bindings.Remove(id);
                }
                var b = new Binding { Id = id, Controls = controls, Vehicle = controls.GetComponentInParent<VehicleController>(true) };
                // A replacement native mount must not replace the guest baseline with prefab
                // defaults (or host state). Host captures still describe its current mount.
                b.Original = !host && existing != null ? existing.Original : Capture(b);
                bindings.Add(id, b); byControls.Add(controls, b);
                if (host) { if (!channel!.Records.ContainsKey(id)) channel.Register(new WorldRecord(id, 1, Adapter, b.Original)); else Publish(b); }
                else if (channel!.Records.TryGetValue(id, out var record)) { Apply(b, record.Payload); appliedReplica = true; }
            }
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        // True when a guest's click was sent to the host instead of running natively.
        internal bool Intercept(Vehicle_Nitrous_Controls controls, byte action)
        {
            if (host || applying || session?.IsRunning != true || !byControls.TryGetValue(controls, out var b)) return false;
            if (channel?.Ready == true) channel.Request(b.Id, new[] { action });
            if (action == PowerToggle) Singleton<SOUND_Manager>.i?.PlaySound_SwitchOn();
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedNitrous.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var b) || b.Controls == null || payload.Length != 1) return;
            if (payload[0] == UseOn || payload[0] == UseOff)
            {
                // Usage is presentation only. The driver's native power modifier owns consumption.
                if (vehicles.DrivenBy(b.Vehicle) != peer) return;
                b.UsagePeer = peer; b.UsageAt = Time.unscaledTime;
                b.RemoteUsing = payload[0] == UseOn;
                b.Vehicle.input.Boost = b.RemoteUsing;
                Publish(b);
                return;
            }
            // Mount removal can hide the controls before an in-flight button intent arrives.
            // Usage releases above must still clear the driver's presentation flag.
            if (!b.Controls.gameObject.activeInHierarchy) return;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
            if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - b.Controls.transform.position).sqrMagnitude > CommandRange * CommandRange) return;
            var button = SwitchField(b.Controls);
            switch (payload[0])
            {
                case ArmOn: button.isOn = true; button.UpdateVisual(); b.Controls.Button_Arm_On(); break;
                case ArmOff: button.isOn = false; button.UpdateVisual(); b.Controls.Button_Arm_Off(); break;
                case PowerToggle: b.Controls.Power_Button_Toggle(); break;
                default: return;
            }
            Publish(b);
        }

        private void Publish(Binding b)
        {
            if (b.Controls == null || !channel!.Records.TryGetValue(b.Id, out var record)) return;
            byte arm = (byte)(SwitchField(b.Controls).isOn ? 1 : 0);
            byte power = (byte)Mathf.Clamp(PowerField(b.Controls), 0, 2);
            byte used = (byte)(Using(b) ? 1 : 0);
            // Compare the three wire fields before allocating an unchanged capture.
            var payload = record.Payload;
            if (payload.Length == 3 && payload[0] == arm && payload[1] == power && payload[2] == used) return;
            channel.Publish(b.Id, new[] { arm, power, used });
        }

        private static bool ModuleUsing(Binding b)
        {
            // A mount replacement gets a new binding. Destroyed wrappers are re-resolved,
            // including a wrapper installed after this binding was first discovered.
            if (b.Nos == null) b.Nos = b.Vehicle.GetComponent<NOSModuleWrapper>();
            return b.Nos?.module?.IsBeingUsed == true;
        }

        private bool Using(Binding b) => b.Vehicle.VehicleMultiplayerInstanceType == NWH.Vehicle.MultiplayerInstanceType.Local
                ? ModuleUsing(b)
                : b.RemoteUsing && (!host || (b.UsagePeer == vehicles.DrivenBy(b.Vehicle) && Time.unscaledTime - b.UsageAt < 1.5f));
        private byte[] Capture(Binding b) =>
            new[] { (byte)(SwitchField(b.Controls).isOn ? 1 : 0), (byte)Mathf.Clamp(PowerField(b.Controls), 0, 2), (byte)(Using(b) ? 1 : 0) };

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b)) Apply(b, record.Payload);
            appliedReplica = true;
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter || !bindings.TryGetValue(record.Id, out var b)) return;
            Apply(b, record.Payload);
            appliedReplica = true;
        }

        private void Apply(Binding b, byte[] state, bool force = true)
        {
            if (b.Controls == null || (state.Length != 2 && state.Length != 3) || state[0] > 1 || state[1] > 2 || (state.Length == 3 && state[2] > 1)) return;
            b.RemoteUsing = state.Length == 3 && state[2] == 1;
            if (b.Vehicle.VehicleMultiplayerInstanceType == NWH.Vehicle.MultiplayerInstanceType.Remote)
                b.Vehicle.input.Boost = b.RemoteUsing;
            if (!force && Capture(b).SequenceEqual(state)) return;
            applying = true;
            try
            {
                // The native save loader sets the switch, NOS module, button position and power without sound.
                b.Controls.LoadFromSave(state[0], state[1]);
                SwitchField(b.Controls).UpdateVisual();
            }
            catch (Exception) { }
            finally { applying = false; }
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true) return;
            bool periodic = nextScan > 0;
            if (Time.unscaledTime >= nextScan && WorldScans.TryClaim<Vehicle_Nitrous_Controls>(periodic)) { nextScan = periodic ? WorldScans.NextDeadline(Time.unscaledTime, ScanInterval) : Time.unscaledTime + ScanInterval; Scan(periodic); }
            foreach (var b in bindings.Values)
            {
                if (b.Controls == null || b.Vehicle == null) continue;
                if (!host && vehicles.DrivenBy(b.Vehicle) == session.LocalPeerId)
                {
                    bool used = ModuleUsing(b);
                    if (used != b.SentUsing || (used && Time.unscaledTime >= b.NextUsage))
                    {
                        channel.Request(b.Id, new[] { used ? UseOn : UseOff }, useLease: true);
                        b.SentUsing = used; b.NextUsage = Time.unscaledTime + 0.5f;
                    }
                }
                else if (b.Vehicle.VehicleMultiplayerInstanceType == NWH.Vehicle.MultiplayerInstanceType.Remote)
                {
                    bool used = host ? Using(b) : b.RemoteUsing;
                    if (vehicles.DrivenBy(b.Vehicle) == ItemLease.Unowned) used = false;
                    b.Vehicle.input.Boost = used;
                }
            }
            if (Time.unscaledTime < nextCapture) return;
            if (host)
            {
                nextCapture = Time.unscaledTime + 0.25f;
                foreach (var b in bindings.Values) if (b.Controls != null) Publish(b);
            }
            else if (appliedReplica)
            {
                nextCapture = Time.unscaledTime + 1f;
                foreach (var b in bindings.Values)
                    if (channel.Records.TryGetValue(b.Id, out var record)) Apply(b, record.Payload, false);
            }
        }


#if MONMP_DEVELOPMENT
        internal static bool TestSwitch(Vehicle_Nitrous_Controls controls) => SwitchField(controls).isOn;


        internal static Furniture_SwitchButton TestSwitchButton(Vehicle_Nitrous_Controls controls) => SwitchField(controls);


        internal static int TestPower(Vehicle_Nitrous_Controls controls) => PowerField(controls);


        internal static bool? TestModuleArmed(Vehicle_Nitrous_Controls controls) =>
            controls.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>(true)?.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>()?.module?.isArmed;


        internal bool TestBound(Vehicle_Nitrous_Controls controls) => byControls.ContainsKey(controls);


        internal string TestWhy(Vehicle_Nitrous_Controls controls)
        {
            var vc = controls.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>(true);
            return "scene=" + controls.gameObject.scene.IsValid() + " switch=" + (SwitchField(controls) != null) + " vc=" + (vc == null ? "none" : vc.name) +
                " entity=" + vehicles.EntityOf(vc).ToString("X") + " bindings=" + bindings.Count + " ready=" + (channel?.Ready == true) + " path=" + SceneIdentity.CanonicalPath(controls.transform);
        }
#endif


        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host && appliedReplica) foreach (var b in bindings.Values) if (b.Controls != null) Apply(b, b.Original);
            session = null; channel = null; presence.Clear();
        }
    }
}
