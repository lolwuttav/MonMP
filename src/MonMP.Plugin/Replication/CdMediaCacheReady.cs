using System;
using System.IO;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // A guest can stop only its own matching transfer after verified local decode.
    // The world command supplies the radio entity; no local path crosses the wire.
    internal sealed class CdMediaCacheReady
    {
        internal const byte Marker = 0xD2;
        private const byte Version = 1;
        internal const int Size = 2 + 4 + CdPlaybackState.HashSize;
        internal readonly uint TransferId;
        internal readonly byte[] ContentHash;

        internal CdMediaCacheReady(uint transferId, byte[] hash)
        {
            if (transferId == 0 || hash == null || hash.Length != CdPlaybackState.HashSize || CdPlaybackState.IsEmptyHash(hash))
                throw new InvalidDataException("Invalid CD cache-ready command.");
            TransferId = transferId;
            ContentHash = (byte[])hash.Clone();
        }

        internal byte[] Encode()
        {
            var payload = new byte[Size];
            payload[0] = Marker; payload[1] = Version;
            BitConverter.GetBytes(TransferId).CopyTo(payload, 2);
            Buffer.BlockCopy(ContentHash, 0, payload, 6, ContentHash.Length);
            return payload;
        }

        internal static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        internal static CdMediaCacheReady Decode(byte[] payload)
        {
            if (!Is(payload) || payload[1] != Version) throw new InvalidDataException("Invalid CD cache-ready command.");
            var hash = new byte[CdPlaybackState.HashSize];
            Buffer.BlockCopy(payload, 6, hash, 0, hash.Length);
            return new CdMediaCacheReady(BitConverter.ToUInt32(payload, 2), hash);
        }

        internal bool Matches(uint transferId, byte[] hash) => TransferId == transferId && CdPlaybackState.HashEquals(ContentHash, hash);
    }
}
