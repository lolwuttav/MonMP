using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MonMP.Core.Persistence
{
    public sealed class RecoveredSaveSlot
    {
        public int Timestamp { get; }
        // Zero means the writer did not record a source generation.
        public int SourceTimestamp { get; }
        public Guid WorldId { get; }
        public string DirectoryPath { get; }
        public bool NativeOnly { get; }
        public string RecoveryReason { get; }
        internal RecoveredSaveSlot(int timestamp, int sourceTimestamp, Guid worldId, string directory,
            bool nativeOnly = false, string recoveryReason = "")
        { Timestamp = timestamp; SourceTimestamp = sourceTimestamp; WorldId = worldId; DirectoryPath = directory; NativeOnly = nativeOnly; RecoveryReason = recoveryReason; }
    }

    // Deliberate degraded checkpoints preserve the complete native world. Their marker
    // prevents a reader from treating missing multiplayer metadata as a fresh history.
    public sealed class NativeOnlySaveInfo
    {
        public int Timestamp { get; }
        public int SourceTimestamp { get; }
        public Guid WorldId { get; }
        public string GameBuild { get; }
        public string RecoveryReason { get; }
        internal Guid StageId { get; }
        internal NativeSaveFingerprint NativeSave { get; }
        internal NativeOnlySaveInfo(int timestamp, int sourceTimestamp, Guid worldId, Guid stageId,
            string gameBuild, string recoveryReason, NativeSaveFingerprint nativeSave)
        { Timestamp = timestamp; SourceTimestamp = sourceTimestamp; WorldId = worldId; StageId = stageId;
            GameBuild = gameBuild; RecoveryReason = recoveryReason; NativeSave = nativeSave; }
    }

    // A previously published numeric save with a validated sidecar/native fingerprint.
    // Used to find same-world durable metadata without making that save the selected world state.
    public sealed class PublishedSaveSlot
    {
        public int Timestamp { get; }
        public string DirectoryPath { get; }
        public SidecarSnapshot Snapshot { get; }
        public Guid WorldId => Snapshot.Document.WorldId;
        internal PublishedSaveSlot(int timestamp, string directory, SidecarSnapshot snapshot)
        { Timestamp = timestamp; DirectoryPath = directory; Snapshot = snapshot; }
    }

    // Stage native files and their sidecar outside the vanilla numeric-folder namespace.
    // Publication and recovery are same-parent renames, never replacement of an existing save.
    public sealed partial class SaveSlot : IDisposable
    {
        private const uint ManifestMagic = 0x504D4E4D; // MNMP
        private const ushort ManifestVersion = 1;
        private const string ManifestName = "monmp-stage.bin";
        private const string NativeOutputMarkerName = "monmp-native-output.bin";
        public const string StagingFolderName = ".monmp-staging";
        private const int ManifestBytes = 4 + 2 + 2 + 4 + 4 + 16 + 16 + 32;
        public const string NativeOnlyMarkerName = "monmp-native-only.bin";
        internal const string RecoveryProfilesName = "monmp-recovery-profiles.bin";
        private const uint NativeOnlyMagic = 0x4E4D4F4D;
        private const ushort NativeOnlyVersion = 1;
        private const int MaxNativeOnlyMarkerBytes = 4096;
        private readonly FileStream writerLock;
        private bool disposed;
        public string StagingDirectory { get; }
        public string DestinationDirectory { get; }
        public int Timestamp { get; }
        public bool Published { get; private set; }
        private SaveSlot(FileStream writerLock, string stage, string destination, int timestamp)
        { this.writerLock = writerLock; StagingDirectory = stage; DestinationDirectory = destination; Timestamp = timestamp; }

        public static SaveSlot Reserve(string persistentRoot, string saveName, int unixTime, bool createSaveDirectory = false,
            int sourceTimestamp = 0, Guid expectedWorld = default)
        {
            if (unixTime <= 0 || sourceTimestamp < -1) throw new InvalidDataException("Invalid native save timestamp.");
            string parent = SaveParent(persistentRoot, saveName, createSaveDirectory);
            var file = AcquireWriterLock(parent);
            try
            {
                int timestamp = unixTime;
                foreach (string entry in Directory.EnumerateFileSystemEntries(parent))
                    if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out int existing) && existing >= timestamp)
                    {
                        if (existing == int.MaxValue) throw new InvalidDataException("Native save timestamp range is exhausted.");
                        timestamp = existing + 1;
                    }
                // An interrupted reservation owns its timestamp until it is recovered or
                // inspected. Never reuse that number for a later staged save.
                foreach (string prior in EnumerateStages(parent))
                    if (TryReadReservedTimestamp(prior, out int reserved) && reserved >= timestamp)
                    {
                        if (reserved == int.MaxValue) throw new InvalidDataException("Native save timestamp range is exhausted.");
                        timestamp = reserved + 1;
                    }
                Guid stageId = Guid.NewGuid();
                string stagingRoot = Path.Combine(parent, StagingFolderName);
                if (!Directory.Exists(stagingRoot)) Directory.CreateDirectory(stagingRoot);
                RegularDirectoryTree(stagingRoot);
                string stage = Path.Combine(stagingRoot, ".monmp-" + stageId.ToString("N") + ".tmp");
                string destination = Path.Combine(parent, timestamp.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(stage);
                WriteManifest(stage, timestamp, sourceTimestamp, expectedWorld, stageId);
                return new SaveSlot(file, stage, destination, timestamp);
            }
            catch { file.Dispose(); throw; }
        }

        // Call before vanilla loads the selected save. Only complete, matching stages are
        // published. Unrecognized, incomplete and colliding stages remain untouched.
        public static IReadOnlyList<RecoveredSaveSlot> Recover(string persistentRoot, string saveName, string expectedGameBuild)
        {
            if (string.IsNullOrWhiteSpace(expectedGameBuild)) throw new ArgumentException("Game build is required.", nameof(expectedGameBuild));
            string parent = SaveParent(persistentRoot, saveName, false);
            using var writerLock = AcquireWriterLock(parent);
            var recovered = new List<RecoveredSaveSlot>();
            foreach (string stage in EnumerateStages(parent).ToArray())
            {
                if (!TryStageId(Path.GetFileName(stage), out Guid stageId)) continue;
                try
                {
                    RegularDirectoryTree(stage);
                    if (!TryReadManifest(stage, stageId, out int timestamp, out int source, out Guid world)) continue;
                    string destination = Path.Combine(parent, timestamp.ToString(CultureInfo.InvariantCulture));
                    QuarantineNativeOutput(stage, destination, stageId, timestamp, source, world);
                    if (PathExists(destination)) continue;
                    if (File.Exists(Path.Combine(stage, NativeOnlyMarkerName)))
                    {
                        var nativeOnly = LoadNativeOnly(stage, expectedGameBuild);
                        if (nativeOnly == null) continue;
                        try { Directory.Move(stage, destination); }
                        catch (IOException) when (PathExists(destination)) { continue; }
                        recovered.Add(new RecoveredSaveSlot(timestamp, source, nativeOnly.WorldId, destination,
                            nativeOnly: true, recoveryReason: nativeOnly.RecoveryReason));
                        continue;
                    }
                    var snapshot = SidecarStore.Load(stage, expectedGameBuild);
                    if (snapshot == null || (world != Guid.Empty && snapshot.Document.WorldId != world)) continue;
                    RequirePermanentReceiptHistory(parent, timestamp, snapshot);
                    SaveAncestry.CommitPublication(stage, timestamp, source, snapshot);
                    try { Directory.Move(stage, destination); }
                    catch (IOException) when (PathExists(destination)) { continue; }
                    recovered.Add(new RecoveredSaveSlot(timestamp, source, snapshot.Document.WorldId, destination));
                }
                catch (IOException) { /* Leave an incomplete or unreadable stage for inspection. */ }
                catch (UnauthorizedAccessException) { /* Leave a protected stage untouched. */ }
                catch (InvalidDataException) { /* Leave corrupt or incompatible stage untouched. */ }
                catch (ArgumentException) { /* Leave malformed stage untouched. */ }
                catch (FormatException) { /* Leave malformed marker strings untouched. */ }
            }
            return recovered;
        }

        // Exposes the same validated per-save parent used by staged saves for auxiliary
        // crash-safe journals. The folder must already exist and may not traverse links.
        public static string GetSaveParentPath(string persistentRoot, string saveName) => SaveParent(persistentRoot, saveName, false);

        // Native accessibility checks inspect every save's immediate child directories,
        // including nonnumeric names. Keep interrupted stages below that scan.
        public static int PreserveLegacyStages(string persistentRoot)
        {
            RegularDirectoryTree(persistentRoot);
            int moved = 0;
            foreach (string parent in Directory.EnumerateDirectories(persistentRoot))
            {
                try
                {
                    RegularDirectoryTree(parent);
                    var legacy = Directory.EnumerateDirectories(parent, ".monmp-*.tmp").ToArray();
                    if (legacy.Length == 0) continue;
                    using var writerLock = AcquireWriterLock(parent);
                    string nested = Path.Combine(parent, StagingFolderName);
                    if (!Directory.Exists(nested)) Directory.CreateDirectory(nested);
                    RegularDirectoryTree(nested);
                    foreach (string stage in legacy)
                    {
                        if (!TryStageId(Path.GetFileName(stage), out _)) continue;
                        RegularDirectoryTree(stage);
                        string destination = Path.Combine(nested, Path.GetFileName(stage));
                        if (PathExists(destination))
                        {
                            // Preserve both colliding owners as evidence. Do not leave
                            // a four-file legacy stage visible to native accessibility.
                            string collision = Path.Combine(nested, "legacy-collision-" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(collision);
                            destination = Path.Combine(collision, Path.GetFileName(stage));
                        }
                        Directory.Move(stage, destination); moved++;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
            }
            return moved;
        }

        private static IEnumerable<string> EnumerateStages(string parent)
        {
            foreach (string stage in Directory.EnumerateDirectories(parent, ".monmp-*.tmp")) yield return stage;
            string nested = Path.Combine(parent, StagingFolderName);
            if (!Directory.Exists(nested)) yield break;
            RegularDirectoryTree(nested);
            foreach (string stage in Directory.EnumerateDirectories(nested, ".monmp-*.tmp")) yield return stage;
        }

        // The fallback writes one native file at a time, moving each into staging
        // before starting the next. This marker proves ownership after a hard stop.
        public string ReserveNativeOutput()
        {
            if (disposed || Published) throw new InvalidOperationException("Save slot is no longer writable.");
            RegularDirectoryTree(StagingDirectory);
            string reservation = Path.Combine(StagingDirectory, "native-output-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(reservation);
            byte[] manifest = File.ReadAllBytes(Path.Combine(StagingDirectory, ManifestName));
            using (var marker = new FileStream(Path.Combine(reservation, NativeOutputMarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { marker.Write(manifest, 0, manifest.Length); marker.Flush(true); }
            Directory.Move(reservation, DestinationDirectory);
            return DestinationDirectory;
        }

        private static void QuarantineNativeOutput(string stage, string destination, Guid stageId, int timestamp, int source, Guid world)
        {
            if (!Directory.Exists(destination)) return;
            RegularDirectoryTree(destination);
            if (!TryReadManifest(destination, stageId, out int outputTimestamp, out int outputSource, out Guid outputWorld, NativeOutputMarkerName) ||
                outputTimestamp != timestamp || outputSource != source || outputWorld != world) return;
            Directory.Move(destination, Path.Combine(stage, "native-output-" + Guid.NewGuid().ToString("N") + ".ignored"));
        }

        // A stale profile journal must never become the recovery source once a newer
        // complete sidecar has been published, including the crash window before the
        // game's selected-timestamp preference is updated.
        public static bool HasValidSidecarAfter(string persistentRoot, string saveName, int timestamp, string expectedGameBuild)
        {
            if (timestamp <= 0) throw new InvalidDataException("Invalid selected save timestamp.");
            string parent = SaveParent(persistentRoot, saveName, false);
            foreach (string directory in Directory.EnumerateDirectories(parent))
            {
                string name = Path.GetFileName(directory);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int candidate) || candidate <= timestamp ||
                    !string.Equals(name, candidate.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) continue;
                try
                {
                    string path = Path.Combine(directory, SidecarStore.FileName);
                    if (!File.Exists(path)) continue;
                    SidecarStore.DirectoryPath(directory); SidecarStore.RegularFile(path);
                    byte[] bytes;
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        if (file.Length > SidecarStore.MaxBytes) continue;
                        bytes = new byte[(int)file.Length]; int offset = 0;
                        while (offset < bytes.Length) { int read = file.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
                    }
                    var sidecar = SidecarStore.Decode(bytes);
                    if (string.Equals(sidecar.Document.GameBuild, expectedGameBuild, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
                catch (ArgumentException) { }
            }
            return false;
        }

        // Find the newest published generation after a selected older anchor that belongs to
        // the same world. Decode first so foreign worlds can be skipped without hashing their
        // native save files; then SidecarStore.Load validates the checksum, build, directory
        // safety, and all four native save fingerprints for a same-world candidate. An
        // unreadable or invalid newer sidecar fails closed because its world cannot be trusted.
        public static PublishedSaveSlot? FindLatestValidSidecarAfter(string persistentRoot, string saveName,
            int timestamp, string expectedGameBuild, Guid expectedWorld)
        {
            if (timestamp <= 0) throw new InvalidDataException("Invalid selected save timestamp.");
            if (expectedWorld == Guid.Empty) throw new InvalidDataException("Invalid selected world identity.");
            string parent = SaveParent(persistentRoot, saveName, false);
            var candidates = new List<(string Directory, int Timestamp)>();
            foreach (string directory in Directory.EnumerateDirectories(parent))
            {
                string name = Path.GetFileName(directory);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int candidate) || candidate <= timestamp ||
                    !string.Equals(name, candidate.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) continue;
                candidates.Add((directory, candidate));
            }
            candidates.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));
            foreach (var candidate in candidates)
            {
                string directory = candidate.Directory;
                string path = Path.Combine(directory, SidecarStore.FileName);
                if (!File.Exists(path)) continue;
                SidecarStore.DirectoryPath(directory); SidecarStore.RegularFile(path);
                byte[] bytes;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (file.Length > SidecarStore.MaxBytes) throw new InvalidDataException("A later published sidecar exceeds its size limit.");
                    bytes = new byte[(int)file.Length]; int offset = 0;
                    while (offset < bytes.Length)
                    { int read = file.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
                }
                var snapshot = SidecarStore.Decode(bytes);
                if (snapshot.Document.WorldId != expectedWorld) continue;
                var validated = SidecarStore.Load(directory, expectedGameBuild);
                if (validated == null)
                    throw new InvalidDataException("A same-world sidecar disappeared during validation.");
                // The sidecar may have been atomically replaced between the initial decode
                // and validation. Never return a different world's document.
                if (validated.Document.WorldId != expectedWorld)
                    throw new InvalidDataException("A same-world sidecar changed worlds during validation.");
                return new PublishedSaveSlot(candidate.Timestamp, directory, validated);
            }
            return null;
        }

        public void Publish(string expectedGameBuild, Guid expectedWorld) => Publish(expectedGameBuild, expectedWorld, rollingRetention: false);
        public void Publish(string expectedGameBuild, Guid expectedWorld, bool rollingRetention)
        {
            if (disposed || Published) throw new InvalidOperationException("Save slot is no longer writable.");
            RegularDirectoryTree(StagingDirectory);
            var snapshot = SidecarStore.Load(StagingDirectory, expectedGameBuild);
            if (snapshot == null || expectedWorld == Guid.Empty || snapshot.Document.WorldId != expectedWorld)
                throw new InvalidDataException("A matching complete sidecar is required before save publication.");
            RequirePermanentReceiptHistory(Path.GetDirectoryName(DestinationDirectory)!, Timestamp, snapshot);
            if (!TryStageId(Path.GetFileName(StagingDirectory), out Guid stageId) ||
                !TryReadManifest(StagingDirectory, stageId, out _, out int source, out _))
                throw new InvalidDataException("Save publication does not match its reservation.");
            SaveAncestry.CommitPublication(StagingDirectory, Timestamp, source, snapshot);
            if (rollingRetention) StageRetention(source, snapshot.Document);
            // Directory.Move refuses a destination created after reservation. Never overwrite it.
            Directory.Move(StagingDirectory, DestinationDirectory); Published = true;
        }

        // A degraded checkpoint must not masquerade as a full receipt-bearing publication.
        // Independently verified profile/configuration data can still publish inside it.
        public void StageRecoveryProfiles(SidecarSnapshot snapshot)
        {
            if (disposed || Published) throw new InvalidOperationException("Save slot is no longer writable.");
            SidecarStore.Commit(StagingDirectory, snapshot, RecoveryProfilesName);
        }

        public static SidecarSnapshot? LoadRecoveryProfiles(string directory, NativeOnlySaveInfo marker)
        {
            var snapshot = SidecarStore.Load(directory, marker.GameBuild, RecoveryProfilesName, marker.NativeSave);
            if (snapshot != null && snapshot.Document.WorldId != marker.WorldId)
                throw new InvalidDataException("Recovery profiles belong to a different world.");
            return snapshot;
        }

        public void PublishNativeOnly(string expectedGameBuild, Guid expectedWorld,
            NativeSaveFingerprint expectedNative, string recoveryReason)
        {
            if (disposed || Published) throw new InvalidOperationException("Save slot is no longer writable.");
            if (string.IsNullOrWhiteSpace(expectedGameBuild) || expectedGameBuild.Length > 128 || expectedWorld == Guid.Empty)
                throw new InvalidDataException("Native checkpoint identity is invalid.");
            if (expectedNative == null) throw new ArgumentNullException(nameof(expectedNative));
            RegularDirectoryTree(StagingDirectory);
            if (!TryStageId(Path.GetFileName(StagingDirectory), out Guid stageId) ||
                !TryReadManifest(StagingDirectory, stageId, out int timestamp, out int source, out Guid world) ||
                timestamp != Timestamp || (world != Guid.Empty && world != expectedWorld))
                throw new InvalidDataException("Native checkpoint does not match its reservation.");
            if (!expectedNative.Matches(NativeSaveFingerprint.Capture(StagingDirectory)))
                throw new InvalidDataException("Native files changed before checkpoint publication.");
            // Never let a partially completed sidecar be loaded against this native-only
            // generation. Keep it as ignored evidence rather than deleting its contents.
            string sidecar = Path.Combine(StagingDirectory, SidecarStore.FileName);
            if (File.Exists(sidecar))
            {
                SidecarStore.RegularFile(sidecar);
                File.Move(sidecar, sidecar + ".recovery-" + Guid.NewGuid().ToString("N") + ".ignored");
            }
            string reason = recoveryReason ?? "Multiplayer metadata was unavailable.";
            if (reason.Length > 512) reason = reason.Substring(0, 512);
            WriteNativeOnlyMarker(StagingDirectory, new NativeOnlySaveInfo(timestamp, source, expectedWorld,
                stageId, expectedGameBuild, reason, expectedNative));
            // Also verifies the durable marker and ensures a valid manifest/native binding
            // is recoverable if the process stops immediately before Directory.Move.
            LoadNativeOnly(StagingDirectory, expectedGameBuild);
            Directory.Move(StagingDirectory, DestinationDirectory); Published = true;
        }

        public static NativeOnlySaveInfo? LoadNativeOnly(string directory, string expectedGameBuild)
        {
            if (string.IsNullOrWhiteSpace(expectedGameBuild)) throw new ArgumentException("Game build is required.", nameof(expectedGameBuild));
            RegularDirectoryTree(directory);
            string path = Path.Combine(directory, NativeOnlyMarkerName);
            if (!File.Exists(path)) return null;
            SidecarStore.RegularFile(path);
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < 4 + 2 + 4 + 4 + 16 + 16 + 2 + 160 + 32 || file.Length > MaxNativeOnlyMarkerBytes)
                    throw new InvalidDataException("Native checkpoint marker size is invalid.");
                bytes = new byte[(int)file.Length]; int offset = 0;
                while (offset < bytes.Length) { int read = file.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            }
            using var sha = SHA256.Create();
            if (!sha.ComputeHash(bytes, 0, bytes.Length - 32).SequenceEqual(bytes.Skip(bytes.Length - 32)))
                throw new InvalidDataException("Native checkpoint marker checksum mismatch.");
            using var memory = new MemoryStream(bytes, 0, bytes.Length - 32, false);
            using var reader = new BinaryReader(memory);
            if (reader.ReadUInt32() != NativeOnlyMagic || reader.ReadUInt16() != NativeOnlyVersion)
                throw new InvalidDataException("Unsupported native checkpoint marker.");
            int timestamp = reader.ReadInt32(), source = reader.ReadInt32();
            Guid world = new Guid(reader.ReadBytes(16)), stageId = new Guid(reader.ReadBytes(16));
            string build = reader.ReadString(), reason = reader.ReadString();
            var native = NativeSaveFingerprint.Read(reader);
            if (memory.Position != memory.Length || timestamp <= 0 || source < -1 || world == Guid.Empty || stageId == Guid.Empty ||
                build.Length > 128 || reason.Length > 512 || !string.Equals(build, expectedGameBuild, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native checkpoint metadata is invalid or targets another game build.");
            string name = Path.GetFileName(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!(TryStageId(name, out Guid directoryStage) && directoryStage == stageId) &&
                !string.Equals(name, timestamp.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidDataException("Native checkpoint directory does not match its reservation.");
            if (!TryReadManifest(directory, stageId, out int reserved, out int prior, out Guid reservedWorld) ||
                reserved != timestamp || prior != source || (reservedWorld != Guid.Empty && reservedWorld != world))
                throw new InvalidDataException("Native checkpoint does not match its stage manifest.");
            if (File.Exists(Path.Combine(directory, SidecarStore.FileName)))
                throw new InvalidDataException("Native-only checkpoint still contains an active multiplayer sidecar.");
            if (!native.Matches(NativeSaveFingerprint.Capture(directory)))
                throw new InvalidDataException("Native checkpoint does not match the four native save files.");
            return new NativeOnlySaveInfo(timestamp, source, world, stageId, build, reason, native);
        }

        private static void WriteNativeOnlyMarker(string directory, NativeOnlySaveInfo info)
        {
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
            writer.Write(NativeOnlyMagic); writer.Write(NativeOnlyVersion); writer.Write(info.Timestamp); writer.Write(info.SourceTimestamp);
            writer.Write(info.WorldId.ToByteArray()); writer.Write(info.StageId.ToByteArray()); writer.Write(info.GameBuild); writer.Write(info.RecoveryReason);
            info.NativeSave.Write(writer); writer.Flush();
            using var sha = SHA256.Create(); writer.Write(sha.ComputeHash(memory.ToArray())); writer.Flush();
            byte[] bytes = memory.ToArray();
            if (bytes.Length > MaxNativeOnlyMarkerBytes) throw new InvalidDataException("Native checkpoint marker exceeds its size limit.");
            using var file = new FileStream(Path.Combine(directory, NativeOnlyMarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(bytes, 0, bytes.Length); file.Flush(true);
        }

        private static void RequirePermanentReceiptHistory(string parent, int timestamp, SidecarSnapshot snapshot)
        {
            if (snapshot.SchemaVersion < 2) return; // Preserve legacy stage recovery.
            var document = snapshot.Document;
            var binding = new TransactionReceiptBinding(document.WorldId, document.Generation, timestamp, document.GameBuild);
            if (TransactionReceiptJournal.Load(parent, binding) == null)
                throw new InvalidDataException("A complete permanent receipt history is required before save publication.");
        }

        private static string SaveParent(string persistentRoot, string saveName, bool create)
        {
            if (string.IsNullOrWhiteSpace(saveName) || saveName == "." || saveName == ".." ||
                saveName.EndsWith(".", StringComparison.Ordinal) || saveName.EndsWith(" ", StringComparison.Ordinal) ||
                saveName.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || saveName.Any(char.IsControl))
                throw new InvalidDataException("Invalid native save location.");
            string stem = saveName.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                throw new InvalidDataException("Reserved native save name.");
            string root = Path.GetFullPath(persistentRoot), parent = Path.Combine(root, saveName);
            RegularDirectoryTree(root);
            if (create && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
            RegularDirectoryTree(parent);
            return parent;
        }
        private static FileStream AcquireWriterLock(string parent)
        {
            string path = Path.Combine(parent, ".monmp-save.lock");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Save writer lock is a link.");
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        }
        private static void WriteManifest(string stage, int timestamp, int source, Guid world, Guid stageId)
        {
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
            writer.Write(ManifestMagic); writer.Write(ManifestVersion); writer.Write((ushort)ManifestBytes);
            writer.Write(timestamp); writer.Write(source); writer.Write(world.ToByteArray()); writer.Write(stageId.ToByteArray()); writer.Flush();
            using var sha = SHA256.Create(); byte[] body = memory.ToArray(); writer.Write(sha.ComputeHash(body)); writer.Flush();
            if (memory.Length != ManifestBytes) throw new InvalidDataException("Stage manifest size is invalid.");
            using var file = new FileStream(Path.Combine(stage, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] bytes = memory.ToArray(); file.Write(bytes, 0, bytes.Length); file.Flush(true);
        }
        private static bool TryReadManifest(string stage, Guid stageId, out int timestamp, out int source, out Guid world, string fileName = ManifestName)
        {
            timestamp = 0; source = 0; world = Guid.Empty;
            string path = Path.Combine(stage, fileName);
            if (!File.Exists(path)) return false;
            SidecarStore.RegularFile(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != ManifestBytes) return false;
            var bytes = new byte[ManifestBytes]; int offset = 0;
            while (offset < bytes.Length) { int n = file.Read(bytes, offset, bytes.Length - offset); if (n == 0) return false; offset += n; }
            using var sha = SHA256.Create(); byte[] hash = sha.ComputeHash(bytes, 0, ManifestBytes - 32);
            if (!hash.SequenceEqual(bytes.Skip(ManifestBytes - 32))) return false;
            using var memory = new MemoryStream(bytes, 0, ManifestBytes - 32, false); using var reader = new BinaryReader(memory);
            if (reader.ReadUInt32() != ManifestMagic || reader.ReadUInt16() != ManifestVersion || reader.ReadUInt16() != ManifestBytes) return false;
            timestamp = reader.ReadInt32(); source = reader.ReadInt32(); world = new Guid(reader.ReadBytes(16));
            return timestamp > 0 && source >= -1 && new Guid(reader.ReadBytes(16)) == stageId && memory.Position == memory.Length;
        }
        private static bool TryReadReservedTimestamp(string stage, out int timestamp)
        {
            timestamp = 0;
            if (!TryStageId(Path.GetFileName(stage), out Guid id)) return false;
            try { RegularDirectoryTree(stage); return TryReadManifest(stage, id, out timestamp, out _, out _); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (ArgumentException) { return false; }
        }
        private static bool TryStageId(string? name, out Guid id)
        {
            id = Guid.Empty;
            return name != null && name.Length == 43 && name.StartsWith(".monmp-", StringComparison.Ordinal) &&
                name.EndsWith(".tmp", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(7, 32), "N", out id) && id != Guid.Empty;
        }
        private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);
        private static void RegularDirectoryTree(string directory)
        {
            for (DirectoryInfo? current = new DirectoryInfo(directory); current != null; current = current.Parent)
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Save directories must exist and cannot traverse links.");
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; writerLock.Dispose();
            // Keep interrupted stages below vanilla's immediate-child accessibility scan.
        }
    }
}
