using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MonMP.Core.Persistence
{
    public sealed partial class SaveSlot
    {
        private const string RetentionName = "monmp-retention.bin";
        private const int RetainedCheckpoints = 6;
        private static readonly string[] RetirableFiles =
        {
            "marguerite.txt", "braaaap.txt", "blopblop.txt", "lyne.txt", SidecarStore.FileName,
            SaveAncestry.FileName, ManifestName, RetentionName
        };

        private void StageRetention(int source, SidecarDocument document)
        {
            using var file = new FileStream(Path.Combine(StagingDirectory, RetentionName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(file);
            writer.Write(Timestamp); writer.Write(source); writer.Write(document.WorldId.ToByteArray()); writer.Write(document.Generation.ToByteArray());
            writer.Flush(); file.Flush(true);
        }

        private static Guid ReadRetention(string directory, int timestamp, Guid world, out int source)
        {
            source = 0;
            string path = Path.Combine(directory, RetentionName);
            if (!File.Exists(path) || File.Exists(Path.Combine(directory, NativeOnlyMarkerName))) return Guid.Empty;
            RegularDirectoryTree(directory);
            SidecarStore.RegularFile(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != 40) return Guid.Empty;
            using var reader = new BinaryReader(file);
            if (reader.ReadInt32() != timestamp) return Guid.Empty;
            source = reader.ReadInt32();
            return source >= -1 && source < timestamp && new Guid(reader.ReadBytes(16)) == world
                ? new Guid(reader.ReadBytes(16)) : Guid.Empty;
        }

        // Run after successful publication with the writer lock still held. Only a
        // continuous run of opted-in checkpoints may roll over. A vanilla, degraded,
        // unrecognized, or branched generation breaks the chain and protects its sources.
        public void RetireOldCheckpoint(string expectedGameBuild, Guid expectedWorld)
        {
            if (!Published || disposed) throw new InvalidOperationException("Retention requires a published save and its writer lock.");
            string parent = Path.GetDirectoryName(DestinationDirectory)!;
            var newest = Directory.EnumerateFileSystemEntries(parent)
                .Select(path => (Path: path, Stamp: int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out int stamp) &&
                    Path.GetFileName(path) == stamp.ToString(CultureInfo.InvariantCulture) ? stamp : 0))
                .Where(entry => entry.Stamp > 0).OrderByDescending(entry => entry.Stamp).Take(RetainedCheckpoints + 1).ToArray();
            if (newest.Length <= RetainedCheckpoints || newest[0].Stamp != Timestamp) return;
            for (int i = 0; i < RetainedCheckpoints; i++)
                if (ReadRetention(newest[i].Path, newest[i].Stamp, expectedWorld, out int source) == Guid.Empty || source != newest[i + 1].Stamp) return;
            var candidate = newest[RetainedCheckpoints];
            Guid generation = ReadRetention(candidate.Path, candidate.Stamp, expectedWorld, out int candidateSource);
            if (generation == Guid.Empty || EnumerateStages(parent).Any(stage => TryStageId(Path.GetFileName(stage), out var id) &&
                TryReadManifest(stage, id, out _, out int source, out _) && source == candidate.Stamp)) return;
            var snapshot = SidecarStore.Load(candidate.Path, expectedGameBuild);
            if (snapshot == null || snapshot.Document.WorldId != expectedWorld || snapshot.Document.Generation != generation) return;
            var ancestry = SaveAncestry.Load(candidate.Path, expectedGameBuild);
            if (ancestry?.WorldId != expectedWorld || ancestry.Receipts?.Generation != generation || ancestry.SourceTimestamp != candidateSource) return;
            var names = Directory.GetFileSystemEntries(candidate.Path).Select(path => Path.GetFileName(path)!).ToArray();
            if (!new System.Collections.Generic.HashSet<string>(names, StringComparer.Ordinal).SetEquals(RetirableFiles)) return;
            foreach (string name in names) SidecarStore.RegularFile(Path.Combine(candidate.Path, name));

            // Hide the whole checkpoint first, so a crash or failed cleanup cannot
            // expose an incomplete numeric save. Neither foreign files nor receipt
            // journals (which live at the slot root) are removed.
            string staging = Path.Combine(parent, StagingFolderName);
            RegularDirectoryTree(staging);
            string retired = Path.Combine(staging, "retired-" + Guid.NewGuid().ToString("N"));
            Directory.Move(candidate.Path, retired);
            foreach (string name in names) File.Delete(Path.Combine(retired, name));
            Directory.Delete(retired);
        }
    }
}
