using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicleWindows
    {
        private static readonly FieldInfo VehicleField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "vc");
        private static readonly FieldInfo PartsField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "PartsManager");
        private static readonly FieldInfo ManualField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "IsManualWindow");
        private static readonly FieldInfo DownField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "DownLocation");
        private static readonly FieldInfo UpPositionField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "UpPosition");
        private ulong nextIntent;
        private readonly Dictionary<(uint Peer, ulong Window), (ulong Token, bool Down, ClientInteractionStatus Status)> windowDecisions =
            new Dictionary<(uint, ulong), (ulong, bool, ClientInteractionStatus)>();
        private sealed class WindowPreview
        {
            internal bool Down, Confirmed;
            internal ItemPose From;
            internal float Until;
            internal readonly WindowTravelPresentation Travel = new WindowTravelPresentation();
        }

        private static bool CanMoveNative(Binding b) => b.NativeVehicle != null && b.DownLocation != null &&
            (b.NativeVehicle.vehicle_type == Vehicle_Type.OlTruck || (b.Button.Get_IsDriverWindow
                ? b.Parts?.Door_Left?.isInstalled == true : b.Parts?.Door_Right?.isInstalled == true)) &&
            (b.Manual || b.NativeVehicle.Get_IgnitionPosition >= 1);

        private static void BeginPreview(Binding b, bool down)
        {
            CancelPreview(b);
            if (!CanMoveNative(b)) return;
            b.Preview = new WindowPreview { Down = down, From = Local(b.Window), Until = Time.unscaledTime + (float)DoorPresentationIntent.TimeoutSeconds };
            if (!b.Motor!.isPlaying) b.Motor.Play();
        }
        private static void CancelPreview(Binding b, bool resumeAuthority = false)
        {
            if (b.Preview == null) return;
            b.Preview = null;
            // Raw source control cannot relay a new native action or switch click.
            if (resumeAuthority && b.State.MotorPlaying && b.Motor != null)
            { if (!b.Motor.isPlaying) b.Motor.Play(); return; }
            if (b.Motor != null && b.Motor.isPlaying) b.Motor.Pause();
        }
        private static bool UpdatePreview(Binding b)
        {
            var preview = b.Preview;
            if (preview == null) return false;
            if (!CanMoveNative(b) || Time.unscaledTime >= preview.Until)
            { b.Intent.Clear(); b.MotorIntent = 0; CancelPreview(b); return false; }
            float elapsed = preview.Travel.Advance(Time.deltaTime);
            var targetPosition = preview.Down ? b.DownLocation!.localPosition : b.UpPosition;
            var targetRotation = preview.Down ? b.DownLocation!.localRotation : b.UpRotation;
            b.Window.localPosition = Vector3.Lerp(new Vector3(preview.From.X, preview.From.Y, preview.From.Z), targetPosition, preview.Travel.Progress);
            b.Window.localRotation = Quaternion.Lerp(new Quaternion(preview.From.Qx, preview.From.Qy, preview.From.Qz, preview.From.Qw), targetRotation, preview.Travel.Progress);
            if (b.Manual && b.Lever != null && elapsed > 0)
                b.Lever.Rotate(Vector3.right * ((preview.Down ? 1 : -1) * elapsed * WindowTravelPresentation.CrankDegreesPerSecond), Space.Self);
            if (preview.Travel.Complete && b.Motor!.isPlaying) b.Motor.Pause();
            if (preview.Confirmed && preview.Travel.Complete && Matches(b.Window, b.State.Window))
            { b.MotorIntent = 0; CancelPreview(b, resumeAuthority: true); return false; }
            b.SawPresentationInterpolation = true;
            return true;
        }
        private void SendIntent(Binding b)
        {
            if (host || channel?.FullySynchronized != true || !b.Intent.Active || b.Intent.Token != 0) return;
            if (++nextIntent == 0) ++nextIntent;
            if (!b.Intent.TryBegin(nextIntent, b.State.TargetDown, Time.unscaledTime))
            {
                if (!b.Intent.Active && b.Preview != null && b.Preview.Down == b.State.TargetDown) b.Preview.Confirmed = true;
                return;
            }
            b.MotorIntent = b.Intent.Token; b.MotorIntentDown = b.Intent.SentOpen;
            channel.Request(b.Id, new ClientInteractionRequest(b.Intent.Token, new[] { b.Intent.SentOpen ? (byte)1 : (byte)0 }).Encode(), true);
        }
        [WorldCommandAdapters(VehicleWindowState.Adapter)]
        private void Rejected(uint peer, ulong id, byte[] payload) => ClientCommand(peer, id, payload, true);

        private void ClientCommand(uint peer, ulong id, byte[] payload, bool stale)
        {
            if (!host || !bindings.TryGetValue(id, out var b) || !ClientInteractionRequest.Is(payload)) return;
            var request = ClientInteractionRequest.Decode(payload);
            if (request.Payload.Length != 1 || request.Payload[0] > 1) return;
            bool down = request.Payload[0] != 0;
            var key = (peer, id);
            if (windowDecisions.TryGetValue(key, out var previous) && request.Token <= previous.Token)
            {
                ReplyIntent(peer, b, request.Token, request.Token == previous.Token && down == previous.Down ? previous.Status : ClientInteractionStatus.Stale);
                return;
            }
            var status = stale ? ClientInteractionStatus.Stale : ClientInteractionStatus.Refused;
            if (!stale && MonMPPlugin.Instance?.World?.SaveGate.Active != true && TryPrepare(b) && CanUseNative(b) &&
                presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2 &&
                (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - b.Button.transform.position).sqrMagnitude <= 16)
            {
                if (Capture(b).TargetDown == down) status = ClientInteractionStatus.Accepted;
                else
                {
                    bool previousApplying = applying; applying = true;
                    try { b.Button.MouseDown(); } finally { applying = previousApplying; }
                    bool changed = Capture(b).TargetDown == down;
                    status = changed ? ClientInteractionStatus.Accepted : ClientInteractionStatus.Refused;
                    if (!changed)
                        PresentationReplays.Events?.HostAnnounce(PresentationKind.VehicleWindowClick, 0, b.Id,
                            b.Button.transform.position, 0, peer, false);
                }
            }
            windowDecisions[key] = (request.Token, down, status);
            ReplyIntent(peer, b, request.Token, status);
        }
        private void ReplyIntent(uint peer, Binding b, ulong token, ClientInteractionStatus status)
        {
            if (TryPrepare(b)) Publish(b);
            var world = channel;
            if (world == null || !world.Records.TryGetValue(b.Id, out var record)) return;
            world.Reply(peer, b.Id, new ClientInteractionReceipt(token, status, record.Revision, b.State.Encode()).Encode());
        }
        private void Reply(ulong id, byte[] payload)
        {
            if (host || !ClientInteractionReceipt.Is(payload) || !bindings.TryGetValue(id, out var b)) return;
            var receipt = ClientInteractionReceipt.Decode(payload);
            if (b.Intent.Token == 0 || b.Intent.Token != receipt.Token) return;
            var state = VehicleWindowState.Decode(receipt.State);
            bool accepted = receipt.Status == ClientInteractionStatus.Accepted;
            if (receipt.Revision < b.Revision)
            {
                state = b.State;
            }
            // A replay can acknowledge an earlier accepted operation while carrying a
            // later opposite host goal. Its current authority supersedes that preview.
            if (accepted && state.TargetDown != b.Intent.SentOpen)
            {
                b.Intent.Clear(); b.MotorIntent = 0; CancelPreview(b);
                Receive(b, state, false, false, Math.Max(b.Revision, receipt.Revision));
                return;
            }
            b.Intent.Acknowledge(receipt.Token, accepted, state.TargetDown, Time.unscaledTime);
            if (!accepted) { b.MotorIntent = 0; CancelPreview(b); }
            else if (b.Preview != null && b.Preview.Down == state.TargetDown && !b.Intent.Active) b.Preview.Confirmed = true;
            Receive(b, state, false, false, Math.Max(b.Revision, receipt.Revision));
            SendIntent(b);
        }
    }
}
