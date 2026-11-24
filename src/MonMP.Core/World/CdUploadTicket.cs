using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    // One admitted upload. Metadata and authenticated peer identity must match the
    // offer before the assembler can allocate or accept any chunk.
    public sealed class CdUploadTicket : IDisposable
    {
        public const float TimeoutSeconds = 1800f;
        public readonly uint Peer;
        public readonly CdMediaMessage Offer;
        private readonly CdMediaAssembler assembler = new CdMediaAssembler(TimeoutSeconds);
        private readonly float startedAt;
        private float lastProgress;
        private bool began;
        public bool Completed { get; private set; }
        public CdUploadTicket(uint peer, CdMediaMessage offer, float now)
        {
            if (peer == 0 || offer.Kind != CdMediaMessageKind.UploadOffer || float.IsNaN(now) || float.IsInfinity(now))
                throw new InvalidDataException("Invalid CD upload ticket.");
            Peer = peer; Offer = offer; startedAt = lastProgress = now;
        }
        public bool Matches(uint peer, CdMediaMessage message) => peer == Peer && message.RadioEntity == Offer.RadioEntity && message.TransferId == Offer.TransferId;
        public bool Expired(float now) => float.IsNaN(now) || float.IsInfinity(now) || now < startedAt ||
            now - startedAt > TimeoutSeconds || now - lastProgress > CdMediaAssembler.InactivityTimeoutSeconds;
        public bool Accept(uint peer, CdMediaMessage message, float now, out byte[]? bytes)
        {
            bytes = null;
            if (!Matches(peer, message) || Completed || Expired(now)) return false;
            if (message.Kind == CdMediaMessageKind.Begin)
            {
                if (began || message.FileBytes != Offer.FileBytes || message.DecodedBytes != Offer.DecodedBytes ||
                    message.Format != Offer.Format || !CdPlaybackState.HashEquals(message.ContentHash, Offer.ContentHash)) return false;
                began = true;
            }
            else if (!began || message.Kind != CdMediaMessageKind.Chunk && message.Kind != CdMediaMessageKind.Complete) return false;
            if (!assembler.Accept(message, Offer.RadioEntity, Offer.ContentHash, now, out bytes, out _, out _)) return false;
            lastProgress = now;
            Completed = bytes != null;
            return true;
        }
        public void Dispose() => assembler.Cancel();
    }
}
