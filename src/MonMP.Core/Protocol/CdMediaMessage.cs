using System;
using System.IO;
using MonMP.Core.World;

namespace MonMP.Core.Protocol
{
    public enum CdMediaMessageKind : byte { Begin = 1, Chunk = 2, Complete = 3, Abort = 4, Offer = 5, UploadOffer = 6, UploadReply = 7 }
    public enum CdUploadStatus : byte { Send = 1, Stored = 2, Busy = 3, Rejected = 4 }
    public enum CdMediaFormat : byte { Wav = 1, Mp3 = 2, Ogg = 3 }

    // Bounded reliable payloads for host-selected CD content. The envelope is carried in Wire's
    // CdMedia message kind (23 bytes of common Wire framing plus this body). No file path or
    // arbitrary filename crosses the connection.
    public sealed class CdMediaMessage
    {
        public const int MaximumFileBytes = 32 * 1024 * 1024;
        public const int ChunkBytes = 900;
        public const int MaximumChunks = (MaximumFileBytes + ChunkBytes - 1) / ChunkBytes;

        public readonly CdMediaMessageKind Kind;
        public readonly ulong RadioEntity;
        public readonly uint TransferId;
        public readonly byte[] ContentHash;
        public readonly uint FileBytes;
        public readonly uint DecodedBytes;
        public readonly CdMediaFormat Format;
        public readonly ushort ChunkCount;
        public readonly ushort ChunkIndex;
        public readonly byte[] Data;
        public readonly byte AbortReason;

        private CdMediaMessage(CdMediaMessageKind kind, ulong radioEntity, uint transferId, byte[]? hash,
            uint fileBytes, uint decodedBytes, CdMediaFormat format, ushort chunkCount, ushort chunkIndex, byte[]? data, byte abortReason)
        {
            Kind = kind; RadioEntity = radioEntity; TransferId = transferId;
            ContentHash = hash == null ? Array.Empty<byte>() : (byte[])hash.Clone();
            FileBytes = fileBytes; DecodedBytes = decodedBytes; Format = format; ChunkCount = chunkCount; ChunkIndex = chunkIndex;
            Data = data == null ? Array.Empty<byte>() : (byte[])data.Clone(); AbortReason = abortReason;
            Validate();
        }

        // StartChunk (carried in ChunkIndex) resumes an interrupted transfer of the same content;
        // chunks before it are not sent again.
        public static CdMediaMessage Begin(ulong entity, uint transferId, byte[] hash, uint fileBytes, uint decodedBytes, CdMediaFormat format, ushort startChunk = 0)
        {
            ushort chunks = ExpectedChunks(fileBytes);
            return new CdMediaMessage(CdMediaMessageKind.Begin, entity, transferId, hash, fileBytes, decodedBytes, format, chunks, startChunk, null, 0);
        }
        // Playlist offers authorize bounded background requests, without disclosing source paths.
        public static CdMediaMessage Offer(ulong entity, uint offerId, byte[] hash, uint fileBytes, uint decodedBytes, CdMediaFormat format) =>
            new CdMediaMessage(CdMediaMessageKind.Offer, entity, offerId, hash, fileBytes, decodedBytes, format, ExpectedChunks(fileBytes), 0, null, 0);
        public static CdMediaMessage UploadOffer(uint id, byte[] hash, uint fileBytes, uint decodedBytes, CdMediaFormat format) =>
            new CdMediaMessage(CdMediaMessageKind.UploadOffer, UploadEntity, id, hash, fileBytes, decodedBytes, format, ExpectedChunks(fileBytes), 0, null, 0);
        public static CdMediaMessage UploadReply(uint id, byte[] hash, CdUploadStatus status) =>
            new CdMediaMessage(CdMediaMessageKind.UploadReply, UploadEntity, id, hash, 0, 0, 0, 0, 0, null, (byte)status);
        public static readonly ulong UploadEntity = StaticId.Hash("radio/cd/manual-upload/v1") | WorldRecord.DynamicIdStart;
        public static CdMediaMessage Chunk(ulong entity, uint transferId, ushort index, byte[] data) =>
            new CdMediaMessage(CdMediaMessageKind.Chunk, entity, transferId, null, 0, 0, 0, 0, index, data, 0);
        public static CdMediaMessage Complete(ulong entity, uint transferId, byte[] hash) =>
            new CdMediaMessage(CdMediaMessageKind.Complete, entity, transferId, hash, 0, 0, 0, 0, 0, null, 0);
        public static CdMediaMessage Abort(ulong entity, uint transferId, byte reason) =>
            new CdMediaMessage(CdMediaMessageKind.Abort, entity, transferId, null, 0, 0, 0, 0, 0, null, reason);

