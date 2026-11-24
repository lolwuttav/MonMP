using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Roadside police scenes are authored spot hierarchies, separate from Gley's traffic pool.
    internal sealed partial class SharedPoliceSpots : IDisposable
    {
        internal const ushort Adapter = 152;
        private sealed class Binding
        {
            internal ulong Id;
            internal Police_SpotDetails Spot = null!;
            internal GameObject Root = null!;
            internal Transform[] Children = Array.Empty<Transform>();
            internal Transform[] Ancestors = Array.Empty<Transform>();
            internal Light Light = null!;
            internal Store_PoliceInterceptor_Car Car = null!;
            internal byte[] Original = Array.Empty<byte>();
            internal byte[] CaptureBuffer = Array.Empty<byte>();
            internal BinaryWriter? CaptureWriter;
            internal readonly Dictionary<uint, Vector3> WarningPeers = new Dictionary<uint, Vector3>();
            internal bool RemoteWarningPending;
        }
        private static readonly System.Reflection.FieldInfo Root = AccessTools.Field(typeof(Police_SpotDetails), "GO_Spot");
        private static readonly System.Reflection.FieldInfo Flash = AccessTools.Field(typeof(Police_SpotDetails), "FlashingLights");
        private static readonly System.Reflection.FieldInfo Car = AccessTools.Field(typeof(Police_SpotDetails), "PoliceCar");
        private static readonly System.Reflection.FieldInfo Warning = AccessTools.Field(typeof(Police_SpotDetails), "Warning_Color");
        private static readonly System.Reflection.FieldInfo WarningOnly = AccessTools.Field(typeof(Police_SpotDetails), "GiveOnlyWarningOnly");
        private static readonly System.Reflection.FieldInfo RealSpot = AccessTools.Field(typeof(Police_SpotDetails), "RealSpotToEnable");
        private static readonly System.Reflection.FieldInfo WarningDistance = AccessTools.Field(typeof(Police_SpotDetails), "DistanceToChangeForRealSpot");
        private static readonly System.Reflection.FieldInfo NativeWaiting = AccessTools.Field(typeof(Police_SpotDetails), "WaitPlayerFarEnough");
        private static readonly System.Reflection.MethodInfo NewTicket = AccessTools.Method(typeof(Store_PoliceInterceptor), "NewTicketCreation", new[] { typeof(PoliceTicketData), typeof(Police_SpotDetails) });
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Police_SpotDetails, Binding> bySpot = new Dictionary<Police_SpotDetails, Binding>();
        private readonly List<(uint Peer, ulong Spot, float At)> drunkTickets = new List<(uint, ulong, float)>();
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float next;
        internal bool Guest => !host && session?.IsRunning == true;
        internal SharedPoliceSpots(bool host, NativeEntityRegistry registry)
        {
            this.host = host;
            foreach (var spot in UnityEngine.Object.FindObjectsOfType<Police_SpotDetails>(true))
            {
                if (!spot.gameObject.scene.IsValid() || Root.GetValue(spot) is not GameObject root || Flash.GetValue(spot) is not Light light || Car.GetValue(spot) is not Store_PoliceInterceptor_Car car) continue;
                string path = SharedBuildingDoors.StablePath(spot.transform) + "/police-spot-v1";
                var b = new Binding { Id = StaticId.Hash(path), Spot = spot, Root = root, Light = light, Car = car,
                    Children = root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform).ToArray() };
                var ancestors = new List<Transform>();
                // Conditional groups (for example the snack's open-hours group) surround
                // the spot. Their activation is part of the police scene's visibility.
                var controller = spot.GetComponentInParent<Store_PoliceInterceptor>(true);
                if (controller != null)
                    for (var parent = root.transform.parent; parent != null && parent != controller.transform; parent = parent.parent)
                        ancestors.Add(parent);
                b.Ancestors = ancestors.ToArray();
                if (b.Ancestors.Length > 16) throw new InvalidOperationException("Police spot ancestry exceeds its bounded presentation state.");
                if (b.Children.Length > 256) throw new InvalidOperationException("Police spot hierarchy exceeds its bounded presentation state.");
                b.Original = (byte[])Capture(b).Clone(); bindings.Add(b.Id, b);
                bySpot.Add(spot, b);
                registry.Bind(b.Id, Adapter, spot.gameObject, path, true, EntityPersistence.SessionOnly);
            }
        }
        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, Adapter, b.Original));
        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRequested += Command; channel.ReplyReceived += Reply;
            session.PeerLeft += Left;
        }
        private void Left(uint peer) { foreach (var b in bindings.Values) b.WarningPeers.Remove(peer); }
        internal void ResetWarning(Police_SpotDetails spot)
        { if (bySpot.TryGetValue(spot, out var b)) { b.WarningPeers.Clear(); b.RemoteWarningPending = false; } }
        // The native detector only knows this process's local player. A guest
        // reports the entry; it never creates an independent saved ticket.
        internal bool DetectedLocally(Police_SpotDetails spot)
        {
            if (!Guest) return false;
            if (channel?.Ready != true || spot == null || !bySpot.TryGetValue(spot, out var b)) return true;
            var world = MonMPPlugin.Instance?.World;
            var gameplay = Singleton<Gameplay>.i;
            var vehicle = gameplay?.Get_CurrentVehicleController as NWH.VehiclePhysics2.VehicleController;
            if (world == null || gameplay == null || gameplay.PlayerWalking == null) return true;
            var at = gameplay.PlayerWalking.transform.position;
            if (vehicle == null || gameplay.Get_PlayerState != PlayerState.InVehicle_Driver || !Eligible(vehicle.vehicle_type) ||
                !world.Vehicles.TryGetDriverState(vehicle, out var state) || state.Driver.Owner != session!.LocalPeerId)
            {
                // Warning-scene entry precedes the native vehicle/speed gates.
                // Walkers and passengers can start its departure wait, never a fine.
                if ((bool)WarningOnly.GetValue(spot))
                    channel.Request(b.Id, new PoliceRadarRequest(0, 0, at.x, at.y, at.z, 0).Encode(), useLease: true);
                return true;
            }
            channel.Request(b.Id, new PoliceRadarRequest(world.Vehicles.EntityOf(vehicle), state.Driver.Generation,
                at.x, at.y, at.z, Singleton<BasicNeeds_Manager>.i.GetDrunkness).Encode(), useLease: true);
            return true;
        }
        private static bool Eligible(Vehicle_Type type) => type != Vehicle_Type.AI_Vehicle && type != Vehicle_Type.PitBike &&
            type != Vehicle_Type.AluminiumTrailer && type != Vehicle_Type.DollyTrailer &&
            type != Vehicle_Type.SmallCruiser && type != Vehicle_Type.FishingBoat;
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (PoliceCollisionRequest.Is(payload)) { CollisionCommand(peer, id, payload); return; }
            if (!host || peer == 0 || session?.IsRunning != true || channel?.Ready != true ||
                !bindings.TryGetValue(id, out var b) || !b.Root.activeInHierarchy || !b.Spot.isActiveAndEnabled ||
                b.Spot.PlayerDetector == null || !b.Spot.PlayerDetector.gameObject.activeInHierarchy ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            PoliceRadarRequest request;
            try { request = PoliceRadarRequest.Decode(payload); }
            catch (InvalidDataException) { return; }
            var reported = new Vector3(request.X, request.Y, request.Z);
            if (!b.Spot.PlayerDetector.GetComponentsInChildren<Collider>(true).Any(c => c != null && c.enabled && c.gameObject.activeInHierarchy && c.isTrigger &&
                (c.ClosestPoint(reported) - reported).sqrMagnitude <= 4f)) return;
            bool warningOnly = (bool)WarningOnly.GetValue(b.Spot);
            if (!request.HasDriver)
            {
                if (!warningOnly || !session.Peers.ContainsKey(peer) || !session.TryGetSocialPose(peer, out var pose) ||
                    (new Vector3(pose.X, pose.Y, pose.Z) - reported).sqrMagnitude > 64f) return;
                b.WarningPeers[peer] = reported; b.RemoteWarningPending = true;
                return;
            }
            var vehicles = MonMPPlugin.Instance?.World?.Vehicles;
            if (vehicles == null || !vehicles.TryAuthorizePoliceRadar(peer, request.Vehicle, request.Generation,
                out var vehicle, out var driver, out var forward, out float speed, out float age) ||
                vehicle == null || !Eligible(vehicle.vehicle_type)) return;
            // The report captures the trigger instant, while the latest ordered
            // vehicle sample can precede it by a send interval. Permit bounded
            // travel, but require actual contact with the native detector volume.
            float travel = Mathf.Min(40f, 2f + speed / 3.6f * Mathf.Min(age + 0.2f, 0.5f));
            if ((driver - reported).sqrMagnitude > travel * travel) return;
            if (warningOnly) { b.WarningPeers[peer] = reported; b.RemoteWarningPending = true; }
            float dot = Vector3.Dot(forward, b.Spot.PlayerDetector.transform.forward);
            var decision = PoliceRadarPolicy.Decide(speed, dot, b.Spot.SpeedLimit, b.Spot.ReverseSpeedLimit, warningOnly);
            if (decision == PoliceRadarDecision.None) return;
            if (decision == PoliceRadarDecision.Speeding)
            {
                CreateTicket(peer, b, new PoliceTicketData { TicketType = PoliceTicketData.Ticket_Type.Speeding,
                    PlayerSpeed = speed, SpeedLimit = dot > 0.1f ? b.Spot.SpeedLimit : b.Spot.ReverseSpeedLimit,
                    DaySinceTicket = 0, Vehicle_Type = vehicle.vehicle_type });
                if (speed > GameManager.Get_PlayerStats[17]) GameManager.Get_PlayerStats[17] = speed;
                if (request.Drunkness > 0.5f && drunkTickets.Count < 64)
                    drunkTickets.Add((peer, b.Id, Time.time + Singleton<SOUND_Manager>.i.Police_Siren.length + 0.5f));
            }
            else if (request.Drunkness > 0.5f) CreateDrunkTicket(peer, b);
            else channel.Reply(peer, b.Id, new byte[] { 0xBF, 0x01 });
        }
        private void CreateTicket(uint peer, Binding b, PoliceTicketData ticket)
        {
            using var quiet = Patches.RemoteActionScope.Enter();
            using var notice = session?.Peers.ContainsKey(peer) == true ?
                NativePoliceFinePresentation.Begin(peer, (recipient, payload) => channel?.Reply(recipient, b.Id, payload)) : SpatialSounds.Mute();
            NewTicket.Invoke(Singleton<Gameplay>.i.Store_PoliceInterceptor, new object[] { ticket, b.Spot });
        }
        private void CreateDrunkTicket(uint peer, Binding b) => CreateTicket(peer, b,
            new PoliceTicketData { TicketType = PoliceTicketData.Ticket_Type.DrunkDriving,
                PlayerSpeed = 0, SpeedLimit = 0, DaySinceTicket = 0, Vehicle_Type = Vehicle_Type.Useless });
        private void Reply(ulong id, byte[] payload)
        {
            if (host || !bindings.ContainsKey(id)) return;
            if (NativePoliceFinePresentation.Apply(payload)) return;
            if (payload.Length != 2 || payload[0] != 0xBF || payload[1] != 0x01) return;
            using var local = SpatialSounds.Local();
            var sounds = Singleton<SOUND_Manager>.i;
            sounds.PlayOneShot2D(sounds.Police_Warning, _randomPitch: false);
        }
        // A warning scene becomes a radar after the observed player moves away.
        // Native code otherwise watches only the host, often far from the guest.
        internal bool WaitingForRemote(Police_SpotDetails spot)
        {
            if (!host || session?.IsRunning != true || !bySpot.TryGetValue(spot, out var b) || !b.RemoteWarningPending) return false;
            if (!b.Root.activeInHierarchy) { ResetWarning(spot); return false; }
            float distance = (float)WarningDistance.GetValue(spot);
            foreach (var peer in b.WarningPeers)
            {
                if (!session.Peers.ContainsKey(peer.Key)) continue;
                // An absent unreliable pose does not mean that the driver left.
                var position = session.TryGetSocialPose(peer.Key, out var pose) ? new Vector3(pose.X, pose.Y, pose.Z) : peer.Value;
                if (Vector3.Distance(position, b.Car.transform.position) < distance) return true;
            }
            var player = Singleton<Gameplay>.i?.Player;
            if ((bool)NativeWaiting.GetValue(spot) && player != null && Vector3.Distance(player.transform.position, b.Car.transform.position) < distance) return true;
            ResetWarning(spot);
            if (RealSpot.GetValue(spot) is Police_SpotDetails real && real != null) real.Enable_Spot();
            spot.Disable_Spot(); return true;
        }
        private static byte[] Capture(Binding b)
        {
            if (b.CaptureWriter == null)
            {
                b.CaptureBuffer = new byte[22 + b.Children.Length];
                b.CaptureWriter = new BinaryWriter(new MemoryStream(b.CaptureBuffer, true));
            }
            var w = b.CaptureWriter;
            w.BaseStream.Position = 0;
            w.Write(b.Root.activeSelf); w.Write(b.Light.enabled); w.Write((bool)Warning.GetValue(b.Spot));
            // The warning phase is unrelated to idle radar lights. Preserve the native
            // banks themselves, including the all-off state after a siren finishes.
            w.Write(b.Car.MeshLights_0.Any(light => light.IsOn));
            w.Write(b.Car.MeshLights_1.Any(light => light.IsOn));
            var color = b.Light.color;
            w.Write(color.r); w.Write(color.g); w.Write(color.b);
            w.Write((ushort)b.Children.Length);
            w.Write((byte)b.Ancestors.Length);
            ushort ancestors = 0;
            for (int i = 0; i < b.Ancestors.Length; i++) if (b.Ancestors[i].gameObject.activeSelf) ancestors |= (ushort)(1 << i);
            w.Write(ancestors);
            foreach (var child in b.Children) w.Write(child != null && child.gameObject.activeSelf);
            return b.CaptureBuffer;
        }
        private static void Apply(Binding b, byte[] data)
        {
            if (data.Length != 22 + b.Children.Length) throw new InvalidDataException("Police spot presentation hierarchy mismatch.");
            using var r = new BinaryReader(new MemoryStream(data, false));
            bool active = r.ReadBoolean(), flash = r.ReadBoolean(), warning = r.ReadBoolean();
            bool bank0 = r.ReadBoolean(), bank1 = r.ReadBoolean();
            var color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (r.ReadUInt16() != b.Children.Length) throw new InvalidDataException("Police spot child count mismatch.");
            if (r.ReadByte() != b.Ancestors.Length) throw new InvalidDataException("Police spot ancestor count mismatch.");
            ushort ancestors = r.ReadUInt16();
            for (int i = b.Ancestors.Length - 1; i >= 0; i--)
            {
                bool shown = (ancestors & (1 << i)) != 0;
                if (b.Ancestors[i].gameObject.activeSelf != shown) b.Ancestors[i].gameObject.SetActive(shown);
            }
            for (int i = 0; i < b.Children.Length; i++)
            {
                bool shown = r.ReadBoolean();
                if (b.Children[i] != null && b.Children[i].gameObject.activeSelf != shown) b.Children[i].gameObject.SetActive(shown);
            }
            if (b.Root.activeSelf != active) b.Root.SetActive(active);
            b.Light.enabled = active && flash; b.Light.color = color;
            Warning.SetValue(b.Spot, warning);
            foreach (var light in b.Car.MeshLights_0) { if (active && bank0) light.TurnOn(); else light.TurnOff(); }
            foreach (var light in b.Car.MeshLights_1) { if (active && bank1) light.TurnOn(); else light.TurnOff(); }
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records) { if (!host) foreach (var record in records.Values) Changed(record); }
        private void Changed(WorldRecord record)
        { if (!host && record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b)) Apply(b, record.Payload); }
        internal void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.1f;
            if (host)
                for (int i = drunkTickets.Count - 1; i >= 0; i--)
                {
                    var delayed = drunkTickets[i];
                    if (Time.time < delayed.At) continue;
                    drunkTickets.RemoveAt(i);
                    if (bindings.TryGetValue(delayed.Spot, out var spot)) CreateDrunkTicket(delayed.Peer, spot);
                }
            foreach (var b in bindings.Values)
                if (host) channel.Publish(b.Id, Capture(b));
                else if (channel.Records.TryGetValue(b.Id, out var record)) Apply(b, record.Payload);
        }
        public void Dispose()
        {
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (session != null) session.PeerLeft -= Left;
            if (!host) foreach (var b in bindings.Values) Apply(b, b.Original);
            foreach (var b in bindings.Values) b.CaptureWriter?.Dispose();
            channel = null; session = null;
            drunkTickets.Clear(); foreach (var b in bindings.Values) { b.WarningPeers.Clear(); b.RemoteWarningPending = false; }
        }
    }
}
