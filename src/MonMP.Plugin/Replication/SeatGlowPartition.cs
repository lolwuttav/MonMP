using System;
using System.Collections.Generic;
using System.Numerics;

namespace MonMP.Plugin.Replication
{
    // Partition existing triangles for independent material slots. Every new vertex remains on the
    // original triangle; barycentric weights preserve its texture coordinates and other attributes.
    internal static class SeatGlowPartition
    {
        internal readonly struct Region
        {
            internal readonly Vector3 Min, Max;
            internal Region(Vector3 min, Vector3 max) { Min = min; Max = max; }
        }

        internal readonly struct Point
        {
            internal readonly Vector3 Position, Weights;
            internal Point(Vector3 position, Vector3 weights) { Position = position; Weights = weights; }
        }

        internal static void Triangle(Vector3 a, Vector3 b, Vector3 c, IReadOnlyList<Region> regions,
            Action<int, Point, Point, Point> emit)
        {
            var remaining = new List<List<Point>> { new List<Point> {
                new Point(a, Vector3.UnitX), new Point(b, Vector3.UnitY), new Point(c, Vector3.UnitZ) } };
            for (int regionIndex = 0; regionIndex < regions.Count && remaining.Count != 0; regionIndex++)
            {
                var region = regions[regionIndex];
                var next = new List<List<Point>>();
                foreach (var polygon in remaining)
                {
                    if (!Intersects(polygon, region)) { next.Add(polygon); continue; }
                    var inside = polygon;
                    for (int plane = 0; plane < 6 && inside.Count >= 3; plane++)
                    {
                        int axis = plane / 2;
                        bool lower = (plane & 1) == 0;
                        float boundary = At(lower ? region.Min : region.Max, axis);
                        Split(inside, axis, boundary, lower, out var kept, out var outside);
                        if (outside.Count >= 3) next.Add(outside);
                        inside = kept;
                    }
                    Emit(inside, regionIndex, emit);
                }
                remaining = next;
            }
            foreach (var polygon in remaining) Emit(polygon, -1, emit);
        }

        private static float At(Vector3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

        private static bool Intersects(List<Point> polygon, Region region)
        {
            var min = polygon[0].Position; var max = min;
            foreach (var point in polygon) { min = Vector3.Min(min, point.Position); max = Vector3.Max(max, point.Position); }
            return max.X >= region.Min.X && min.X <= region.Max.X && max.Y >= region.Min.Y && min.Y <= region.Max.Y &&
                max.Z >= region.Min.Z && min.Z <= region.Max.Z;
        }

        private static void Split(List<Point> polygon, int axis, float boundary, bool lower,
            out List<Point> inside, out List<Point> outside)
        {
            inside = new List<Point>(); outside = new List<Point>();
            var previous = polygon[polygon.Count - 1];
            float previousDistance = (At(previous.Position, axis) - boundary) * (lower ? 1f : -1f);
            foreach (var current in polygon)
            {
                float distance = (At(current.Position, axis) - boundary) * (lower ? 1f : -1f);
                if ((distance >= 0) != (previousDistance >= 0))
                {
                    float t = previousDistance / (previousDistance - distance);
                    var intersection = new Point(Vector3.Lerp(previous.Position, current.Position, t), Vector3.Lerp(previous.Weights, current.Weights, t));
                    inside.Add(intersection); outside.Add(intersection);
                }
                (distance >= 0 ? inside : outside).Add(current);
                previous = current; previousDistance = distance;
            }
        }

        private static void Emit(List<Point> polygon, int region, Action<int, Point, Point, Point> emit)
        {
            for (int i = 1; i + 1 < polygon.Count; i++)
                if (Vector3.Cross(polygon[i].Position - polygon[0].Position, polygon[i + 1].Position - polygon[0].Position).LengthSquared() > 1e-16f)
                    emit(region, polygon[0], polygon[i], polygon[i + 1]);
        }
    }
}
