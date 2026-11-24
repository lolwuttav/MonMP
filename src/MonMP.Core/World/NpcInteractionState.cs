using System;
using System.IO;
using System.Linq;
using System.Text;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum NpcTalkResult : byte
    {
        Applied = 0,
        Unavailable = 1,
        OutOfRange = 2,
        StalePlayerPose = 3,
        UnsupportedConversation = 4,
        DuplicateRequest = 5
    }

    public readonly struct NpcTalkRequest
    {
        public const byte Marker = 0xB1;
        public const int Size = 25;
        public Guid RequestId { get; }
        public ulong ActorId { get; }

        public NpcTalkRequest(Guid requestId, ulong actorId)
        {
            if (requestId == Guid.Empty || actorId == 0) throw new InvalidDataException("Invalid NPC talk request.");
            RequestId = requestId; ActorId = actorId;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(ActorId); writer.Write(RequestId.ToByteArray());
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static NpcTalkRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC talk request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            _ = reader.ReadByte(); ulong actorId = reader.ReadUInt64(); var requestId = new Guid(reader.ReadBytes(16));
            return new NpcTalkRequest(requestId, actorId);
        }
    }

    public readonly struct NpcTalkReply
    {
        public const byte Marker = 0xB2;
        public const int Size = 19;
        public Guid RequestId { get; }
        public NpcTalkResult Result { get; }
        public bool FullFriendship { get; }

        public NpcTalkReply(Guid requestId, NpcTalkResult result, bool fullFriendship = false)
        {
            if (requestId == Guid.Empty || !Enum.IsDefined(typeof(NpcTalkResult), result)) throw new InvalidDataException("Invalid NPC talk reply.");
            RequestId = requestId; Result = result; FullFriendship = fullFriendship;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(RequestId.ToByteArray()); writer.Write((byte)Result); writer.Write(FullFriendship);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static NpcTalkReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC talk reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            _ = reader.ReadByte(); var requestId = new Guid(reader.ReadBytes(16)); byte result = reader.ReadByte();
            if (!Enum.IsDefined(typeof(NpcTalkResult), result)) throw new InvalidDataException("Unknown NPC talk result.");
            byte fullFriendship = reader.ReadByte();
            if (fullFriendship > 1) throw new InvalidDataException("Invalid NPC talk achievement state.");
            return new NpcTalkReply(requestId, (NpcTalkResult)result, fullFriendship != 0);
        }
    }

    public enum MotherPhoneCallStatus : byte { Applied = 0, AlreadyTalkedToday = 1, Unavailable = 2 }
    public enum MotherPhoneVoiceKind : byte { None = 0, Unique = 1, Random = 2 }

    public readonly struct MotherPhoneCallRequest
    {
        public const byte Marker = 0xB9;
        public const int Size = 25;
        public Guid RequestId { get; }
        public ulong PhoneId { get; }

        public MotherPhoneCallRequest(Guid requestId, ulong phoneId)
        {
            if (requestId == Guid.Empty || phoneId == 0) throw new InvalidDataException("Invalid Mother phone call request.");
            RequestId = requestId; PhoneId = phoneId;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(PhoneId); writer.Write(RequestId.ToByteArray());
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static MotherPhoneCallRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid Mother phone call request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            _ = reader.ReadByte(); ulong phoneId = reader.ReadUInt64(); var requestId = new Guid(reader.ReadBytes(16));
            return new MotherPhoneCallRequest(requestId, phoneId);
        }
    }

    public readonly struct MotherPhoneCallReply
    {
        public const byte Marker = 0xBA;
        public const int Size = 21;
        public Guid RequestId { get; }
        public MotherPhoneCallStatus Status { get; }
        public MotherPhoneVoiceKind VoiceKind { get; }
        public byte Friendship { get; }
        public byte VoiceIndex { get; }

        public MotherPhoneCallReply(Guid requestId, MotherPhoneCallStatus status, MotherPhoneVoiceKind voiceKind,
            byte friendship, byte voiceIndex)
        {
            if (requestId == Guid.Empty || !Enum.IsDefined(typeof(MotherPhoneCallStatus), status) ||
                !Enum.IsDefined(typeof(MotherPhoneVoiceKind), voiceKind) || friendship > 50 ||
                (status != MotherPhoneCallStatus.Applied && voiceKind != MotherPhoneVoiceKind.None) ||
                (voiceKind == MotherPhoneVoiceKind.None && voiceIndex != 0))
                throw new InvalidDataException("Invalid Mother phone call reply.");
            RequestId = requestId; Status = status; VoiceKind = voiceKind; Friendship = friendship; VoiceIndex = voiceIndex;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(RequestId.ToByteArray()); writer.Write((byte)Status);
            writer.Write((byte)VoiceKind); writer.Write(Friendship); writer.Write(VoiceIndex);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static MotherPhoneCallReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid Mother phone call reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload));
            _ = reader.ReadByte(); var requestId = new Guid(reader.ReadBytes(16)); byte status = reader.ReadByte();
            byte voice = reader.ReadByte(), friendship = reader.ReadByte(), voiceIndex = reader.ReadByte();
            if (!Enum.IsDefined(typeof(MotherPhoneCallStatus), status) || !Enum.IsDefined(typeof(MotherPhoneVoiceKind), voice))
                throw new InvalidDataException("Unknown Mother phone call result.");
            return new MotherPhoneCallReply(requestId, (MotherPhoneCallStatus)status, (MotherPhoneVoiceKind)voice, friendship, voiceIndex);
        }
    }

    public readonly struct MotherPhoneCallPlan
    {
        public bool Applied { get; }
        public bool UniqueGreeting { get; }
        public bool UnlockBasket { get; }
        public int Friendship { get; }

        private MotherPhoneCallPlan(bool applied, bool uniqueGreeting, bool unlockBasket, int friendship)
        { Applied = applied; UniqueGreeting = uniqueGreeting; UnlockBasket = unlockBasket; Friendship = friendship; }

        public static MotherPhoneCallPlan Create(bool talkedToday, int friendship, bool basketUnlocked)
        {
            if (friendship < 0 || friendship > 50) throw new InvalidDataException("Invalid Mother friendship value.");
            if (talkedToday) return new MotherPhoneCallPlan(false, false, false, friendship);
            bool unique = !basketUnlocked && friendship < 50;
            int next = friendship < 50 ? friendship + 1 : friendship;
            return new MotherPhoneCallPlan(true, unique, unique && next == 50, next);
        }
    }

    // Facts the host owns for NPC conversations. The event fields let clients
    // show the exact host-localized greeting once, while late joiners only get
    // the authoritative friendship/day state.
    public sealed class NpcInteractionState
    {
        public const ushort Adapter = 47;
        public const int MaxActorTypes = 32;
        public const int MaxGreetingBytes = 192;
        public const ulong Entity = 0x4D4F4E4D505F4E50UL;

        private readonly int[] friendship;
        private readonly bool[] canBeFriends, talkedToday, boughtWeedToday, giftedToday;
        public ulong EventSequence { get; }
        public ulong SpeakerId { get; }
        public string Greeting { get; }
        public int Count => friendship.Length;
        public int[] Friendship => (int[])friendship.Clone();
        public bool[] CanBeFriends => (bool[])canBeFriends.Clone();
        public bool[] TalkedToday => (bool[])talkedToday.Clone();
        public bool[] BoughtWeedToday => (bool[])boughtWeedToday.Clone();
        public bool[] GiftedToday => (bool[])giftedToday.Clone();

        // Compare native polling data against the owned snapshot without exposing or
        // cloning its arrays. Changed data still passes through the constructor's
        // complete validation before it can become an authoritative snapshot.
        public bool MatchesNative(int[]? friendship, bool[]? canBeFriends, bool[]? talkedToday,
            bool[]? boughtWeedToday, bool[]? giftedToday)
        {
            if (friendship == null || canBeFriends == null || talkedToday == null || boughtWeedToday == null || giftedToday == null ||
                friendship.Length != Count || canBeFriends.Length != Count || talkedToday.Length != Count ||
                boughtWeedToday.Length != Count || giftedToday.Length != Count) return false;
            for (int i = 0; i < Count; i++)
                if (friendship[i] != this.friendship[i] || canBeFriends[i] != this.canBeFriends[i] ||
                    talkedToday[i] != this.talkedToday[i] || boughtWeedToday[i] != this.boughtWeedToday[i] ||
                    giftedToday[i] != this.giftedToday[i]) return false;
            return true;
        }

        public NpcInteractionState(ulong eventSequence, ulong speakerId, string greeting,
            int[] friendship, bool[] canBeFriends, bool[] talkedToday, bool[] boughtWeedToday, bool[] giftedToday)
        {
            if (friendship == null || canBeFriends == null || talkedToday == null || boughtWeedToday == null || giftedToday == null ||
                friendship.Length < 1 || friendship.Length > MaxActorTypes || canBeFriends.Length != friendship.Length ||
                talkedToday.Length != friendship.Length || boughtWeedToday.Length != friendship.Length || giftedToday.Length != friendship.Length ||
                friendship.Any(value => value < 0 || value > 50) || greeting == null || Encoding.UTF8.GetByteCount(greeting) > MaxGreetingBytes ||
                (eventSequence == 0 && speakerId != 0) || (eventSequence != 0 && speakerId == 0))
                throw new InvalidDataException("Invalid shared NPC interaction state.");
            EventSequence = eventSequence; SpeakerId = speakerId; Greeting = greeting;
            this.friendship = (int[])friendship.Clone(); this.canBeFriends = (bool[])canBeFriends.Clone();
            this.talkedToday = (bool[])talkedToday.Clone(); this.boughtWeedToday = (bool[])boughtWeedToday.Clone(); this.giftedToday = (bool[])giftedToday.Clone();
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(0x31504E4D); writer.Write(EventSequence); writer.Write(SpeakerId); writer.Write((byte)Count);
            foreach (int value in friendship) writer.Write((byte)value);
            WriteFlags(writer, canBeFriends); WriteFlags(writer, talkedToday); WriteFlags(writer, boughtWeedToday); WriteFlags(writer, giftedToday);
            byte[] text = Encoding.UTF8.GetBytes(Greeting); writer.Write((ushort)text.Length); writer.Write(text);
            return stream.ToArray();
        }

        public static NpcInteractionState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 24 || payload.Length > 512) throw new InvalidDataException("Invalid shared NPC interaction state size.");
            using var stream = new MemoryStream(payload);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 0x31504E4D) throw new InvalidDataException("Invalid shared NPC interaction marker.");
            ulong sequence = reader.ReadUInt64(), speaker = reader.ReadUInt64(); int count = reader.ReadByte();
            if (count < 1 || count > MaxActorTypes || stream.Length - stream.Position < count * 5L + 2) throw new InvalidDataException("Invalid NPC actor count.");
            var friendship = new int[count]; for (int i = 0; i < count; i++) friendship[i] = reader.ReadByte();
            bool[] canFriends = ReadFlags(reader, count), talked = ReadFlags(reader, count), bought = ReadFlags(reader, count), gifted = ReadFlags(reader, count);
            int textLength = reader.ReadUInt16();
            if (textLength > MaxGreetingBytes || textLength != stream.Length - stream.Position) throw new InvalidDataException("Invalid NPC greeting length.");
            string greeting = new UTF8Encoding(false, true).GetString(reader.ReadBytes(textLength));
            return new NpcInteractionState(sequence, speaker, greeting, friendship, canFriends, talked, bought, gifted);
        }

        private static void WriteFlags(BinaryWriter writer, bool[] flags)
        { foreach (bool value in flags) writer.Write((byte)(value ? 1 : 0)); }
        private static bool[] ReadFlags(BinaryReader reader, int count)
        {
            var flags = new bool[count];
            for (int i = 0; i < count; i++)
            {
                byte value = reader.ReadByte();
                if (value > 1) throw new InvalidDataException("Invalid NPC flag.");
                flags[i] = value == 1;
            }
            return flags;
        }
    }

    public static class NpcTalkPolicy
    {
        public static bool IsSafeHostConversation(bool isActor, bool baseMouseDown, bool baseCanTalk,
            bool baseActionOnClick, bool baseDialogStarted, bool baseGreeting, bool baseOptionHandler,
            bool canTalk, bool hasChoice, int actorType, bool allowApprovedGreetingOverride = false,
            bool isRouteBound = false, bool hasSharedRouteBinding = false)
        {
            return isActor && actorType > 0 && actorType < NpcInteractionState.MaxActorTypes && baseMouseDown && baseCanTalk &&
                baseActionOnClick && baseDialogStarted && (baseGreeting || allowApprovedGreetingOverride) && baseOptionHandler && canTalk && !hasChoice &&
                (!isRouteBound || hasSharedRouteBinding);
        }

        public static NpcTalkResult ValidateHostRequest(bool safeConversation, bool actorActive, bool playerPoseFresh,
            bool canTalk, bool hasChoice, PlayerPose player, float actorX, float actorY, float actorZ, float maxDistance)
        {
            if (!safeConversation) return NpcTalkResult.UnsupportedConversation;
            if (!actorActive) return NpcTalkResult.Unavailable;
            if (!playerPoseFresh) return NpcTalkResult.StalePlayerPose;
            if (!Finite(actorX) || !Finite(actorY) || !Finite(actorZ) || !Finite(maxDistance) || maxDistance <= 0 || maxDistance > 100)
                return NpcTalkResult.UnsupportedConversation;
            double dx = player.X - actorX, dy = player.Y - actorY, dz = player.Z - actorZ;
            if (dx * dx + dy * dy + dz * dz > maxDistance * maxDistance) return NpcTalkResult.OutOfRange;
            if (!canTalk || hasChoice) return NpcTalkResult.Unavailable;
            return NpcTalkResult.Applied;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
