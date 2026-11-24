using System;
using System.IO;

namespace MonMP.Core.World
{
    // A transient trash object spawned by native fishing. It wraps the ordinary catalogued-item
    // request without changing that request's wire format; the host binds it to one accepted catch.
    public readonly struct FishingTrashSpawnRequest
    {
        private const byte Marker = 0xA6;
        private const int HeaderSize = 1 + 8 + 4 + 4 + 2 + 2;
        public readonly ulong Rod;
        public readonly uint RodGeneration, CatchTicket;
        public readonly ushort Template, RemainingMilliseconds;
        public readonly ItemSpawnRequest Spawn;

        public FishingTrashSpawnRequest(ulong rod, uint rodGeneration, uint catchTicket, ushort template, ItemSpawnRequest spawn,
            ushort remainingMilliseconds = 6000)
        { Rod = rod; RodGeneration = rodGeneration; CatchTicket = catchTicket; Template = template; Spawn = spawn; RemainingMilliseconds = remainingMilliseconds; }

        private void Validate()
        {
            if (Rod == 0 || RodGeneration == 0 || CatchTicket == 0 || Template == FishingPresentationState.NoTrashTemplate ||
                RemainingMilliseconds == 0 || RemainingMilliseconds > 6000 || Spawn.Nonce == 0 || Spawn.Hold)
                throw new InvalidDataException("Invalid fishing trash spawn request.");
        }

        public byte[] Encode()
        {
            Validate();
            byte[] spawn = Spawn.Encode();
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Rod); writer.Write(RodGeneration); writer.Write(CatchTicket); writer.Write(Template);
            writer.Write(RemainingMilliseconds); writer.Write(spawn);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static FishingTrashSpawnRequest Decode(byte[] payload)
        {
            if (payload == null || payload.Length <= HeaderSize) throw new InvalidDataException("Invalid fishing trash spawn request.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid fishing trash spawn marker.");
            ulong rod = reader.ReadUInt64(); uint generation = reader.ReadUInt32(), ticket = reader.ReadUInt32();
            ushort template = reader.ReadUInt16(), remaining = reader.ReadUInt16();
            var request = new FishingTrashSpawnRequest(rod, generation, ticket, template,
                ItemSpawnRequest.Decode(reader.ReadBytes(checked((int)(stream.Length - stream.Position)))), remaining);
            request.Validate();
            return request;
        }
    }
}
