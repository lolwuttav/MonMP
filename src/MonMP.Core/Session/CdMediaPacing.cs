using System;
using MonMP.Core.Transport;

namespace MonMP.Core.Session
{
    // Bulk audio stays on channel 2 and yields to the existing queue-pressure gate.
    // Background playlist prefetch draws from an additional slower bucket so it
    // never competes with a track a player is waiting to hear.
    public sealed class CdMediaPacing
    {
        public const int PeerBytesPerSecond = 512 * 1024;
        public const int BackgroundBytesPerSecond = 256 * 1024;
        public const int HostBytesPerSecond = 2 * 1024 * 1024;
        public const int HostBurstBytes = 64 * 1024;
        public const int PacketBurst = 32;
        public const int BackgroundPacketBurst = 16;
        public const int UploadBytesPerSecond = 256 * 1024, UploadPacketBurst = 16;
        // The receiver allows five seconds of queued legal traffic after a game
        // frame stall, while independently bounding both bytes and tiny messages.
        public const int UploadReceiveBytesPerSecond = 2 * UploadBytesPerSecond;
        public const int UploadReceivePacketsPerSecond = 640, UploadReceivePacketBurst = 3200;
        public const int UploadReceiveBurstBytes = 5 * UploadReceiveBytesPerSecond;
        private const double PacketBytes = Protocol.CdMediaMessage.ChunkBytes + 64.0;
        private double tokens = PacketBurst, backgroundTokens = BackgroundPacketBurst, previous;
        private double uploadTokens = UploadPacketBurst, uploadPrevious;
        private double receiveBytes = UploadReceiveBurstBytes, receivePackets = UploadReceivePacketBurst, receivePrevious;
        public static int TransportBudget(TransportKind kind, in TransportPeerMetrics pressure) =>
            kind == TransportKind.Steam && pressure.MediaBytesPerSecond >= 0 ? pressure.MediaBytesPerSecond : int.MaxValue;
        public bool TryReceiveUpload(double now, int bytes)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0 || bytes < 23 || bytes > Protocol.Wire.MaxPacketBytes) return false;
            // Different reliable lanes may deliver older local receipt times.
            // Never grant time twice or reset a healthy budget on that ordering.
            now = Math.Max(now, receivePrevious);
            double elapsed = Math.Max(0, now - receivePrevious);
            receiveBytes = Math.Min(UploadReceiveBurstBytes, receiveBytes + elapsed * UploadReceiveBytesPerSecond);
            receivePackets = Math.Min(UploadReceivePacketBurst, receivePackets + elapsed * UploadReceivePacketsPerSecond);
            receivePrevious = now;
            if (receiveBytes < bytes || receivePackets < 1) return false;
            receiveBytes -= bytes; receivePackets--; return true;
        }
        public bool TryTakeUpload(double now) => TryTakeUpload(now, int.MaxValue);
        public bool TryTakeUpload(double now, int mediaBytesPerSecond)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) return false;
            if (mediaBytesPerSecond <= 0) { uploadTokens = 0; uploadPrevious = now; return false; }
            double rate = Math.Min(UploadBytesPerSecond, mediaBytesPerSecond);
            double burst = Math.Min(UploadPacketBurst, Math.Max(1, rate * 0.1 / PacketBytes));
            if (now < uploadPrevious) uploadTokens = 0;
            uploadTokens = Math.Min(burst, uploadTokens + Math.Max(0, now - uploadPrevious) * rate / PacketBytes);
            uploadPrevious = now;
            if (uploadTokens < 1) return false;
            uploadTokens--; return true;
        }
        public bool TryTake(double now, bool background = false) => TryTake(now, background, int.MaxValue);
        public bool TryTake(double now, bool background, int mediaBytesPerSecond)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) return false;
            if (mediaBytesPerSecond <= 0) { tokens = backgroundTokens = 0; previous = now; return false; }
            double rate = Math.Min(PeerBytesPerSecond, mediaBytesPerSecond);
            double backgroundRate = Math.Min(BackgroundBytesPerSecond, mediaBytesPerSecond);
            double burst = Math.Min(PacketBurst, Math.Max(1, rate * 0.1 / PacketBytes));
            double backgroundBurst = Math.Min(BackgroundPacketBurst, Math.Max(1, backgroundRate * 0.1 / PacketBytes));
            if (now < previous) tokens = backgroundTokens = 0;
            double elapsed = Math.Max(0, now - previous);
            tokens = Math.Min(burst, tokens + elapsed * rate / PacketBytes);
            backgroundTokens = Math.Min(backgroundBurst, backgroundTokens + elapsed * backgroundRate / PacketBytes);
            previous = now;
            if (tokens < 1 || (background && backgroundTokens < 1)) return false;
            tokens--;
            if (background) backgroundTokens--;
            return true;
        }
    }
}
