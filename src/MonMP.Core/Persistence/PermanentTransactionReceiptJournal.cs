using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MonMP.Core.Protocol;

namespace MonMP.Core.Persistence
{
    public static partial class TransactionReceiptJournal
    {
        public const int ChunkReceipts = 1024;
        private const ushort PermanentVersion = 2;
        private const int PermanentRootBytes = 180;

        private sealed class ReceiptRoot
        {
            public readonly byte[] Bytes;
            public readonly Guid Snapshot;
            public readonly long Count;
            public readonly List<TransactionReceipt>? Legacy;
            public ReceiptRoot(byte[] bytes, Guid snapshot, long count, List<TransactionReceipt>? legacy = null)
            { Bytes = bytes; Snapshot = snapshot; Count = count; Legacy = legacy; }
        }
        private sealed class ReceiptIndex
        {
            public readonly string Path;
            public ReceiptRoot Root;
            public readonly List<TransactionReceipt> Receipts;
            public readonly HashSet<(Guid Profile, Guid Operation)> Keys;
            public ReceiptIndex(string path, ReceiptRoot root, List<TransactionReceipt> receipts)
            {
                Path = path; Root = root; Receipts = receipts;
                Keys = new HashSet<(Guid, Guid)>();
                foreach (var receipt in receipts)
                    if (!Keys.Add((receipt.Profile, receipt.Operation)))
                        throw new InvalidDataException("Duplicate transaction receipt across journal pages.");
            }
        }
        // One active journal cache, not an ever-growing cache of saved generations.
        // The entire small root identifies its contents; timestamps/length are not
        // sufficient to detect another writer's atomic replacement.
        private static readonly object receiptIndexLock = new object();
        private static ReceiptIndex? receiptIndex;

