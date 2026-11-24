namespace MonMP.Core.World
{
    // One request per door; additional clicks change only the latest local visual intent.
    // Hinged vehicle doors can acknowledge a closing impulse before their native latch closes.
    public sealed class DoorPresentationIntent
    {
        public const double TimeoutSeconds = 8;
        public ulong Token { get; private set; }
        public bool DesiredOpen { get; private set; }
        public bool SentOpen { get; private set; }
        public bool WaitingForLatch { get; private set; }
        private bool queued;
        private double deadline;
        public bool Active => Token != 0 || WaitingForLatch || queued;
        public bool PresentationOpen(bool authoritativeOpen) => Active ? DesiredOpen : authoritativeOpen;
        public void Toggle(bool authoritativeOpen)
        {
            if (!Active) DesiredOpen = authoritativeOpen;
            DesiredOpen = !DesiredOpen; queued = true;
        }
        public bool TryBegin(ulong token, bool authoritativeOpen, double now)
        {
            if (Token != 0 || WaitingForLatch || !queued) return false;
            queued = false;
            if (DesiredOpen == authoritativeOpen) return false;
            Token = token; SentOpen = DesiredOpen; deadline = now + TimeoutSeconds; return true;
        }
        public bool Acknowledge(ulong token, bool accepted, bool authoritativeOpen, double now, bool waitForLatch = false)
        {
            if (Token == 0 || Token != token) return false;
            Token = 0;
            if (!accepted) { Clear(); return true; }
            WaitingForLatch = waitForLatch && authoritativeOpen != SentOpen;
            deadline = now + TimeoutSeconds;
            if (!Active) DesiredOpen = authoritativeOpen;
            return true;
        }
        public void Observe(bool authoritativeOpen)
        {
            if (WaitingForLatch && authoritativeOpen == SentOpen) WaitingForLatch = false;
            if (!Active) DesiredOpen = authoritativeOpen;
        }
        public bool Expire(double now)
        {
            if (!Active || now < deadline || Token == 0 && !WaitingForLatch) return false;
            Clear(); return true;
        }
        public void Clear() { Token = 0; WaitingForLatch = false; queued = false; }
    }
}
