using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicleHood
    {
        private ulong nextPreviewToken;
        private readonly Dictionary<(uint Peer, ulong Entity), (ulong Token, byte Action, byte Flags, ClientInteractionStatus Status)> hoodDecisions =
            new Dictionary<(uint, ulong), (ulong, byte, byte, ClientInteractionStatus)>();
        private static byte Flags(VehicleHoodState state) => (byte)((state.Released ? 1 : 0) | (state.Open ? 2 : 0));
        private static bool InstalledAndActive(Binding b) => b.Hood != null && b.Hood.isActiveAndEnabled && b.Hood.gameObject.activeInHierarchy &&
            b.Hood.vehicle_PartsManager?.Door_Hood?.isInstalled == true;
        private static IEnumerable<MonoBehaviour> ActiveHandles(Binding b, byte action)
        {
            var handles = action == 0 ? b.ReleaseHandles : action == 1 ? b.OpenHandles : b.CloseHandles;
            foreach (var handle in handles)
                if (handle != null && handle.isActiveAndEnabled && handle.gameObject.activeInHierarchy &&
                    (handle is Vehicle_ExteriorHoodRelease exterior ? exterior.vehicle_Hood == b.Hood :
                     handle is Vehicle_InteriorHoodRelease interior && interior.vehicle_ExteriorHoodRelease?.vehicle_Hood == b.Hood)) yield return handle;
        }
        private static void RefreshPreview(Binding b)
        {
            var visual = b.Host ? b.State : b.Preview.Presented(b.Authority);
            b.TargetAngle = visual.Angle;
            // Guest flags serve native handle hover/CanUse only. All mutation and hinge physics
            // still route to the host; command expectations always use b.Authority.
            IsReleasedField.SetValue(b.Hood, visual.Released); IsOpenField.SetValue(b.Hood, visual.Open);
            (CloseButtonField.GetValue(b.Hood) as GameObject)?.SetActive(visual.Open);
        }
        private void SendPreview(Binding b)
        {
            if (host || !b.Preview.CanSend || channel?.FullySynchronized != true || b.DeferReplica?.Invoke() == true || !InstalledAndActive(b)) return;
            if (++nextPreviewToken == 0) ++nextPreviewToken;
            if (!b.Preview.TryBegin(nextPreviewToken, b.Authority, b.Revision, Time.unscaledTime)) return;
            channel.Request(b.Id, new ClientInteractionRequest(b.Preview.Token,
                new[] { b.Preview.SentAction, Flags(b.Authority), b.Preview.SentCuePlayed ? (byte)1 : (byte)0 }).Encode(), true);
        }
        private void AcceptState(Binding b, VehicleHoodState state, uint revision, bool snap)
        {
            if (revision < b.Revision) return;
            b.Revision = revision;
            b.Authority = state;
            if (!state.Open) b.LastClosedRevision = revision;
            Apply(b, state, snap);
        }
        private void Rejected(uint peer, ulong id, byte[] payload)
        { if (host && ClientInteractionRequest.Is(payload)) ClientCommand(peer, id, payload, true); }
        private void ClientCommand(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || channel?.Ready != true || !bindings.TryGetValue(id, out var b) || b.Hood == null) return;
            var request = ClientInteractionRequest.Decode(payload);
            if (request.Payload.Length != 3 || request.Payload[0] > 2 || request.Payload[1] > 3 || request.Payload[1] == 2 || request.Payload[2] > 1) return;
            byte action = request.Payload[0], flags = request.Payload[1];
            bool preplayed = request.Payload[2] == 1;
            var key = (peer, id);
            if (hoodDecisions.TryGetValue(key, out var previous) && request.Token <= previous.Token)
            {
                ReplyPreview(peer, b, request.Token, request.Token == previous.Token && action == previous.Action && flags == previous.Flags
                    ? previous.Status : ClientInteractionStatus.Stale); return;
            }
            var status = stale ? ClientInteractionStatus.Stale : ClientInteractionStatus.Refused;
            if (!stale && InstalledAndActive(b) && MonMPPlugin.Instance?.World?.SaveGate.Active != true &&
                presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2 &&
                ActiveHandles(b, action).Any(handle => (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - handle.transform.position).sqrMagnitude <= 16))
            {
                var before = NativeState(b);
                if (Flags(before) != flags) status = ClientInteractionStatus.Stale;
                else if (action == 0 ? !before.Released && !before.Open : action == 1 ? before.Released : before.Open)
                {
                    bool previousApplying = applying; applying = true;
                    try { if (action == 0) b.Hood.Release(); else if (action == 1) b.Hood.Open(); else b.Hood.Close(); }
                    finally { applying = previousApplying; }
                    status = ClientInteractionStatus.Accepted;
                    var after = NativeState(b);
                    if (action == 0 ? !before.Released && after.Released : action == 1 && !before.Open && after.Open)
                        Announce(b, action, preplayed ? peer : 0);
                }
            }
            hoodDecisions[key] = (request.Token, action, flags, status);
            ReplyPreview(peer, b, request.Token, status);
        }
        private void ReplyPreview(uint peer, Binding b, ulong token, ClientInteractionStatus status)
        {
            Publish(b);
            channel!.Reply(peer, b.Id, new ClientInteractionReceipt(token, status, channel.Records[b.Id].Revision, b.State.Encode()).Encode());
        }
        private void Reply(ulong id, byte[] payload)
        {
            if (host || !ClientInteractionReceipt.Is(payload) || !bindings.TryGetValue(id, out var b) || b.Hood == null) return;
            var receipt = ClientInteractionReceipt.Decode(payload);
            if (b.Preview.Token == 0 || b.Preview.Token != receipt.Token) return;
            var state = VehicleHoodState.Decode(receipt.State);
            bool current = receipt.Revision >= b.Revision;
            var authority = current ? state : b.Authority;
            b.Preview.Acknowledge(receipt.Token, receipt.Status == ClientInteractionStatus.Accepted, authority,
                receipt.Revision, b.Revision, b.LastClosedRevision, Time.unscaledTime);
            if (current) AcceptState(b, state, receipt.Revision, false);
            else RefreshPreview(b);
            SendPreview(b);
            RefreshPreview(b);
        }
        private static void PlayActionSound(Binding b, byte action)
        {
            var sound = b.Hood.GetComponent<SOUND_Vehicle_Hood>();
            if (sound == null) return;
            if (action == 0) sound.Release(); else if (action == 1) sound.Open();
        }
        private static void Announce(Binding b, byte action, uint actor) => PresentationReplays.Events?.HostAnnounce(
            PresentationKind.HoodAction, action, b.Id, b.Hood.transform.position, 0, actor, playHere: false);
        internal void PlayAnnounced(PresentationEvent cue)
        {
            if (host || disposed || session?.IsRunning != true || cue.Kind != (byte)PresentationKind.HoodAction || cue.Variant > 1 ||
                cue.Value != 0 || !bindings.TryGetValue(cue.Target, out var b) || !InstalledAndActive(b)) return;
            PlayActionSound(b, cue.Variant);
        }
    }
}