        private static FileStream AcquireReceiptWriter(string directory, TransactionReceiptBinding binding)
        {
            string path = LockPath(directory, binding);
            if (Exists(path)) SidecarStore.RegularFile(path);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        }
        private static byte[] ReadReceiptFile(string path)
        {
            SidecarStore.RegularFile(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > MaxBytes) throw new InvalidDataException("Transaction receipt file exceeds its page size limit.");
            return Exact(file, checked((int)file.Length));
        }
        private static ReceiptRoot? ReadReceiptRoot(string directory, TransactionReceiptBinding binding)
        {
            string path = Path.Combine(directory, Name(binding));
            if (!File.Exists(path)) return null;
            byte[] bytes = ReadReceiptFile(path);
            if (bytes.Length < 6) throw new InvalidDataException("Invalid transaction receipt root.");
            if (BitConverter.ToUInt16(bytes, 4) == Version)
            {
                var legacy = Decode(bytes, out var actual);
                if (!binding.Equals(actual)) throw new InvalidDataException("Transaction receipt journal binding mismatch.");
                return new ReceiptRoot(bytes, Guid.Empty, legacy.Count, legacy.ToList());
            }
            if (bytes.Length != PermanentRootBytes) throw new InvalidDataException("Invalid permanent transaction receipt root size.");
            using var sha = SHA256.Create();
            if (!sha.ComputeHash(bytes, 0, bytes.Length - 32).SequenceEqual(bytes.Skip(bytes.Length - 32)))
                throw new InvalidDataException("Transaction receipt root checksum mismatch.");
            using var stream = new MemoryStream(bytes, 0, bytes.Length - 32, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != PermanentVersion)
                throw new InvalidDataException("Unknown transaction receipt journal format.");
            var found = new TransactionReceiptBinding(new Guid(Exact(reader, 16)), new Guid(Exact(reader, 16)),
                reader.ReadInt32(), Wire.ReadText(reader, 64));
            if (!binding.Equals(found)) throw new InvalidDataException("Transaction receipt journal binding mismatch.");
            var snapshot = new Guid(Exact(reader, 16)); var commit = new Guid(Exact(reader, 16)); long count = reader.ReadInt64();
            if (snapshot == Guid.Empty || commit == Guid.Empty || count < 0 || stream.Position != stream.Length)
                throw new InvalidDataException("Invalid permanent transaction receipt root.");
            // The on-disk count is 64 bit. This process must be able to index every
            // retained key; resource exhaustion refuses operation, never retires history.
            if (count > int.MaxValue) throw new InvalidDataException("Receipt history exceeds this process's index capacity.");
            return new ReceiptRoot(bytes, snapshot, count);
        }
        private static ReceiptRoot NewReceiptRoot(TransactionReceiptBinding binding, Guid snapshot, int count)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(PermanentVersion);
            writer.Write(binding.WorldId.ToByteArray()); writer.Write(binding.Generation.ToByteArray());
            writer.Write(binding.SourceTimestamp); Wire.WriteText(writer, binding.GameBuild, 64);
            writer.Write(snapshot.ToByteArray()); writer.Write(Guid.NewGuid().ToByteArray()); writer.Write((long)count); writer.Flush();
            using var sha = SHA256.Create(); byte[] body = stream.ToArray(); stream.Write(sha.ComputeHash(body), 0, 32);
            byte[] bytes = stream.ToArray();
            if (bytes.Length != PermanentRootBytes) throw new InvalidOperationException("Permanent receipt root layout changed.");
            return new ReceiptRoot(bytes, snapshot, count);
        }
        private static string ReceiptPagePath(string directory, TransactionReceiptBinding binding, Guid snapshot, long page) =>
            Path.Combine(directory, "monmp-transactions-" + binding.Generation.ToString("N") + "." + snapshot.ToString("N") +
                "." + page.ToString("D8", CultureInfo.InvariantCulture) + ".receipts");
        private static List<TransactionReceipt> ReadReceiptPages(string directory, TransactionReceiptBinding binding, ReceiptRoot root)
        {
            if (root.Legacy != null) return root.Legacy;
            var receipts = new List<TransactionReceipt>();
            for (long page = 0, start = 0; start < root.Count; page++, start += ChunkReceipts)
            {
                var values = Decode(ReadReceiptFile(ReceiptPagePath(directory, binding, root.Snapshot, page)), out var actual);
                int required = (int)Math.Min(ChunkReceipts, root.Count - start);
                if (!binding.Equals(actual) || values.Count < required || values.Count > ChunkReceipts)
                    throw new InvalidDataException("Transaction receipt page does not match its committed root.");
                // Atomic tail replacement only extends the committed prefix. An
                // older root remains readable while a newer tail is being prepared.
                // A flushed but unpublished suffix is not a durable claim.
                for (int i = 0; i < required; i++) receipts.Add(values[i]);
            }
            return receipts;
        }
        private static ReceiptIndex GetReceiptIndex(string directory, TransactionReceiptBinding binding, ReceiptRoot? root)
        {
            string path = Path.Combine(directory, Name(binding));
            if (root == null) return new ReceiptIndex(path, NewReceiptRoot(binding, Guid.NewGuid(), 0), new List<TransactionReceipt>());
            if (receiptIndex != null && receiptIndex.Path == path && receiptIndex.Root.Bytes.SequenceEqual(root.Bytes)) return receiptIndex;
            return new ReceiptIndex(path, root, ReadReceiptPages(directory, binding, root));
        }
        private static IReadOnlyList<TransactionReceipt>? LoadPermanent(string directory, TransactionReceiptBinding expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            directory = SidecarStore.DirectoryPath(directory);
            var root = ReadReceiptRoot(directory, expected);
            if (root == null) return null;
            var loaded = new ReceiptIndex(Path.Combine(directory, Name(expected)), root, ReadReceiptPages(directory, expected, root));
            return Array.AsReadOnly(loaded.Receipts.ToArray());
        }
        private static void CheckReceipt(TransactionReceipt receipt)
        {
            if (receipt.Profile == Guid.Empty || receipt.Operation == Guid.Empty || receipt.Kind != TransactionReceipt.StorePurchase || receipt.Entity == 0)
                throw new InvalidDataException("Invalid transaction receipt.");
        }
        private static byte[] EncodeReceiptPage(TransactionReceiptBinding binding, TransactionReceipt[] values)
        {
            // Preserve insertion order: sorting a rewritten active tail could move
            // a new receipt into an older root's committed prefix.
            Validate(values);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(Version); writer.Write(binding.WorldId.ToByteArray()); writer.Write(binding.Generation.ToByteArray());
            writer.Write(binding.SourceTimestamp); Wire.WriteText(writer, binding.GameBuild, 64); writer.Write((ushort)values.Length);
            foreach (var receipt in values)
            { writer.Write(receipt.Profile.ToByteArray()); writer.Write(receipt.Operation.ToByteArray()); writer.Write(receipt.Kind); writer.Write(receipt.Entity); }
            writer.Flush(); using var sha = SHA256.Create(); byte[] body = stream.ToArray(); stream.Write(sha.ComputeHash(body), 0, 32);
            return stream.ToArray();
        }
        private static void WriteReceiptPage(string path, byte[] bytes)
        {
            if (Exists(path)) SidecarStore.RegularFile(path);
            string temporary = path + ".tmp";
            if (Exists(temporary)) { SidecarStore.RegularFile(temporary); File.Delete(temporary); }
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static ReceiptRoot WriteReceiptSnapshot(string directory, TransactionReceiptBinding binding, List<TransactionReceipt> receipts)
        {
            Guid snapshot = Guid.NewGuid();
            for (int start = 0, page = 0; start < receipts.Count; page++)
            {
                int length = Math.Min(ChunkReceipts, receipts.Count - start);
                WriteReceiptPage(ReceiptPagePath(directory, binding, snapshot, page), EncodeReceiptPage(binding, receipts.GetRange(start, length).ToArray()));
                start += length;
            }
            return NewReceiptRoot(binding, snapshot, receipts.Count);
        }
        private static bool TryClaimPermanent(string directory, TransactionReceiptBinding binding, TransactionReceipt receipt,
            Action<TransactionReceiptCommitStage>? checkpoint, bool requireExisting = false)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            CheckReceipt(receipt); directory = SidecarStore.DirectoryPath(directory);
            using var writer = AcquireReceiptWriter(directory, binding);
            lock (receiptIndexLock)
            {
                var found = ReadReceiptRoot(directory, binding);
                if (requireExisting && found == null) throw new InvalidDataException("Permanent receipt history is missing for this save.");
                var index = GetReceiptIndex(directory, binding, found);
                if (index.Keys.Contains((receipt.Profile, receipt.Operation))) { receiptIndex = index; return false; }
                ReceiptRoot next;
                if (found == null || found.Legacy != null)
                {
                    var migrated = new List<TransactionReceipt>(index.Receipts) { receipt };
                    next = WriteReceiptSnapshot(directory, binding, migrated);
                }
                else
                {
                    int tailStart = index.Receipts.Count / ChunkReceipts * ChunkReceipts;
                    var tail = index.Receipts.GetRange(tailStart, index.Receipts.Count - tailStart);
                    tail.Add(receipt);
                    WriteReceiptPage(ReceiptPagePath(directory, binding, found.Snapshot, tailStart / ChunkReceipts), EncodeReceiptPage(binding, tail.ToArray()));
                    next = NewReceiptRoot(binding, found.Snapshot, checked(index.Receipts.Count + 1));
                }
                // This is the sole claim commit point. All pages have been flushed.
                // If Published throws, the changed root forces a reload on retry.
                Publish(directory, binding, next.Bytes, checkpoint);
                index.Receipts.Add(receipt); index.Keys.Add((receipt.Profile, receipt.Operation)); index.Root = next; receiptIndex = index;
                return true;
            }
        }
        private static void CommitPermanent(string directory, TransactionReceiptBinding binding, IEnumerable<TransactionReceipt> receipts,
            Action<TransactionReceiptCommitStage>? checkpoint)
        {
            if (binding == null || receipts == null) throw new ArgumentNullException(binding == null ? nameof(binding) : nameof(receipts));
            directory = SidecarStore.DirectoryPath(directory);
            using var writer = AcquireReceiptWriter(directory, binding);
            lock (receiptIndexLock)
            {
                var existing = ReadReceiptRoot(directory, binding);
                var merged = new Dictionary<(Guid Profile, Guid Operation), TransactionReceipt>();
                if (existing != null)
                    foreach (var prior in ReadReceiptPages(directory, binding, existing))
                        merged.Add((prior.Profile, prior.Operation), prior);
                var inputKeys = new HashSet<(Guid, Guid)>();
                foreach (var receipt in receipts)
                {
                    CheckReceipt(receipt); var key = (receipt.Profile, receipt.Operation);
                    if (!inputKeys.Add(key)) throw new InvalidDataException("Duplicate transaction receipt in commit.");
                    if (merged.TryGetValue(key, out var prior) && !prior.Equals(receipt))
                        throw new InvalidDataException("A retained transaction receipt cannot be replaced.");
                    merged[key] = receipt;
                }
                var values = merged.Values.OrderBy(receipt => receipt.Profile).ThenBy(receipt => receipt.Operation).ToList();
                var next = WriteReceiptSnapshot(directory, binding, values);
                Publish(directory, binding, next.Bytes, checkpoint);
                receiptIndex = new ReceiptIndex(Path.Combine(directory, Name(binding)), next, values);
            }
        }
        internal static void InitializeLegacyHistory(string directory, TransactionReceiptBinding binding)
        {
            directory = SidecarStore.DirectoryPath(directory);
            using var writer = AcquireReceiptWriter(directory, binding);
            if (ReadReceiptRoot(directory, binding) != null) return;
            // Establish a valid empty history before the sidecar compatibility gate.
            // A crash after that gate must not leave an ambiguous missing history.
            Publish(directory, binding, Encode(binding, Array.Empty<TransactionReceipt>()), null);
        }
    }
}