        public static ushort ExpectedChunks(uint fileBytes)
        {
            if (fileBytes == 0 || fileBytes > MaximumFileBytes) throw new InvalidDataException("CD media size exceeds its limit.");
            return checked((ushort)((fileBytes + ChunkBytes - 1) / ChunkBytes));
        }

        private void Validate()
        {
            if (RadioEntity <= WorldRecord.DynamicIdStart || TransferId == 0 || !Enum.IsDefined(typeof(CdMediaMessageKind), Kind))
                throw new InvalidDataException("Invalid CD media message.");
            switch (Kind)
            {
                case CdMediaMessageKind.Offer:
                case CdMediaMessageKind.UploadOffer:
                case CdMediaMessageKind.Begin:
                    if (ContentHash.Length != CdPlaybackState.HashSize || CdPlaybackState.IsEmptyHash(ContentHash) ||
                        FileBytes == 0 || FileBytes > MaximumFileBytes ||
                        !MonMP.Core.World.CdMediaCachePolicy.CanRetainDecoded(0, DecodedBytes) || !Enum.IsDefined(typeof(CdMediaFormat), Format) ||
                        ChunkCount != ExpectedChunks(FileBytes) || Data.Length != 0 || AbortReason != 0 ||
                        (Kind == CdMediaMessageKind.Begin ? ChunkIndex > ChunkCount : ChunkIndex != 0))
                        throw new InvalidDataException("Invalid CD media begin message.");
                    if (Kind == CdMediaMessageKind.UploadOffer && RadioEntity != UploadEntity) throw new InvalidDataException("Invalid CD upload entity.");
                    break;
                case CdMediaMessageKind.UploadReply:
                    if (RadioEntity != UploadEntity || ContentHash.Length != CdPlaybackState.HashSize || CdPlaybackState.IsEmptyHash(ContentHash) ||
                        FileBytes != 0 || DecodedBytes != 0 || Format != 0 || ChunkCount != 0 || ChunkIndex != 0 || Data.Length != 0 ||
                        AbortReason < (byte)CdUploadStatus.Send || AbortReason > (byte)CdUploadStatus.Rejected)
                        throw new InvalidDataException("Invalid CD upload reply.");
                    break;
                case CdMediaMessageKind.Chunk:
                    if (ContentHash.Length != 0 || FileBytes != 0 || DecodedBytes != 0 || Format != 0 || ChunkCount != 0 ||
                        Data.Length == 0 || Data.Length > ChunkBytes || AbortReason != 0)
                        throw new InvalidDataException("Invalid CD media chunk message.");
                    break;
                case CdMediaMessageKind.Complete:
                    if (ContentHash.Length != CdPlaybackState.HashSize || CdPlaybackState.IsEmptyHash(ContentHash) ||
                        FileBytes != 0 || DecodedBytes != 0 || Format != 0 || ChunkCount != 0 || ChunkIndex != 0 || Data.Length != 0 || AbortReason != 0)
                        throw new InvalidDataException("Invalid CD media completion message.");
                    break;
                case CdMediaMessageKind.Abort:
                    if (ContentHash.Length != 0 || FileBytes != 0 || DecodedBytes != 0 || Format != 0 || ChunkCount != 0 || ChunkIndex != 0 || Data.Length != 0 || AbortReason == 0)
                        throw new InvalidDataException("Invalid CD media abort message.");
                    break;
            }
        }

