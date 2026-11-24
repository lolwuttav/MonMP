using System;

namespace MonMP.Plugin.Diagnostics
{
    // Development request files are interactive controls, not frame inputs.
    // Retain negative results for one second. Positive requests remain live so
    // deleting/consuming one immediately stops its action. A reset clock polls afresh.
    internal sealed class RequestFilePoll
    {
        private readonly Func<bool> exists;
        private bool polled, present;
        private float last, next;
        internal RequestFilePoll(Func<bool> exists) { this.exists = exists; }
        internal bool Check(float now)
        {
            if (polled && !present && now >= last && now < next) return false;
            last = now; next = now + 1f; polled = true;
            present = exists(); return present;
        }
    }
}
