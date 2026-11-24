using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;

namespace MonMP.Core.Persistence
{
    public sealed class SidecarSection
    {
        private readonly byte[] bytes;
        public ushort Version { get; }
        public byte[] Payload => (byte[])bytes.Clone();
        public SidecarSection(ushort version, byte[] payload)
        {
            if (version == 0 || payload == null || payload.Length > 32768) throw new InvalidDataException("Invalid sidecar section.");
            Version = version; bytes = (byte[])payload.Clone();
        }
        internal void Write(BinaryWriter writer) { writer.Write(Version); writer.Write(bytes.Length); writer.Write(bytes); }
        internal static SidecarSection Read(BinaryReader reader)
        {
            ushort version = reader.ReadUInt16(); int size = reader.ReadInt32();
            if (size < 0 || size > 32768 || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid sidecar section length.");
            return new SidecarSection(version, reader.ReadBytes(size));
        }
    }
    internal static class SidecarFields
    {
        internal static IReadOnlyDictionary<string, SidecarSection> Copy(IEnumerable<KeyValuePair<string, SidecarSection>> sections)
        {
            var copy = new Dictionary<string, SidecarSection>(StringComparer.Ordinal);
            foreach (var p in sections)
            {
                if (p.Key.Length < 1 || p.Key.Length > 64 || p.Key.Any(c => !((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_')) || p.Value == null || copy.ContainsKey(p.Key))
                    throw new InvalidDataException("Invalid or duplicate sidecar section key.");
                copy.Add(p.Key, p.Value); if (copy.Count > 32) throw new InvalidDataException("Too many sidecar sections.");
            }
            return new ReadOnlyDictionary<string, SidecarSection>(copy);
        }
        internal static void Write(BinaryWriter writer, IReadOnlyDictionary<string, SidecarSection> sections)
        {
            writer.Write((byte)sections.Count);
            foreach (var p in sections.OrderBy(p => p.Key, StringComparer.Ordinal)) { Wire.WriteText(writer, p.Key, 64); p.Value.Write(writer); if (writer.BaseStream.Position > SidecarStore.MaxBytes - 32) throw new InvalidDataException("Sidecar exceeds its size limit."); }
        }
        internal static IReadOnlyDictionary<string, SidecarSection> Read(BinaryReader reader)
        {
            int count = reader.ReadByte(); if (count > 32) throw new InvalidDataException("Too many sidecar sections.");
            var fields = new List<KeyValuePair<string, SidecarSection>>();
            for (int i = 0; i < count; i++) fields.Add(new KeyValuePair<string, SidecarSection>(Wire.ReadText(reader, 64), SidecarSection.Read(reader)));
            return Copy(fields);
        }
        internal static void Timestamp(long value)
        { if (value < 0 || value > 253402300799L) throw new InvalidDataException("Invalid sidecar timestamp."); }
    }
    public sealed class SidecarProfile
    {
        public Guid Id { get; }
        public string DisplayName { get; }
        public uint ColorRgb { get; }
        public long LastSeenUtc { get; }
        public IReadOnlyDictionary<string, SidecarSection> State { get; }
        public SidecarProfile(Guid id, string displayName, uint colorRgb, long lastSeenUtc, IEnumerable<KeyValuePair<string, SidecarSection>> state)
        {
            if (id == Guid.Empty || displayName == null || System.Text.Encoding.UTF8.GetByteCount(displayName) > Wire.MaxNameBytes || colorRgb > 0xFFFFFF) throw new InvalidDataException("Invalid persistent profile.");
            SidecarFields.Timestamp(lastSeenUtc); Id = id; DisplayName = displayName; ColorRgb = colorRgb; LastSeenUtc = lastSeenUtc; State = SidecarFields.Copy(state);
        }
        internal void Write(BinaryWriter writer)
        { writer.Write(Id.ToByteArray()); Wire.WriteText(writer, DisplayName, Wire.MaxNameBytes); writer.Write(ColorRgb); writer.Write(LastSeenUtc); SidecarFields.Write(writer, State); }
        internal static SidecarProfile Read(BinaryReader reader) => new SidecarProfile(new Guid(reader.ReadBytes(16)), Wire.ReadText(reader, Wire.MaxNameBytes), reader.ReadUInt32(), reader.ReadInt64(), SidecarFields.Read(reader));
    }
    public sealed class SidecarDocument
    {
        // Schema two retains the document layout and requires the permanent
        // receipt-aware reader. Older builds must not save an upgraded world.
        public const ushort SchemaVersion = 2;
        public Guid WorldId { get; }
        public Guid Generation { get; }
        public long SavedUtc { get; }
        public string GameBuild { get; }
        public string ModVersion { get; }
        public IReadOnlyDictionary<string, SidecarSection> Configuration { get; }
        public IReadOnlyDictionary<Guid, SidecarProfile> Profiles { get; }
        public SidecarDocument(Guid worldId, Guid generation, long savedUtc, string gameBuild, string modVersion,
            IEnumerable<KeyValuePair<string, SidecarSection>> configuration, IEnumerable<SidecarProfile> profiles)
        {
            if (worldId == Guid.Empty || generation == Guid.Empty || gameBuild == null || gameBuild.Length != 64 || gameBuild.Any(c => !Uri.IsHexDigit(c)) ||
                string.IsNullOrEmpty(modVersion) || System.Text.Encoding.UTF8.GetByteCount(modVersion) > 32) throw new InvalidDataException("Invalid sidecar identity.");
            SidecarFields.Timestamp(savedUtc); WorldId = worldId; Generation = generation; SavedUtc = savedUtc; GameBuild = gameBuild.ToUpperInvariant(); ModVersion = modVersion;
            Configuration = SidecarFields.Copy(configuration);
            var copy = new Dictionary<Guid, SidecarProfile>();
            foreach (var profile in profiles)
            {
                if (profile == null || copy.ContainsKey(profile.Id)) throw new InvalidDataException("Duplicate or missing persistent profile.");
                copy.Add(profile.Id, profile); if (copy.Count > 512) throw new InvalidDataException("Too many persistent profiles.");
            }
            Profiles = new ReadOnlyDictionary<Guid, SidecarProfile>(copy);
        }
        internal void WriteVersion1(BinaryWriter writer)
        {
            writer.Write(WorldId.ToByteArray()); writer.Write(Generation.ToByteArray()); writer.Write(SavedUtc);
            Wire.WriteText(writer, GameBuild, 64); Wire.WriteText(writer, ModVersion, 32); SidecarFields.Write(writer, Configuration);
            writer.Write((ushort)Profiles.Count); foreach (var p in Profiles.OrderBy(p => p.Key)) p.Value.Write(writer);
        }
        internal static SidecarDocument ReadVersion1(BinaryReader reader)
        {
            var world = new Guid(reader.ReadBytes(16)); var generation = new Guid(reader.ReadBytes(16)); long time = reader.ReadInt64();
            string build = Wire.ReadText(reader, 64), mod = Wire.ReadText(reader, 32); var fields = SidecarFields.Read(reader);
            int count = reader.ReadUInt16(); if (count > 512) throw new InvalidDataException("Too many persistent profiles.");
            var profiles = new List<SidecarProfile>(); for (int i = 0; i < count; i++) profiles.Add(SidecarProfile.Read(reader));
            return new SidecarDocument(world, generation, time, build, mod, fields, profiles);
        }
    }
}
