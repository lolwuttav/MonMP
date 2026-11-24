using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MonMP.Core.Protocol;

namespace MonMP.Core.Persistence
{
    // A durable claim means that native effects may have started. After a crash, the
    // operation must be reconciled; replaying it as a new purchase could charge twice.
    public readonly struct TransactionReceipt : IEquatable<TransactionReceipt>
    {
        public const byte StorePurchase = 1;
        public Guid Profile { get; }
        public Guid Operation { get; }
        public byte Kind { get; }
        public ulong Entity { get; }
        public TransactionReceipt(Guid profile, Guid operation, byte kind, ulong entity)
        {
            if (profile == Guid.Empty || operation == Guid.Empty || kind != StorePurchase || entity == 0)
                throw new InvalidDataException("Invalid transaction receipt.");
            Profile = profile; Operation = operation; Kind = kind; Entity = entity;
        }
        public bool Equals(TransactionReceipt other) => Profile == other.Profile && Operation == other.Operation &&
            Kind == other.Kind && Entity == other.Entity;
        public override bool Equals(object? obj) => obj is TransactionReceipt other && Equals(other);
        public override int GetHashCode() => Profile.GetHashCode() ^ Operation.GetHashCode() ^ Kind ^ Entity.GetHashCode();
    }

    public sealed class TransactionReceiptBinding : IEquatable<TransactionReceiptBinding>
    {
        public Guid WorldId { get; }
        public Guid Generation { get; }
        public int SourceTimestamp { get; }
        public string GameBuild { get; }
        public TransactionReceiptBinding(Guid worldId, Guid generation, int sourceTimestamp, string gameBuild)
        {
            if (worldId == Guid.Empty || generation == Guid.Empty || sourceTimestamp <= 0 || sourceTimestamp == int.MaxValue ||
                gameBuild == null || gameBuild.Length != 64 || gameBuild.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid transaction receipt binding.");
            WorldId = worldId; Generation = generation; SourceTimestamp = sourceTimestamp; GameBuild = gameBuild.ToUpperInvariant();
        }
        public bool Equals(TransactionReceiptBinding? other) => other != null && WorldId == other.WorldId &&
            Generation == other.Generation && SourceTimestamp == other.SourceTimestamp &&
            string.Equals(GameBuild, other.GameBuild, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object? obj) => Equals(obj as TransactionReceiptBinding);
        public override int GetHashCode() => WorldId.GetHashCode() ^ Generation.GetHashCode() ^ SourceTimestamp ^
            StringComparer.OrdinalIgnoreCase.GetHashCode(GameBuild);
    }

    public enum TransactionReceiptCommitStage { TemporaryFileFlushed, BeforePublish, Published }

    // A proven refusal before any new claim is published, distinct from an I/O
    // failure whose durable outcome may be unknown. Existing receipts are retained.
    public sealed class TransactionReceiptCapacityException : InvalidOperationException
    {
        public TransactionReceiptCapacityException() : base("Transaction receipt history is full; no purchase was started.") { }
    }

    public static partial class TransactionReceiptJournal
    {
        // The shipped version-one codec remains bounded and byte-compatible.
        // Permanent journals use bounded pages instead of a lifetime receipt cap.
        public const int MaxReceipts = 16384;
        public const int MaxBytes = 1024 * 1024;
        private const uint Magic = 0x4A52544D; // MTRJ
        private const ushort Version = 1;
        private static string Name(TransactionReceiptBinding binding) => "monmp-transactions-" + binding.Generation.ToString("N") + ".bin";

        public static byte[] Encode(TransactionReceiptBinding binding, IEnumerable<TransactionReceipt> receipts)
        {
            if (binding == null || receipts == null) throw new ArgumentNullException(binding == null ? nameof(binding) : nameof(receipts));
            var values = receipts.OrderBy(r => r.Profile).ThenBy(r => r.Operation).ToArray();
            Validate(values);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(Version); writer.Write(binding.WorldId.ToByteArray());
            writer.Write(binding.Generation.ToByteArray()); writer.Write(binding.SourceTimestamp);
            Wire.WriteText(writer, binding.GameBuild, 64); writer.Write((ushort)values.Length);
            foreach (var receipt in values)
            { writer.Write(receipt.Profile.ToByteArray()); writer.Write(receipt.Operation.ToByteArray()); writer.Write(receipt.Kind); writer.Write(receipt.Entity); }
            writer.Flush();
            if (stream.Length > MaxBytes - 32) throw new InvalidDataException("Transaction receipt journal exceeds its size limit.");
            byte[] body = stream.ToArray(); using var sha = SHA256.Create(); stream.Write(sha.ComputeHash(body), 0, 32);
            return stream.ToArray();
        }

