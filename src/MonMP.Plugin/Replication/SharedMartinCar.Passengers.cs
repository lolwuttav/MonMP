using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedMartinCar
    {
        private const byte PassengerCommandTag = 3, PassengerReplyTag = 4;
        private readonly SeatRegistry passengerRegistry = new SeatRegistry();
        private bool applyingPassenger;
        private Gameplay gameplay => Singleton<Gameplay>.i;
        private bool PassengerReady => !disposed && session?.IsRunning == true && channel?.Ready == true;
        internal bool Applying => applyingPassenger;
        internal bool TransfersBlocked { get; set; }
        internal SharedSeatAdmission? SeatAdmission { get; set; }
        internal uint[] Owners => bindings.Values.Where(b => b.Passenger.Occupied).Select(b => b.Passenger.Owner).Distinct().ToArray();
        internal bool IsOwner(uint peer) => peer != SeatOccupancy.Unoccupied && bindings.Values.Any(b => b.Passenger.Owner == peer);
        internal bool Handles(Vehicle vehicle) => vehicle != null && bindings.Values.Any(b => b.Vehicle == vehicle);
        internal bool LocalFlushComplete => session == null || !IsOwner(session.LocalPeerId) &&
            !bindings.Values.Any(b => b.EnterPending || IsNativePassenger(b));

        private bool IsNativePassenger(Binding binding) => gameplay != null &&
            gameplay.Get_CurrentVehicleController == binding.Vehicle && gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger;

        internal bool EnterExit(Vehicle vehicle, Transform seat, bool driver)
        {
            var binding = bindings.Values.FirstOrDefault(b => b.Vehicle == vehicle);
            if (binding == null || applyingPassenger || disposed) return true;
            if (!PassengerReady || session == null) return false;
            if (IsNativePassenger(binding)) { RequestPassengerExit(binding); return false; }
            if (SeatAdmission?.IsPendingLocal(this, binding.Id) == true) return false; // a repeat click keeps waiting
            if (binding.EnterPending && SeatAdmission?.IsPendingLocal(this, binding.Id) == false)
            { binding.EnterPending = false; binding.CancelEntry = true; }
            if (driver || binding.PassengerSeat != seat || TransfersBlocked || binding.Passenger.Occupied || binding.EnterPending ||
                gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject || !PassengerSeatUsable(binding)) return false;
            if (SeatAdmission?.TryBeginLocal(this, binding.Id, binding.Passenger.Generation, session.LocalPeerId) == false) return false;
            binding.EnterPending = true;
            binding.CancelEntry = false;
            binding.RequestedEnterGeneration = binding.Passenger.Generation;
            channel!.Request(binding.Id, EncodePassengerCommand(VehiclePassengerSeatAction.Enter, binding.Passenger.Generation), true);
            return false;
        }

        internal void NativeExited()
        {
            if (applyingPassenger || !PassengerReady || session == null) return;
            foreach (var binding in bindings.Values.Where(b => b.Passenger.Owner == session.LocalPeerId).ToArray())
                if (!IsNativePassenger(binding)) RequestPassengerExit(binding);
        }

        internal void FlushLocalOwnership()
        {
            SeatAdmission?.CancelLocal(this);
            if (!PassengerReady || session == null) return;
            foreach (var binding in bindings.Values)
            {
                if (binding.EnterPending) binding.CancelEntry = true;
                if (binding.Passenger.Owner == session.LocalPeerId) RequestPassengerExit(binding);
                else if (IsNativePassenger(binding)) ExitNativePassenger(binding, false);
            }
        }

        internal void RevokeOwnership(uint peer)
        {
            if (!host || session?.IsHost != true) return;
            foreach (var binding in bindings.Values.Where(b => b.Passenger.Owner == peer).ToArray())
            {
                if (peer == session.LocalPeerId && !ExitNativePassenger(binding, false))
                { binding.ExitPending = true; binding.CancelEntry = true; continue; }
                if (!passengerRegistry.TryRelease(binding.PassengerKey, peer, binding.Passenger.Generation, out var released)) continue;
                ApplyPassenger(binding, new VehiclePassengerSeatState(released.Peer, released.Generation));
                PublishPassenger(binding);
            }
        }

        private static bool PassengerSeatUsable(Binding binding)
        {
            var seat = binding.NativePassengerSeat;
            try { return seat != null && seat.isActiveAndEnabled && seat.gameObject.activeInHierarchy &&
                binding.PassengerSeat != null && binding.PassengerSeat.gameObject.activeInHierarchy && seat.CanUse(); }
            catch { return false; }
        }

        private static bool IsPassengerCommand(byte[] payload) => payload.Length == 8 &&
            payload[0] == 0x4D && payload[1] == 0x43 && payload[2] == PassengerCommandTag;
        private static byte[] EncodePassengerCommand(VehiclePassengerSeatAction action, uint generation) =>
            new byte[] { 0x4D, 0x43, PassengerCommandTag }.Concat(new VehiclePassengerSeatCommand(action, generation).Encode()).ToArray();

        private void CommandPassenger(uint peer, Binding binding, byte[] payload)
        {
            if (session == null || peer != 0 && !session.Peers.ContainsKey(peer)) return;
            var command = VehiclePassengerSeatCommand.Decode(payload.Skip(3).ToArray());
            if (command.Action == VehiclePassengerSeatAction.Exit)
            {
                // Releases survive save/door/range gates; only the current claim can be released.
                if (passengerRegistry.TryRelease(binding.PassengerKey, peer, command.ExpectedGeneration, out var released))
                {
                    ApplyPassenger(binding, new VehiclePassengerSeatState(released.Peer, released.Generation));
                    PublishPassenger(binding);
                }
                ReplyPassenger(peer, binding, command);
                return;
            }
            bool local = peer == 0;
            bool freshWalking;
            Vector3 position;
            if (local)
            {
                freshWalking = gameplay.Get_PlayerState == PlayerState.Walking && !gameplay.Player.IsHoldingAnObject;
                position = gameplay.PlayerWalking.transform.position;
            }
            else
            {
                freshWalking = presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2f &&
                    seen.Pose.State == (byte)PlayerState.Walking && MonMPPlugin.Instance?.Items?.PeerHolding(peer) == false;
                position = freshWalking ? new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) : Vector3.zero;
            }
            if (!TransfersBlocked && freshWalking && PassengerSeatUsable(binding) &&
                binding.PassengerSeat != null && (position - binding.PassengerSeat.position).sqrMagnitude <= 4f * 4f &&
                SeatAdmission?.IsSeated(peer) != true &&
                passengerRegistry.TryClaim(binding.PassengerKey, peer, command.ExpectedGeneration, out var claimed))
            {
                ApplyPassenger(binding, new VehiclePassengerSeatState(claimed.Peer, claimed.Generation));
                PublishPassenger(binding);
            }
            else if (binding.Passenger.Owner != peer)
            {
                bool holding = local ? gameplay.Player.IsHoldingAnObject : MonMPPlugin.Instance?.Items?.PeerHolding(peer) != false;
                SeatRefusals.Send(channel, peer, binding.Id, command.ExpectedGeneration,
                    TransfersBlocked ? FurnitureSeatRefusal.TransfersBlocked :
                    binding.Passenger.Occupied ? FurnitureSeatRefusal.Occupied :
                    SeatAdmission?.IsSeated(peer) == true ? FurnitureSeatRefusal.AlreadySeated :
                    !freshWalking ? (holding ? FurnitureSeatRefusal.Holding : FurnitureSeatRefusal.NotWalking) :
                    !PassengerSeatUsable(binding) || binding.PassengerSeat == null ? FurnitureSeatRefusal.NotUsable :
                    (position - binding.PassengerSeat.position).sqrMagnitude > 4f * 4f ? FurnitureSeatRefusal.TooFar :
                    FurnitureSeatRefusal.Stale);
            }
            ReplyPassenger(peer, binding, command);
        }

        private void ReplyPassenger(uint peer, Binding binding, VehiclePassengerSeatCommand command) => channel!.Reply(peer, binding.Id,
            new byte[] { 0x4D, 0x43, PassengerReplyTag }.Concat(command.Encode()).Concat(binding.Passenger.Encode()).ToArray());

        private void PassengerReply(ulong id, byte[] payload)
        {
            if (payload.Length != 16 || payload[0] != 0x4D || payload[1] != 0x43 || payload[2] != PassengerReplyTag ||
                !bindings.TryGetValue(id, out var binding)) return;
            var command = VehiclePassengerSeatCommand.Decode(payload.Skip(3).Take(5).ToArray());
            var state = VehiclePassengerSeatState.Decode(payload.Skip(8).ToArray());
            if (command.Action == VehiclePassengerSeatAction.Enter && binding.EnterPending &&
                command.ExpectedGeneration == binding.RequestedEnterGeneration)
            {
                binding.EnterPending = false;
                SeatAdmission?.CompleteLocal(this, binding.Id);
            }
            ApplyPassenger(binding, state);
        }

        private void RequestPassengerExit(Binding binding)
        {
            if (session == null || binding.Passenger.Owner != session.LocalPeerId) return;
            binding.CancelEntry = true;
            if (!ExitNativePassenger(binding, false)) { binding.ExitPending = true; return; }
            if (binding.ExitPending && Time.unscaledTime < binding.NextExitRetry) return;
            binding.ExitPending = true;
            binding.NextExitRetry = Time.unscaledTime + 0.5f;
            channel!.Request(binding.Id, EncodePassengerCommand(VehiclePassengerSeatAction.Exit, binding.Passenger.Generation), true);
        }

        private bool ExitNativePassenger(Binding binding, bool immediate)
        {
            if (!IsNativePassenger(binding)) return true;
            // The native walker finishes its seat entry at the end of this frame.
            if (!immediate && binding.EnteredFrame == Time.frameCount) return false;
            applyingPassenger = true;
            try { AccessTools.Method(typeof(Gameplay), "Force_ExitVehicle").Invoke(gameplay, null); }
            finally { applyingPassenger = false; }
            return !IsNativePassenger(binding);
        }

        private void ApplyPassenger(Binding binding, VehiclePassengerSeatState state)
        {
            if (state.Generation != binding.Passenger.Generation && !Wire.IsNewer(state.Generation, binding.Passenger.Generation)) return;
            bool allowEntry = SeatAdmission?.AllowsLocalEntry(this, binding.Id, state.Generation) != false;
            SeatAdmission?.Observe(this, binding.Id, state.Generation);
            bool changed = binding.Passenger.Owner != state.Owner || binding.Passenger.Generation != state.Generation;
            binding.Passenger = state;
            if (changed) { binding.EnterPending = false; binding.ExitPending = false; }
            if (session == null) return;
            if (state.Owner != session.LocalPeerId)
            {
                if (IsNativePassenger(binding)) ExitNativePassenger(binding, false);
                return;
            }
            if (!allowEntry || binding.CancelEntry) { RequestPassengerExit(binding); return; }
            if (IsNativePassenger(binding) || binding.ExitPending) return;
            if (TransfersBlocked) { RequestPassengerExit(binding); return; }
            if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject || !PassengerSeatUsable(binding))
            { RequestPassengerExit(binding); return; }
            applyingPassenger = true;
            try
            {
                gameplay.EnterExitVehicle(binding.Vehicle, binding.PassengerSeat!, false);
                binding.EnteredFrame = Time.frameCount;
            }
            finally { applyingPassenger = false; }
            if (!IsNativePassenger(binding)) RequestPassengerExit(binding);
        }

        private void PublishPassenger(Binding binding)
        {
            binding.Current = Capture(binding);
            channel!.Publish(binding.Id, binding.Current.Encode());
        }

        private void UpdatePassengerOwnership()
        {
            if (!PassengerReady || session == null) return;
            foreach (var binding in bindings.Values)
            {
                if (IsNativePassenger(binding) && binding.Passenger.Owner != session.LocalPeerId)
                {
                    // Adopt only a free host-native seat, never infer a remote rider from proximity.
                    if (host && !binding.Passenger.Occupied && SeatAdmission?.IsSeated(0) != true &&
                        passengerRegistry.TryClaim(binding.PassengerKey, 0, binding.Passenger.Generation, out var adopted))
                    {
                        ApplyPassenger(binding, new VehiclePassengerSeatState(adopted.Peer, adopted.Generation));
                        PublishPassenger(binding);
                    }
                    else ExitNativePassenger(binding, false);
                }
                if (binding.Passenger.Owner == session.LocalPeerId && (binding.ExitPending || binding.CancelEntry ||
                    !IsNativePassenger(binding) && Time.frameCount > binding.EnteredFrame + 2)) RequestPassengerExit(binding);
            }
        }
    }
}
