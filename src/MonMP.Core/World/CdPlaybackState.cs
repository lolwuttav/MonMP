using System;
using System.Collections.Generic;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    // CD playback state is separate from the radio's FM/power/volume state.
    // The content key names bytes sent by the host, never a
    // guest's locally indexed CDPlayer entry.
    public sealed class CdPlaybackState
    {
        public const ushort Adapter = 141;
        public const byte Version = 2;
        public const int HashSize = 32;
        public const int LegacySize = 1 + 4 + HashSize + 4;
        public const int Size = LegacySize + 4;
        public static readonly ulong EntitySeed = StaticId.Hash("radio/furniture/cd/v1");

        public readonly uint Revision;
        public readonly byte[] ContentHash;
        public readonly float Time;
        // Presentation only: playback still uses the guest's content-cache index.
        public readonly int DisplayTrackIndex;
        public bool HasContent
        {
            get
            {
                for (int i = 0; i < ContentHash.Length; i++) if (ContentHash[i] != 0) return true;
                return false;
            }
        }

        public CdPlaybackState(uint revision, byte[] contentHash, float time, int displayTrackIndex = -1)
        {
            if (contentHash == null || contentHash.Length != HashSize || float.IsNaN(time) || float.IsInfinity(time) || time < 0 || time > 36000 ||
                (IsEmptyHash(contentHash) && time != 0f) || displayTrackIndex < -1 || displayTrackIndex > 1000000)
                throw new InvalidDataException("Invalid CD playback state.");
            Revision = revision;
            ContentHash = (byte[])contentHash.Clone();
            Time = time;
            DisplayTrackIndex = displayTrackIndex;
        }

        public static ulong EntityFor(string identity) => StaticId.Hash("radio/furniture/cd/v1/" + identity) | WorldRecord.DynamicIdStart;

        public byte[] Encode()
        {
            var payload = new byte[Size];
            payload[0] = Version;
            BitConverter.GetBytes(Revision).CopyTo(payload, 1);
            Buffer.BlockCopy(ContentHash, 0, payload, 5, HashSize);
            BitConverter.GetBytes(Time).CopyTo(payload, 5 + HashSize);
            BitConverter.GetBytes(DisplayTrackIndex).CopyTo(payload, LegacySize);
            return payload;
        }

        public static CdPlaybackState Decode(byte[] payload)
        {
            if (payload == null || !((payload.Length == Size && payload[0] == Version) || (payload.Length == LegacySize && payload[0] == 1)))
                throw new InvalidDataException("Invalid CD playback state.");
            var hash = new byte[HashSize];
            Buffer.BlockCopy(payload, 5, hash, 0, HashSize);
            return new CdPlaybackState(BitConverter.ToUInt32(payload, 1), hash, BitConverter.ToSingle(payload, 5 + HashSize),
                payload.Length == Size ? BitConverter.ToInt32(payload, LegacySize) : -1);
        }

        public static bool HashEquals(byte[]? left, byte[]? right)
        {
            if (left == null || right == null || left.Length != HashSize || right.Length != HashSize) return false;
            int difference = 0;
            for (int i = 0; i < HashSize; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        public static bool IsEmptyHash(byte[]? hash)
        {
            if (hash == null || hash.Length != HashSize) return false;
            int aggregate = 0;
            for (int i = 0; i < hash.Length; i++) aggregate |= hash[i];
            return aggregate == 0;
        }
    }

    // A guest requests only the exact host-selected content key attached to the CD playback record.
    // It cannot nominate a path or ask the host to enumerate its CD folder. ResumeChunk names the
    // first chunk the guest still lacks from an interrupted transfer of that same content.
    public sealed class CdMediaRequest
    {
        public const byte Marker = 0xD1;
        public const byte PrefetchMarker = 0xD3;
        public const byte Version = 2;
        public const int Size = 4 + CdPlaybackState.HashSize;
        public readonly byte[] ContentHash;
        public readonly bool Prefetch;
        public readonly ushort ResumeChunk;

        public CdMediaRequest(byte[] contentHash, bool prefetch = false, ushort resumeChunk = 0)
        {
            if (contentHash == null || contentHash.Length != CdPlaybackState.HashSize || CdPlaybackState.IsEmptyHash(contentHash) ||
                resumeChunk > CdMediaMessage.MaximumChunks)
                throw new InvalidDataException("Invalid CD media request.");
            ContentHash = (byte[])contentHash.Clone();
            Prefetch = prefetch;
            ResumeChunk = resumeChunk;
        }

        public byte[] Encode()
        {
            var payload = new byte[Size];
            payload[0] = Prefetch ? PrefetchMarker : Marker;
            payload[1] = Version;
            Buffer.BlockCopy(ContentHash, 0, payload, 2, ContentHash.Length);
            payload[2 + CdPlaybackState.HashSize] = (byte)ResumeChunk;
            payload[3 + CdPlaybackState.HashSize] = (byte)(ResumeChunk >> 8);
            return payload;
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && (payload[0] == Marker || payload[0] == PrefetchMarker);
        public static CdMediaRequest Decode(byte[] payload)
        {
            if (!Is(payload) || payload[1] != Version) throw new InvalidDataException("Invalid CD media request.");
            var hash = new byte[CdPlaybackState.HashSize];
            Buffer.BlockCopy(payload, 2, hash, 0, hash.Length);
            ushort resume = (ushort)(payload[2 + CdPlaybackState.HashSize] | payload[3 + CdPlaybackState.HashSize] << 8);
            return new CdMediaRequest(hash, payload[0] == PrefetchMarker, resume);
        }
    }

    public enum CdPlaybackAction : byte { PreserveNative, SilenceLocalContent, RequestContent, ApplyHostTrack, Hold }

    // Pure decision helper used by the Unity bridge and the offline tests. Local CD list indexes
    // are deliberately ignored; only a verified transferred content hash can produce ApplyHostTrack.
    public static class CdPlaybackSyncPolicy
    {
        public static float PredictGuestTime(float appliedTime, float appliedAt, float now)
        {
            if (float.IsNaN(appliedTime) || float.IsInfinity(appliedTime) || appliedTime < 0 ||
                float.IsNaN(appliedAt) || float.IsInfinity(appliedAt) || float.IsNaN(now) || float.IsInfinity(now)) return 0f;
            float elapsed = now >= appliedAt ? now - appliedAt : 0f;
            return Math.Min(36000f, appliedTime + elapsed);
        }

        public static CdPlaybackAction Plan(CdPlaybackState state, bool cdMode, bool radioOn, bool audioManagerReady,
            bool transferredTrackAvailable, byte[]? transferredTrackHash, bool requestInFlight,
            byte[]? appliedTrackHash, uint appliedRevision, float appliedTime)
        {
            if (!cdMode || !radioOn) return CdPlaybackAction.PreserveNative;
            if (!state.HasContent || !audioManagerReady) return CdPlaybackAction.SilenceLocalContent;
            if (!transferredTrackAvailable || !CdPlaybackState.HashEquals(state.ContentHash, transferredTrackHash))
                return requestInFlight ? CdPlaybackAction.SilenceLocalContent : CdPlaybackAction.RequestContent;
            if (!CdPlaybackState.HashEquals(state.ContentHash, appliedTrackHash) || state.Revision != appliedRevision ||
                float.IsNaN(appliedTime) || float.IsInfinity(appliedTime) || Math.Abs(state.Time - appliedTime) > 1.5f)
                return CdPlaybackAction.ApplyHostTrack;
            return CdPlaybackAction.Hold;
        }
    }

    public static class CdMediaAuthorityPolicy
    {
        // World commands can arrive only from a guest at the host; each source still has to pass
        // current selection and pose/range checks before this shared state is read or served.
        public static bool MayServe(bool isHost, uint peer, bool poseFresh, bool inRange, bool selectedHashMatches, bool transferSlotAvailable) =>
            isHost && peer != 0 && poseFresh && inRange && selectedHashMatches && transferSlotAvailable;

        // The host is peer zero. A guest accepts media only from that authenticated connection.
        public static bool MayReceive(bool isHost, uint senderPeer) => !isHost && senderPeer == 0;
    }

    public static class CdMediaFilePolicy
    {
        // Native CD reads are limited to the direct child files selected by BuildPlayerMusicList
        // in persistentDataPath/CDPlayer. Reparse points are refused so a CD entry cannot redirect
        // the host read outside that exact folder. Callers still recheck the byte count after read.
        public static bool TryValidateNativeFile(string nativeDirectory, string selectedPath, long fileBytes,
            bool directoryIsReparsePoint, bool fileIsReparsePoint, out CdMediaFormat format)
        {
            format = 0;
            if (string.IsNullOrWhiteSpace(nativeDirectory) || string.IsNullOrWhiteSpace(selectedPath) ||
                directoryIsReparsePoint || fileIsReparsePoint || fileBytes < 1 || fileBytes > MonMP.Core.Protocol.CdMediaMessage.MaximumFileBytes)
                return false;
            try
            {
                string root = System.IO.Path.GetFullPath(nativeDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string path = System.IO.Path.GetFullPath(selectedPath);
                string? parent = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent) || !string.Equals(parent.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                    root, StringComparison.OrdinalIgnoreCase)) return false;
                string name = System.IO.Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0) return false;
                switch (System.IO.Path.GetExtension(name).ToLowerInvariant())
                {
                    case ".wav": format = CdMediaFormat.Wav; return true;
                    case ".mp3": format = CdMediaFormat.Mp3; return true;
                    case ".ogg": format = CdMediaFormat.Ogg; return true;
                    default: return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException || ex is System.Security.SecurityException)
            { return false; }
        }

        public static bool IsExpectedContent(byte[]? bytes, byte[]? expectedHash)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > MonMP.Core.Protocol.CdMediaMessage.MaximumFileBytes ||
                expectedHash == null || expectedHash.Length != CdPlaybackState.HashSize) return false;
            byte[] actual;
            using (var sha = System.Security.Cryptography.SHA256.Create()) actual = sha.ComputeHash(bytes);
            return CdPlaybackState.HashEquals(actual, expectedHash);
        }
    }

    public static class CdMediaCachePolicy
    {
        public const long MaximumFileCacheBytes = 128L * 1024 * 1024;
        public const long MaximumDecodedCacheBytes = 256L * 1024 * 1024;
        // A single selected clip can use the existing decoded-equivalent budget. Reservations and pin-aware
        // eviction still account for all other retained clips and in-flight decodes.
        public const long MaximumDecodedTrackBytes = MaximumDecodedCacheBytes;
        public const int MaximumTracks = 64;

        public static bool CanReserveFile(long currentBytes, long additionalBytes) =>
            currentBytes >= 0 && additionalBytes > 0 && additionalBytes <= MaximumFileCacheBytes &&
            currentBytes <= MaximumFileCacheBytes - additionalBytes;

        public static bool CanRetainDecoded(long currentBytes, long additionalBytes) =>
            currentBytes >= 0 && additionalBytes > 0 && additionalBytes <= MaximumDecodedTrackBytes &&
            currentBytes <= MaximumDecodedCacheBytes - additionalBytes;
    }

    // Associates the native CDPlayer AudioClip object with the exact FileInfo path whose native
    // LoadMusic coroutine added it. Reference identity is required because the game names every
    // clip SongN and async completion order, rather than directory order, determines N.
    public sealed class CdTrackSourceMap<TClip> where TClip : class
    {
        private sealed class SourcePath
        {
            public readonly string Path;
            public readonly long FileBytes, LastWriteUtcTicks;
            public SourcePath(string path, long fileBytes, long lastWriteUtcTicks)
            { Path = path; FileBytes = fileBytes; LastWriteUtcTicks = lastWriteUtcTicks; }
        }
        // AudioClip lifetime is controlled by the game. Keep only weak keys so loading a new
        // native CD list or changing scenes does not pin every historical clip in plugin memory.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TClip, SourcePath> sources =
            new System.Runtime.CompilerServices.ConditionalWeakTable<TClip, SourcePath>();
        public void Associate(TClip clip, string exactSourcePath)
            => Associate(clip, exactSourcePath, 0, 0);

        public void Associate(TClip clip, string exactSourcePath, long fileBytes, long lastWriteUtcTicks)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (string.IsNullOrWhiteSpace(exactSourcePath)) throw new ArgumentException("A CD source path is required.", nameof(exactSourcePath));
            if (fileBytes < 0 || lastWriteUtcTicks < 0 || ((fileBytes == 0) != (lastWriteUtcTicks == 0)))
                throw new ArgumentOutOfRangeException(nameof(fileBytes), "Native source metadata must be paired and nonnegative.");
            sources.Remove(clip);
            sources.Add(clip, new SourcePath(exactSourcePath, fileBytes, lastWriteUtcTicks));
        }
        public bool TryGet(TClip clip, out string sourcePath)
        {
            if (TryGet(clip, out sourcePath, out _, out _)) return true;
            sourcePath = string.Empty;
            return false;
        }
        public bool TryGet(TClip clip, out string sourcePath, out long fileBytes, out long lastWriteUtcTicks)
        {
            if (clip != null && sources.TryGetValue(clip, out var source))
            {
                sourcePath = source.Path; fileBytes = source.FileBytes; lastWriteUtcTicks = source.LastWriteUtcTicks; return true;
            }
            sourcePath = string.Empty;
            fileBytes = lastWriteUtcTicks = 0;
            return false;
        }
        public void Remove(TClip clip) { if (clip != null) sources.Remove(clip); }
    }
}
