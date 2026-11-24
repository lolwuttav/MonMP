using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles
    {
        private void CommandPassenger(Binding b, uint peer, byte[] payload)
        {
            var command = VehiclePassengerSeatCommand.Decode(payload);
            if (b.Dormant && command.Action != VehiclePassengerSeatAction.Exit) return;
            if (command.Action == VehiclePassengerSeatAction.Exit)
            {
                if (!passengers.TryRelease(b.PassengerKey, peer, command.ExpectedGeneration, out var released))
                {
                    // Reconcile a stale generation; the owner retries its exit
                    // using this authoritative state. Never release a newer claim.
                    channel!.Reply(peer, b.PassengerId, b.PassengerState.Encode());
                    return;
                }
                ApplyPassenger(b, released); PublishPassenger(b);
                return;
            }
            if (TransfersBlocked || SeatAdmission?.IsSeated(peer) == true || bindings.Values.Any(value => drivers[value.Id].Owner == peer) ||
                MonMPPlugin.Instance?.World?.SchoolBusPassengers.IsOwner(peer) == true ||
                MonMPPlugin.Instance?.World?.BoatMotion.IsOwner(peer) == true)
            {
                RefuseSeat(peer, b.PassengerId, command.ExpectedGeneration,
                    TransfersBlocked ? FurnitureSeatRefusal.TransfersBlocked : FurnitureSeatRefusal.AlreadySeated);
                return;
            }
            Vector3 location;
            if (peer == 0)
            {
                if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject)
                {
                    RefuseSeat(peer, b.PassengerId, command.ExpectedGeneration,
                        gameplay.Player.IsHoldingAnObject ? FurnitureSeatRefusal.Holding : FurnitureSeatRefusal.NotWalking);
                    return;
                }
                location = gameplay.PlayerWalking.transform.position;
            }
            else
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2 ||
                    p.Pose.State != (byte)PlayerState.Walking || MonMPPlugin.Instance!.Items!.PeerHolding(peer))
                {
                    RefuseSeat(peer, b.PassengerId, command.ExpectedGeneration,
                        MonMPPlugin.Instance!.Items!.PeerHolding(peer) ? FurnitureSeatRefusal.Holding : FurnitureSeatRefusal.NotWalking);
                    return;
                }
                location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            }
            var seat = b.KonigPassengerSeat!;
            var refusal = b.PassengerState.Occupied ? FurnitureSeatRefusal.Occupied :
                (location - seat.transform.position).sqrMagnitude > 16 ? FurnitureSeatRefusal.TooFar :
                !seat.CanSeat() || !b.Vehicle.vehicle_PartsManager.CheckSeatSolidity(false) ? FurnitureSeatRefusal.NotUsable :
                FurnitureSeatRefusal.None;
            if (refusal != FurnitureSeatRefusal.None || !passengers.TryClaim(b.PassengerKey, peer, command.ExpectedGeneration, out var claimed))
            {
                RefuseSeat(peer, b.PassengerId, command.ExpectedGeneration, refusal != FurnitureSeatRefusal.None ? refusal : FurnitureSeatRefusal.Stale);
                return;
            }
            ApplyPassenger(b, claimed); PublishPassenger(b);
        }
        private void ApplyPassenger(Binding b, SeatOccupancy occupancy)
            => ApplyPassenger(b, new VehiclePassengerSeatState(occupancy.Peer, occupancy.Generation));
        private void ApplyPassenger(Binding b, VehiclePassengerSeatState state)
        {
            // A reply can overtake a queued snapshot. Never resurrect an older
            // occupancy after learning the authoritative newer generation.
            if (!host && state.Generation < b.PassengerState.Generation) return;
            bool allowEntry = SeatAdmission?.AllowsLocalEntry(this, b.PassengerId, state.Generation,
                StillWaitingAt(b.KonigPassengerSeat?.seatPosition)) != false;
            SeatAdmission?.Observe(this, b.PassengerId, state.Generation);
            bool changed = b.PassengerState.Owner != state.Owner || b.PassengerState.Generation != state.Generation;
            b.PassengerState = state;
            if (state.Owner != session!.LocalPeerId) b.PassengerExitPending = false;
            else if (changed && b.PassengerExitPending) b.NextPassengerExitRetry = 0;
            if (b.Dormant || !b.Vehicle.gameObject.activeInHierarchy) return;
            if (gameplay.Get_CurrentVehicleController == b.Vehicle &&
                gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger && state.Owner != session!.LocalPeerId) ExitLocal();
            if (state.Owner != session!.LocalPeerId || gameplay.Get_CurrentVehicleController == b.Vehicle || b.PassengerExitPending) return;
            if (!allowEntry || TransfersBlocked || gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject)
            {
                RequestPassengerExit(b);
                return;
            }
            applying = true;
            try
            {
                gameplay.EnterExitVehicle(b.Vehicle, b.KonigPassengerSeat!.seatPosition, false);
                b.EnteredFrame = Time.frameCount;
            }
            finally { applying = false; }
        }
        // A slow grant still seats a player who is free and standing within the host's 4 m
        // claim range of that seat; one who walked off, sat elsewhere or took an item is not.
        private bool StillWaitingAt(Transform? seat) => seat != null && gameplay.PlayerWalking != null &&
            gameplay.Get_PlayerState == PlayerState.Walking && !gameplay.Player.IsHoldingAnObject &&
            (gameplay.PlayerWalking.transform.position - seat.position).sqrMagnitude <= 16f;
        private void PublishPassenger(Binding b) => channel!.Publish(b.PassengerId, b.PassengerState.Encode());
        private void PassengerReply(ulong entity, byte[] payload)
        {
            if (host || payload.Length != 8 || !passengerBindings.TryGetValue(entity, out var binding) || !binding.PassengerExitPending) return;
            ApplyPassenger(binding, VehiclePassengerSeatState.Decode(payload));
        }
        private void RequestPassengerExit(Binding binding)
        {
            if (!Ready || binding.PassengerState.Owner != session!.LocalPeerId ||
                binding.PassengerExitPending && Time.unscaledTime < binding.NextPassengerExitRetry) return;
            binding.PassengerExitPending = true;
            binding.NextPassengerExitRetry = Time.unscaledTime + .5f;
            channel!.Request(binding.PassengerId,
                new VehiclePassengerSeatCommand(VehiclePassengerSeatAction.Exit, binding.PassengerState.Generation).Encode(), true);
        }

        private void UpdatePassengerReleases()
        {
            foreach (var binding in passengerBindings.Values)
            {
                if (binding.PassengerState.Owner != session!.LocalPeerId) continue;
                bool seated = gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger &&
                    gameplay.Get_CurrentVehicleController == binding.Vehicle && gameplay.PlayerWalking != null &&
                    gameplay.PlayerWalking.transform.IsChildOf(binding.Vehicle.transform);
                if (binding.PassengerExitPending || !seated && Time.frameCount > binding.EnteredFrame + 2)
                    RequestPassengerExit(binding);
            }
        }
    }
}
