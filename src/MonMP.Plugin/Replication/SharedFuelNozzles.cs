using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Scene nozzles never enter the loose-item catalog. Their native pour remains under
    // SharedFuel; this bounded lease describes the equipped nozzle and its hose only.
    internal sealed class SharedFuelNozzles : IDisposable
    {
        internal const ushort Adapter = 153;
        private static readonly System.Reflection.FieldInfo DockGun = AccessTools.Field(typeof(Kaligaz_FuelGun), "IsDockFuelGun");
        private sealed class Binding
        {
            internal ulong Id;
            internal Kaligaz_FuelGun Gun = null!;
            internal string Shape = "";
            internal Vector3 Origin, Position;
            internal Quaternion Rotation;
            internal readonly RemoteToolPose Pose = new RemoteToolPose();
            internal uint Owner = ItemLease.Unowned;
            internal float Seen;
            internal RemoteSceneAirEquipmentVisual Visual = new RemoteSceneAirEquipmentVisual();
        }
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float At)> poses = new Dictionary<uint, (PlayerPose, float)>();
        private readonly RemoteEquippedPose equippedPose = new RemoteEquippedPose();
        private readonly bool host;
        private readonly Gameplay gameplay;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextSend;
        private ulong local;
        internal SharedFuelNozzles(bool host, Gameplay gameplay, NativeEntityRegistry registry)
        {
            this.host = host; this.gameplay = gameplay;
            var localToolLocation = gameplay.Player?.Get_ToolLocation(ToolsType.FuelGun)?.Location;
            foreach (var gun in UnityEngine.Object.FindObjectsOfType<Kaligaz_FuelGun>(true))
            {
                if (!gun.gameObject.scene.IsValid()) continue;
                string path = Identity(gun.transform, localToolLocation);
                ulong id = StaticId.Hash(path);
                var b = new Binding { Id = id, Gun = gun, Origin = gun.transform.position, Position = gun.transform.position, Rotation = gun.transform.rotation };
                HeldGripOverrides.RegisterSceneEquipment(gun, out _, out b.Shape);
                bindings.Add(id, b); registry.Bind(id, Adapter, gun.gameObject, path, true, EntityPersistence.SessionOnly);
            }
        }
        private static string Identity(Transform nozzle, Transform? localToolLocation)
        {
            // A saved chair/vehicle parents the local walker under its seat. The native
            // serialized fuel-tool placeholder is still the same local player anchor.
            // Match that exact object; real pump/dock nozzles keep their world identities.
            if (localToolLocation != null && nozzle == localToolLocation)
            {
                var scene = nozzle.gameObject.scene;
                return StaticId.Segment(scene.path, scene.buildIndex) + "/local-player/fuel-gun-location/nozzle-presentation-v1";
            }
            return SharedBuildingDoors.StablePath(nozzle) + "/nozzle-presentation-v1";
        }
        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, Adapter, Encode(b)));
        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += Pose; session.PeerLeft += Left;
            channel.CommandRequested += Command; channel.Changed += Changed; channel.SnapshotApplied += Snapshot;
        }
        private void Pose(uint peer, PlayerPose pose) => poses[peer] = (pose, Time.unscaledTime);
        private void Left(uint peer)
        {
            poses.Remove(peer);
            if (host) foreach (var b in bindings.Values.Where(b => b.Owner == peer)) Release(b);
        }
        private static byte[] Encode(Binding b)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(b.Owner); w.Write(b.Position.x); w.Write(b.Position.y); w.Write(b.Position.z);
            w.Write(b.Rotation.x); w.Write(b.Rotation.y); w.Write(b.Rotation.z); w.Write(b.Rotation.w);
            return stream.ToArray();
        }
        private static bool Read(byte[] data, out uint owner, out Vector3 position, out Quaternion rotation)
        {
            owner = ItemLease.Unowned; position = default; rotation = Quaternion.identity;
            if (data.Length != 32) return false;
            using var r = new BinaryReader(new MemoryStream(data, false));
            owner = r.ReadUInt32(); position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return Finite(position.x) && Finite(position.y) && Finite(position.z) && Finite(length) && length > 0.9f && length < 1.1f;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        [WorldCommandAdapters(Adapter)]
        private void Command(uint peer, ulong id, byte[] data)
        {
            if (!host || channel?.FullySynchronized != true || !bindings.TryGetValue(id, out var b) || !Read(data, out uint owner, out var position, out var rotation)) return;
            if (owner == ItemLease.Unowned) { if (b.Owner == peer) Release(b); return; }
            if (owner != peer || peer == 0 || MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
                b.Owner != ItemLease.Unowned && b.Owner != peer || !poses.TryGetValue(peer, out var pose) ||
                Time.unscaledTime - pose.At > 2f || pose.Pose.Rest != PlayerRest.None || pose.Pose.Spectating ||
                pose.Pose.State != (byte)PlayerState.Walking && pose.Pose.State != (byte)PlayerState.InFurnitureChair ||
                bindings.Values.Any(other => other != b && other.Owner == peer)) return;
            var player = new Vector3(pose.Pose.X, pose.Pose.Y, pose.Pose.Z);
            bool dock = (bool)DockGun.GetValue(b.Gun);
            float range = dock ? 13f : 5f;
            if ((player - b.Origin).sqrMagnitude > range * range || (position - player).sqrMagnitude > 9f) return;
            SetPose(b, peer, position, rotation);
            channel.Publish(id, Encode(b));
        }
        private static void SetPose(Binding b, uint owner, Vector3 position, Quaternion rotation)
        {
            b.Pose.Receive(position, rotation, Time.unscaledTime, b.Owner != owner);
            b.Owner = owner; b.Position = position; b.Rotation = rotation; b.Seen = Time.unscaledTime;
        }
        private void Release(Binding b) { b.Owner = ItemLease.Unowned; b.Pose.Reset(); b.Visual.Reset(); channel?.Publish(b.Id, Encode(b)); }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (!host) foreach (var record in records.Values) Changed(record); }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter || !bindings.TryGetValue(record.Id, out var b) || !Read(record.Payload, out uint owner, out var pos, out var rot)) return;
            SetPose(b, owner, pos, rot);
            if (owner == ItemLease.Unowned || owner == session?.LocalPeerId) b.Visual.Reset();
        }
        internal void Update()
        {
            if (session?.IsRunning != true || channel?.FullySynchronized != true) return;
            if (Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + 0.1f;
                var held = gameplay.Player.Get_ActiveTool as Kaligaz_FuelGun;
                var mine = bindings.Values.FirstOrDefault(b => b.Gun == held && held != null && held.isEquiped);
                if (local != 0 && (mine == null || mine.Id != local) && bindings.TryGetValue(local, out var previous))
                {
                    previous.Owner = ItemLease.Unowned;
                    if (host) Release(previous); else channel.Request(previous.Id, Encode(previous), true);
                    local = 0;
                }
                if (mine != null)
                {
                    // Do not replace another peer's approved nozzle while their lease is live.
                    if (mine.Owner == ItemLease.Unowned || mine.Owner == session.LocalPeerId)
                    {
                        local = mine.Id; mine.Position = held!.transform.position; mine.Rotation = held.transform.rotation;
                        if (host) { mine.Owner = 0; mine.Seen = Time.unscaledTime; channel.Publish(mine.Id, Encode(mine)); }
                        else
                        {
                            uint approved = mine.Owner; mine.Owner = session.LocalPeerId;
                            channel.Request(mine.Id, Encode(mine), true); mine.Owner = approved;
                        }
                    }
                    else AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool").Invoke(gameplay.Player, null);
                }
                if (host) foreach (var b in bindings.Values.Where(b => b.Owner != ItemLease.Unowned && b.Owner != 0 && Time.unscaledTime - b.Seen > 2f)) Release(b);
            }
            foreach (var b in bindings.Values)
            {
                if (b.Owner == ItemLease.Unowned || b.Owner == session.LocalPeerId ||
                    !poses.TryGetValue(b.Owner, out var ownerPose) || Time.unscaledTime - ownerPose.At > 2f) { b.Visual.Reset(); continue; }
                b.Pose.Sample(Time.unscaledTime, out var position, out var rotation);
                b.Visual.PresentFuel(b.Gun, position, rotation);
            }
        }
        internal bool TryPalm(uint peer, out Vector3 position, out Quaternion rotation, out Vector3? leftPalm,
            out Quaternion? leftPalmRotation, out float heldWidth, Quaternion? bodyRotation = null,
            Vector3? eyes = null, Quaternion? look = null, Gameplay? gameplay = null)
        {
            leftPalm = null; leftPalmRotation = null; heldWidth = 0;
            foreach (var b in bindings.Values)
            {
                if (b.Owner != peer || !b.Visual.Active) continue;
                b.Pose.Sample(Time.unscaledTime, out var at, out var turn);
                // Avatar rendering uses the displayed camera frame so a quick turn cannot
                // leave the nozzle and palms following a separate 10 Hz pose interpolation.
                if (eyes.HasValue && look.HasValue && gameplay != null &&
                    equippedPose.TryGet(b.Gun, gameplay, eyes.Value, look.Value, out var nativeAt, out var nativeTurn))
                { at = nativeAt; turn = nativeTurn; }
                var grips = MonMPPlugin.Instance?.HeldGrips;
                if (grips != null && grips.TryGetVisualOffset(b.Gun, b.Shape, out var offset, out var extraTurn))
                {
                    at += (bodyRotation ?? turn) * offset; turn *= extraTurn;
                }
                if (!b.Visual.PresentFuel(b.Gun, at, turn)) break;
                position = at; rotation = turn;
                if (grips != null && grips.TryGetRuntime(b.Gun, b.Shape, out var localPalm, out var localTurn, out heldWidth,
                    out var localLeft, out var localLeftTurn))
                {
                    position = at + turn * Vector3.Scale(localPalm, b.Gun.transform.lossyScale); rotation = turn * localTurn;
                    if (localLeft.HasValue && localLeftTurn.HasValue)
                    { leftPalm = at + turn * Vector3.Scale(localLeft.Value, b.Gun.transform.lossyScale); leftPalmRotation = turn * localLeftTurn.Value; }
                }
                return true;
            }
            position = default; rotation = Quaternion.identity;
            return false;
        }
        internal bool TryPalm(uint peer, out Vector3 position, out Quaternion rotation)
            => TryPalm(peer, out position, out rotation, out _, out _, out _);
        internal Transform? PresentedTransform(uint peer)
        {
            foreach (var b in bindings.Values)
                if (b.Owner == peer && b.Visual.Active) return b.Visual.PresentedTransform;
            return null;
        }
        internal void TranslatePresentation(uint peer, Vector3 correction)
        {
            foreach (var b in bindings.Values)
                if (b.Owner == peer && b.Visual.Active) { b.Visual.Translate(correction); return; }
        }
        public void Dispose()
        {
            foreach (var b in bindings.Values) b.Visual.Dispose();
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= Left; }
            if (channel != null) { channel.CommandRequested -= Command; channel.Changed -= Changed; channel.SnapshotApplied -= Snapshot; }
            session = null; channel = null; poses.Clear();
        }
    }
}
