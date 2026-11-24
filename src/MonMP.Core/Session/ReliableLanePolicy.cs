using MonMP.Core.Transport;

namespace MonMP.Core.Session
{
    // Admission rules for the host's per-peer reliable send queue. World
    // snapshot/delta traffic pauses with control headroom below the transport's
    // hard per-peer cap. Optional CD media is admitted only while the world
    // stream is nearly idle and its own small queue has room, so audio fills
    // spare bandwidth instead of delaying pickups, leases and vehicle state.
    public static class ReliableLanePolicy
    {
        public const int WorldQueueHighWaterPackets = 1024;
        public const long WorldQueueHighWaterBytes = 64 * 1024;
        public const int MediaQueueHighWaterPackets = 64;
        public const long MediaQueueHighWaterBytes = 16 * 1024;
        public const long BackgroundMediaQueueHighWaterBytes = 8 * 1024;
        public const int MediaWorldIdlePackets = 16;
        public const long MediaWorldIdleBytes = 32 * 1024;

        public static bool WorldHasHeadroom(in TransportPeerMetrics pressure) =>
            pressure.ReliableQueuePackets - pressure.MediaQueuePackets < WorldQueueHighWaterPackets &&
            pressure.ReliableQueueBytes - pressure.MediaQueueBytes < WorldQueueHighWaterBytes;

        public static bool MediaHasHeadroom(in TransportPeerMetrics pressure, bool background) =>
            pressure.ReliableQueuePackets - pressure.MediaQueuePackets < MediaWorldIdlePackets &&
            pressure.ReliableQueueBytes - pressure.MediaQueueBytes < MediaWorldIdleBytes &&
            pressure.MediaQueuePackets < (background ? MediaQueueHighWaterPackets / 2 : MediaQueueHighWaterPackets) &&
            pressure.MediaQueueBytes < (background ? BackgroundMediaQueueHighWaterBytes : MediaQueueHighWaterBytes);
    }
}
