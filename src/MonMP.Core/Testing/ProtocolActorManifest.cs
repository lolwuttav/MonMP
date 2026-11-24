using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;

namespace MonMP.Core.Testing
{
    // Read-only description of the host catalog needed by a protocol-only actor.
    // It intentionally contains no player credentials, password, or world payloads.
    public sealed class ProtocolActorManifest
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public ushort Protocol { get; set; }
        public string GameHash { get; set; } = "";
        public string ModVersion { get; set; } = "";
        public string Scene { get; set; } = "";
        public string Catalog { get; set; } = "";
        // This checksum covers only the descriptor list carried below. The host's
        // Catalog may be produced by a different layer and is verified separately
        // against the authoritative Welcome message during deferred join.
        public string RequiredRecordsDigest { get; set; } = "";
        public ProtocolActorRecord[] RequiredRecords { get; set; } = Array.Empty<ProtocolActorRecord>();
        public ushort[] DynamicAdapters { get; set; } = Array.Empty<ushort>();
        public ushort[] LeaseAdapters { get; set; } = Array.Empty<ushort>();
        public ulong[] RequiredDynamicEntities { get; set; } = Array.Empty<ulong>();
        public ProtocolActorSpawn Spawn { get; set; } = new ProtocolActorSpawn();

        public static ProtocolActorManifest Capture(NetworkSession session, WorldChannel world)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (world == null) throw new ArgumentNullException(nameof(world));
#if MONMP_CORE_TESTING
            if (!session.IsHost || !ReferenceEquals(world.SessionForActorManifest, session))
                throw new InvalidOperationException("An actor manifest can only be captured from the attached host world.");

            var requiredRecords = world.CatalogDescriptorsForActorManifest
                .Select(record => new ProtocolActorRecord { Id = record.Key, Adapter = record.Value }).ToArray();
            var manifest = new ProtocolActorManifest
            {
                Version = CurrentVersion,
                Protocol = Wire.Version,
                GameHash = session.GameHashForActorManifest,
                ModVersion = session.ModVersionForActorManifest,
                Scene = session.SceneForActorManifest,
                Catalog = session.CatalogForActorManifest,
                RequiredRecords = requiredRecords,
                RequiredRecordsDigest = DescriptorDigest(requiredRecords),
                DynamicAdapters = world.DynamicAdaptersForActorManifest,
                LeaseAdapters = world.LeaseAdaptersForActorManifest,
                RequiredDynamicEntities = world.RequiredDynamicEntitiesForActorManifest,
                Spawn = ProtocolActorSpawn.FromPose(session.SpawnForActorManifest)
            };
#else
            // Standalone tests/actors can read a production Core without adding
            // test exports or friend-assembly access to the shipping library.
            if (!session.IsHost || !ReferenceEquals(ReadField<object>(world, "session"), session))
                throw new InvalidOperationException("An actor manifest can only be captured from the attached host world.");
            var options = ReadField<SessionOptions>(session, "options");
            object receiver = ReadField<object>(world, "receiver");
            var descriptors = (IReadOnlyList<KeyValuePair<ulong, ushort>>)receiver.GetType()
                .GetProperty("CatalogDescriptors", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(receiver)!;
            var requiredRecords = descriptors.Select(record => new ProtocolActorRecord { Id = record.Key, Adapter = record.Value }).ToArray();
            var manifest = new ProtocolActorManifest
            {
                Version = CurrentVersion, Protocol = Wire.Version,
                GameHash = options.GameHash, ModVersion = options.ModVersion,
                Scene = options.Scene, Catalog = options.WorldCatalog,
                RequiredRecords = requiredRecords, RequiredRecordsDigest = DescriptorDigest(requiredRecords),
                DynamicAdapters = ReadField<HashSet<ushort>>(world, "dynamicAdapters").OrderBy(value => value).ToArray(),
                LeaseAdapters = ReadField<HashSet<ushort>>(world, "leaseAdapters").OrderBy(value => value).ToArray(),
                RequiredDynamicEntities = ReadField<HashSet<ulong>>(world, "requiredDynamicEntities").OrderBy(value => value).ToArray(),
                Spawn = ProtocolActorSpawn.FromPose(options.Spawn)
            };
#endif
            manifest.Validate(requireCurrentBuild: true);
            return manifest;
        }
#if !MONMP_CORE_TESTING
        private static T ReadField<T>(object owner, string name) => (T)(owner.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException("Protocol actor fixture field is missing: " + name));
#endif

        public void Validate() => Validate(requireCurrentBuild: true);

