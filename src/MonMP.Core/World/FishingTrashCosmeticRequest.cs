using System;
using System.IO;

namespace MonMP.Core.World
{
    // A validated native result was detached from the rod. No prefab path, item values or saveable
    // object are sent; the host resolves this small request only against its loaded trash array.
    public readonly struct FishingTrashCosmeticRequest
    {
        public const byte Marker = 0xA7;
        public const int Size = 1 + 8 + 4 + 4 + 2 + 2 + 28;
        public readonly ulong Rod;
        public readonly uint Generation, Ticket;
        public readonly ushort Template, RemainingMilliseconds;
        public readonly ItemPose Pose;

        public FishingTrashCosmeticRequest(ulong rod, uint generation, uint ticket, ushort template, ItemPose pose,
            ushort remainingMilliseconds = 6000)
        { Rod = rod; Generation = generation; Ticket = ticket; Template = template; Pose = pose; RemainingMilliseconds = remainingMilliseconds; }

        private void Validate()
        {
            if (Rod == 0 || Generation == 0 || Ticket == 0 || Template == FishingPresentationState.NoTrashTemplate ||
                RemainingMilliseconds == 0 || RemainingMilliseconds > 6000)
                throw new InvalidDataException("Invalid transient fishing trash request.");
            Pose.Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Rod); writer.Write(Generation); writer.Write(Ticket); writer.Write(Template);
            writer.Write(RemainingMilliseconds); Pose.Write(writer);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static FishingTrashCosmeticRequest Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid transient fishing trash request.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid transient fishing trash marker.");
            ulong rod = reader.ReadUInt64(); uint generation = reader.ReadUInt32(), ticket = reader.ReadUInt32();
            ushort template = reader.ReadUInt16(), remaining = reader.ReadUInt16();
            var request = new FishingTrashCosmeticRequest(rod, generation, ticket, template, ItemPose.Read(reader), remaining);
            request.Validate(); return request;
        }
    }
}
