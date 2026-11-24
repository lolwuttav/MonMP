using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Guests come back where they left. The host remembers each guest profile's last pose, keeps
    // it in that profile's sidecar entry when saving, and answers the rejoining guest's single
    // request with it; the guest then stands there (beside the vehicle if it left while driving).
    internal sealed class SharedReconnectPositions : IDisposable
    {
        internal const string SectionKey = "reconnect-position";
        private const ushort SectionVersion = 1;
        private const int PayloadBytes = 17;
        public const ushort Adapter = 97;
        internal static readonly ulong Entity = StaticId.Hash("reconnect-position/v1");

        private readonly bool host;
        private readonly Func<Guid, SidecarSection?> saved;
        private readonly Func<bool> ready;
        private readonly Func<uint, PlayerPose, Vector3?>? seatedExit;
        private readonly Func<uint, bool>? ownsOtherSeat;
        // Host: the latest pose of each connection, and the pose each departed guest profile left at.
        // Poses are kept per connection: a guest kicked to the menu often rejoins before its old
        // connection times out, and the new connection's default spawn must not overwrite (or be
        // saved over) where the player really was (#109).
        private readonly Dictionary<uint, byte[]> latest = new Dictionary<uint, byte[]>();
        private readonly Dictionary<uint, byte[]> lastWalking = new Dictionary<uint, byte[]>();
        private readonly HashSet<uint> releasedVehicle = new HashSet<uint>();
        private readonly Dictionary<Guid, byte[]> departed = new Dictionary<Guid, byte[]>();
        private readonly Dictionary<uint, Guid> profiles = new Dictionary<uint, Guid>();
        // Connections whose rejoin position was answered (or that have been here long enough); only
        // their poses count as where that player is.
        private readonly HashSet<uint> answered = new HashSet<uint>();
        private readonly Dictionary<uint, float> firstSeen = new Dictionary<uint, float>();
        private readonly Dictionary<uint, (Vector3 Position, float Since)> restoring = new Dictionary<uint, (Vector3, float)>();
        private const float UnansweredGrace = 60f;
        // Guest: the host's answer, applied once the player is on foot.
        private byte[]? pendingReply; private float pendingSince, nextRequest;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool requested, disposed;


        internal bool Settled { get; private set; }


#if MONMP_DEVELOPMENT
        internal bool TestApplied { get; private set; }
#endif


        internal SharedReconnectPositions(bool host, Func<Guid, SidecarSection?> saved, Func<bool> ready,
            Func<uint, PlayerPose, Vector3?>? seatedExit = null, Func<uint, bool>? ownsOtherSeat = null)
        { this.host = host; this.saved = saved; this.ready = ready; this.seatedExit = seatedExit; this.ownsOtherSeat = ownsOtherSeat; }

        internal static IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(Entity, 1, Adapter, new byte[] { 1 }) };

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            connected.PoseReceived += OnPose; connected.PeerLeft += OnLeft;
            world.CommandRequested += Command; world.ReplyReceived += Reply;
        }

        // x, y, z, yaw, and whether the player was on foot.
        private static byte[] Encode(PlayerPose pose)
        {
            using var stream = new MemoryStream(PayloadBytes);
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(pose.X); writer.Write(pose.Y); writer.Write(pose.Z); writer.Write(pose.Yaw);
                writer.Write((byte)(pose.State == (byte)PlayerState.Walking ? 1 : 0));
            }
            return stream.ToArray();
        }

        private static bool Valid(byte[]? bytes)
        {
            if (bytes == null || bytes.Length != PayloadBytes) return false;
            for (int i = 0; i < 4; i++)
            {
                float value = BitConverter.ToSingle(bytes, i * 4);
                if (!float.IsFinite(value) || Math.Abs(value) > (i == 3 ? 360f : 100000f)) return false;
            }
            return BitConverter.ToSingle(bytes, 4) >= NativePlayerSpawn.MinimumY && bytes[16] <= 1;
        }

        private static Vector3 Position(byte[] bytes) =>
            new Vector3(BitConverter.ToSingle(bytes, 0), BitConverter.ToSingle(bytes, 4), BitConverter.ToSingle(bytes, 8));

        private static Vector3 Placement(byte[] bytes)
        {
            var position = Position(bytes);
            // A legacy seated checkpoint stores the seat anchor, not a native exit point.
            // Keep its existing beside-seat fallback; native gravity settles it naturally.
            if (bytes[16] != 0) return position;
            var beside = position + Quaternion.Euler(0, BitConverter.ToSingle(bytes, 12), 0) * Vector3.left * 2f;
            // The decoded root can lie exactly on the protocol boundary. Its offset must
            // remain a valid future player pose too, otherwise reconnect can kick on send.
            return Math.Abs(beside.x) <= 100000f && Math.Abs(beside.y) <= 100000f && Math.Abs(beside.z) <= 100000f ? beside : position;
        }

        private void OnPose(uint peer, PlayerPose pose)
        {
            if (!host || session == null || !session.Peers.TryGetValue(peer, out var info)) return;
            profiles[peer] = info.ProfileId;
            if (!firstSeen.ContainsKey(peer)) firstSeen[peer] = Time.unscaledTime;
            if (!answered.Contains(peer) && Time.unscaledTime - firstSeen[peer] < UnansweredGrace) return;
            // Asleep or passed out the pose is the eyes; keep where they last stood instead.
            if (pose.Rest != PlayerRest.None) return;
            var bytes = Encode(pose);
            if (!Valid(bytes)) return;
            // Unreliable poses sampled at the default join spawn can arrive after the reliable
            // position reply. Wait for the first pose at the restored root before trusting this
            // connection; its sequence then excludes those older in-flight poses.
            if (restoring.TryGetValue(peer, out var restore))
            {
                if ((Position(bytes) - restore.Position).sqrMagnitude > 1f && Time.unscaledTime - restore.Since < UnansweredGrace) return;
                restoring.Remove(peer);
            }
            Vector3? exit = bytes[16] == 0 ? seatedExit?.Invoke(peer, pose) : null;
            // A final reliable vehicle frame can beat its last unreliable seated pose.
            // Keep its exit until walking resumes or an actual new leased seat resolves.
            if (releasedVehicle.Contains(peer))
            {
                if (bytes[16] == 0 && !exit.HasValue && ownsOtherSeat?.Invoke(peer) != true) return;
                releasedVehicle.Remove(peer);
            }
            if (bytes[16] == 1) lastWalking[peer] = bytes;
            else
            {
                if (exit.HasValue)
                {
                    // A resolved native exit is already an on-foot controller root. Keep
                    // the existing 17-byte format and ordinary walking placement semantics.
                    var point = exit.Value;
                    var resolved = Encode(new PlayerPose(point.x, point.y, point.z, pose.Yaw, pose.Pitch));
                    if (Valid(resolved)) bytes = resolved;
                }
                if (bytes[16] == 0 && lastWalking.TryGetValue(peer, out var walking) &&
                    (Position(walking) - Position(bytes)).sqrMagnitude <= 16f) bytes = walking;
            }
            latest[peer] = bytes;
            departed.Remove(info.ProfileId);
        }

        private void OnLeft(uint peer)
        {
            firstSeen.Remove(peer); answered.Remove(peer); restoring.Remove(peer); lastWalking.Remove(peer);
            releasedVehicle.Remove(peer);
            if (!profiles.TryGetValue(peer, out var profile)) { latest.Remove(peer); return; }
            profiles.Remove(peer);
            // A newer connection of the same player that already took its position over is authoritative.
            bool superseded = profiles.Any(pair => pair.Value == profile && latest.ContainsKey(pair.Key));
            if (latest.TryGetValue(peer, out var bytes) && !superseded) departed[profile] = bytes;
            latest.Remove(peer);
        }

        internal void RecordVehicleExit(uint peer, Vector3 point)
        {
            if (!host || disposed || !latest.TryGetValue(peer, out var previous) || !Valid(previous)) return;
            var bytes = Encode(new PlayerPose(point.x, point.y, point.z, BitConverter.ToSingle(previous, 12), 0));
            if (!Valid(bytes)) return;
            latest[peer] = bytes;
            lastWalking[peer] = bytes;
            releasedVehicle.Add(peer);
            restoring.Remove(peer);
        }

        private bool TryCurrent(Guid profile, uint except, out byte[] bytes)
        {
            foreach (var pair in profiles)
                if (pair.Key != except && pair.Value == profile && latest.TryGetValue(pair.Key, out bytes!)) return true;
            bytes = null!; return false;
        }

        // Host save: the known positions go into each profile's sidecar entry; others keep theirs.
        internal IReadOnlyList<SidecarProfile> Stamp(IEnumerable<SidecarProfile> profilesToSave) => profilesToSave.Select(profile =>
        {
            if (!TryCurrent(profile.Id, 0, out var bytes) && !departed.TryGetValue(profile.Id, out bytes)) return profile;
            var state = profile.State.ToDictionary(pair => pair.Key, pair => pair.Value);
            state[SectionKey] = new SidecarSection(SectionVersion, bytes);
            return new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, profile.LastSeenUtc, state);
        }).ToArray();

        [MonMP.Core.World.WorldCommandAdapters(SharedReconnectPositions.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != Entity || peer == 0 || session == null || !session.Peers.TryGetValue(peer, out var info)) return;
            profiles[peer] = info.ProfileId;
            byte[]? bytes = null;
            // Retry a lost/rejected request using the same seed. Do not consume the departed
            // checkpoint until a valid pose from the restored connection replaces it.
            if (answered.Contains(peer) && latest.TryGetValue(peer, out var seed)) bytes = seed;
            else if (departed.TryGetValue(info.ProfileId, out var left)) bytes = left;
            // Rejoined before the old connection timed out: it still holds where the player was.
            else if (TryCurrent(info.ProfileId, peer, out var live)) bytes = live;
            else
            {
                var section = saved(info.ProfileId);
                if (section != null && section.Version == SectionVersion) bytes = section.Payload;
            }
            // A first visit, old/missing section or damaged checkpoint starts at the same
            // scene-authored root advertised in the host world configuration. Seed it just
            // like a saved restore so delayed pre-join poses cannot replace that default.
            if (!Valid(bytes)) bytes = Encode(NativePlayerSpawn.For(Singleton<Gameplay>.i));
            bool firstAnswer = answered.Add(peer);
            if (Valid(bytes))
            {
                latest[peer] = bytes!;
                if (firstAnswer) restoring[peer] = (Placement(bytes!), Time.unscaledTime);
            }
            channel!.Reply(peer, Entity, Valid(bytes) ? new byte[] { 1 }.Concat(bytes!).ToArray() : new byte[] { 0 });
        }

        internal void Update()
        {
            if (!host && pendingReply != null && !disposed)
            {
                // The answer can arrive while the player is still waking up or seated; wait for them to
                // stand before placing them, rather than dropping it.
                if (Place(pendingReply) || Time.unscaledTime - pendingSince > 30f) { pendingReply = null;
Settled = true;
 }
                return;
            }
            if (host || Settled || disposed || channel?.FullySynchronized != true || !ready() || requested && Time.unscaledTime < nextRequest) return;
            var gameplay = Singleton<Gameplay>.i;
            if (gameplay == null || gameplay.Get_PlayerState != PlayerState.Walking) return;
            requested = true;
            nextRequest = Time.unscaledTime + 1f;
            channel.Request(Entity, new byte[] { 1 });
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != Entity || !requested || Settled || pendingReply != null) return;
            byte[]? bytes = payload.Length == PayloadBytes + 1 && payload[0] == 1 ? payload.Skip(1).ToArray() : null;
            // Retain the same fallback with an older/absent host checkpoint response and
            // for guests joining from an already loaded offline world.
            if (!Valid(bytes) && session?.HostWorld != null) bytes = Encode(session.HostWorld.Spawn);
            if (bytes == null || !Valid(bytes) || Place(bytes)) {
Settled = true;
 return; }
            pendingReply = bytes; pendingSince = Time.unscaledTime;
        }

        private bool Place(byte[] bytes)
        {
            var gameplay = Singleton<Gameplay>.i;
            if (!ready() || !Valid(bytes) || gameplay == null || gameplay.Get_PlayerState != PlayerState.Walking ||
                gameplay.Get_IsSleeping || gameplay.Get_IsDead || gameplay.PlayerWalking?.playerCameraController == null) return false;
            var at = Placement(bytes);
            float yaw = BitConverter.ToSingle(bytes, 12);
            // Native saves store this controller root verbatim. A support ray can select a
            // table, vehicle roof or different floor and silently replace a correct saved Y.
            NativePlayerSpawn.PlaceWalking(gameplay, new PlayerPose(at.x, at.y, at.z, yaw, 0));

#if MONMP_DEVELOPMENT
            TestApplied = true;
#endif

            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            session = null; channel = null;
        }

#if MONMP_DEVELOPMENT
        internal bool TestSettled => Settled;
#endif
    }
}
