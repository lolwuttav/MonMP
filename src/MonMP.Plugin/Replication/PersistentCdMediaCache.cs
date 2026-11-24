using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonMP.Core.Protocol;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Only compressed, content-addressed files survive sessions. Never touches CDPlayer originals.
    internal static class PersistentCdMediaCache
    {
        internal const long MaximumBytes = 512L * 1024 * 1024;
        internal const int MaximumTracks = 64;
        private static readonly SemaphoreSlim Io = new SemaphoreSlim(1, 1);

        internal static string FilePath(string root, byte[] hash, CdMediaFormat format)
        {
            if (hash == null || hash.Length != CdPlaybackState.HashSize || CdPlaybackState.IsEmptyHash(hash))
                throw new ArgumentException("Invalid cache hash.");
            string extension = format == CdMediaFormat.Mp3 ? ".mp3" : format == CdMediaFormat.Ogg ? ".ogg" : format == CdMediaFormat.Wav ? ".wav" : "";
            if (extension.Length == 0) throw new ArgumentException("Invalid cache format.");
            return Path.Combine(Path.GetFullPath(root), BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + extension);
        }

        internal static async Task<byte[]?> ReadAsync(string root, CdMediaMessage announcement, CancellationToken token)
        {
            if (announcement.Kind != CdMediaMessageKind.Begin && announcement.Kind != CdMediaMessageKind.Offer) return null;
            await Io.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!SafeRoot(root, false)) return null;
                string path = FilePath(root, announcement.ContentHash, announcement.Format);
                var info = new FileInfo(path);
                if (!info.Exists || !SafeFile(info)) return null;
                if (info.Length != announcement.FileBytes) { File.Delete(path); return null; }
                byte[] bytes = new byte[checked((int)info.Length)];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                {
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                        if (count == 0) return null;
                        offset += count;
                    }
                    if (stream.Length != bytes.Length) return null;
                }
                token.ThrowIfCancellationRequested();
                if (!SafeRoot(root, false) || !CdMediaFilePolicy.IsExpectedContent(bytes, announcement.ContentHash))
                { if (SafeRoot(root, false)) File.Delete(path); return null; }
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return bytes;
            }
            catch (Exception ex) when (FileError(ex)) { return null; }
            finally { Io.Release(); }
        }

        internal static async Task<bool> WriteAsync(string root, byte[] bytes, byte[] hash, CdMediaFormat format, CancellationToken token,
            IReadOnlyCollection<string>? retainedFiles = null)
        {
            await Io.WaitAsync(token).ConfigureAwait(false);
            string temporary = "";
            try
            {
                token.ThrowIfCancellationRequested();
                if (!CdMediaFilePolicy.IsExpectedContent(bytes, hash) || !SafeRoot(root, true)) return false;
                string path = FilePath(root, hash, format);
                if (File.Exists(path) && !SafeFile(new FileInfo(path))) return false;
                if (File.Exists(path) && await MatchesAsync(path, bytes, token).ConfigureAwait(false))
                {
                    if (!SafeRoot(root, false)) return false;
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                    Prune(root, path, retainedFiles);
                    return true;
                }
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (!SafeRoot(root, false)) return false;
                if (File.Exists(path))
                {
                    if (!SafeFile(new FileInfo(path))) return false;
                    File.Delete(path);
                }
                File.Move(temporary, path);
                temporary = "";
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                Prune(root, path, retainedFiles);
                return true;
            }
            catch (Exception ex) when (FileError(ex)) { return false; }
            finally
            {
                if (temporary.Length != 0 && SafeRoot(root, false))
                    try { File.Delete(temporary); } catch (Exception ex) when (FileError(ex)) { }
                Io.Release();
            }
        }

        private static async Task<bool> MatchesAsync(string path, byte[] expected, CancellationToken token)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                if (stream.Length != expected.Length) return false;
                byte[] buffer = new byte[65536]; int offset = 0;
                while (offset < expected.Length)
                {
                    int count = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, expected.Length - offset), token).ConfigureAwait(false);
                    if (count == 0) return false;
                    for (int i = 0; i < count; i++) if (buffer[i] != expected[offset + i]) return false;
                    offset += count;
                }
                return stream.Length == expected.Length;
            }
        }

        private static void Prune(string root, string keep, IReadOnlyCollection<string>? retainedFiles)
        {
            var retained = retainedFiles == null ? null : new HashSet<string>(retainedFiles, StringComparer.OrdinalIgnoreCase);
            var files = new DirectoryInfo(root).GetFiles().Where(f => SafeFile(f) && IsContentName(f.Name))
                .OrderByDescending(f => string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => retained?.Contains(f.FullName) == true)
                .ThenByDescending(f => f.LastWriteTimeUtc).ToArray();
            long bytes = 0; int tracks = 0;
            foreach (var file in files)
            {
                if (tracks >= MaximumTracks || file.Length > MaximumBytes - bytes) file.Delete();
                else { bytes += file.Length; tracks++; }
            }
            // Interrupted writes from a previous process cannot become playable files.
            foreach (var file in new DirectoryInfo(root).GetFiles("*.part"))
                if (SafeFile(file) && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) file.Delete();
        }

        private static bool IsContentName(string name)
        {
            if (name.Length != 68 || !(name.EndsWith(".mp3", StringComparison.Ordinal) || name.EndsWith(".ogg", StringComparison.Ordinal) || name.EndsWith(".wav", StringComparison.Ordinal))) return false;
            for (int i = 0; i < 64; i++) if (!((name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f'))) return false;
            return true;
        }

        internal static bool SafeRoot(string root, bool create)
        {
            try
            {
                string full = Path.GetFullPath(root);
                for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
                    if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (create) Directory.CreateDirectory(full);
                return Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception ex) when (FileError(ex)) { return false; }
        }
        private static bool SafeFile(FileInfo file) => (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        private static bool FileError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException || ex is CryptographicException;
    }
}
