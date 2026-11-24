using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MonMP.Core.Protocol;

namespace MonMP.Core.Persistence
{
    public sealed class PersonalProfileJournalBinding : IEquatable<PersonalProfileJournalBinding>
    {
        public Guid WorldId { get; }
        public Guid Generation { get; }
        public int SourceTimestamp { get; }
        public string GameBuild { get; }

        public PersonalProfileJournalBinding(Guid worldId, Guid generation, int sourceTimestamp, string gameBuild)
        {
            if (worldId == Guid.Empty || generation == Guid.Empty || sourceTimestamp <= 0 || sourceTimestamp == int.MaxValue ||
                gameBuild == null || gameBuild.Length != 64 || gameBuild.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid personal profile journal binding.");
            WorldId = worldId; Generation = generation; SourceTimestamp = sourceTimestamp; GameBuild = gameBuild.ToUpperInvariant();
        }

        public bool Equals(PersonalProfileJournalBinding? other) => other != null && WorldId == other.WorldId && Generation == other.Generation &&
            SourceTimestamp == other.SourceTimestamp && string.Equals(GameBuild, other.GameBuild, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object? obj) => Equals(obj as PersonalProfileJournalBinding);
        public override int GetHashCode() => WorldId.GetHashCode() ^ Generation.GetHashCode() ^ SourceTimestamp.GetHashCode() ^ StringComparer.OrdinalIgnoreCase.GetHashCode(GameBuild);
    }

    public enum PersonalProfileJournalCommitStage { TemporaryFileFlushed, BeforePublish, Published }

    // Crash-durable latest-safe personal state. Exact binding remains available for
    // storage checks; restoration may carry personal state across the same world's saves.
    public static class PersonalProfileJournal
    {
        public const string FileName = "monmp-profiles.bin";
        public const int MaxProfiles = 512;
        public const int MaxBytes = 128 * 1024;
        private const uint Magic = 0x4A50504D; // MPPJ
        private const ushort Version = 2;
        private const ushort LegacyVersion = 1;
        private const string LockName = "monmp-profile-journal.lock";
        private const string TemporaryName = ".monmp-profile-journal.tmp";

        public static byte[] Encode(PersonalProfileJournalBinding binding, IEnumerable<SidecarProfile> profiles)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            var values = ValidateProfiles(profiles);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Magic); writer.Write(Version); writer.Write(binding.WorldId.ToByteArray()); writer.Write(binding.Generation.ToByteArray());
            writer.Write(binding.SourceTimestamp); Wire.WriteText(writer, binding.GameBuild, 64); writer.Write((ushort)values.Length);
            foreach (var profile in values)
            {
                writer.Write(profile.Id.ToByteArray()); Wire.WriteText(writer, profile.DisplayName, Wire.MaxNameBytes);
                writer.Write(profile.ColorRgb); writer.Write(profile.LastSeenUtc);
                writer.Write(PlayerProfileState.FromSection(profile.State[PlayerProfileState.SectionKey]).Encode());
            }
            writer.Flush();
            if (stream.Length > MaxBytes - 32) throw new InvalidDataException("Personal profile journal exceeds its size limit.");
            byte[] body = stream.ToArray(); using var sha = SHA256.Create(); stream.Write(sha.ComputeHash(body), 0, 32);
            return stream.ToArray();
        }

