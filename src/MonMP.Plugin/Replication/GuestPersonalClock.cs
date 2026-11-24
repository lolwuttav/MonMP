using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // World time can arrive before the private profile reply. Hold its personal
    // work until restoration (or the local fallback) has chosen a baseline.
    internal sealed class GuestPersonalClock
    {
        private readonly PersonalClockCursor cursor = new PersonalClockCursor();
        private ClockState? current;
        private bool wasDead;
        internal bool HasPending => cursor.HasPending;
        internal void BeginSleep(int counter) => cursor.BeginSleep(counter);
        internal void EndSleep(int counter) => cursor.EndSleep(counter);
        internal void Observe(ClockState state, BasicNeeds_Manager needs, Gameplay? gameplay, bool profileReady, bool repair = false)
        {
            cursor.Observe(state, profileReady && gameplay?.Get_IsSleeping == true,
                profileReady && needs.Dead, profileReady && needs.Get_isSaving, repair);
            current = state;
        }
        internal bool TryReplay(BasicNeeds_Manager needs, Gameplay? gameplay, int minute, uint localPeer,
            bool profileReady, out bool refill)
        {
            refill = false;
            if (!profileReady) return false;
            // The next day-skip refill can arrive in a later packet. Wait for the
            // end/cancellation so all reset boundaries can suppress their older
            // need effects before any irreversible native death/drop is started.
            if (current?.FastForwardRunning == true && current.FastForwardTarget == 1440) return false;
            if (needs.Dead && !wasDead) cursor.MarkDead();
            wasDead = needs.Dead;
            if (!cursor.TryTake(out uint user, out bool sleeping, out bool dead, out bool saving, out bool historical, out refill)) return false;
            if (refill) needs.Fill_Everything();
            else GuestPersonalMinute.Tick(needs, gameplay, sleeping, minute, user,
                dead || needs.Dead, historical, localPeer, saving);
            return true;
        }
    }
}
