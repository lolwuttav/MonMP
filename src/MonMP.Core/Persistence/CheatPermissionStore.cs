using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MonMP.Core.Persistence
{
    /// <summary>Optional, bounded host preferences. Never gate a join or a game save.</summary>
    public sealed class CheatPermissionStore
    {
        public const string FileName = "monmp-cheat-permissions.bin";
        public const int MaximumProfiles = 4096;
        private const uint Magic = 0x5043484D;
        private const ushort Version = 1;
        private const int MaximumNameBytes = 200, MaximumFileBytes = 1024 * 1024;
        private static readonly Dictionary<string, WeakReference<CheatPermissionStore>> openStores =
            new Dictionary<string, WeakReference<CheatPermissionStore>>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();
        private readonly string path;
        private readonly Action<string>? report;
        private readonly Dictionary<Guid, (string Name, bool Allowed)> entries = new Dictionary<Guid, (string, bool)>();
        private byte[]? pending;
        private bool writing;
        private Task writer = Task.CompletedTask;
        public Task PendingWrites { get { lock (sync) return writer; } }

        // Rehosting must see pending memory immediately and share its one writer,
        // rather than loading an older disk copy or waiting for optional disk I/O.
        public static CheatPermissionStore Open(string directory, Action<string>? report = null)
        {
            string key = Path.GetFullPath(directory);
            lock (openStores)
            {
                if (openStores.TryGetValue(key, out var reference) && reference.TryGetTarget(out var existing)) return existing;
                foreach (string stale in openStores.Where(p => !p.Value.TryGetTarget(out _)).Select(p => p.Key).ToArray()) openStores.Remove(stale);
                var store = new CheatPermissionStore(key, report);
                openStores[key] = new WeakReference<CheatPermissionStore>(store);
                return store;
            }
        }

        public CheatPermissionStore(string directory, Action<string>? report = null)
        {
            this.report = report;
            path = Path.Combine(directory, FileName);
            try { Load(); }
            catch (Exception ex) when (Recoverable(ex))
            { entries.Clear(); Report("Remembered cheat permissions were ignored: " + ex.Message); }
        }
        public bool TryResume(Guid profile, string name, IEnumerable<Guid> connected, out bool allowed,
            IEnumerable<KeyValuePair<Guid, string>>? knownProfiles = null)
        {
            allowed = false;
            if (profile == Guid.Empty || !ValidName(name)) return false;
            lock (sync)
            {
                if (entries.TryGetValue(profile, out var exact)) { allowed = exact.Allowed; return true; }
                string normalized = Normalize(name);
                if (normalized.Length == 0) return false;
                var matches = entries.Where(p => Normalize(p.Value.Name) == normalized).ToArray();
                if (matches.Length != 1 || connected.Contains(matches[0].Key)) return false;
                // Permission history is only part of the host's identity history.
                // Another saved player with this name must also keep it ambiguous.
                if ((knownProfiles ?? Array.Empty<KeyValuePair<Guid, string>>())
                    .Where(p => p.Key != Guid.Empty && Normalize(p.Value) == normalized)
                    .Select(p => p.Key).Append(matches[0].Key).Distinct().Take(2).Count() > 1) return false;
                var match = matches[0]; allowed = match.Value.Allowed;
                // A new computer may receive a new resolved ID when profile history
                // is missing. Move the one unambiguous offline preference with it.
                entries.Remove(match.Key);
                entries[profile] = (name, allowed);
                QueueWrite();
                return true;
            }
        }
        public void Remember(Guid profile, string name, bool allowed)
        {
            if (profile == Guid.Empty || !ValidName(name)) return;
            lock (sync)
            {
                if (entries.TryGetValue(profile, out var old) && old.Name == name && old.Allowed == allowed) return;
                if (!entries.ContainsKey(profile) && entries.Count >= MaximumProfiles)
                { Report("Remembered cheat permissions reached their profile limit; the live permission still applies."); return; }
                entries[profile] = (name, allowed);
                QueueWrite();
            }
        }
        private void QueueWrite()
        {
            try { pending = Encode(); }
            catch (Exception ex) when (Recoverable(ex)) { Report("Cheat permission could not be remembered: " + ex.Message); return; }
            if (!writing) { writing = true; writer = Task.Run(WritePending); }
        }
        private void WritePending()
        {
            while (true)
            {
                byte[] bytes;
                lock (sync)
                {
                    if (pending == null) { writing = false; return; }
                    bytes = pending; pending = null;
                }
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                catch (Exception ex) when (Recoverable(ex))
                { Report("Cheat permission could not be remembered; the live permission still applies: " + ex.Message); }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception ex) when (Recoverable(ex)) { /* Optional temporary file cleanup. */ }
                }
            }
        }
        private byte[] Encode()
        {
            using var memory = new MemoryStream(); using var binary = new BinaryWriter(memory, Encoding.UTF8, true);
            binary.Write(Magic); binary.Write(Version); binary.Write((ushort)entries.Count);
            foreach (var entry in entries.OrderBy(p => p.Key))
            {
                byte[] name = Encoding.UTF8.GetBytes(entry.Value.Name);
                binary.Write(entry.Key.ToByteArray()); binary.Write(entry.Value.Allowed);
                binary.Write((ushort)name.Length); binary.Write(name);
            }
            binary.Flush();
            using var sha = SHA256.Create(); binary.Write(sha.ComputeHash(memory.ToArray())); binary.Flush();
            if (memory.Length > MaximumFileBytes) throw new InvalidDataException("Permission preferences exceed their file limit.");
            return memory.ToArray();
        }
        private void Load()
        {
            if (!File.Exists(path)) return;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < 40 || file.Length > MaximumFileBytes) throw new InvalidDataException("Invalid permission preference length.");
            byte[] bytes = new byte[(int)file.Length]; int at = 0;
            while (at < bytes.Length)
            { int count = file.Read(bytes, at, bytes.Length - at); if (count == 0) throw new EndOfStreamException(); at += count; }
            using var sha = SHA256.Create();
            if (!sha.ComputeHash(bytes, 0, bytes.Length - 32).SequenceEqual(bytes.Skip(bytes.Length - 32)))
                throw new InvalidDataException("Permission preference checksum failed.");
            using var memory = new MemoryStream(bytes, 0, bytes.Length - 32, false); using var reader = new BinaryReader(memory);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version) throw new InvalidDataException("Unsupported permission preferences.");
            int countEntries = reader.ReadUInt16();
            if (countEntries > MaximumProfiles) throw new InvalidDataException("Too many remembered permissions.");
            var utf8 = new UTF8Encoding(false, true);
            for (int index = 0; index < countEntries; index++)
            {
                Guid profile = new Guid(reader.ReadBytes(16)); byte flag = reader.ReadByte(); int length = reader.ReadUInt16();
                if (profile == Guid.Empty || flag > 1 || length < 1 || length > MaximumNameBytes || entries.ContainsKey(profile))
                    throw new InvalidDataException("Invalid remembered permission identity or name.");
                byte[] nameBytes = reader.ReadBytes(length); if (nameBytes.Length != length) throw new EndOfStreamException();
                string name = utf8.GetString(nameBytes);
                if (!ValidName(name)) throw new InvalidDataException("Invalid remembered permission name.");
                entries.Add(profile, (name, flag != 0));
            }
            if (memory.Position != memory.Length) throw new InvalidDataException("Trailing permission preference bytes.");
        }
        private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && Encoding.UTF8.GetByteCount(name) <= MaximumNameBytes &&
            !name.Any(char.IsControl) && Normalize(name).Length != 0;
        private static string Normalize(string? name)
        { try { return (name ?? "").Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant(); } catch (ArgumentException) { return ""; } }
        private static bool Recoverable(Exception ex) => ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException ||
            ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException;
        private void Report(string message) { try { report?.Invoke(message); } catch { /* Optional diagnostics cannot gate gameplay. */ } }
    }
}
