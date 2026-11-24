using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Signed progression envelopes are submitted through the host's native post-office
    // flow. The network request carries only an exact item generation and action; price,
    // signature, affordability and delayed progression effects remain host-native.
    internal sealed class SharedHydroMail : IDisposable
    {
        private sealed class NativePost
        {
            public float Deadline;
        }

        private static readonly FieldInfo ReadyField = AccessTools.Field(typeof(Store_PostOffice), "ReadyToSendAnotherEnvelope");
        private const float RequestTimeout = 10f, NativeTimeout = 8f, PeerRange = 4.5f, MailboxItemRange = 3.5f;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly HydroMailClaims claims = new HydroMailClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), (ulong Envelope, HydroMailReply Reply)> results =
            new Dictionary<(Guid, Guid), (ulong, HydroMailReply)>();
        private readonly Dictionary<Guid, (ulong Envelope, float Deadline)> pendingRequests =
            new Dictionary<Guid, (ulong, float)>();
        private readonly List<NativePost> nativePosts = new List<NativePost>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        // Set while the host starts a guest's native post: its delayed confirmation belongs to the guest.
        internal static bool PostingForGuest { get; private set; }

        public bool NewActionsBlocked { get; set; }
        public bool HasUncertainOutcome { get; private set; }
        public bool TransactionsSettled => !HasUncertainOutcome && pendingRequests.Count == 0 && nativePosts.Count == 0;
        // Used by the post-office callback guard to avoid falling back to the native
        // guest-side cash mutation before this shared host route has been attached.
        internal bool CanRouteGuestPost => !host && !disposed && session?.IsRunning == true && channel != null;

#if MONMP_DEVELOPMENT
        internal int TestReplies { get; private set; }


        internal HydroMailReply? TestLastReply { get; private set; }


        internal int TestNativePosts => nativePosts.Count;
