using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonMP.Core.Protocol;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal static class LocalCdMediaLookup
    {
        internal const int MaximumCandidates = CdMediaCachePolicy.MaximumTracks;
        internal const long MaximumReadBytes = CdMediaCachePolicy.MaximumFileCacheBytes;
        private static readonly SemaphoreSlim FileIo = new SemaphoreSlim(1, 1);

        // Called from a worker: no native list order, filename or Unity object establishes a match.
        // Returned bytes still pass the normal transferred-media decoder and decoded-size checks.
        internal static async Task<byte[]?> FindAsync(string nativeDirectory, CdMediaMessage announcement,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (announcement == null || (announcement.Kind != CdMediaMessageKind.Begin && announcement.Kind != CdMediaMessageKind.Offer) ||
                announcement.FileBytes == 0 || announcement.FileBytes > CdMediaMessage.MaximumFileBytes ||
                !CdMediaCachePolicy.CanRetainDecoded(0, announcement.DecodedBytes) ||
                !Enum.IsDefined(typeof(CdMediaFormat), announcement.Format) ||
                announcement.ContentHash.Length != CdPlaybackState.HashSize ||
                CdPlaybackState.IsEmptyHash(announcement.ContentHash) || string.IsNullOrWhiteSpace(nativeDirectory)) return null;

            byte[] expectedHash = (byte[])announcement.ContentHash.Clone();
            await FileIo.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                string root = Path.GetFullPath(nativeDirectory);
                if (!IsNativeDirectory(root)) return null;
                int inspected = 0;
                long reservedReads = 0;
                byte[]? bytes = null;
                foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (inspected++ >= MaximumCandidates) break;
                    try
                    {
                        var before = new FileInfo(path);
                        before.Refresh();
                        if (!before.Exists || (before.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                            before.Length != announcement.FileBytes ||
                            !CdMediaFilePolicy.TryValidateNativeFile(root, before.FullName, before.Length, false, false, out var format) ||
                            format != announcement.Format || !IsNativeDirectory(root)) continue;

                        if (reservedReads > MaximumReadBytes - before.Length) break;
                        reservedReads += before.Length;
                        long modified = before.LastWriteTimeUtc.Ticks;
                        long created = before.CreationTimeUtc.Ticks;
                        if (bytes == null) bytes = new byte[checked((int)announcement.FileBytes)];
                        bool complete = true;
                        using (var stream = new FileStream(before.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                        {
                            if (stream.Length != bytes.LongLength || !MatchesMetadata(root, before.FullName, bytes.LongLength, modified, created)) continue;
                            int offset = 0;
                            while (offset < bytes.Length)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                int count = await stream.ReadAsync(bytes, offset, Math.Min(65536, bytes.Length - offset), cancellation).ConfigureAwait(false);
                                if (count == 0) { complete = false; break; }
                                offset += count;
                            }
                            if (stream.Length != bytes.LongLength) complete = false;
                        }
                        cancellation.ThrowIfCancellationRequested();
                        if (!complete || !MatchesMetadata(root, before.FullName, bytes.LongLength, modified, created)) continue;
                        using (var sha = SHA256.Create())
                        {
                            byte[] actual = sha.ComputeHash(bytes);
                            cancellation.ThrowIfCancellationRequested();
                            if (CdPlaybackState.HashEquals(actual, expectedHash)) return bytes;
                        }
                    }
                    catch (Exception ex) when (IsFileError(ex)) { }
                }
                return null;
            }
            catch (Exception ex) when (IsFileError(ex)) { return null; }
            finally { FileIo.Release(); }
        }

        private static bool IsNativeDirectory(string root)
        {
            var directory = new DirectoryInfo(root);
            directory.Refresh();
            return directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) == 0;
        }

        private static bool MatchesMetadata(string root, string path, long length, long modified, long created)
        {
            if (!IsNativeDirectory(root)) return false;
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                file.Length == length && file.LastWriteTimeUtc.Ticks == modified && file.CreationTimeUtc.Ticks == created &&
                CdMediaFilePolicy.TryValidateNativeFile(root, path, file.Length, false, false, out _);
        }

        private static bool IsFileError(Exception exception) => exception is IOException ||
            exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException ||
            exception is System.Security.SecurityException || exception is CryptographicException;
    }
}
