using System;
using System.Buffers.Binary;

namespace MonMP.Plugin.Replication
{
    // All three native hinge records encode one flag byte followed by the exact float bits.
    // Compare against the channel's current record so recovery/re-registration needs no cache invalidation.
    internal static class VehicleHingePayload
    {
        internal static bool Matches(ReadOnlySpan<byte> payload, int offset, byte flags, float angle) =>
            offset >= 0 && payload.Length - offset >= 5 && payload[offset] == flags &&
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset + 1, 4)) == BitConverter.SingleToInt32Bits(angle);
    }
}