        public static IReadOnlyList<TransactionReceipt> Decode(byte[] payload, out TransactionReceiptBinding binding)
        {
            if (payload == null || payload.Length < 142 || payload.Length > MaxBytes)
                throw new InvalidDataException("Invalid transaction receipt journal size.");
            using var sha = SHA256.Create(); byte[] hash = sha.ComputeHash(payload, 0, payload.Length - 32);
            if (!hash.SequenceEqual(payload.Skip(payload.Length - 32))) throw new InvalidDataException("Transaction receipt journal checksum mismatch.");
            using var stream = new MemoryStream(payload, 0, payload.Length - 32, false); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version) throw new InvalidDataException("Unknown transaction receipt journal format.");
            Guid world = new Guid(Exact(reader, 16)), generation = new Guid(Exact(reader, 16));
            int timestamp = reader.ReadInt32(); string build = Wire.ReadText(reader, 64); int count = reader.ReadUInt16();
            if (count > MaxReceipts) throw new InvalidDataException("Too many transaction receipts.");
            binding = new TransactionReceiptBinding(world, generation, timestamp, build);
            var result = new TransactionReceipt[count];
            for (int i = 0; i < count; i++)
                result[i] = new TransactionReceipt(new Guid(Exact(reader, 16)), new Guid(Exact(reader, 16)), reader.ReadByte(), reader.ReadUInt64());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing transaction receipt journal data.");
            Validate(result);
            return Array.AsReadOnly(result);
        }

        public static IReadOnlyList<TransactionReceipt>? Load(string directory, TransactionReceiptBinding expected)
        {
            return LoadPermanent(directory, expected);
        }

        public static bool TryClaim(string directory, TransactionReceiptBinding binding, TransactionReceipt receipt,
            Action<TransactionReceiptCommitStage>? checkpoint = null)
        {
            return TryClaimPermanent(directory, binding, receipt, checkpoint);
        }

        // Loaded worlds already established their journal before the sidecar
        // compatibility gate. A missing root now is a failure, not empty history.
        public static bool TryClaimExisting(string directory, TransactionReceiptBinding binding, TransactionReceipt receipt,
            Action<TransactionReceiptCommitStage>? checkpoint = null)
        {
            return TryClaimPermanent(directory, binding, receipt, checkpoint, requireExisting: true);
        }

        public static void Commit(string directory, TransactionReceiptBinding binding, IEnumerable<TransactionReceipt> receipts,
            Action<TransactionReceiptCommitStage>? checkpoint = null)
        {
            CommitPermanent(directory, binding, receipts, checkpoint);
        }

        private static void Publish(string directory, TransactionReceiptBinding binding, byte[] bytes,
            Action<TransactionReceiptCommitStage>? checkpoint)
        {
            string target = Path.Combine(directory, Name(binding));
            string temporary = Path.Combine(directory, ".monmp-transactions-" + binding.Generation.ToString("N") + ".tmp");
            if (Exists(target)) SidecarStore.RegularFile(target);
            if (Exists(temporary)) { SidecarStore.RegularFile(temporary); File.Delete(temporary); }
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                checkpoint?.Invoke(TransactionReceiptCommitStage.TemporaryFileFlushed);
                checkpoint?.Invoke(TransactionReceiptCommitStage.BeforePublish);
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                checkpoint?.Invoke(TransactionReceiptCommitStage.Published);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void Validate(TransactionReceipt[] values)
        {
            if (values.Length > MaxReceipts) throw new InvalidDataException("Too many transaction receipts.");
            var keys = new HashSet<(Guid, Guid)>();
            foreach (var receipt in values)
                if (receipt.Profile == Guid.Empty || receipt.Operation == Guid.Empty || receipt.Kind != TransactionReceipt.StorePurchase ||
                    receipt.Entity == 0 || !keys.Add((receipt.Profile, receipt.Operation)))
                    throw new InvalidDataException("Invalid or duplicate transaction receipt.");
        }
        private static byte[] Exact(BinaryReader reader, int length)
        { byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return bytes; }
        private static byte[] Exact(Stream stream, int length)
        { byte[] bytes = new byte[length]; int offset = 0; while (offset < length) { int n = stream.Read(bytes, offset, length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; } return bytes; }
        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        private static string LockPath(string directory, TransactionReceiptBinding binding) =>
            Path.Combine(directory, ".monmp-transactions-" + binding.Generation.ToString("N") + ".lock");
    }
}