        public void WriteBody(BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            Validate();
            writer.Write((byte)Kind);
            writer.Write(RadioEntity);
            writer.Write(TransferId);
            switch (Kind)
            {
                case CdMediaMessageKind.Offer:
                case CdMediaMessageKind.UploadOffer:
                case CdMediaMessageKind.Begin:
                    writer.Write(ContentHash);
                    writer.Write(FileBytes);
                    writer.Write(DecodedBytes);
                    writer.Write((byte)Format);
                    writer.Write(ChunkCount);
                    if (Kind == CdMediaMessageKind.Begin) writer.Write(ChunkIndex);
                    break;
                case CdMediaMessageKind.Chunk:
                    writer.Write(ChunkIndex);
                    writer.Write((ushort)Data.Length);
                    writer.Write(Data);
                    break;
                case CdMediaMessageKind.Complete:
                    writer.Write(ContentHash);
                    break;
                case CdMediaMessageKind.Abort:
                    writer.Write(AbortReason);
                    break;
                case CdMediaMessageKind.UploadReply:
                    writer.Write(ContentHash); writer.Write(AbortReason); break;
            }
        }

        public static CdMediaMessage ReadBody(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            try
            {
                CdMediaMessageKind kind = (CdMediaMessageKind)reader.ReadByte();
                ulong entity = reader.ReadUInt64();
                uint transfer = reader.ReadUInt32();
                CdMediaMessage message;
                switch (kind)
                {
                    case CdMediaMessageKind.Offer:
                    case CdMediaMessageKind.UploadOffer:
                    case CdMediaMessageKind.Begin:
                        var beginHash = ReadExact(reader, CdPlaybackState.HashSize);
                        uint size = reader.ReadUInt32();
                        uint decodedBytes = reader.ReadUInt32();
                        var format = (CdMediaFormat)reader.ReadByte();
                        ushort chunks = reader.ReadUInt16();
                        ushort start = kind == CdMediaMessageKind.Begin ? reader.ReadUInt16() : (ushort)0;
                        message = new CdMediaMessage(kind, entity, transfer, beginHash, size, decodedBytes, format, chunks, start, null, 0);
                        break;
                    case CdMediaMessageKind.Chunk:
                        ushort index = reader.ReadUInt16();
                        ushort count = reader.ReadUInt16();
                        if (count == 0 || count > ChunkBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
                            throw new InvalidDataException("Invalid CD media chunk length.");
                        message = new CdMediaMessage(kind, entity, transfer, null, 0, 0, 0, 0, index, ReadExact(reader, count), 0);
                        break;
                    case CdMediaMessageKind.Complete:
                        message = new CdMediaMessage(kind, entity, transfer, ReadExact(reader, CdPlaybackState.HashSize), 0, 0, 0, 0, 0, null, 0);
                        break;
                    case CdMediaMessageKind.Abort:
                        message = new CdMediaMessage(kind, entity, transfer, null, 0, 0, 0, 0, 0, null, reader.ReadByte());
                        break;
                    case CdMediaMessageKind.UploadReply:
                        message = new CdMediaMessage(kind, entity, transfer, ReadExact(reader, CdPlaybackState.HashSize), 0, 0, 0, 0, 0, null, reader.ReadByte());
                        break;
                    default: throw new InvalidDataException("Unknown CD media message kind.");
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing CD media bytes.");
                return message;
            }
            catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated CD media message.", ex); }
        }

        private static byte[] ReadExact(BinaryReader reader, int count)
        {
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Truncated CD media field.");
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new InvalidDataException("Truncated CD media field.");
            return bytes;
        }
    }

    // Single-transfer receiver. It accepts only sequential chunks for the exact currently selected
    // host hash, allocates at most MaximumFileBytes, and verifies the digest incrementally as chunks arrive.
    // An interrupted transfer is kept as one suspended partial (bounded by the same size and lifetime),
    // so a later request for that content can resume at ResumeChunkFor instead of starting over.
    public sealed class CdMediaAssembler
    {
        public const float TimeoutSeconds = 900f;
        public const float InactivityTimeoutSeconds = 30f;
        private sealed class Transfer
        {
            public ulong Entity;
            public uint Id;
            public byte[] Hash = Array.Empty<byte>();
            public byte[] Bytes = Array.Empty<byte>();
            public CdMediaFormat Format;
            public uint DecodedBytes;
            public ushort ChunkCount, NextChunk;
            public float StartedAt, LastPacketAt;
            public System.Security.Cryptography.SHA256 Hasher = System.Security.Cryptography.SHA256.Create();
        }
        private Transfer? transfer, suspended;
        private float suspendedAt;
        private readonly float timeoutSeconds;
        public CdMediaAssembler() : this(TimeoutSeconds) { }
        public CdMediaAssembler(float timeoutSeconds)
        {
            if (float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 3600)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            this.timeoutSeconds = timeoutSeconds;
        }
        public bool Active => transfer != null;
        public uint ActiveTransferId => transfer?.Id ?? 0;
        public byte[]? ActiveHash => transfer == null ? null : (byte[])transfer.Hash.Clone();
        // Discards the active transfer and any suspended partial.
        public void Cancel() { ClearTransfer(); DiscardSuspended(); }
        // Stops the active transfer but keeps its received prefix for a later resume.
        public void Suspend(float now) => SuspendActive(now);

        // The first chunk still missing from a suspended partial of exactly this content, or 0.
        public ushort ResumeChunkFor(byte[]? hash, float now)
        {
            ExpireSuspended(now);
            return suspended != null && hash != null && CdPlaybackState.HashEquals(hash, suspended.Hash) ? suspended.NextChunk : (ushort)0;
        }

        public bool Accept(CdMediaMessage message, ulong expectedEntity, byte[]? expectedHash, float now, out byte[]? completedBytes, out CdMediaFormat completedFormat, out uint completedDecodedBytes)
        {
            completedBytes = null; completedFormat = 0; completedDecodedBytes = 0;
            if (message == null || float.IsNaN(now) || float.IsInfinity(now)) return false;
            if (transfer != null && (now < transfer.StartedAt || now - transfer.StartedAt > timeoutSeconds || now - transfer.LastPacketAt > InactivityTimeoutSeconds))
                SuspendActive(now);
            ExpireSuspended(now);
            switch (message.Kind)
            {
                case CdMediaMessageKind.Begin:
                    SuspendActive(now);
                    if (message.RadioEntity != expectedEntity || expectedHash == null ||
                        !CdPlaybackState.HashEquals(message.ContentHash, expectedHash) || message.FileBytes > CdMediaMessage.MaximumFileBytes)
                        return false;
                    var partial = suspended;
                    if (partial != null && CdPlaybackState.HashEquals(partial.Hash, message.ContentHash) &&
                        partial.Bytes.Length == message.FileBytes && partial.Format == message.Format &&
                        partial.DecodedBytes == message.DecodedBytes && partial.ChunkCount == message.ChunkCount &&
                        message.ChunkIndex <= partial.NextChunk)
                    {
                        // Resent chunks before the partial's end are skipped, not stored again.
                        suspended = null;
                        partial.Entity = message.RadioEntity; partial.Id = message.TransferId;
                        partial.StartedAt = now; partial.LastPacketAt = now;
                        transfer = partial;
                        return true;
                    }
                    if (message.ChunkIndex != 0) return false;
                    if (partial != null && CdPlaybackState.HashEquals(partial.Hash, message.ContentHash)) DiscardSuspended();
                    transfer = new Transfer
                    {
                        Entity = message.RadioEntity, Id = message.TransferId, Hash = (byte[])message.ContentHash.Clone(),
                        Bytes = new byte[(int)message.FileBytes], Format = message.Format, DecodedBytes = message.DecodedBytes, ChunkCount = message.ChunkCount,
                        StartedAt = now, LastPacketAt = now
                    };
                    return true;
                case CdMediaMessageKind.Chunk:
                    if (transfer == null || !Matches(transfer, message, expectedEntity, expectedHash) || message.ChunkIndex > transfer.NextChunk ||
                        message.ChunkIndex >= transfer.ChunkCount) { SuspendActive(now); return false; }
                    if (message.ChunkIndex < transfer.NextChunk) { transfer.LastPacketAt = now; return true; }
                    int offset = message.ChunkIndex * CdMediaMessage.ChunkBytes;
                    int expectedLength = Math.Min(CdMediaMessage.ChunkBytes, transfer.Bytes.Length - offset);
                    if (expectedLength <= 0 || message.Data.Length != expectedLength) { SuspendActive(now); return false; }
                    Buffer.BlockCopy(message.Data, 0, transfer.Bytes, offset, message.Data.Length);
                    // Hash each bounded packet as it arrives. Completion then verifies in constant
                    // work instead of hashing a possible 32 MiB file on Unity's main thread.
                    transfer.Hasher.TransformBlock(message.Data, 0, message.Data.Length, message.Data, 0);
                    transfer.NextChunk++;
                    transfer.LastPacketAt = now;
                    return true;
                case CdMediaMessageKind.Complete:
                    if (transfer == null || !Matches(transfer, message, expectedEntity, expectedHash) || transfer.NextChunk != transfer.ChunkCount)
                    { SuspendActive(now); return false; }
                    if (!FinishHash(transfer)) { ClearTransfer(); return false; }
                    completedBytes = transfer.Bytes;
                    completedFormat = transfer.Format;
                    completedDecodedBytes = transfer.DecodedBytes;
                    ClearTransfer();
                    return true;
                case CdMediaMessageKind.Abort:
                    if (transfer == null || message.RadioEntity != transfer.Entity || message.TransferId != transfer.Id) return false;
                    SuspendActive(now);
                    return true;
                default: SuspendActive(now); return false;
            }
        }

        public void Expire(float now)
        {
            if (transfer != null && (float.IsNaN(now) || float.IsInfinity(now) || now < transfer.StartedAt || now - transfer.StartedAt > timeoutSeconds || now - transfer.LastPacketAt > InactivityTimeoutSeconds))
                SuspendActive(now);
            ExpireSuspended(now);
        }

        private static bool Matches(Transfer transfer, CdMediaMessage message, ulong entity, byte[]? hash) =>
            message.RadioEntity == entity && message.RadioEntity == transfer.Entity && message.TransferId == transfer.Id &&
            hash != null && CdPlaybackState.HashEquals(hash, transfer.Hash);

        private void SuspendActive(float now)
        {
            var old = transfer;
            if (old == null) return;
            transfer = null;
            if (old.NextChunk == 0 || float.IsNaN(now) || float.IsInfinity(now)) { old.Hasher.Dispose(); return; }
            DiscardSuspended();
            suspended = old; suspendedAt = now;
        }
        private void ExpireSuspended(float now)
        {
            if (suspended != null && (float.IsNaN(now) || float.IsInfinity(now) || now < suspendedAt || now - suspendedAt > timeoutSeconds))
                DiscardSuspended();
        }
        private void DiscardSuspended()
        {
            var old = suspended;
            suspended = null;
            old?.Hasher.Dispose();
        }
        private void ClearTransfer()
        {
            var old = transfer;
            transfer = null;
            old?.Hasher.Dispose();
        }
        private static bool FinishHash(Transfer transfer)
        {
            transfer.Hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return FixedHashEquals(transfer.Hasher.Hash!, transfer.Hash);
        }
        private static bool FixedHashEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
