using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using UnityEngine;
using UnityEngine.Networking;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedFurnitureRadios
    {
        private CachedMedia? hostPlaybackLoading;
        private long hostPlaybackReservedBytes;
        private float nextHostPlayback;

        // Metadata-only native slots retain song length and ordinal. The native
        // playback prefix never lets these silent handles become audible sources.
        private static AudioClip CreateHostCdCatalogueClip(AudioClip validated) =>
            AudioClip.Create(validated.name, validated.samples, validated.channels, validated.frequency, true, SilenceCatalogue);
        private static void SilenceCatalogue(float[] samples) => Array.Clear(samples, 0, samples.Length);

        private bool HostCdSelected(CachedMedia cached)
        {
            var manager = Singleton<SOUND_Manager>.i;
            var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
            var playing = manager == null ? null : RadioCdNative.PlayingRadios(manager);
            if (tracks == null || playing == null) return false;
            foreach (var sound in playing)
            {
                if (sound == null) continue;
                if (sound.Speakers != null && sound.Speakers.Any(s => s != null && s.isPlaying && ReferenceEquals(s.clip, cached.Clip))) return true;
                int index = CdSong(sound);
                if (RadioCdNative.RadioMode(sound) == 1 && index >= 0 && index < tracks.Count && ReferenceEquals(tracks[index], cached.Clip)) return true;
            }
            return false;
        }
        private bool EvictHostCdPlayback()
        {
            var candidate = transferredMedia.Values.Where(c => c.CatalogueClip != null && c.MemoryBytes > 0 && !HostCdSelected(c))
                .OrderBy(c => c.LastUsed).FirstOrDefault();
            if (candidate == null) return false;
            var decoded = candidate.Clip;
            SwapHostCdClip(candidate, candidate.CatalogueClip!);
            decodedCacheBytes -= candidate.MemoryBytes; candidate.MemoryBytes = 0;
            UnityEngine.Object.Destroy(decoded);
            return true;
        }
        private void SwapHostCdClip(CachedMedia cached, AudioClip replacement)
        {
            var old = cached.Clip;
            if (uploadedCdSources.TryGetValue(old, out var source)) { uploadedCdSources.Remove(old); uploadedCdSources[replacement] = source; }
            if (playlistMedia.TryGetValue(old, out var catalogue)) { playlistMedia.Remove(old); playlistMedia[replacement] = catalogue; }
            var manager = Singleton<SOUND_Manager>.i;
            var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
            if (tracks != null)
            {
                int index = tracks.IndexOf(old);
                if (index >= 0) { tracks[index] = replacement; cached.Index = index; }
                else cached.Index = NativeCdTrackIndex.Ensure(tracks, replacement);
            }
            foreach (var binding in bindings.Values)
                if (ReferenceEquals(binding.HostTrackClip, old)) binding.HostTrackClip = replacement;
            cached.Clip = replacement;
        }
        private bool AllowHostCdPlayback(SOUND_Manager manager, SOUND_Radio sound)
        {
            if (RadioCdNative.RadioMode(sound) != 1) return true;
            var tracks = RadioCdNative.CdPlayer(manager);
            int index = CdSong(sound);
            if (tracks == null || index < 0 || index >= tracks.Count) return true;
            CachedMedia? cached = null;
            foreach (var entry in transferredMedia.Values)
                if (entry.CatalogueClip != null && ReferenceEquals(entry.Clip, tracks[index])) { cached = entry; break; }
            if (cached == null) return true; // Native personal songs stay vanilla-owned.
            TouchCachedMedia(cached);
            if (!ReferenceEquals(cached.Clip, cached.CatalogueClip)) return true;
            // Keep the old station from playing under the newly selected song.
            if (sound.Speakers != null) foreach (var speaker in sound.Speakers)
                if (speaker != null && (speaker.clip != null || speaker.isPlaying)) { speaker.Stop(); speaker.clip = null; }
            StartHostCdPlayback(cached);
            return false;
        }
        private void PumpHostCdPlayback(float now)
        {
            if (now < nextHostPlayback) return;
            nextHostPlayback = now + .1f;
            var manager = Singleton<SOUND_Manager>.i;
            var playing = manager == null ? null : RadioCdNative.PlayingRadios(manager);
            if (manager == null || playing == null) return;
            for (int i = 0; i < playing.Count; i++) if (playing[i] != null) AllowHostCdPlayback(manager, playing[i]);
        }
        private void StartHostCdPlayback(CachedMedia cached)
        {
            if (disposed || hostPlaybackLoading != null || !MediaRetryReady("play:" + cached.Path) || !uploadedCdSources.TryGetValue(cached.Clip, out var source)) return;
            while (!CdMediaCachePolicy.CanRetainDecoded(decodedCacheBytes + reservedDecodedBytes, cached.DecodedBytes))
                if (!EvictHostCdPlayback()) return;
            var plugin = MonMPPlugin.Instance;
            if (plugin == null) return;
            hostPlaybackLoading = cached; hostPlaybackReservedBytes = cached.DecodedBytes;
            try { plugin.StartCoroutine(LoadHostCdPlayback(cached, source)); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            { hostPlaybackLoading = null; hostPlaybackReservedBytes = 0; WarnCdMedia("host CD playback decode could not start"); }
        }
        private IEnumerator LoadHostCdPlayback(CachedMedia cached, HostMedia source)
        {
            AudioClip? decoded = null;
            UnityWebRequest? request = null;
            Task<byte[]?>? read = null;
            bool committed = false;
            try
            {
                var announcement = CdMediaMessage.Begin(CdMediaMessage.UploadEntity, 1, source.Hash, source.Length, source.DecodedBytes, source.Format);
                var token = cacheCancellation.Token;
                read = Task.Run(() => PersistentCdMediaCache.ReadAsync(cacheDirectory, announcement, token));
                _ = read.ContinueWith(done => { if (done.IsFaulted) { var ignored = done.Exception; } }, TaskScheduler.Default);
                while (!read.IsCompleted) yield return null;
                bool verified = false;
                try { verified = read.GetAwaiter().GetResult() != null; }
                catch (OperationCanceledException) { }
                read = null;
                if (!verified || disposed || !ReferenceEquals(hostPlaybackLoading, cached)) yield break;
                request = TryCreateAudioRequest(cached.Path, source.Format);
                if (request == null) yield break;
                request.timeout = 30;
                if (request.downloadHandler is DownloadHandlerAudioClip audio) { audio.streamAudio = false; audio.compressed = true; }
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) yield break;
                bool loaded = true;
                try { decoded = DownloadHandlerAudioClip.GetContent(request); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException) { loaded = false; }
                if (!loaded || disposed || !ReferenceEquals(hostPlaybackLoading, cached) || !HostCdSelected(cached)) yield break;
                if (decoded == null || DecodedCdBytes(decoded) != cached.DecodedBytes || decoded.frequency != cached.Clip.frequency ||
                    decoded.channels != cached.Clip.channels || !CdMediaCachePolicy.CanRetainDecoded(decodedCacheBytes + reservedDecodedBytes, cached.DecodedBytes)) yield break;
                decoded.name = cached.Clip.name;
                SwapHostCdClip(cached, decoded);
                cached.MemoryBytes = cached.DecodedBytes; decodedCacheBytes += cached.MemoryBytes;
                mediaRetries.Remove("play:" + cached.Path);
                decoded = null; committed = true;
                var manager = Singleton<SOUND_Manager>.i;
                var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
                var playing = manager == null ? null : RadioCdNative.PlayingRadios(manager);
                if (manager != null && tracks != null && playing != null)
                    foreach (var sound in playing.ToArray())
                        if (sound != null && RadioCdNative.RadioMode(sound) == 1 && CdSong(sound) >= 0 && CdSong(sound) < tracks.Count && ReferenceEquals(tracks[CdSong(sound)], cached.Clip))
                            RadioCdNative.Play(manager, sound);
            }
            finally
            {
                request?.Dispose();
                if (decoded != null) UnityEngine.Object.Destroy(decoded);
                if (read != null) _ = read.ContinueWith(done => { if (done.IsFaulted) { var ignored = done.Exception; } }, TaskScheduler.Default);
                if (ReferenceEquals(hostPlaybackLoading, cached))
                {
                    hostPlaybackLoading = null; hostPlaybackReservedBytes = 0;
                    if (!disposed && !committed) DeferMedia("play:" + cached.Path, "host CD playback will retry when its selected cached file is ready");
                }
            }
        }
        private void DisposeHostCdPlayback() { hostPlaybackLoading = null; hostPlaybackReservedBytes = 0; }
    }
}
