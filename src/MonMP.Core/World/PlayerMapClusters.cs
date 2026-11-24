using System;
using System.Collections.Generic;

namespace MonMP.Core.World
{
    // Reusable connected components in paper metres. The threshold follows label height,
    // independent of world-map calibration, aspect ratio, resolution and player ordering.
    public sealed class PlayerMapClusters
    {
        public readonly struct Point
        {
            public readonly uint Peer;
            public readonly float U, V;
            public Point(uint peer, float u, float v) { Peer = peer; U = u; V = v; }
        }
        public sealed class Group
        {
            public readonly List<int> Members = new List<int>();
            public float U, V;
            public int Leader;
        }
        private readonly List<Group> groups = new List<Group>();
        private int[] parents = Array.Empty<int>();
        public int Count { get; private set; }
        public Group this[int index] => index >= 0 && index < Count ? groups[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public void Clear() { Count = 0; foreach (var group in groups) group.Members.Clear(); }

        public void Build(IReadOnlyList<Point> points, float width, float height, float threshold)
        {
            if (!(width > 0) || !(height > 0) || !(threshold > 0) ||
                float.IsInfinity(width) || float.IsInfinity(height) || float.IsInfinity(threshold))
                throw new ArgumentOutOfRangeException(nameof(threshold));
            Count = 0;
            if (parents.Length < points.Count) parents = new int[points.Count];
            for (int i = 0; i < points.Count; i++) parents[i] = i;
            for (int i = 0; i < points.Count; i++)
                for (int j = 0; j < i; j++)
                {
                    double x = (points[i].U - points[j].U) * width, y = (points[i].V - points[j].V) * height;
                    if (x * x + y * y > (double)threshold * threshold) continue;
                    int a = Root(i), b = Root(j);
                    if (points[a].Peer < points[b].Peer) parents[b] = a; else parents[a] = b;
                }
            for (int i = 0; i < points.Count; i++)
            {
                if (Root(i) != i) continue;
                if (Count == groups.Count) groups.Add(new Group());
                var group = groups[Count++]; group.Members.Clear(); group.Leader = i; group.U = group.V = 0;
                for (int j = 0; j < points.Count; j++)
                    if (Root(j) == i) { group.Members.Add(j); group.U += points[j].U; group.V += points[j].V; }
                group.U /= group.Members.Count; group.V /= group.Members.Count;
            }
            // Retire member references/indices when the map empties or a session shrinks.
            for (int i = Count; i < groups.Count; i++) groups[i].Members.Clear();
        }
        private int Root(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
    }
}