        internal void ValidateForClient() => Validate(requireCurrentBuild: true);

        private void Validate(bool requireCurrentBuild)
        {
            if (Version != CurrentVersion) throw new InvalidDataException("Unsupported protocol actor manifest version.");
            if (Protocol != Wire.Version) throw new InvalidDataException("Protocol actor manifest does not match this wire protocol.");
            if (requireCurrentBuild && (GameHash != SessionOptions.SupportedGameHash || ModVersion != SessionOptions.CurrentModVersion))
                throw new InvalidDataException("Protocol actor manifest does not match this game and MonMP build.");
            if (RequiredRecords == null || DynamicAdapters == null || LeaseAdapters == null || RequiredDynamicEntities == null || Spawn == null)
                throw new InvalidDataException("Protocol actor manifest is incomplete.");
            if (string.IsNullOrEmpty(GameHash) || string.IsNullOrEmpty(ModVersion) || string.IsNullOrWhiteSpace(Scene) ||
                string.IsNullOrEmpty(Catalog) || Catalog.Length != 64 || Catalog.Any(c => !Uri.IsHexDigit(c)) ||
                string.IsNullOrEmpty(RequiredRecordsDigest) || RequiredRecordsDigest.Length != 64 || RequiredRecordsDigest.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Protocol actor manifest has invalid host metadata.");

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                Wire.WriteText(writer, GameHash, 64);
                Wire.WriteText(writer, ModVersion, 32);
                Wire.WriteText(writer, Scene, 200);
                Wire.WriteText(writer, Catalog, 64);
            }

            if (RequiredRecords.Length > SnapshotReceiver.MaxEntities || RequiredRecords.Any(record => record == null || record.Id == 0 || record.Adapter == 0) ||
                RequiredRecords.Select(record => record.Id).Distinct().Count() != RequiredRecords.Length)
                throw new InvalidDataException("Protocol actor manifest has invalid catalog descriptors.");
            if (DynamicAdapters.Any(adapter => adapter == 0) || DynamicAdapters.Distinct().Count() != DynamicAdapters.Length ||
                LeaseAdapters.Any(adapter => adapter == 0) || LeaseAdapters.Distinct().Count() != LeaseAdapters.Length ||
                RequiredDynamicEntities.Any(entity => entity == 0) || RequiredDynamicEntities.Distinct().Count() != RequiredDynamicEntities.Length)
                throw new InvalidDataException("Protocol actor manifest has invalid adapter or entity sets.");

            var dynamic = new HashSet<ushort>(DynamicAdapters);
            var descriptors = RequiredRecords.ToDictionary(record => record.Id);
            var requiredDynamic = new HashSet<ulong>(RequiredDynamicEntities);
            foreach (ulong id in RequiredDynamicEntities)
            {
                if (id <= WorldRecord.DynamicIdStart || !descriptors.TryGetValue(id, out var descriptor) || !dynamic.Contains(descriptor.Adapter))
                    throw new InvalidDataException("Protocol actor manifest is missing a required dynamic catalog descriptor.");
            }
            foreach (var descriptor in RequiredRecords)
                if (descriptor.Id > WorldRecord.DynamicIdStart && dynamic.Contains(descriptor.Adapter) && !requiredDynamic.Contains(descriptor.Id))
                    throw new InvalidDataException("Protocol actor manifest marks an unrequired dynamic record as a required catalog descriptor.");

            if (!string.Equals(DescriptorDigest(RequiredRecords), RequiredRecordsDigest, StringComparison.Ordinal))
                throw new InvalidDataException("Protocol actor manifest catalog descriptors do not match their digest.");

            Spawn.ToPose().Validate();
        }

        private static string DescriptorDigest(IEnumerable<ProtocolActorRecord> descriptors) => WorldRecord.Digest(
            descriptors.Select(record => new WorldRecord(record.Id, 1, record.Adapter, Array.Empty<byte>())), catalogOnly: true);
    }

    public sealed class ProtocolActorRecord
    {
        public ulong Id { get; set; }
        public ushort Adapter { get; set; }
    }

    public sealed class ProtocolActorSpawn
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public byte State { get; set; }

        internal static ProtocolActorSpawn FromPose(PlayerPose pose) => new ProtocolActorSpawn
        {
            X = pose.X, Y = pose.Y, Z = pose.Z, Yaw = pose.Yaw, Pitch = pose.Pitch, State = pose.State
        };

        public PlayerPose ToPose() => new PlayerPose(X, Y, Z, Yaw, Pitch, State);
    }
}
