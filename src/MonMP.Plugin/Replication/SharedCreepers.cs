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

namespace MonMP.Plugin.Replication
{
    // The garage's mechanic creeper. Natively it rides as a child of the player lying on it, so another
    // player saw it stay put under a rider sliding along the floor. The host owns its pose; one player
    // rides at a time: the host natively, or a guest the host names in the record, who streams the pose.
    internal sealed class SharedCreepers : IDisposable
    {
        public const ushort Adapter = 106;
        private const uint HostRider = uint.MaxValue;
        private const float Reach = 4f, SendInterval = 0.1f, RiderTimeout = 5f;
        private enum Action : byte { Enter = 1, Move = 2, Exit = 3 }
        private sealed class Binding
        {
            public ulong Id;
            public MechanicCreeper Creeper = null!;
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
            public uint Rider;
            public float RiderSeen;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public bool EnterPending;
            public float EnterPendingSince;
            public byte[]? LastPublished;
            public Transform? Pillow;
            public Vector3 LocalFeet;
        }
        private static readonly FieldInfo? PillowField = AccessTools.Field(typeof(MechanicCreeper), "PillowTransform");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<MechanicCreeper, Binding> byCreeper = new Dictionary<MechanicCreeper, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Gameplay gameplay;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private Binding? riding;
        private bool appliedReplica, applying;
        private float nextCapture, nextSend;
        internal bool TransfersBlocked { get; set; }
        internal SharedSeatAdmission? SeatAdmission { get; set; }
        internal bool IsOwner(uint peer) => bindings.Values.Any(b => b.Rider != 0 &&
            (b.Rider == HostRider ? peer == 0 : b.Rider == peer));
        private bool HostRiding(Binding b) => host && gameplay.Get_PlayerState == PlayerState.InCreeper &&
            gameplay.Get_CurrentCreeper == b.Creeper;
        internal uint[] Owners => bindings.Values.Where(b => b.Rider != 0)
            .Select(b => b.Rider == HostRider ? 0u : b.Rider).Distinct().ToArray();
        internal void RevokeOwnership(uint peer)
        {
            if (!host) throw new InvalidOperationException("Only the host can revoke creeper riders.");
            foreach (var b in bindings.Values.Where(b => b.Rider != 0 && (b.Rider == peer || (b.Rider == HostRider && peer == session?.LocalPeerId))))
            {
                ItemPose? final = null;
                if (gameplay.Get_PlayerState == PlayerState.InCreeper && gameplay.Get_CurrentCreeper == b.Creeper)
                { NativeEnterExit(b); b.Creeper.ResetOnTopOfGround(); final = Pose(b.Creeper.transform); }
                Exit(b, final);
            }
        }
        internal bool LocalFlushComplete => session != null && riding == null && !Owners.Contains(session.LocalPeerId) &&
            !bindings.Values.Any(b => b.EnterPending || gameplay.Get_PlayerState == PlayerState.InCreeper && gameplay.Get_CurrentCreeper == b.Creeper);

        internal void FlushLocalOwnership()
        {
            SeatAdmission?.CancelLocal(this);
            if (session == null || channel?.Ready != true) return;
            foreach (var b in bindings.Values.ToArray())
            {
                bool here = gameplay.Get_PlayerState == PlayerState.InCreeper && gameplay.Get_CurrentCreeper == b.Creeper;
                if (host)
                {
                    if (!here && b.Rider != HostRider) continue;
                    if (here) { NativeEnterExit(b); b.Creeper.ResetOnTopOfGround(); }
                    Exit(b, Pose(b.Creeper.transform));
                    continue;
                }
                bool pending = b.EnterPending;
                b.EnterPending = false;
                if (here || riding == b || b.Rider == session.LocalPeerId || pending) ExitLocal(b, true);
            }
        }

