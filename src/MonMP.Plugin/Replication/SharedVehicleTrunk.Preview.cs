using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicleTrunk
    {
        private static readonly FieldInfo OpeningAngleField = AccessTools.Field(typeof(Vehicle_Trunk), "MaxOpeningAngle");
        private static readonly FieldInfo TailgateField = AccessTools.Field(typeof(Vehicle_Trunk), "isTailgate");
        private static readonly FieldInfo LockedOpenField = AccessTools.Field(typeof(Vehicle_Trunk), "isLockedFullyOpen");
        private ulong nextLocalIntent;
        private readonly Dictionary<(uint Peer, ulong Hinge), (ulong Token, bool Goal, ClientInteractionStatus Status)> trunkDecisions =
            new Dictionary<(uint, ulong), (ulong, bool, ClientInteractionStatus)>();

        private static float OpenPreviewAngle(Binding b)
        {
            float angle = (float)OpeningAngleField.GetValue(b.Trunk);
            return Mathf.Clamp((bool)TailgateField.GetValue(b.Trunk) ? -Math.Abs(angle) : angle, -180f, 180f);
        }
        private static void PresentLocalIntent(Binding b)
        {
            if (b.Host) return;
            // Only the replica's affordance/transform is predicted. Never invoke
            // Release/CloseTrunk or create a client hinge/body to obtain feedback.
            if (CloseButtonField?.GetValue(b.Trunk) is GameObject closeButton && closeButton != null)
                closeButton.SetActive(b.Intent.PresentationOpen(b.State.Open));
        }
        private static bool InHandleRange(Binding b, Vector3 location)
        {
            foreach (var handle in b.Handles)
                if (handle != null && handle.isActiveAndEnabled && handle.gameObject.activeInHierarchy &&
                    (location - handle.transform.position).sqrMagnitude <= 16) return true;
            return b.Static && b.Handles.Length == 0 && (location - b.Trunk.transform.position).sqrMagnitude <= 16;
        }
        private void SendIntent(Binding b)
        {
            if (host || !b.Intent.Active || b.Intent.Token != 0 || b.Intent.WaitingForLatch ||
                channel?.FullySynchronized != true || b.DeferReplica?.Invoke() == true) return;
            if (++nextLocalIntent == 0) ++nextLocalIntent;
            if (!b.Intent.TryBegin(nextLocalIntent, b.State.Open, Time.unscaledTime)) return;
            byte[] native = b.Late ? new byte[9] : new byte[1];
            if (b.Late) BitConverter.GetBytes(b.Id).CopyTo(native, 0);
            native[native.Length - 1] = b.Intent.SentOpen ? (byte)1 : (byte)0;
            channel.Request(b.Late ? LateEntity : b.Id, new ClientInteractionRequest(b.Intent.Token, native).Encode(), true);
        }
        [WorldCommandAdapters(VehicleTrunkState.Adapter, SharedVehicleTrunk.LateAdapter)]
        private void Rejected(uint peer, ulong id, byte[] payload)
        { if (host) ClientCommand(peer, id, payload, true); }

        private void ClientCommand(uint peer, ulong entity, byte[] bytes, bool stale)
        {
            if (!host || channel?.Ready != true || !ClientInteractionRequest.Is(bytes)) return;
            ClientInteractionRequest request;
            try { request = ClientInteractionRequest.Decode(bytes); } catch (InvalidDataException) { return; }
            bool late = entity == LateEntity;
            if (request.Payload.Length != (late ? 9 : 1) || request.Payload[request.Payload.Length - 1] > 1) return;
            ulong id = late ? BitConverter.ToUInt64(request.Payload, 0) : entity;
            if (!bindings.TryGetValue(id, out var b) || b.Late != late || b.Trunk == null) return;
            bool goal = request.Payload[request.Payload.Length - 1] != 0;
            var key = (peer, id);
            if (trunkDecisions.TryGetValue(key, out var previous) && request.Token <= previous.Token)
            {
                var replay = request.Token == previous.Token && goal == previous.Goal ? previous.Status : ClientInteractionStatus.Stale;
                ReplyIntent(peer, b, request.Token, replay); return;
            }
            var status = ClientInteractionStatus.Refused;
            if (stale) status = ClientInteractionStatus.Stale;
            else if ((b.Button != null || b.Static) && b.Trunk.gameObject.activeInHierarchy &&
                MonMPPlugin.Instance?.World?.SaveGate.Active != true &&
                presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2 &&
                InHandleRange(b, new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z)))
            {
                if (!b.Trunk.Get_IsOpen || Time.unscaledTime >= b.ClosingUntil) b.ClosingInFlight = false;
                if (goal && b.ClosingInFlight)
                {
                    // Do not acknowledge a reopen while a different player's
                    // confirmed close is still physically traveling to its latch.
                    trunkDecisions[key] = (request.Token, goal, status);
                    ReplyIntent(peer, b, request.Token, status); return;
                }
                // An absolute goal makes two clients opening the same closed lid
                // idempotent. Native close remains a host impulse until its latch.
                if (b.Trunk.Get_IsOpen != goal && (goal || !b.ClosingInFlight))
                {
                    bool lockedOpen = (bool)LockedOpenField.GetValue(b.Trunk);
                    bool previousApplying = applying; applying = true;
                    try { b.Trunk.Release(); } finally { applying = previousApplying; }
                    // Native CloseTrunk on a lid still opening is another lift
                    // impulse, not a close. Preserve that behavior without hiding
                    // its real open state behind an eight-second close preview.
                    b.ClosingInFlight = !goal && lockedOpen && b.Trunk.Get_IsOpen;
                    if (b.ClosingInFlight) b.ClosingUntil = Time.unscaledTime + (float)DoorPresentationIntent.TimeoutSeconds;
                }
                status = goal && !b.Trunk.Get_IsOpen ? ClientInteractionStatus.Refused : ClientInteractionStatus.Accepted;
            }
            trunkDecisions[key] = (request.Token, goal, status);
            ReplyIntent(peer, b, request.Token, status);
        }
        private void ReplyIntent(uint peer, Binding b, ulong token, ClientInteractionStatus status)
        {
            if (!b.Trunk.Get_IsOpen || Time.unscaledTime >= b.ClosingUntil) b.ClosingInFlight = false;
            Publish(b);
            ulong entity = b.Late ? LateEntity : b.Id;
            if (!channel!.Records.TryGetValue(entity, out var record)) return;
            var payload = new byte[14]; BitConverter.GetBytes(b.Id).CopyTo(payload, 0); b.State.Encode().CopyTo(payload, 8);
            payload[13] = b.ClosingInFlight ? (byte)1 : (byte)0;
            channel.Reply(peer, entity, new ClientInteractionReceipt(token, status, record.Revision, payload).Encode());
        }
        private void Reply(ulong entity, byte[] payload)
        {
            // Replies reach every adapter: a trunk receipt is exactly a 14-byte header and 14-byte state.
            if (host || !ClientInteractionReceipt.Is(payload) || payload.Length != 28) return;
            var receipt = ClientInteractionReceipt.Decode(payload);
            if (receipt.State.Length != 14 || receipt.State[13] > 1) return;
            ulong id = BitConverter.ToUInt64(receipt.State, 0);
            if (!bindings.TryGetValue(id, out var b) || (b.Late ? LateEntity : b.Id) != entity || b.Intent.Token != receipt.Token) return;
            var state = VehicleTrunkState.Decode(receipt.State.AsSpan(8, 5).ToArray());
            bool opening = b.Intent.SentOpen;
            bool accepted = receipt.Status == ClientInteractionStatus.Accepted;
            bool newerAuthority = receipt.Revision < b.LocalRevision;
            if (newerAuthority)
            {
                state = b.State;
                // A later closed state supersedes an old successful open. Native
                // closes remain Open until latch, so a confirmed close still waits
                // for that latch before a queued reopen can send.
                if (accepted && opening && !state.Open)
                { b.Intent.Clear(); b.PreviewHandoffUntil = 0; PresentLocalIntent(b); return; }
            }
            b.Intent.Acknowledge(receipt.Token, accepted, state.Open, Time.unscaledTime,
                // A newer Open=true sample may just be the closing hinge in motion.
                // Only an observed closed state after this receipt proves its latch
                // already completed; a subsequent reopen must not restart that wait.
                waitForLatch: !opening && receipt.State[13] == 1 && b.LastClosedRevision <= receipt.Revision);
            b.PreviewHandoffUntil = accepted && opening && state.Open && !newerAuthority ? Time.unscaledTime + 0.5f : 0;
            Apply(b, state, false, Math.Max(receipt.Revision, b.LocalRevision));
            SendIntent(b);
        }
    }
}
