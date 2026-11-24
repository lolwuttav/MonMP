using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Host refusals of seat claims, and the native error each one shows its requester. A
    // refusal ends the pending claim immediately; the claim timeout remains the fallback.
    internal static class SeatRefusals
    {
        internal static void Send(WorldChannel? channel, uint peer, ulong entity, uint generation, FurnitureSeatRefusal reason)
        {
            if (channel == null || reason == FurnitureSeatRefusal.None) return;
            try { channel.Reply(peer, entity, new SeatRefusalReply(reason, generation).Encode()); }
            catch (InvalidOperationException) { }
        }

        internal static string Message(FurnitureSeatRefusal reason)
        {
            switch (reason)
            {
                case FurnitureSeatRefusal.Occupied:
                case FurnitureSeatRefusal.Npc: return "Someone is already sitting there.";
                case FurnitureSeatRefusal.TooFar: return "Move closer to the seat.";
                case FurnitureSeatRefusal.Holding:
                    try { return TEXT.GetText("ERROR_AlreadyHaveAItemInHand"); }
                    catch (Exception) { return "Empty your hands first."; }
                case FurnitureSeatRefusal.AlreadySeated: return "You are already in another seat.";
                case FurnitureSeatRefusal.Stale:
                case FurnitureSeatRefusal.Moved: return "That seat just changed. Try again.";
                case FurnitureSeatRefusal.TransfersBlocked: return "Seats are unavailable while the game saves.";
                default: return "That seat can't be used right now.";
            }
        }
    }
}
