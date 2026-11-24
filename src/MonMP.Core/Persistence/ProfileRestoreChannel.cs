using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Session;
using MonMP.Core.World;

namespace MonMP.Core.Persistence
{
    public sealed class ProfileRestoreMessage
    {
        public const int EncodedBytes = 48 + PlayerProfileState.EncodedBytes;
        public Guid World { get; }
        public Guid Generation { get; }
        public Guid Profile { get; }
        public PlayerProfileState State { get; }
        public ProfileRestoreMessage(Guid world, Guid generation, Guid profile, PlayerProfileState state)
        {
            if (world == Guid.Empty || generation == Guid.Empty || profile == Guid.Empty || state == null)
                throw new InvalidDataException("Invalid restored profile identity.");
            World = world; Generation = generation; Profile = profile; State = state;
        }
        public byte[] Encode() => World.ToByteArray().Concat(Generation.ToByteArray()).Concat(Profile.ToByteArray()).Concat(State.Encode()).ToArray();
        public static ProfileRestoreMessage Decode(byte[] bytes)
        {
            if (bytes.Length != EncodedBytes) throw new InvalidDataException("Invalid restored profile length.");
            return new ProfileRestoreMessage(new Guid(bytes.Take(16).ToArray()), new Guid(bytes.Skip(16).Take(16).ToArray()),
                new Guid(bytes.Skip(32).Take(16).ToArray()), PlayerProfileState.Decode(bytes.Skip(48).ToArray()));
        }
    }
    public enum ProfileRestorePhase { Waiting, Receiving, Ready, Applied, Absent, Failed }

    // Private host replies only; native application remains a separate explicit step.
    public sealed class ProfileRestoreChannel : IDisposable
    {
        public const ushort Adapter = 12;
        public static readonly ulong Entity = StaticId.Hash("private-profile-restore/v1");
        private readonly NetworkSession session;
        private readonly WorldChannel world;
        private readonly Func<Guid, ProfileRestoreMessage?> lookup;
        private readonly byte[] buffer = new byte[ProfileRestoreMessage.EncodedBytes];
        private readonly bool[] received = new bool[(ProfileRestoreMessage.EncodedBytes + SaveFlushPart.ChunkBytes - 1) / SaveFlushPart.ChunkBytes];
        private Guid transfer;
        private ProfileRestoreMessage? message;
        private bool disposed;
        public ProfileRestorePhase Phase { get; private set; }
        public static WorldRecord InitialRecord() => new WorldRecord(Entity, 1, Adapter, new byte[] { 1 });
        public ProfileRestoreChannel(NetworkSession session, WorldChannel world, Func<Guid, ProfileRestoreMessage?> lookup)
        {
            this.session = session; this.world = world; this.lookup = lookup;
            world.PeerSynchronized += Send; world.ReplyReceived += Receive;
        }
        private void Send(uint peer)
        {
            if (!session.IsHost || !session.Peers.TryGetValue(peer, out var info)) return;
            byte[]? payload = null;
            try
            {
                var saved = lookup(info.ProfileId);
                if (saved != null)
                {
                    if (saved.Profile != info.ProfileId) throw new InvalidDataException("Saved profile lookup returned another player's identity.");
                    payload = saved.Encode();
                }
            }
            catch (Exception) { /* Optional saved personal data must not prevent a friend joining. */ }
            if (payload == null) { world.Reply(peer, Entity, new byte[] { 0 }); return; }
            foreach (var part in SaveFlushPart.Split(Guid.NewGuid(), payload))
                world.Reply(peer, Entity, new byte[] { 1 }.Concat(part.Encode()).ToArray());
        }
        // This reply is addressed to this channel, so unlike another adapter's misread it is the host
        // breaking the private restore: it still ends the connection.
        private void Receive(ulong entity, byte[] bytes)
        {
            if (session.IsHost || entity != Entity || disposed) return;
            try { ReceiveOwn(bytes); }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException)
            { throw new ReplyProtocolException(ex.Message, ex); }
        }
        private void ReceiveOwn(byte[] bytes)
        {
            if (bytes.Length == 1 && bytes[0] == 0)
            {
                if (Phase == ProfileRestorePhase.Failed) return;
                if (Phase != ProfileRestorePhase.Waiting && Phase != ProfileRestorePhase.Absent) throw new InvalidDataException("Profile restoration changed to absent mid-transfer.");
                Phase = ProfileRestorePhase.Absent; return;
            }
            if (bytes.Length < 2 || bytes[0] != 1 || Phase == ProfileRestorePhase.Absent)
                throw new InvalidDataException("Invalid profile restore reply.");
            var part = SaveFlushPart.Decode(bytes.Skip(1).ToArray());
            if (part.TotalBytes != buffer.Length) throw new InvalidDataException("Invalid profile restore size.");
            // Late valid saved data is inert after fallback; malformed wire framing still rejects.
            if (Phase == ProfileRestorePhase.Failed) return;
            if (transfer == Guid.Empty) transfer = part.Round;
            if (transfer != part.Round) throw new InvalidDataException("Profile restoration transfer changed.");
            byte[] chunk = part.Payload; int offset = part.Index * SaveFlushPart.ChunkBytes;
            if (received[part.Index])
            {
                for (int i = 0; i < chunk.Length; i++) if (buffer[offset + i] != chunk[i]) throw new InvalidDataException("Contradictory profile restore chunk.");
                return;
            }
            Array.Copy(chunk, 0, buffer, offset, chunk.Length); received[part.Index] = true; Phase = ProfileRestorePhase.Receiving;
            if (!received.All(p => p)) return;
            // Validate routing/identity before treating invalid saved values as an optional restore failure.
            var savedWorld = new Guid(buffer.Take(16).ToArray());
            var generation = new Guid(buffer.Skip(16).Take(16).ToArray());
            var profile = new Guid(buffer.Skip(32).Take(16).ToArray());
            if (savedWorld == Guid.Empty || generation == Guid.Empty || profile != session.ProfileId)
                throw new InvalidDataException("Restored profile has an invalid world or player identity.");
            ProfileRestoreMessage decoded;
            try { decoded = ProfileRestoreMessage.Decode(buffer); }
            catch (InvalidDataException) { UseLocalFallback(); return; }
            message = decoded; Phase = ProfileRestorePhase.Ready;
        }
        public void UseLocalFallback()
        { if (!session.IsHost && !disposed) { message = null; Phase = ProfileRestorePhase.Failed; } }
        public bool TryApply(Action<ProfileRestoreMessage> apply)
        {
            if (disposed || Phase != ProfileRestorePhase.Ready) return false;
            // Claim before invoking native code: a partial application must never be retried.
            Phase = ProfileRestorePhase.Failed;
            apply(message!); Phase = ProfileRestorePhase.Applied; return true;
        }
        public void Dispose()
        { if (disposed) return; disposed = true; world.PeerSynchronized -= Send; world.ReplyReceived -= Receive; }
    }
}
