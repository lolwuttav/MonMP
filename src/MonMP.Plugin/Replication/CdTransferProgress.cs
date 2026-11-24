using System.IO;

namespace MonMP.Plugin.Replication
{
    // Cosmetic queue counts only. This cannot admit, finish or cancel a transfer.
    internal readonly struct CdTransferProgress
    {
        internal const byte Marker = 0xD4;
        // A second manual load can queue 64 clips while one earlier consented
        // upload is still waiting for its receipt.
        internal const int Size = 4, MaximumUploads = 65, MaximumDownloads = 65;
        internal readonly byte Uploads, Downloads;
        internal CdTransferProgress(int uploads, int downloads)
        {
            if (uploads < 0 || uploads > MaximumUploads || downloads < 0 || downloads > MaximumDownloads)
                throw new InvalidDataException("Invalid CD transfer progress.");
            Uploads = (byte)uploads; Downloads = (byte)downloads;
        }
        internal byte[] Encode() => new[] { Marker, (byte)1, Uploads, Downloads };
        internal static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        internal static CdTransferProgress Decode(byte[] payload)
        {
            if (!Is(payload) || payload[1] != 1) throw new InvalidDataException("Invalid CD transfer progress.");
            return new CdTransferProgress(payload[2], payload[3]);
        }
    }
}