        public SharedCreepers(Gameplay gameplay, bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.gameplay = gameplay; this.host = host;
            var lines = new List<string>();
            if (PillowField != null)
                foreach (var creeper in UnityEngine.Object.FindObjectsOfType<MechanicCreeper>(true))
                {
                    if (!creeper.gameObject.scene.IsValid()) continue;
                    string path = SharedBuildingDoors.StablePath(creeper.transform) + "/creeper-v1";
                    ulong id = StaticId.Hash(path);
                    if (bindings.ContainsKey(id)) continue;
                    var b = new Binding { Id = id, Creeper = creeper, OriginalPosition = creeper.transform.position, OriginalRotation = creeper.transform.rotation,
                        TargetPosition = creeper.transform.position, TargetRotation = creeper.transform.rotation };
                    b.Pillow = PillowField.GetValue(creeper) as Transform;
                    b.LocalFeet = FeetDirection(creeper, b.Pillow);
                    bindings.Add(id, b); byCreeper.Add(creeper, b);
                    registry.Bind(id, Adapter, creeper.gameObject, path, true, EntityPersistence.SessionOnly);
                    lines.Add(id.ToString("X16") + "\t" + path);
                }
            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-creepers.tsv"), new[] { "id\tpath" }.Concat(lines));
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, Adapter, Encode(Pose(b.Creeper.transform), 0)));

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            if (host) foreach (var b in bindings.Values) if (b.Rider == peer) Exit(b, null);
        }
        private bool Near(uint peer, Vector3 at, float range) =>
            presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2f &&
            (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - at).sqrMagnitude <= range * range;

        // The rider lies with the head on the pillow and the feet toward the far end of the board.
        private static Vector3 FeetDirection(MechanicCreeper creeper, Transform? pillow)
        {
            var t = creeper.transform;
            var bounds = new Bounds(t.position, Vector3.zero);
            bool any = false;
            foreach (var r in creeper.GetComponentsInChildren<Renderer>(true))
            { if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds); }
            var local = pillow != null ? t.InverseTransformPoint(bounds.center) - t.InverseTransformPoint(pillow.position) : Vector3.zero;
            local.y = 0;
            return local.sqrMagnitude > 0.01f ? local.normalized : Vector3.forward;
        }

        // Where a remote player lying on a creeper rests their head, and which way their feet point.
        internal bool TryRiderFrame(uint peer, out Vector3 pillow, out Quaternion feet)
        {
            uint rider = peer == 0 ? HostRider : peer;
            foreach (var b in bindings.Values)
            {
                if (b.Rider != rider || b.Creeper == null || b.Pillow == null) continue;
                pillow = b.Pillow.position;
                var along = b.Creeper.transform.TransformDirection(b.LocalFeet); along.y = 0;
                feet = along.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(along.normalized) : Quaternion.Euler(0, b.Creeper.transform.eulerAngles.y, 0);
                // The rider lies on the board's centre line: a pillow modelled off-centre put one side of
                // the body into the board (#209).
                var center = b.Creeper.transform.position;
                var lengthwise = feet * Vector3.forward;
                var offset = pillow - center; offset.y = 0;
                pillow -= offset - lengthwise * Vector3.Dot(offset, lengthwise);
                return true;
            }
            pillow = default; feet = Quaternion.identity;
            return false;
        }

        private static byte[] Encode(ItemPose pose, uint rider)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream)) { pose.Write(w); w.Write(rider); }
            return stream.ToArray();
        }
        private static (ItemPose Pose, uint Rider) Decode(byte[] payload)
        {
            using var r = new BinaryReader(new MemoryStream(payload));
            var pose = ItemPose.Read(r); uint rider = r.ReadUInt32();
            if (r.BaseStream.Position != payload.Length) throw new InvalidDataException("Invalid creeper state.");
            return (pose, rider);
        }
        private static byte[] EncodeCommand(Action action, ItemPose pose)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream)) { w.Write((byte)action); pose.Write(w); }
            return stream.ToArray();
        }
        private static ItemPose Pose(Transform t)
        {
            var p = t.position; var q = t.rotation;
            ItemPose.TryLocal(p.x, p.y, p.z, q.x, q.y, q.z, q.w, out var pose);
            return pose;
        }
        private static void SetPose(Transform t, ItemPose pose) =>
            t.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw));

        // A click on a creeper; false lets the native enter/exit run.
        internal bool Click(MechanicCreeper creeper)
        {
            if (applying || session?.IsRunning != true || !byCreeper.TryGetValue(creeper, out var b)) return false;
            if (TransfersBlocked && gameplay.Get_PlayerState == PlayerState.Walking) return true;
            if (host)
            {
                if (HostRiding(b))
                {
                    NativeEnterExit(b);
                    if (!HostRiding(b)) { b.Creeper.ResetOnTopOfGround(); Exit(b, Pose(b.Creeper.transform)); }
                    return true;
                }
                if (b.Rider != 0)
                {
                    MonMPPlugin.Instance?.ReportItemUseUnavailable("Another player is on the creeper.");
                    return true;
                }
                if (gameplay.Get_PlayerState != PlayerState.Walking || SeatAdmission?.IsSeated(0) == true) return true;
                // Reserve synchronously, before native state changes or a pumped guest claim.
                // A native refusal/exception rolls the reservation back in the same call.
                b.Rider = HostRider;
                try { NativeEnterExit(b); }
                finally
                {
                    if (!HostRiding(b)) b.Rider = 0;
                    Publish(b, true);
                }
                return true;
            }
            if (riding == b) { ExitLocal(b, true); return true; }
            if (SeatAdmission?.IsPendingLocal(this, b.Id) == true) return true; // a repeat click keeps waiting
            // A host refusal ends the shared claim; let the next click ask again.
            if (b.EnterPending && SeatAdmission?.IsPendingLocal(this, b.Id) == false) b.EnterPending = false;
            if (channel?.Ready != true || riding != null || b.EnterPending || gameplay.Get_PlayerState != PlayerState.Walking) return true;
            if (b.Rider != 0) { MonMPPlugin.Instance?.ReportItemUseUnavailable("Another player is on the creeper."); return true; }
            if (SeatAdmission?.TryBeginLocal(this, b.Id, 1, session.LocalPeerId) == false) return true;
            b.EnterPending = true; b.EnterPendingSince = Time.unscaledTime;
            channel.Request(b.Id, EncodeCommand(Action.Enter, Pose(creeper.transform)), true);
            return true;
        }

        private FurnitureSeatRefusal EnterRefusal(Binding b, uint peer)
        {
            if (TransfersBlocked) return FurnitureSeatRefusal.TransfersBlocked;
            if (b.Rider != 0) return FurnitureSeatRefusal.Occupied;
            if (SeatAdmission?.IsSeated(peer) == true) return FurnitureSeatRefusal.AlreadySeated;
            if (!presence.TryGetValue(peer, out var entering) || entering.Pose.State != (byte)PlayerState.Walking) return FurnitureSeatRefusal.NotWalking;
            if (MonMPPlugin.Instance?.Items?.PeerHolding(peer) != false) return FurnitureSeatRefusal.Holding;
            return FurnitureSeatRefusal.TooFar;
        }

        private void NativeEnterExit(Binding b)
        {
            applying = true;
            try { gameplay.EnterExitCreeper(b.Creeper, b.Pillow ?? (Transform)PillowField!.GetValue(b.Creeper)); }
            finally { applying = false; }
        }

        private void EnterLocal(Binding b)
        {
            b.EnterPending = false;
            if (TransfersBlocked || gameplay.Get_PlayerState != PlayerState.Walking)
            { channel!.Request(b.Id, EncodeCommand(Action.Exit, Pose(b.Creeper.transform)), true); return; }
            NativeEnterExit(b);
            riding = b; nextSend = Time.unscaledTime + SendInterval;
        }

        private void ExitLocal(Binding b, bool notifyHost)
        {
            if (riding == b) riding = null;
            if (gameplay.Get_PlayerState == PlayerState.InCreeper && gameplay.Get_CurrentCreeper == b.Creeper)
            {
                NativeEnterExit(b);
                b.Creeper.ResetOnTopOfGround();
            }
            if (b.Creeper.transform.parent != null && b.Creeper.transform.parent == gameplay.PlayerWalking.transform) b.Creeper.transform.SetParent(null);
            b.TargetPosition = b.Creeper.transform.position; b.TargetRotation = b.Creeper.transform.rotation;
            if (notifyHost && channel?.Ready == true) channel.Request(b.Id, EncodeCommand(Action.Exit, Pose(b.Creeper.transform)), true);
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedCreepers.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var b) || payload.Length != 29) return;
            Action action; ItemPose pose;
            try
            {
                using var r = new BinaryReader(new MemoryStream(payload));
                action = (Action)r.ReadByte(); pose = ItemPose.Read(r);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { return; }
            var at = new Vector3(pose.X, pose.Y, pose.Z);
            switch (action)
            {
                case Action.Enter:
                    // A native entry reached through another route is also authoritative. Do
                    // not let the old unowned record override live host use before Update.
                    if (HostRiding(b)) { b.Rider = HostRider; Publish(b, true); return; }
                    if (TransfersBlocked || b.Rider != 0 || SeatAdmission?.IsSeated(peer) == true ||
                        !presence.TryGetValue(peer, out var entering) || entering.Pose.State != (byte)PlayerState.Walking ||
                        MonMPPlugin.Instance?.Items?.PeerHolding(peer) != false || !Near(peer, b.Creeper.transform.position, Reach))
                    {
                        Publish(b, true);
                        SeatRefusals.Send(channel, peer, id, 1, EnterRefusal(b, peer));
                        return;
                    }
                    b.Rider = peer; b.RiderSeen = Time.unscaledTime;
                    b.TargetPosition = b.Creeper.transform.position; b.TargetRotation = b.Creeper.transform.rotation;
                    Publish(b, true);
                    return;
                case Action.Move:
                    if (b.Rider != peer || !Near(peer, at, 4f)) return;
                    b.RiderSeen = Time.unscaledTime;
                    b.TargetPosition = at; b.TargetRotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
                    return;
                case Action.Exit:
                    if (b.Rider != peer) return;
                    Exit(b, Near(peer, at, 4f) ? pose : (ItemPose?)null);
                    return;
            }
        }

        private void Exit(Binding b, ItemPose? final)
        {
            b.Rider = 0;
            if (b.Creeper == null) return;
            if (final is ItemPose pose) SetPose(b.Creeper.transform, pose);
            else b.Creeper.transform.SetPositionAndRotation(b.TargetPosition, b.TargetRotation);
            Publish(b, true);
        }

        private void Publish(Binding b, bool force)
        {
            if (b.Creeper == null || channel == null) return;
            var payload = Encode(Pose(b.Creeper.transform), b.Rider);
            if (!force && b.LastPublished != null && payload.AsSpan().SequenceEqual(b.LastPublished)) return;
            b.LastPublished = payload;
            channel.Publish(b.Id, payload);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b)) Apply(b, record.Payload, true);
            appliedReplica = true;
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter || !bindings.TryGetValue(record.Id, out var b)) return;
            Apply(b, record.Payload, false);
        }
        private void Apply(Binding b, byte[] payload, bool snap)
        {
            if (b.Creeper == null) return;
            var (pose, rider) = Decode(payload);
            uint self = session?.LocalPeerId ?? 0;
            bool allowEntry = SeatAdmission?.AllowsLocalEntry(this, b.Id, rider == 0 ? 1u : 2u) != false;
            b.Rider = rider;
            SeatAdmission?.Observe(this, b.Id, rider == 0 ? 1u : 2u);
            if (riding == b)
            {
                // The host let us go (timeout, range): get off and follow its pose.
                if (rider != self) ExitLocal(b, false);
                else return;
            }
            b.TargetPosition = new Vector3(pose.X, pose.Y, pose.Z); b.TargetRotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
            if (rider == self && !allowEntry)
            {
                b.EnterPending = false;
                channel!.Request(b.Id, EncodeCommand(Action.Exit, pose), true);
                return;
            }
            if (b.EnterPending && rider == self) { SetPose(b.Creeper.transform, pose); EnterLocal(b); return; }
            if (b.EnterPending && rider != 0) b.EnterPending = false;
            if (snap || (b.Creeper.transform.position - b.TargetPosition).sqrMagnitude > 9f) SetPose(b.Creeper.transform, pose);
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true || bindings.Count == 0) return;
            float now = Time.unscaledTime;
            if (host)
            {
                foreach (var b in bindings.Values)
                {
                    if (b.Creeper == null) continue;
                    bool hostRiding = HostRiding(b);
                    if (hostRiding && b.Rider != HostRider)
                    {
                        // Reconcile a direct native entry before remote interpolation can move
                        // a board parented to the host walker, including a preexisting guest claim.
                        b.Rider = HostRider;
                        Publish(b, true);
                    }
                    else if (b.Rider == HostRider && !hostRiding) b.Rider = 0;
                    if (b.Rider == 0 || b.Rider == HostRider) continue;
                    if (now - b.RiderSeen > RiderTimeout) { Exit(b, null); continue; }
                    float blend = 1 - Mathf.Exp(-18 * Time.unscaledDeltaTime);
                    b.Creeper.transform.SetPositionAndRotation(Vector3.Lerp(b.Creeper.transform.position, b.TargetPosition, blend),
                        Quaternion.Slerp(b.Creeper.transform.rotation, b.TargetRotation, blend));
                }
                if (now < nextCapture) return;
                nextCapture = now + 0.1f;
                foreach (var b in bindings.Values) Publish(b, false);
                return;
            }
            float follow = 1 - Mathf.Exp(-14 * Time.unscaledDeltaTime);
            foreach (var b in bindings.Values)
            {
                if (b.EnterPending && now - b.EnterPendingSince > 3)
                { b.EnterPending = false; SeatAdmission?.CancelLocal(this); }
                if (b == riding || b.Creeper == null || !appliedReplica) continue;
                var t = b.Creeper.transform;
                if ((t.position - b.TargetPosition).sqrMagnitude < 1e-6f && Quaternion.Dot(t.rotation, b.TargetRotation) > 0.99999f) continue;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, b.TargetPosition, follow), Quaternion.Slerp(t.rotation, b.TargetRotation, follow));
            }
            if (riding != null)
            {
                // Off by another route (sleep, teleport, the escape menu).
                if (gameplay.Get_PlayerState != PlayerState.InCreeper || gameplay.Get_CurrentCreeper != riding.Creeper) { ExitLocal(riding, true); return; }
                if (now >= nextSend)
                {
                    nextSend = now + SendInterval;
                    channel.Request(riding.Id, EncodeCommand(Action.Move, Pose(riding.Creeper.transform)), true);
                }
            }
        }

        public void Dispose()
        {
            SeatAdmission?.CancelLocal(this);
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host)
            {
                if (riding != null) ExitLocal(riding, false);
                if (appliedReplica)
                    foreach (var b in bindings.Values)
                        if (b.Creeper != null) b.Creeper.transform.SetPositionAndRotation(b.OriginalPosition, b.OriginalRotation);
            }
            else foreach (var b in bindings.Values) if (b.Rider != 0 && b.Rider != HostRider) Exit(b, null);
            session = null; channel = null; presence.Clear();
        }
    }
}
