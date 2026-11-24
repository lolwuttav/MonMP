using System.Collections.Generic;
using System.IO;
using System;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedFurnitureRadios
    {
        private readonly Dictionary<uint, (CdTransferProgress Counts, float At)> cdPeerProgress =
            new Dictionary<uint, (CdTransferProgress, float)>();
        private float nextCdProgressAt, lastCdProgressAt = -10;
        private CdTransferProgress lastCdProgress;

        private bool ReceiveCdTransferProgress(uint peer, byte[] payload)
        {
            if (!CdTransferProgress.Is(payload)) return false;
            if (host && mediaPeersReady.Contains(peer))
            {
                try { cdPeerProgress[peer] = (CdTransferProgress.Decode(payload), Time.unscaledTime); }
                catch (InvalidDataException) { }
            }
            return true;
        }

        private (int Uploads, int Downloads) CdQueueCounts(float now)
        {
            if (host)
            {
                int uploads = 0, downloads = 0;
                foreach (var progress in cdPeerProgress.Values)
                    if (now - progress.At <= 15f)
                    {
                        uploads += progress.Counts.Downloads;
                        downloads += progress.Counts.Uploads;
                    }
                return (Math.Max(uploads, outboundMedia.Count), Math.Max(downloads, incomingCdUpload != null ? 1 : 0));
            }
            int remaining = 0;
            foreach (string key in playlistOffers.Keys)
                if (!preloadReady.Contains(key) && !preloadFailed.Contains(key) && !rejectedMedia.Contains(key) &&
                    !transferredMedia.ContainsKey(key)) remaining++;
            if (guestRequestEntity != 0 && guestRequestedHash != null)
            {
                string current = Convert.ToBase64String(guestRequestedHash);
                if (!playlistOffers.ContainsKey(current) && !preloadReady.Contains(current) &&
                    !rejectedMedia.Contains(current) && !transferredMedia.ContainsKey(current)) remaining++;
            }
            return (manualCdTracks.Count + (guestCdUpload != null ? 1 : 0), remaining);
        }

        private void PublishCdTransferProgress(float now, int uploads, int downloads)
        {
            if (host || channel?.FullySynchronized != true || now < nextCdProgressAt) return;
            nextCdProgressAt = now + 1f;
            if (uploads == lastCdProgress.Uploads && downloads == lastCdProgress.Downloads && now - lastCdProgressAt < 5f) return;
            foreach (ulong id in byCdId.Keys)
            {
                if (!channel.Records.ContainsKey(id)) continue;
                var progress = new CdTransferProgress(uploads, downloads);
                try { channel.Request(id, progress.Encode(), useLease: true); }
                catch (InvalidDataException) { return; }
                lastCdProgress = progress; lastCdProgressAt = now;
                return;
            }
        }
    }
}