        public static IReadOnlyList<SidecarProfile> Decode(byte[] payload, out PersonalProfileJournalBinding binding)
        {
            if (payload == null || payload.Length < 142 || payload.Length > MaxBytes) throw new InvalidDataException("Invalid personal profile journal size.");
            using var sha = SHA256.Create(); byte[] hash = sha.ComputeHash(payload, 0, payload.Length - 32);
            if (!hash.SequenceEqual(payload.Skip(payload.Length - 32))) throw new InvalidDataException("Personal profile journal checksum mismatch.");
            using var stream = new MemoryStream(payload, 0, payload.Length - 32, false); using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unknown personal profile journal file.");
            ushort version = reader.ReadUInt16();
            if (version != Version && version != LegacyVersion) throw new InvalidDataException("Unsupported personal profile journal version.");
            int stateBytes = version == Version ? PlayerProfileState.EncodedBytes : PlayerProfileState.LegacyEncodedBytes;
            var world = new Guid(ReadExact(reader, 16)); var generation = new Guid(ReadExact(reader, 16)); int timestamp = reader.ReadInt32();
            string build = Wire.ReadText(reader, 64); int count = reader.ReadUInt16();
            if (count > MaxProfiles) throw new InvalidDataException("Too many personal profile journal entries.");
            binding = new PersonalProfileJournalBinding(world, generation, timestamp, build);
            var profiles = new List<SidecarProfile>(count); var ids = new HashSet<Guid>();
            for (int i = 0; i < count; i++)
            {
                var id = new Guid(ReadExact(reader, 16)); string name = Wire.ReadText(reader, Wire.MaxNameBytes);
                uint color = reader.ReadUInt32(); long seen = reader.ReadInt64(); var state = PlayerProfileState.Decode(ReadExact(reader, stateBytes));
                if (id == Guid.Empty || !ids.Add(id) || seen <= 0) throw new InvalidDataException("Invalid or duplicate journal profile.");
                profiles.Add(new SidecarProfile(id, name, color, seen,
                    new[] { new KeyValuePair<string, SidecarSection>(PlayerProfileState.SectionKey, state.ToSection()) }));
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing personal profile journal data.");
            return Array.AsReadOnly(profiles.ToArray());
        }

        public static IReadOnlyList<SidecarProfile>? Load(string directory, PersonalProfileJournalBinding expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var profiles = Read(directory, out var actual);
            return expected.Equals(actual) ? profiles : null;
        }

        public static IReadOnlyList<SidecarProfile>? LoadForWorld(string directory, Guid expectedWorld, string expectedGameBuild)
            => LoadForWorld(directory, expectedWorld, expectedGameBuild, out _);

        public static IReadOnlyList<SidecarProfile>? LoadForWorld(string directory, Guid expectedWorld, string expectedGameBuild,
            out PersonalProfileJournalBinding? binding)
        {
            if (expectedWorld == Guid.Empty || expectedGameBuild == null || expectedGameBuild.Length != 64 ||
                expectedGameBuild.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid personal profile journal world binding.");
            var profiles = Read(directory, out var actual);
            binding = actual != null && actual.WorldId == expectedWorld &&
                string.Equals(actual.GameBuild, expectedGameBuild, StringComparison.OrdinalIgnoreCase) ? actual : null;
            return binding != null ? profiles : null;
        }

        private static IReadOnlyList<SidecarProfile>? Read(string directory, out PersonalProfileJournalBinding? actual)
        {
            actual = null;
            directory = SidecarStore.DirectoryPath(directory); string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;
            SidecarStore.RegularFile(path);
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length > MaxBytes) throw new InvalidDataException("Personal profile journal exceeds its size limit.");
                bytes = ReadExact(stream, (int)stream.Length);
            }
            var profiles = Decode(bytes, out var binding);
            actual = binding;
            return profiles;
        }

        public static IReadOnlyList<SidecarProfile>? LoadForSelectedSave(string persistentRoot, string saveName, int selectedTimestamp,
            string expectedGameBuild, PersonalProfileJournalBinding expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (selectedTimestamp <= 0 ||
                !string.Equals(expectedGameBuild, expected.GameBuild, StringComparison.OrdinalIgnoreCase)) return null;
            string parent = SaveSlot.GetSaveParentPath(persistentRoot, saveName);
            return LoadForWorld(parent, expected.WorldId, expectedGameBuild);
        }

        public static void Commit(string directory, PersonalProfileJournalBinding binding, IEnumerable<SidecarProfile> profiles,
            Action<PersonalProfileJournalCommitStage>? checkpoint = null)
        {
            directory = SidecarStore.DirectoryPath(directory);
            byte[] bytes = Encode(binding, profiles);
            string target = Path.Combine(directory, FileName), temporary = Path.Combine(directory, TemporaryName), lockPath = Path.Combine(directory, LockName);
            if (Exists(lockPath)) SidecarStore.RegularFile(lockPath);
            using var writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            if (Exists(target)) SidecarStore.RegularFile(target);
            if (Exists(temporary)) { SidecarStore.RegularFile(temporary); File.Delete(temporary); }
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                checkpoint?.Invoke(PersonalProfileJournalCommitStage.TemporaryFileFlushed);
                checkpoint?.Invoke(PersonalProfileJournalCommitStage.BeforePublish);
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                checkpoint?.Invoke(PersonalProfileJournalCommitStage.Published);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static SidecarProfile[] ValidateProfiles(IEnumerable<SidecarProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            var values = profiles.OrderBy(p => p?.Id).ToArray();
            if (values.Length > MaxProfiles) throw new InvalidDataException("Too many personal profile journal entries.");
            var ids = new HashSet<Guid>();
            foreach (var profile in values)
            {
                if (profile == null || !ids.Add(profile.Id) || profile.LastSeenUtc <= 0 || profile.State.Count != 1 ||
                    !profile.State.TryGetValue(PlayerProfileState.SectionKey, out var section) || (section.Version != PlayerProfileState.Version && section.Version != PlayerProfileState.LegacyVersion))
                    throw new InvalidDataException("Invalid personal profile journal entry.");
                Wire.ValidateName(profile.DisplayName);
                _ = PlayerProfileState.FromSection(section);
            }
            return values;
        }

        private static byte[] ReadExact(BinaryReader reader, int size)
        {
            byte[] bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return bytes;
        }
        private static byte[] ReadExact(Stream stream, int size)
        {
            byte[] bytes = new byte[size]; int offset = 0;
            while (offset < size) { int count = stream.Read(bytes, offset, size - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return bytes;
        }
        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    }
}
