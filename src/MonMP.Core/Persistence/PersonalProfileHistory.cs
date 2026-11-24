using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MonMP.Core.Persistence
{
    // Personal observations follow the player across same-world checkpoints. This history
    // never supplies world configuration, positions, or permanent transaction receipts.
    public sealed class PersonalProfileHistory
    {
        public const long MaximumObservation = 253402300799L;
        public const long MaximumSavedObservation = int.MaxValue;
        private readonly Guid worldId;
        private readonly string gameBuild;
        private readonly Dictionary<Guid, Candidate> profiles = new Dictionary<Guid, Candidate>();
        private readonly Dictionary<Guid, Candidate> identities = new Dictionary<Guid, Candidate>();
        private sealed class Candidate
        {
            internal SidecarProfile Profile = null!;
            internal Guid Generation;
            internal int Timestamp;
        }

        public PersonalProfileHistory(Guid worldId, string gameBuild)
        {
            if (worldId == Guid.Empty || string.IsNullOrEmpty(gameBuild)) throw new ArgumentException("A personal history needs a world and game build.");
            this.worldId = worldId; this.gameBuild = gameBuild;
        }

        public IReadOnlyList<SidecarProfile> Profiles => Array.AsReadOnly(profiles.Values.Select(p => p.Profile).OrderBy(p => p.Id).ToArray());
        public IReadOnlyList<KeyValuePair<Guid, string>> Identities => Array.AsReadOnly(identities.Values.OrderBy(p => p.Profile.Id)
            .Select(p => new KeyValuePair<Guid, string>(p.Profile.Id, p.Profile.DisplayName)).ToArray());
        public SidecarProfile? Find(Guid profile) => profiles.TryGetValue(profile, out var candidate) ? candidate.Profile : null;
        // LastSeenUtc also orders observations when peers have different wall clocks. A
        // received state advances past the restored floor; it never waits for UTC to catch up.
        public static long AfterObservation(long observedUtc, long restoredUtc) => Math.Min(MaximumObservation,
            observedUtc > restoredUtc ? observedUtc : restoredUtc >= MaximumObservation ? MaximumObservation : restoredUtc + 1);

        public void AddPublication(SidecarDocument document, int timestamp)
        {
            if (document.WorldId != worldId || !string.Equals(document.GameBuild, gameBuild, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var profile in document.Profiles.Values)
            {
                var candidate = new Candidate { Profile = profile, Generation = document.Generation, Timestamp = timestamp };
                if (!identities.TryGetValue(profile.Id, out var identity) || NewerPublication(candidate, identity)) identities[profile.Id] = candidate;
                if (!ValidPersonalState(profile)) continue;
                if (!profiles.TryGetValue(profile.Id, out var prior) || NewerPublication(candidate, prior)) profiles[profile.Id] = candidate;
            }
        }

        public void AddJournal(IEnumerable<SidecarProfile> journal, PersonalProfileJournalBinding binding)
        {
            if (binding.WorldId != worldId || !string.Equals(binding.GameBuild, gameBuild, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var profile in journal)
            {
                var candidate = new Candidate { Profile = profile, Generation = binding.Generation, Timestamp = binding.SourceTimestamp };
                if (!identities.TryGetValue(profile.Id, out var identity) || NewerObservation(candidate, identity)) identities[profile.Id] = candidate;
                if (!ValidPersonalState(profile)) continue;
                if (!profiles.TryGetValue(profile.Id, out var prior) || NewerObservation(candidate, prior)) profiles[profile.Id] = candidate;
            }
        }

        // Corrupt/missing personal data in one publication does not erase an older valid
        // observation. Disk recovery for purchases deliberately uses its separate strict reader.
        public void LoadPublications(string persistentRoot, string saveName, Action<string>? ignored = null)
        {
            string parent = SaveSlot.GetSaveParentPath(persistentRoot, saveName);
            var directories = Directory.EnumerateDirectories(parent).Select(path => new { Path = path, Name = Path.GetFileName(path) })
                .Where(p => int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int timestamp) && timestamp > 0 &&
                    p.Name == timestamp.ToString(CultureInfo.InvariantCulture)).OrderByDescending(p => int.Parse(p.Name, CultureInfo.InvariantCulture));
            foreach (var directory in directories)
            {
                try
                {
                    var snapshot = File.Exists(Path.Combine(directory.Path, SaveSlot.RecoveryProfilesName)) &&
                        SaveSlot.LoadNativeOnly(directory.Path, gameBuild) is { } recovery
                        ? SaveSlot.LoadRecoveryProfiles(directory.Path, recovery) : ReadPersonalPublication(directory.Path);
                    if (snapshot != null) AddPublication(snapshot.Document, int.Parse(directory.Name, CultureInfo.InvariantCulture));
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                { ignored?.Invoke(ex.Message); }
            }
        }

        private static SidecarSnapshot? ReadPersonalPublication(string directory)
        {
            directory = SidecarStore.DirectoryPath(directory);
            string path = Path.Combine(directory, SidecarStore.FileName);
            if (!File.Exists(path)) return null;
            SidecarStore.RegularFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > SidecarStore.MaxBytes) throw new InvalidDataException("Personal checkpoint exceeds the sidecar size limit.");
            var bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            // The bounded checksum/build/world metadata is sufficient for personal values.
            // Native world fingerprints and receipt history retain their strict readers.
            return SidecarStore.Decode(bytes);
        }

        public static bool ValidPersonalState(SidecarProfile profile)
        {
            // Native checkpoint timestamps are signed Unix seconds. Dates beyond that
            // supported range are unusable optional metadata; retain the saved identity.
            if (profile.LastSeenUtc > MaximumSavedObservation) return false;
            if (!profile.State.TryGetValue(PlayerProfileState.SectionKey, out var section)) return false;
            try { _ = PlayerProfileState.FromSection(section); return true; }
            catch (InvalidDataException) { return false; }
        }

        private static bool NewerPublication(Candidate candidate, Candidate prior) => candidate.Profile.LastSeenUtc > prior.Profile.LastSeenUtc ||
            (candidate.Profile.LastSeenUtc == prior.Profile.LastSeenUtc && (candidate.Timestamp > prior.Timestamp ||
                (candidate.Timestamp == prior.Timestamp && candidate.Generation.CompareTo(prior.Generation) > 0)));
        // A tied sample bound to the winning publication was observed after that publication.
        // A cross-generation tie favors the complete publication, including crash-before-rollover.
        private static bool NewerObservation(Candidate candidate, Candidate prior) => candidate.Profile.LastSeenUtc > prior.Profile.LastSeenUtc ||
            (candidate.Profile.LastSeenUtc == prior.Profile.LastSeenUtc && candidate.Generation == prior.Generation && candidate.Timestamp == prior.Timestamp);
    }
}
