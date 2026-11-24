using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    internal static class NativeCdTrackIndex
    {
        internal static int Ensure<T>(List<T> tracks, T clip) where T : class
        {
            // The vanilla Load CD menu clears/rebuilds the list while cached transferred
            // clips remain alive. Never reuse an ordinal that now names another clip.
            for (int i = 0; i < tracks.Count; i++)
                if (ReferenceEquals(tracks[i], clip)) return i;
            int index = tracks.Count;
            tracks.Add(clip);
            return index;
        }
    }
}
