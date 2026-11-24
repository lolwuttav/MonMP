using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MonMP.Core.Persistence
{
    public sealed class NativeSaveFingerprint
    {
        private static readonly string[] names = { "marguerite.txt", "blopblop.txt", "braaaap.txt", "lyne.txt" };
        public const long MaxNativeFileBytes = 256L * 1024 * 1024;
        private readonly long[] lengths;
        private readonly byte[][] hashes;
        private NativeSaveFingerprint(long[] lengths, byte[][] hashes) { this.lengths = lengths; this.hashes = hashes; }
        public static NativeSaveFingerprint Capture(string directory)
        {
            directory = SidecarStore.DirectoryPath(directory); var lengths = new long[4]; var hashes = new byte[4][];
            for (int i = 0; i < names.Length; i++)
            {
                string path = Path.Combine(directory, names[i]); SidecarStore.RegularFile(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                lengths[i] = stream.Length; if (lengths[i] < 1 || lengths[i] > MaxNativeFileBytes) throw new InvalidDataException("Native save file size is invalid.");
                using var sha = SHA256.Create(); hashes[i] = sha.ComputeHash(stream);
            }
            return new NativeSaveFingerprint(lengths, hashes);
        }
        public bool Matches(NativeSaveFingerprint other) => other != null && lengths.SequenceEqual(other.lengths) && hashes.Zip(other.hashes, (a, b) => a.SequenceEqual(b)).All(equal => equal);
        internal void Write(BinaryWriter writer) { for (int i = 0; i < 4; i++) { writer.Write(lengths[i]); writer.Write(hashes[i]); } }
        internal static NativeSaveFingerprint Read(BinaryReader reader)
        {
            var lengths = new long[4]; var hashes = new byte[4][];
            for (int i = 0; i < 4; i++)
            {
                lengths[i] = reader.ReadInt64(); hashes[i] = reader.ReadBytes(32);
                if (lengths[i] < 1 || lengths[i] > MaxNativeFileBytes || hashes[i].Length != 32) throw new InvalidDataException("Invalid native save binding.");
            }
            return new NativeSaveFingerprint(lengths, hashes);
        }
    }
    public sealed class SidecarSnapshot
    {
        public SidecarDocument Document { get; }
        public NativeSaveFingerprint NativeSave { get; }
        public ushort SchemaVersion { get; }
        public SidecarSnapshot(SidecarDocument document, NativeSaveFingerprint nativeSave)
            : this(document, nativeSave, SidecarDocument.SchemaVersion) { }
        internal SidecarSnapshot(SidecarDocument document, NativeSaveFingerprint nativeSave, ushort schemaVersion)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document)); NativeSave = nativeSave ?? throw new ArgumentNullException(nameof(nativeSave));
            SidecarSchemaMigrations.RequireKnown(schemaVersion); SchemaVersion = schemaVersion;
        }
    }
    public enum SidecarCommitStage { TemporaryFileFlushed, BeforePublish, Published }
    public static class SidecarStore
    {
        public const string FileName = "monmp.bin";
        public const int MaxBytes = 8 * 1024 * 1024;
        private const uint Magic = 0x534D4F4D;
        internal static string DirectoryPath(string directory)
        {
            string full = Path.GetFullPath(directory);
            if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Save directory must be an existing regular directory.");
            return full;
        }
        internal static void RegularFile(string path)
        { if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Save path must be a regular file."); }
        public static byte[] Encode(SidecarSnapshot snapshot)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(snapshot.SchemaVersion); snapshot.NativeSave.Write(writer);
            SidecarSchemaMigrations.Write(snapshot.SchemaVersion, snapshot.Document, writer); writer.Flush();
            if (stream.Length > MaxBytes - 32) throw new InvalidDataException("Sidecar exceeds its size limit.");
            using var sha = SHA256.Create(); byte[] body = stream.ToArray(); stream.Write(sha.ComputeHash(body), 0, 32); return stream.ToArray();
        }
        public static SidecarSnapshot Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 198 || payload.Length > MaxBytes) throw new InvalidDataException("Invalid sidecar size.");
            using var sha = SHA256.Create(); var hash = sha.ComputeHash(payload, 0, payload.Length - 32);
            if (!hash.SequenceEqual(payload.Skip(payload.Length - 32))) throw new InvalidDataException("Sidecar checksum mismatch.");
            using var stream = new MemoryStream(payload, 0, payload.Length - 32, false); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unknown sidecar file.");
            ushort schema = reader.ReadUInt16();
            // Check the version before interpreting the binding or document. A future
            // reader belongs in SidecarSchemaMigrations with an explicit conversion.
            SidecarSchemaMigrations.RequireKnown(schema);
            var binding = NativeSaveFingerprint.Read(reader); var document = SidecarSchemaMigrations.ReadAndUpgrade(schema, reader);
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing sidecar data."); return new SidecarSnapshot(document, binding, schema);
        }
        public static SidecarSnapshot? Load(string directory, string expectedGameBuild) => Load(directory, expectedGameBuild, FileName);
        internal static SidecarSnapshot? Load(string directory, string expectedGameBuild, string fileName, NativeSaveFingerprint? verifiedNative = null)
        {
            directory = DirectoryPath(directory); string path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) return null;
            var result = Decode(ReadFile(path));
            if (!string.Equals(expectedGameBuild, result.Document.GameBuild, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Sidecar targets a different game build.");
            if (!result.NativeSave.Matches(verifiedNative ?? NativeSaveFingerprint.Capture(directory))) throw new InvalidDataException("Sidecar does not match the four native save files.");
            return result;
        }
        private static byte[] ReadFile(string path)
        {
            RegularFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxBytes) throw new InvalidDataException("Sidecar exceeds its size limit.");
            var bytes = new byte[(int)stream.Length]; int offset = 0;
            while (offset < bytes.Length) { int read = stream.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            return bytes;
        }
        public static void Commit(string directory, SidecarSnapshot snapshot, Guid? expectedGeneration = null, Action<SidecarCommitStage>? checkpoint = null) =>
            Commit(directory, snapshot, FileName, expectedGeneration, checkpoint);
        internal static void Commit(string directory, SidecarSnapshot snapshot, string fileName, Guid? expectedGeneration = null, Action<SidecarCommitStage>? checkpoint = null)
        {
            directory = DirectoryPath(directory); string target = Path.Combine(directory, fileName);
            string temporary = Path.Combine(directory, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string lockPath = Path.Combine(directory, "monmp.lock");
            if (File.Exists(lockPath)) RegularFile(lockPath);
            using var writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            if (File.Exists(target))
            {
                var prior = Decode(ReadFile(target)).Document;
                if (!expectedGeneration.HasValue || prior.Generation != expectedGeneration.Value || prior.WorldId != snapshot.Document.WorldId || prior.GameBuild != snapshot.Document.GameBuild)
                    throw new InvalidDataException("Existing sidecar generation or world identity changed.");
            }
            else if (expectedGeneration.HasValue) throw new InvalidDataException("Expected sidecar generation is missing.");
            if (!snapshot.NativeSave.Matches(NativeSaveFingerprint.Capture(directory))) throw new InvalidDataException("Native save changed before sidecar publication.");
            byte[] bytes = Encode(snapshot);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                checkpoint?.Invoke(SidecarCommitStage.TemporaryFileFlushed);
                if (!snapshot.NativeSave.Matches(NativeSaveFingerprint.Capture(directory))) throw new InvalidDataException("Native save changed during sidecar publication.");
                checkpoint?.Invoke(SidecarCommitStage.BeforePublish);
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                checkpoint?.Invoke(SidecarCommitStage.Published);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
