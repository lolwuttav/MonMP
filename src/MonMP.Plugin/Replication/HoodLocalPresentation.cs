using System.Collections.Generic;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Hood actions are not toggles: repeated exterior lifts add another native impulse.
    // Retain a bounded ordered action queue while only presenting local motion/affordances.
    internal sealed class HoodLocalPresentation
    {
        internal const int Capacity = 4;
        private readonly Queue<(byte Action, bool CuePlayed)> queued = new Queue<(byte, bool)>();
        internal ulong Token { get; private set; }
        internal byte SentAction { get; private set; }
        internal bool SentCuePlayed { get; private set; }
        internal uint RequestRevision { get; private set; }
        private byte? settling;
        private double deadline, settleUntil;
        internal bool Active => Token != 0 || queued.Count != 0 || settling.HasValue;
        internal int PendingCount => queued.Count + (Token != 0 ? 1 : 0);
        internal bool CanSend => Token == 0 && queued.Count != 0;
        internal bool Enqueue(byte action, VehicleHoodState authority, bool cuePlayed = false)
        {
            var visual = Presented(authority);
            if (action > 2 || PendingCount >= Capacity ||
                (action == 0 ? visual.Released || visual.Open : action == 1 ? !visual.Released : !visual.Open)) return false;
            queued.Enqueue((action, cuePlayed)); return true;
        }
        internal bool TryBegin(ulong token, VehicleHoodState authority, uint revision, double now)
        {
            if (Token != 0) return false;
            while (queued.Count != 0)
            {
                var queuedAction = queued.Dequeue();
                byte action = queuedAction.Action;
                if (action == 0 ? authority.Released || authority.Open : action == 1 ? !authority.Released : !authority.Open) continue;
                Token = token; SentAction = action; RequestRevision = revision;
                SentCuePlayed = queuedAction.CuePlayed;
                deadline = now + DoorPresentationIntent.TimeoutSeconds; settling = null; return true;
            }
            return false;
        }
        internal bool Acknowledge(ulong token, bool accepted, VehicleHoodState authority, uint receiptRevision,
            uint latestRevision, uint lastClosedRevision, double now)
        {
            if (Token == 0 || token != Token) return false;
            Token = 0;
            if (!accepted) { Clear(); return true; }
            if (SentAction == 1 && authority.Open && receiptRevision >= latestRevision)
            { settling = 1; settleUntil = now + 0.75; }
            else if (SentAction == 2 && authority.Open && lastClosedRevision <= receiptRevision)
            { settling = 2; settleUntil = now + DoorPresentationIntent.TimeoutSeconds; }
            return true;
        }
        internal void Observe(VehicleHoodState authority, uint revision, double now)
        {
            if (settling.HasValue && (now >= settleUntil || !authority.Open || settling == 1 && authority.Angle <= -45)) settling = null;
        }
        internal VehicleHoodState Presented(VehicleHoodState authority)
        {
            var state = authority;
            if (settling.HasValue) state = Action(state, settling.Value);
            if (Token != 0) state = Action(state, SentAction);
            foreach (var action in queued) state = Action(state, action.Action);
            return state;
        }
        private static VehicleHoodState Action(VehicleHoodState state, byte action) => action == 0
            ? new VehicleHoodState(true, false, 0) : action == 1 ? new VehicleHoodState(true, true, -60) : new VehicleHoodState(true, true, 0);
        internal bool Expire(double now)
        {
            if (Token != 0 && now >= deadline) { Clear(); return true; }
            if (settling.HasValue && now >= settleUntil) { settling = null; return true; }
            return false;
        }
        internal void Clear() { Token = 0; queued.Clear(); settling = null; }
    }
}
