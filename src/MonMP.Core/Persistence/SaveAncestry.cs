using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MonMP.Core.Persistence
{
    // Explicit native-generation ancestry. A newer timestamp alone never identifies
    // a world: each link is checksummed and bound to all four selected native files.
    public sealed class SaveAncestryInfo
    {
        public int Timestamp { get; }
        public int SourceTimestamp { get; }
        public Guid WorldId { get; }
        public TransactionReceiptBinding? Receipts { get; }
        public string GameBuild { get; }
        public string UnavailableReason { get; }
        public SidecarSnapshot? MetadataSnapshot { get; }
        public int MetadataTimestamp { get; }
        internal NativeSaveFingerprint NativeSave { get; }
        internal SaveAncestryInfo(int timestamp, int source, Guid world, TransactionReceiptBinding? receipts,
            string build, string reason, NativeSaveFingerprint native, SidecarSnapshot? metadata = null, int metadataTimestamp = 0)
        { Timestamp = timestamp; SourceTimestamp = source; WorldId = world; Receipts = receipts;
            GameBuild = build; UnavailableReason = reason; NativeSave = native; MetadataSnapshot = metadata; MetadataTimestamp = metadataTimestamp; }
    }

    public static class SaveAncestry
    {
        public const string FileName = "monmp-ancestry.bin";
        private const uint Magic = 0x414D4F4D;
        private const ushort Version = 1;
        private const int MaxBytes = SidecarStore.MaxBytes + 4096;

        public static SaveAncestryInfo? Capture(string directory, int timestamp, string gameBuild)
        {
            if (timestamp <= 0 || !string.Equals(Path.GetFileName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                timestamp.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidDataException("Save ancestry source must be its selected numeric generation.");
            var degraded = SaveSlot.LoadNativeOnly(directory, gameBuild);
            if (degraded != null)
            {
                SidecarSnapshot? profiles;
                try { profiles = SaveSlot.LoadRecoveryProfiles(directory, degraded); }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                { profiles = null; }
                return new SaveAncestryInfo(timestamp, degraded.SourceTimestamp, degraded.WorldId, null,
                    gameBuild, degraded.RecoveryReason, degraded.NativeSave, profiles, profiles == null ? 0 : timestamp);
            }
            var selected = SidecarStore.Load(directory, gameBuild);
            if (selected != null)
            {
                var document = selected.Document;
                return new SaveAncestryInfo(timestamp, 0, document.WorldId,
                    new TransactionReceiptBinding(document.WorldId, document.Generation, timestamp, gameBuild),
                    gameBuild, "", selected.NativeSave, selected, timestamp);
            }
            return Load(directory, gameBuild);
        }

        public static SaveAncestryInfo? Load(string directory, string gameBuild)
        {
            SidecarStore.DirectoryPath(directory);
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;
            SidecarStore.RegularFile(path);
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < 240 || file.Length > MaxBytes) throw new InvalidDataException("Invalid save ancestry size.");
                bytes = new byte[(int)file.Length]; int offset = 0;
                while (offset < bytes.Length) { int read = file.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            }
            using var sha = SHA256.Create();
            if (!sha.ComputeHash(bytes, 0, bytes.Length - 32).SequenceEqual(bytes.Skip(bytes.Length - 32)))
                throw new InvalidDataException("Save ancestry checksum mismatch.");
            using var memory = new MemoryStream(bytes, 0, bytes.Length - 32, false);
            using var reader = new BinaryReader(memory);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version) throw new InvalidDataException("Unsupported save ancestry.");
            int timestamp = reader.ReadInt32(), source = reader.ReadInt32();
            Guid world = new Guid(reader.ReadBytes(16));
            string build = reader.ReadString(), reason = reader.ReadString();
            TransactionReceiptBinding? receipts = reader.ReadBoolean()
                ? new TransactionReceiptBinding(world, new Guid(reader.ReadBytes(16)), reader.ReadInt32(), build) : null;
            var native = NativeSaveFingerprint.Read(reader);
            int metadataTimestamp = reader.ReadInt32(), metadataLength = reader.ReadInt32();
            if (metadataLength < 0 || metadataLength > SidecarStore.MaxBytes || metadataLength > memory.Length - memory.Position)
                throw new InvalidDataException("Invalid ancestry metadata size.");
            var metadata = metadataLength == 0 ? null : SidecarStore.Decode(reader.ReadBytes(metadataLength));
            string name = Path.GetFileName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (memory.Position != memory.Length || timestamp <= 0 || source < -1 || source >= timestamp || world == Guid.Empty ||
                build.Length != 64 || !string.Equals(build, gameBuild, StringComparison.OrdinalIgnoreCase) || reason.Length > 512 ||
                !string.Equals(name, timestamp.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                (receipts != null && receipts.SourceTimestamp > timestamp) ||
                (metadata == null ? metadataTimestamp != 0 : metadataTimestamp <= 0 || metadataTimestamp > timestamp ||
                    metadata.Document.WorldId != world || !string.Equals(metadata.Document.GameBuild, gameBuild, StringComparison.OrdinalIgnoreCase) ||
                    (receipts != null && metadata.Document.Generation != receipts.Generation) ||
                    (metadataTimestamp == timestamp && !metadata.NativeSave.Matches(native))) ||
                !native.Matches(NativeSaveFingerprint.Capture(directory)))
                throw new InvalidDataException("Save ancestry does not match the selected native generation.");
            return new SaveAncestryInfo(timestamp, source, world, receipts, build, reason, native, metadata, metadataTimestamp);
        }

        internal static void CommitPublication(string directory, int timestamp, int source, SidecarSnapshot snapshot)
        {
            var document = snapshot.Document;
            Commit(directory, new SaveAncestryInfo(timestamp, source, document.WorldId,
                new TransactionReceiptBinding(document.WorldId, document.Generation, timestamp, document.GameBuild),
                document.GameBuild, "", snapshot.NativeSave, snapshot, timestamp));
        }

        public static void CommitDescendant(string directory, int timestamp, SaveAncestryInfo source, SidecarProfile? localObservation = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (timestamp <= source.Timestamp) throw new InvalidDataException("Offline ancestry must descend from the loaded generation.");
            SidecarSnapshot? metadata = source.MetadataSnapshot;
            if (metadata != null && localObservation != null && PersonalProfileHistory.ValidPersonalState(localObservation))
            {
                var document = metadata.Document;
                var profiles = document.Profiles.ToDictionary(p => p.Key, p => p.Value);
                var fields = profiles.TryGetValue(localObservation.Id, out var old)
                    ? old.State.ToDictionary(p => p.Key, p => p.Value) : localObservation.State.ToDictionary(p => p.Key, p => p.Value);
                fields[PlayerProfileState.SectionKey] = localObservation.State[PlayerProfileState.SectionKey];
                long seen = PersonalProfileHistory.AfterObservation(localObservation.LastSeenUtc, old?.LastSeenUtc ?? 0);
                profiles[localObservation.Id] = new SidecarProfile(localObservation.Id, localObservation.DisplayName,
                    old?.ColorRgb ?? localObservation.ColorRgb, seen, fields);
                metadata = new SidecarSnapshot(new SidecarDocument(document.WorldId, document.Generation, document.SavedUtc,
                    document.GameBuild, document.ModVersion, document.Configuration, profiles.Values), metadata.NativeSave);
            }
            Commit(directory, new SaveAncestryInfo(timestamp, source.Timestamp, source.WorldId, source.Receipts,
                source.GameBuild, source.UnavailableReason, NativeSaveFingerprint.Capture(directory), metadata, source.MetadataTimestamp));
        }

        private static void Commit(string directory, SaveAncestryInfo info)
        {
            SidecarStore.DirectoryPath(directory);
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
            writer.Write(Magic); writer.Write(Version); writer.Write(info.Timestamp); writer.Write(info.SourceTimestamp);
            writer.Write(info.WorldId.ToByteArray()); writer.Write(info.GameBuild); writer.Write(info.UnavailableReason);
            writer.Write(info.Receipts != null);
            if (info.Receipts != null) { writer.Write(info.Receipts.Generation.ToByteArray()); writer.Write(info.Receipts.SourceTimestamp); }
            info.NativeSave.Write(writer);
            byte[] metadata = info.MetadataSnapshot == null ? Array.Empty<byte>() : SidecarStore.Encode(info.MetadataSnapshot);
            writer.Write(info.MetadataTimestamp); writer.Write(metadata.Length); writer.Write(metadata); writer.Flush();
            using var sha = SHA256.Create(); writer.Write(sha.ComputeHash(memory.ToArray())); writer.Flush();
            byte[] bytes = memory.ToArray();
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Save ancestry exceeds its size limit.");
            string target = Path.Combine(directory, FileName);
            if (File.Exists(target))
            {
                SidecarStore.RegularFile(target);
                if (new FileInfo(target).Length != bytes.Length || !File.ReadAllBytes(target).SequenceEqual(bytes))
                    throw new InvalidDataException("Existing save ancestry conflicts with the publication.");
                return;
            }
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            File.Move(temporary, target);
        }

        // Configuration and reconnect positions follow explicit source links only.
        // Optional personal history can subsequently merge same-world observations.
        public static PublishedSaveSlot? FindSource(string parent, SaveAncestryInfo selected, string gameBuild) =>
            FindSource(parent, selected, gameBuild, includeEmbeddedMetadata: true);
        public static PublishedSaveSlot? FindSource(string parent, SaveAncestryInfo selected, string gameBuild, bool includeEmbeddedMetadata)
        {
            // Carry the validated source metadata forward so ordinary native retention
            // can prune old generations without erasing reconnect/configuration history.
            if (includeEmbeddedMetadata && selected.MetadataSnapshot != null)
                return new PublishedSaveSlot(selected.MetadataTimestamp,
                    Path.Combine(parent, selected.MetadataTimestamp.ToString(CultureInfo.InvariantCulture)), selected.MetadataSnapshot);
            int source = selected.SourceTimestamp, current = selected.Timestamp;
            for (int depth = 0; depth < 32 && source > 0 && source < current; depth++)
            {
                string directory = Path.Combine(parent, source.ToString(CultureInfo.InvariantCulture));
                var publication = SidecarStore.Load(directory, gameBuild);
                if (publication != null)
                {
                    if (publication.Document.WorldId != selected.WorldId) throw new InvalidDataException("Save ancestry points to another world.");
                    return new PublishedSaveSlot(source, directory, publication);
                }
                var degraded = SaveSlot.LoadNativeOnly(directory, gameBuild);
                if (degraded != null)
                {
                    if (degraded.WorldId != selected.WorldId) throw new InvalidDataException("Save ancestry points to another world.");
                    current = source; source = degraded.SourceTimestamp; continue;
                }
                var ancestry = Load(directory, gameBuild);
                if (ancestry == null) return null;
                if (ancestry.WorldId != selected.WorldId) throw new InvalidDataException("Save ancestry points to another world.");
                current = source; source = ancestry.SourceTimestamp;
            }
            return null;
        }

        public static bool HasMultiplayerHistory(string parent)
        {
            if (Directory.EnumerateFiles(parent, "monmp-transactions-*").Any()) return true;
            foreach (string directory in Directory.EnumerateDirectories(parent))
                if (File.Exists(Path.Combine(directory, SidecarStore.FileName)) || File.Exists(Path.Combine(directory, FileName)) ||
                    File.Exists(Path.Combine(directory, SaveSlot.NativeOnlyMarkerName))) return true;
            return false;
        }
    }
}