#endif


        public SharedHydroMail(bool host, SharedItems items)
        {
            this.host = host;
            this.items = items;
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.CommandRequested += Command;
            channel.CommandRejected += Stale;
            channel.ReplyReceived += Reply;
        }

        // Harmony returns true only when this call belongs to the local game or to the
        // validated host execution below. All guest-local managed posting is fail-closed.
        internal bool Intercept(Store_PostOffice source, Tool_Envelope envelope, out bool trackLocalNativeCall)
        {
            trackLocalNativeCall = false;
            if (disposed || session?.IsRunning != true || !IsManagedEnvelope(envelope) || applying) return false;
            ulong id = items.EntityOf(envelope);
            if (host)
            {
                if (id == 0 || !TryMailbox(source, out var postOffice, out Vector3 mailbox))
                    return Suppress("The host could not identify this envelope at the post office.");
                if (items.OwnedBy(id, 0))
                {
                    if (ActionsBlocked()) return Suppress("Mail posting is paused while the multiplayer save finishes.");
                    trackLocalNativeCall = NativeReady(postOffice!);
                    return false;
                }
                if (items.OwnedBy(id, ItemLease.Unowned) && !session.Peers.Keys.Any(peer => items.HostPeerNear(peer, mailbox, PeerRange)))
                {
                    if (ActionsBlocked()) return Suppress("Mail posting is paused while the multiplayer save finishes.");
                    trackLocalNativeCall = NativeReady(postOffice!);
                    return false;
                }
                // A remote-owned envelope can overlap the host's trigger; its owner must
                // submit the request so the host can verify that peer's position and lease.
                return true;
            }

            uint generation;
            bool hasLease;
            if (envelope.envelope_Type == Envelope_Type.Police_Ticket)
                hasLease = items.TryGetLocalPoliceEnvelope(envelope, out id, out generation);
            else
                hasLease = items.TryGetLocalHydroEnvelope(envelope, out id, out generation);
            if (id == 0 || !hasLease)
                return Suppress("Wait for the envelope lease to synchronize before posting it.");
            if (channel?.FullySynchronized != true) return Suppress("Wait for the multiplayer world to finish synchronizing before posting this letter.");
            if (ActionsBlocked()) return Suppress("Mail posting is paused while the multiplayer save finishes.");
            if (pendingRequests.Count >= 16) return Suppress("Wait for the pending letter to finish.");

            Guid operation = Guid.NewGuid();
            pendingRequests.Add(operation, (id, Time.unscaledTime + RequestTimeout));
            channel!.Request(id, new HydroMailRequest(operation, id, generation, KindOf(envelope)).Encode(), true);
            return true;
        }

        private bool ActionsBlocked() => NewActionsBlocked || HasUncertainOutcome || pendingRequests.Values.Any(request => float.IsPositiveInfinity(request.Deadline)) || MonMPPlugin.Instance?.World?.SaveGate.Active == true;
        private static bool IsManagedEnvelope(Tool_Envelope envelope) => envelope != null &&
            (envelope.envelope_Type == Envelope_Type.Hydro_Facture || envelope.envelope_Type == Envelope_Type.Hydro_StartConstruction ||
             envelope.envelope_Type == Envelope_Type.Police_Ticket);
        private static HydroMailKind KindOf(Tool_Envelope envelope) => envelope.envelope_Type switch
        {
            Envelope_Type.Hydro_Facture => HydroMailKind.Bill,
            Envelope_Type.Hydro_StartConstruction => HydroMailKind.StartConstruction,
            Envelope_Type.Police_Ticket => HydroMailKind.PoliceTicket,
            _ => throw new InvalidDataException("Unsupported managed envelope type.")
        };

        // The post office's reception box, where letters are posted. LocationMailBoxPlayer is the
        // player's home mailbox, where letters arrive: measuring a guest at the post office against
        // it failed every guest post with "move closer" (#204).
        private static PostOffice_ReceptionBox? receptionBox;
        private static bool TryMailbox(Store_PostOffice? source, out Store_PostOffice? postOffice, out Vector3 position)
        {
            postOffice = source != null ? source : UnityEngine.Object.FindObjectOfType<Store_PostOffice>();
            if (receptionBox == null) receptionBox = UnityEngine.Object.FindObjectOfType<PostOffice_ReceptionBox>();
            position = receptionBox != null ? receptionBox.transform.position : Vector3.zero;
            return postOffice != null && receptionBox != null;
        }

        private static bool NativeReady(Store_PostOffice postOffice) => (bool)ReadyField.GetValue(postOffice)!;

        private bool Suppress(string message)
        {
            MonMPPlugin.Instance?.ReportItemUseUnavailable(message);
            return true;
        }

        private void Stale(uint peer, ulong id, byte[] payload) => Command(peer, id, payload, true);
        [MonMP.Core.World.WorldCommandAdapters(ItemState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload) => Command(peer, id, payload, false);

        private void Command(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || disposed || session?.IsHost != true || channel == null || !session.Peers.TryGetValue(peer, out var actor)) return;
            if (HydroMailSign.Is(payload)) { if (!stale) Sign(peer, id, payload); return; }
            HydroMailRequest request;
            try { request = HydroMailRequest.Decode(payload); }
            catch (InvalidDataException) { return; }

            var key = (actor.ProfileId, request.Operation);
            if (results.TryGetValue(key, out var prior))
            {
                channel.Reply(peer, id, (prior.Envelope == id ? prior.Reply : new HydroMailReply(request.Operation, id, HydroMailStatus.RetryRejected)).Encode());
                return;
            }

            HydroMailStatus? rejection = null;
            if (request.Envelope != id || stale) rejection = HydroMailStatus.Stale;
            var candidate = items.ItemOf(id) as Tool_Envelope;
            bool authorizedLease = request.Kind == HydroMailKind.PoliceTicket
                ? items.HostAuthorizePoliceEnvelope(peer, id, request.Generation, out _)
                : items.HostAuthorizeHydroEnvelope(peer, id, request.Generation, out _);
            if (!authorizedLease || candidate == null)
                rejection ??= HydroMailStatus.Stale;
            var envelope = candidate;
            if (envelope != null && (!IsManagedEnvelope(envelope) || KindOf(envelope) != request.Kind)) rejection ??= HydroMailStatus.Unsupported;
            if (envelope != null && (!envelope.NeedASignature || !envelope.CanBePosted)) rejection ??= HydroMailStatus.Unsupported;
            if (envelope != null && envelope.IntValue1 != 1) rejection ??= HydroMailStatus.Unsigned;

            var postOffice = UnityEngine.Object.FindObjectOfType<Store_PostOffice>();
            if (postOffice == null || envelope == null || !TryMailbox(postOffice, out _, out Vector3 mailbox))
                rejection ??= HydroMailStatus.Unsupported;
            else if (!items.HostPeerNear(peer, mailbox, PeerRange) || !items.HostItemInRange(peer, id, PeerRange) ||
                !items.HostItemNear(id, mailbox, MailboxItemRange))
                rejection ??= HydroMailStatus.TooFar;
            else if (!NativeReady(postOffice)) rejection ??= HydroMailStatus.Busy;

            var hydro = UnityEngine.Object.FindObjectOfType<Store_HydroSaintClin>();
            var police = UnityEngine.Object.FindObjectOfType<Store_PoliceInterceptor>();
            var cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            if (cash == null || (request.Kind != HydroMailKind.PoliceTicket && hydro == null) ||
                (request.Kind == HydroMailKind.PoliceTicket && police == null)) rejection ??= HydroMailStatus.Unsupported;
            else if (request.Kind == HydroMailKind.StartConstruction && hydro!.Get_DaySinceBuildStarted != -1)
                rejection ??= HydroMailStatus.NativeRejected;
            else if (request.Kind == HydroMailKind.Bill && hydro!.Get_BillToPay <= 0f)
                rejection ??= HydroMailStatus.NativeRejected;
            else if (request.Kind == HydroMailKind.PoliceTicket && police!.Get_BillToPay <= 0f)
                rejection ??= HydroMailStatus.NativeRejected;

            float hostBill = envelope == null ? 0f : envelope.PriceToPayWhenShippedBack;
            if (envelope != null && request.Kind == HydroMailKind.PoliceTicket && police != null &&
                (!FinitePrice(police.Get_BillToPay) || Math.Abs(hostBill - police.Get_BillToPay) > 0.01f))
                rejection ??= HydroMailStatus.Stale;
            if (envelope != null && (!FinitePrice(hostBill) || cash == null || !cash.isCanAfford(hostBill)))
                rejection ??= HydroMailStatus.NoFunds;

            if (ActionsBlocked()) rejection ??= HydroMailStatus.SaveBlocked;
            if (nativePosts.Count > 0) rejection ??= HydroMailStatus.Busy;
            if (rejection.HasValue)
            {
                Finish(peer, actor.ProfileId, id, new HydroMailReply(request.Operation, id, rejection.Value));
                return;
            }

            // A rejected or unaffordable request never consumes an operation claim.
            // Once claimed, the host calls the native flow at most once; its coroutine
            // later runs LetterShipped/Pay_Bill and applies ticket penalties normally.
            var claim = claims.Claim(actor.ProfileId, request.Operation);
            if (claim != HydroMailClaimResult.Claimed)
            {
                var status = claim == HydroMailClaimResult.Capacity ? HydroMailStatus.Capacity : HydroMailStatus.RetryRejected;
                channel.Reply(peer, id, new HydroMailReply(request.Operation, id, status).Encode());
                return;
            }

            try
            {
                applying = true;
                bool wasReady = NativeReady(postOffice!);
                PostingForGuest = true;
                try { postOffice!.TryingToSendThisLetter(envelope!); }
                finally { PostingForGuest = false; }
                applying = false;
                if (wasReady && !NativeReady(postOffice))
                {
                    nativePosts.Add(new NativePost { Deadline = Time.unscaledTime + NativeTimeout });
                    Finish(peer, actor.ProfileId, id, new HydroMailReply(request.Operation, id, HydroMailStatus.Accepted));
                }
                else Finish(peer, actor.ProfileId, id, new HydroMailReply(request.Operation, id, HydroMailStatus.NativeRejected));
            }
            catch (Exception ex)
            {
                applying = false;
                HasUncertainOutcome = true;
                Debug.LogException(ex);
                Finish(peer, actor.ProfileId, id, new HydroMailReply(request.Operation, id, HydroMailStatus.Uncertain));
            }
        }

        // Guest: the native click signed this letter locally. Guest-held item values never travel
        // back, so the host's next snapshot erased the signature a moment later (#204); the host signs its copy.
        internal void LocalSigned(Tool_Envelope envelope)
        {
            if (host || disposed || applying || session?.IsRunning != true || channel == null || !IsManagedEnvelope(envelope)) return;
            ulong id; uint generation;
            bool leased = envelope.envelope_Type == Envelope_Type.Police_Ticket
                ? items.TryGetLocalPoliceEnvelope(envelope, out id, out generation)
                : items.TryGetLocalHydroEnvelope(envelope, out id, out generation);
            if (!leased || id == 0) return;
            channel.Request(id, new HydroMailSign(id, generation).Encode(), true);
        }

        private void Sign(uint peer, ulong id, byte[] payload)
        {
            HydroMailSign sign;
            try { sign = HydroMailSign.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (sign.Envelope != id || !(items.ItemOf(id) is Tool_Envelope envelope) || !IsManagedEnvelope(envelope) ||
                !envelope.NeedASignature || envelope.IntValue1 == 1) return;
            bool authorized = envelope.envelope_Type == Envelope_Type.Police_Ticket
                ? items.HostAuthorizePoliceEnvelope(peer, id, sign.Generation, out _)
                : items.HostAuthorizeHydroEnvelope(peer, id, sign.Generation, out _);
            if (!authorized || !items.HostItemInRange(peer, id, PeerRange)) return;
            envelope.IntValue1 = 1;
            items.HostPublishItem(id);
        }

        private static bool FinitePrice(float price) => !float.IsNaN(price) && !float.IsInfinity(price) && price > 0 && price <= 10000000f;

        private void Finish(uint peer, Guid profile, ulong id, HydroMailReply reply)
        {
            results[(profile, reply.Operation)] = (id, reply);
            channel!.Reply(peer, id, reply.Encode());
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || !HydroMailReply.Is(payload)) return;
            HydroMailReply result;
            try { result = HydroMailReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!pendingRequests.TryGetValue(result.Operation, out var request) || request.Envelope != id) return;
            pendingRequests.Remove(result.Operation);

#if MONMP_DEVELOPMENT
            TestLastReply = result;

 TestReplies++;
#endif

            if (result.Status == HydroMailStatus.Accepted)
            {
                if (items.ItemOf(id) is Tool_Envelope envelope) { items.Present(id); SalePresentation.PresentPost(envelope); }
                return;
            }
            if (result.Status == HydroMailStatus.Uncertain) HasUncertainOutcome = true;
            MonMPPlugin.Instance?.ReportItemUseUnavailable(Message(result.Status));
        }

        private static string Message(HydroMailStatus status)
        {
            switch (status)
            {
                case HydroMailStatus.Unsigned: return "Sign the letter before posting it.";
                case HydroMailStatus.TooFar: return "Move closer to the post office mailbox before posting the hydro letter.";
                case HydroMailStatus.NoFunds: return "Not enough shared cash to post this letter.";
                case HydroMailStatus.Busy: return "The host is already processing another letter.";
                case HydroMailStatus.SaveBlocked: return "Mail posting is paused while the multiplayer save finishes.";
                case HydroMailStatus.Unsupported: return "The host could not process this letter.";
                case HydroMailStatus.Capacity: return "This session cannot accept more hydro mail transactions.";
                case HydroMailStatus.Uncertain: return "The host could not confirm whether this letter was posted.";
                case HydroMailStatus.NativeRejected: return "The host's mail or bill system declined this letter.";
                case HydroMailStatus.Stale: return "This envelope changed. Pick it up again and retry.";
                default: return "This letter request was already handled.";
            }
        }

        internal void NativeCallFinished(bool track)
        {
            if (!track || !host) return;
            var postOffice = UnityEngine.Object.FindObjectOfType<Store_PostOffice>();
            if (postOffice != null && !NativeReady(postOffice)) nativePosts.Add(new NativePost { Deadline = Time.unscaledTime + NativeTimeout });
        }

        private Store_PostOffice? postOffice;
        private float nextPostOfficeLookup;
        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (!host)
            {
                if (pendingRequests.Count > 0) foreach (var request in pendingRequests.Where(p => Time.unscaledTime >= p.Value.Deadline).ToArray())
                {
                    // A transport delay is not a native failure. Retain the bounded operation
                    // identity so a late definitive reply can settle it; do not submit it again.
                    pendingRequests[request.Key] = (request.Value.Envelope, float.PositiveInfinity);
                    MonMPPlugin.Instance?.ReportItemUseUnavailable("Mail confirmation is delayed. Waiting for the host's result before another post.");
                }
                return;
            }

            // A scene-wide search every frame cost milliseconds on large saves; look up again only
            // when the cached post office is gone, at most every two seconds.
            if (postOffice == null && Time.unscaledTime >= nextPostOfficeLookup)
            {
                nextPostOfficeLookup = Time.unscaledTime + 2;
                postOffice = UnityEngine.Object.FindObjectOfType<Store_PostOffice>();
            }
            if (postOffice == null) return;
            if (NativeReady(postOffice)) nativePosts.Clear();
            else if (nativePosts.Any(p => Time.unscaledTime >= p.Deadline)) HasUncertainOutcome = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            { channel.CommandRequested -= Command; channel.CommandRejected -= Stale; channel.ReplyReceived -= Reply; }
            pendingRequests.Clear(); nativePosts.Clear(); session = null; channel = null;
        }
    }
}
