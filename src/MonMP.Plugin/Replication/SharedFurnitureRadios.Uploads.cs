using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedFurnitureRadios
    {
        private sealed class GuestCdUpload
        {
            internal AudioClip Clip = null!;
            internal HostMedia? Source;
            internal CdMediaMessage? Offer;
            internal CdMediaMessage? PendingMessage;
            internal Task<byte[]>? Read;
            internal byte[]? Bytes;
            internal ushort NextChunk;
            internal float StartedAt, NextAt;
            internal bool BeginSent, AwaitingStored;
        }
        private readonly Queue<AudioClip> manualCdTracks = new Queue<AudioClip>();
        private readonly HashSet<string> manualCdHashes = new HashSet<string>(StringComparer.Ordinal);
        // Acknowledgements belong to this host session. Reloading a native clip
        // creates a new object, so retain its validated file identity separately.
        private readonly Dictionary<string, HostMedia> acknowledgedCdSources = new Dictionary<string, HostMedia>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, CdMediaMessage> cdUploadReplies = new Dictionary<uint, CdMediaMessage>();
        private readonly Dictionary<AudioClip, HostMedia> uploadedCdSources = new Dictionary<AudioClip, HostMedia>();
        private GuestCdUpload? guestCdUpload;
        private CdUploadTicket? incomingCdUpload;
        private MediaReservation? incomingCdReservation;
        private Task<byte[]?>? incomingCdCacheRead;
        private CancellationTokenSource? incomingCdCacheCancellation;
        private bool incomingCdDecoding;
        private uint manualCdGeneration;
        private int manualCdCount;
        private long manualCdBytes;
        private bool restoreUploadedCd;

        internal Action<AudioClip>? BeginManualCdLoad()
        {
            if (!Active || host || channel?.FullySynchronized != true) return null;
            uint generation = ++manualCdGeneration;
            manualCdTracks.Clear(); manualCdCount = 0; manualCdBytes = 0;
            return clip =>
            {
                if (!Active || host || generation != manualCdGeneration || clip == null || manualCdCount >= CdMediaCachePolicy.MaximumTracks) return;
                if (RadioCdNative.TrackSources.TryGet(clip, out string path, out long length, out long ticks) &&
                    (acknowledgedCdSources.TryGetValue(path, out var known) && known.Length == length && known.LastWriteUtcTicks == ticks ||
                     guestCdUpload?.Source is HostMedia current && string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase) &&
                        current.Length == length && current.LastWriteUtcTicks == ticks)) return;
                manualCdCount++; manualCdTracks.Enqueue(clip);
            };
        }
        internal void NativeCdPlaylistRebuilt()
        {
            restoreUploadedCd = host && uploadedCdSources.Count != 0;
            RestoreUploadedCd();
            rebuildingCdList = false;
            FinishNativeCdRebuild();
        }
        private void RestoreUploadedCd()
        {
            if (!restoreUploadedCd) return;
            var manager = Singleton<SOUND_Manager>.i;
            var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
            if (tracks == null) return;
            restoreUploadedCd = false;
            foreach (var pair in transferredMedia)
                if (pair.Value.Clip != null && uploadedCdSources.ContainsKey(pair.Value.Clip))
                    pair.Value.Index = NativeCdTrackIndex.Ensure(tracks, pair.Value.Clip);
            foreach (var binding in bindings.Values)
                if (binding.Sound != null && binding.HostTrackClip != null && uploadedCdSources.ContainsKey(binding.HostTrackClip))
                    CdSong(binding.Sound) = tracks.IndexOf(binding.HostTrackClip);
        }
        private void RegisterUploadedCd(CachedMedia cached, byte[] hash, CdMediaFormat format)
        {
            uploadedCdSources[cached.Clip] = new HostMedia
            {
                Path = cached.Path, Hash = (byte[])hash.Clone(), Length = checked((uint)cached.FileBytes),
                DecodedBytes = checked((uint)cached.DecodedBytes), Format = format, HashReady = true, CachedUpload = true
            };
            MonMPPlugin.Instance?.LogCdInfo("Added a manually loaded guest track to the session CD playlist.");
        }
        private void ReceiveCdUpload(uint peer, CdMediaMessage message)
        {
            if (!Active || !host || peer == 0 || !mediaPeersReady.Contains(peer)) return;
            float now = Time.unscaledTime;
            if (message.Kind == CdMediaMessageKind.UploadOffer)
            {
                string key = Convert.ToBase64String(message.ContentHash);
                CdUploadStatus status;
                bool pendingNativeHash = false;
                // Native files may not have reached the periodic catalog yet.
                // Start bounded source hashing, then ask the sender to wait.
                if (FindPlaylistMedia(message.ContentHash)?.HashReady != true)
                    pendingNativeHash = PrepareHostCdHashes();
                if (FindPlaylistMedia(message.ContentHash)?.HashReady == true ||
                    transferredMedia.TryGetValue(key, out var cached) && cached.Clip != null && uploadedCdSources.ContainsKey(cached.Clip))
                    status = CdUploadStatus.Stored;
                else if (incomingCdUpload != null)
                {
                    status = incomingCdUpload.Matches(peer, message) &&
                        CdPlaybackState.HashEquals(incomingCdUpload.Offer.ContentHash, message.ContentHash) &&
                        incomingCdUpload.Offer.FileBytes == message.FileBytes && incomingCdUpload.Offer.DecodedBytes == message.DecodedBytes &&
                        incomingCdUpload.Offer.Format == message.Format && !incomingCdUpload.Completed &&
                        incomingCdCacheRead == null && !incomingCdDecoding ? CdUploadStatus.Send : CdUploadStatus.Busy;
                }
                else if (pendingNativeHash) status = CdUploadStatus.Busy;
                else if (rejectedMedia.Contains(key)) status = CdUploadStatus.Rejected;
                else if (!TryReserveMedia(key, message.FileBytes, message.DecodedBytes, out incomingCdReservation)) status = CdUploadStatus.Busy;
                else
                {
                    incomingCdUpload = new CdUploadTicket(peer, message, now);
                    incomingCdCacheCancellation = CancellationTokenSource.CreateLinkedTokenSource(cacheCancellation.Token);
                    var token = incomingCdCacheCancellation.Token;
                    var announcement = CdMediaMessage.Begin(message.RadioEntity, message.TransferId, message.ContentHash,
                        message.FileBytes, message.DecodedBytes, message.Format);
                    incomingCdCacheRead = Task.Run(() => PersistentCdMediaCache.ReadAsync(cacheDirectory, announcement, token));
                    status = CdUploadStatus.Busy;
                }
                cdUploadReplies[peer] = CdMediaMessage.UploadReply(message.TransferId, message.ContentHash, status);
                return;
            }
            var ticket = incomingCdUpload;
            // Stale or unsolicited media cannot allocate or cancel another sender's slot.
            if (ticket == null || !ticket.Matches(peer, message) || ticket.Completed) return;
            if (message.Kind == CdMediaMessageKind.Abort) { EndCdUpload(CdUploadStatus.Rejected); return; }
            if (incomingCdCacheRead != null || incomingCdDecoding) return;
            if (!ticket.Accept(peer, message, now, out var bytes)) { EndCdUpload(CdUploadStatus.Rejected); return; }
            if (bytes == null) return;
            StartUploadedCdDecode(ticket, bytes);
        }
        private bool PrepareHostCdHashes()
        {
            var manager = Singleton<SOUND_Manager>.i;
            var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
            if (tracks == null) return false;
            bool pending = false;
            foreach (var clip in tracks.Take(CdMediaCachePolicy.MaximumTracks))
            {
                if (clip == null) continue;
                if (!playlistMedia.TryGetValue(clip, out var media))
                {
                    media = InspectHostMedia(clip, PersistentCdMediaCache.MaximumBytes - playlistMedia.Values.Sum(m => (long)m.Length));
                    if (media == null) continue;
                    playlistMedia[clip] = media;
                }
                CompleteHostHash(media);
                pending |= !media.HashReady && !media.HashFailed;
            }
            return pending;
        }
        private void StartUploadedCdDecode(CdUploadTicket ticket, byte[] bytes)
        {
            string completedKey = Convert.ToBase64String(ticket.Offer.ContentHash);
            var plugin = MonMPPlugin.Instance;
            if (plugin == null || incomingCdReservation == null || !loadingMedia.Add(completedKey))
            { EndCdUpload(CdUploadStatus.Busy); return; }
            var reservation = incomingCdReservation; incomingCdReservation = null;
            incomingCdDecoding = true;
            try { plugin.StartCoroutine(StoreUploadedCd(ticket, bytes, completedKey, reservation)); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                loadingMedia.Remove(completedKey); ReleaseMediaReservation(completedKey, reservation);
                EndCdUpload(CdUploadStatus.Rejected); WarnCdMedia("manual CD upload could not start audio loading");
            }
        }
        private IEnumerator StoreUploadedCd(CdUploadTicket ticket, byte[] bytes, string key, MediaReservation reservation)
        {
            try
            {
                yield return LoadTransferredMedia(bytes, ticket.Offer.ContentHash, ticket.Offer.Format,
                    ticket.Offer.DecodedBytes, key, uploadTicket: ticket, uploadReservation: reservation);
            }
            finally
            {
                ReleaseMediaReservation(key, reservation); loadingMedia.Remove(key);
                if (ReferenceEquals(incomingCdUpload, ticket))
                    EndCdUpload(transferredMedia.TryGetValue(key, out var cached) && cached.Clip != null &&
                        uploadedCdSources.ContainsKey(cached.Clip) ? CdUploadStatus.Stored : CdUploadStatus.Rejected);
            }
        }
        private void EndCdUpload(CdUploadStatus status)
        {
            var ticket = incomingCdUpload; incomingCdUpload = null;
            incomingCdDecoding = false;
            var read = incomingCdCacheRead; incomingCdCacheRead = null;
            var cancellation = incomingCdCacheCancellation; incomingCdCacheCancellation = null;
            cancellation?.Cancel();
            if (read != null)
                _ = read.ContinueWith(done => { if (done.IsFaulted) { var ignored = done.Exception; } cancellation?.Dispose(); }, TaskScheduler.Default);
            else cancellation?.Dispose();
            if (ticket == null) return;
            string key = Convert.ToBase64String(ticket.Offer.ContentHash);
            if (incomingCdReservation != null) ReleaseMediaReservation(key, incomingCdReservation);
            incomingCdReservation = null; ticket.Dispose();
            if (!disposed && mediaPeersReady.Contains(ticket.Peer))
                cdUploadReplies[ticket.Peer] = CdMediaMessage.UploadReply(ticket.Offer.TransferId, ticket.Offer.ContentHash, status);
        }
        private void ReceiveCdUploadReply(CdMediaMessage message)
        {
            var upload = guestCdUpload;
            if (host || upload?.Offer == null || message.TransferId != upload.Offer.TransferId ||
                !CdPlaybackState.HashEquals(message.ContentHash, upload.Offer.ContentHash)) return;
            switch ((CdUploadStatus)message.AbortReason)
            {
                case CdUploadStatus.Stored:
                    if (manualCdHashes.Count < CdMediaCachePolicy.MaximumTracks) manualCdHashes.Add(Convert.ToBase64String(message.ContentHash));
                    if (upload.Source != null && acknowledgedCdSources.Count < CdMediaCachePolicy.MaximumTracks)
                        acknowledgedCdSources[upload.Source.Path] = upload.Source;
                    FinishGuestCdUpload(); break;
                case CdUploadStatus.Rejected:
                    WarnCdMedia("host could not accept a manually loaded CD track"); FinishGuestCdUpload(); break;
                case CdUploadStatus.Busy:
                    if (!upload.BeginSent && upload.Read == null) upload.NextAt = Time.unscaledTime + 5f;
                    break;
                case CdUploadStatus.Send:
                    if (upload.Read != null || upload.BeginSent || upload.Source == null) break;
                    var source = upload.Source;
                    upload.NextAt = Time.unscaledTime;
                    upload.Read = Task.Run(() => ReadSelectedMediaAsync(NativeCdDirectory.Path, source, cacheCancellation.Token,
                        () => System.Threading.Interlocked.Exchange(ref source.LastProgressTimestamp, System.Diagnostics.Stopwatch.GetTimestamp())));
                    // Observe failures even if the session is disposed before the next pump.
                    _ = upload.Read.ContinueWith(done => { if (done.IsFaulted) { var ignored = done.Exception; } }, TaskScheduler.Default);
                    break;
            }
        }
        private void PumpCdUploads(float now)
        {
            if (host)
            {
                RestoreUploadedCd();
                PumpHostCdPlayback(now);
                if (incomingCdUpload?.Expired(now) == true) EndCdUpload(CdUploadStatus.Rejected);
                if (incomingCdUpload is CdUploadTicket ticket && incomingCdCacheRead?.IsCompleted == true)
                {
                    byte[]? cached = null;
                    try { cached = incomingCdCacheRead.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
                    incomingCdCacheRead = null;
                    incomingCdCacheCancellation?.Dispose(); incomingCdCacheCancellation = null;
                    if (cached != null) StartUploadedCdDecode(ticket, cached);
                    else cdUploadReplies[ticket.Peer] = CdMediaMessage.UploadReply(ticket.Offer.TransferId, ticket.Offer.ContentHash, CdUploadStatus.Send);
                }
                if (cdUploadReplies.Count != 0)
                    foreach (var pair in cdUploadReplies.ToArray())
                        if (SendMedia(pair.Key, pair.Value, true)) cdUploadReplies.Remove(pair.Key);
                return;
            }
            if (channel?.FullySynchronized != true) return;
            if (guestCdUpload == null && manualCdTracks.Count != 0)
                guestCdUpload = new GuestCdUpload { Clip = manualCdTracks.Dequeue(), StartedAt = now };
            var upload = guestCdUpload;
            if (upload == null) return;
            if (now < upload.StartedAt || now - upload.StartedAt > CdUploadTicket.TimeoutSeconds)
            {
                NoteCd("manual CD upload timed out: hostAnswered=" + (upload.Read != null) + "; offerReady=" + (upload.Offer != null) + "; " + CdLink(0), true);
                WarnCdMedia("manual CD upload timed out; use Load CD to try again"); FinishGuestCdUpload(); return;
            }
            if (upload.Source == null)
            {
                upload.Source = InspectHostMedia(upload.Clip, PersistentCdMediaCache.MaximumBytes - manualCdBytes);
                if (upload.Source == null) { FinishGuestCdUpload(); return; }
            }
            CompleteHostHash(upload.Source);
            if (upload.Source.HashFailed) { FinishGuestCdUpload(); return; }
            if (!upload.Source.HashReady) return;
            if (upload.Offer == null)
            {
                if (manualCdHashes.Contains(Convert.ToBase64String(upload.Source.Hash))) { FinishGuestCdUpload(); return; }
                nextTransferId++; if (nextTransferId == 0) nextTransferId++;
                upload.Offer = CdMediaMessage.UploadOffer(nextTransferId, upload.Source.Hash, upload.Source.Length, upload.Source.DecodedBytes, upload.Source.Format);
                manualCdBytes += upload.Source.Length;
            }
            if (now < upload.NextAt) return;
            if (upload.Read == null)
            {
                if (SendMedia(0, upload.Offer, true)) upload.NextAt = now + 5f;
                return;
            }
            if (!upload.Read.IsCompleted) return;
            if (upload.Bytes == null)
            {
                try { upload.Bytes = upload.Read.GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                    ex is NotSupportedException || ex is System.Security.SecurityException || ex is OperationCanceledException || ex is System.Security.Cryptography.CryptographicException)
                { WarnCdMedia("manually loaded CD changed or could not be read; reload it to try again"); FinishGuestCdUpload(); return; }
            }
            if (upload.AwaitingStored)
            {
                // The host retains its reliable receipt under congestion. Do not
                // request a new slot while its decoder is finishing this one.
                return;
            }
            // Bounded batches remove the one-packet/FPS cap. Session byte pacing,
            // gameplay reserves and transport pressure can stop every iteration.
            for (int sent = 0; sent < CdMediaPacing.UploadPacketBurst && !upload.AwaitingStored; sent++)
            {
                var message = upload.PendingMessage ??= !upload.BeginSent ? CdMediaMessage.Begin(CdMediaMessage.UploadEntity, upload.Offer.TransferId,
                    upload.Offer.ContentHash, upload.Offer.FileBytes, upload.Offer.DecodedBytes, upload.Offer.Format) :
                    upload.NextChunk < upload.Offer.ChunkCount ? UploadChunk(upload) :
                    CdMediaMessage.Complete(CdMediaMessage.UploadEntity, upload.Offer.TransferId, upload.Offer.ContentHash);
                if (!SendMedia(0, message, true)) return;
                upload.PendingMessage = null;
                if (!upload.BeginSent) upload.BeginSent = true;
                else if (upload.NextChunk < upload.Offer.ChunkCount) upload.NextChunk++;
                else { upload.AwaitingStored = true; upload.NextAt = now + 5f; }
            }
        }
        private static CdMediaMessage UploadChunk(GuestCdUpload upload)
        {
            int offset = upload.NextChunk * CdMediaMessage.ChunkBytes;
            var bytes = new byte[Math.Min(CdMediaMessage.ChunkBytes, upload.Bytes!.Length - offset)];
            Buffer.BlockCopy(upload.Bytes, offset, bytes, 0, bytes.Length);
            return CdMediaMessage.Chunk(CdMediaMessage.UploadEntity, upload.Offer!.TransferId, upload.NextChunk, bytes);
        }
        private void FinishGuestCdUpload()
        { guestCdUpload = null; PruneHostMediaCache(); }
        private void DisposeCdUploads()
        {
            manualCdGeneration++; manualCdTracks.Clear(); manualCdHashes.Clear(); acknowledgedCdSources.Clear();
            EndCdUpload(CdUploadStatus.Rejected); guestCdUpload = null; cdUploadReplies.Clear(); uploadedCdSources.Clear();
        }
    }
}
