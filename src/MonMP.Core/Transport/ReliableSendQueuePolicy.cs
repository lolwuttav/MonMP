using System;

namespace MonMP.Core.Transport
{
    // LiteNetLib 2.1.4 queues one NetPacket per unfragmented send and one per
    // fragment. Keep the charge calculation aligned with its public MTU API and
    // SendInternal's six-byte fragment header.
    internal static class ReliableSendQueuePolicy
    {
        public const int HardLimitPacketsPerPeer = 4096;

        public static int PacketCharge(int payloadBytes, int maxSinglePacketPayloadBytes)
        {
            if (payloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            if (maxSinglePacketPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxSinglePacketPayloadBytes));
            if (payloadBytes <= maxSinglePacketPayloadBytes) return 1;

            int fragmentPayloadBytes = maxSinglePacketPayloadBytes - 6;
            if (fragmentPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxSinglePacketPayloadBytes));
            return 1 + ((payloadBytes - 1) / fragmentPayloadBytes);
        }

        public static bool ExceedsLimit(long queuedPackets, int nextPacketCharge) =>
            queuedPackets < 0 || nextPacketCharge < 1 || queuedPackets + nextPacketCharge > HardLimitPacketsPerPeer;
    }
}
