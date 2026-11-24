using System;

namespace MonMP.Plugin.Replication
{
    // Double precision keeps nearly tangent reach volumes stable. This small value type also
    // lets the geometric policy run in headless tests without Unity or scene objects.
    internal readonly struct HeldReachVector
    {
        internal readonly double X, Y, Z;
        internal HeldReachVector(double x, double y, double z) { X = x; Y = y; Z = z; }
        internal double LengthSquared => Dot(this, this);
        internal double Length => Math.Sqrt(LengthSquared);
        internal bool Finite => Number(X) && Number(Y) && Number(Z);
        internal static bool Number(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static HeldReachVector operator +(HeldReachVector a, HeldReachVector b) => new HeldReachVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static HeldReachVector operator -(HeldReachVector a, HeldReachVector b) => new HeldReachVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static HeldReachVector operator -(HeldReachVector a) => new HeldReachVector(-a.X, -a.Y, -a.Z);
        public static HeldReachVector operator *(HeldReachVector a, double scale) => new HeldReachVector(a.X * scale, a.Y * scale, a.Z * scale);
        internal static double Dot(HeldReachVector a, HeldReachVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static HeldReachVector Cross(HeldReachVector a, HeldReachVector b) => new HeldReachVector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    // A translation of the whole held visual must put its wrist within this spherical shell.
    // Center = shoulder - requested wrist, so zero is the unmodified visual pose.
    internal readonly struct HeldReachArm
    {
        internal readonly HeldReachVector Center;
        internal readonly double Minimum, Maximum;
        internal HeldReachArm(HeldReachVector center, double minimum, double maximum)
        { Center = center; Minimum = minimum; Maximum = maximum; }
        internal bool Valid => Center.Finite && HeldReachVector.Number(Minimum) && HeldReachVector.Number(Maximum) && Minimum >= 0 && Maximum > 0 && Minimum <= Maximum;
    }

    internal static class HeldItemReachMath
    {
        private const double Epsilon = 1e-10, Tolerance = 1e-8;

        // Finds the least translation of an item that preserves its rotation, scale and hand
        // spacing. With two arms there are at most two shell boundaries and a front plane active
        // at once: enumerate their exact sphere/circle/point candidates instead of an iteration
        // limit that can leave one palm outside a narrow lens. No arrays or delegates per frame.
        // False means the full layout was impossible/invalid: the returned finite fallback keeps
        // the primary wrist reachable, preferring the front plane when those constraints overlap.
        internal static bool TryConstrain(HeldReachArm primary, HeldReachArm? secondary,
            HeldReachVector forward, double minimumForward, out HeldReachVector translation)
        {
            translation = default;
            double forwardLength = forward.Length;
            if (!primary.Valid || !forward.Finite || !HeldReachVector.Number(minimumForward) ||
                !HeldReachVector.Number(forwardLength) || forwardLength < Epsilon) return false;
            var normal = forward * (1 / forwardLength);
            bool validSecondary = !secondary.HasValue || secondary.Value.Valid;
            if (validSecondary && Find(primary, secondary, normal, minimumForward, true, out translation)) return true;
            // Rigidly separated calibration points can exceed the combined arm span. Keep the
            // chosen primary grip exact rather than stretch bones or silently rescale the item.
            if (Find(primary, null, normal, minimumForward, true, out translation)) return false;
            Find(primary, null, normal, minimumForward, false, out translation);
            return false;
        }

        private struct Search
        {
            internal HeldReachArm Primary;
            internal HeldReachArm? Secondary;
            internal HeldReachVector Normal, Best;
            internal double Plane, BestDistance;
            internal bool UsePlane, Found;
            internal void Consider(HeldReachVector candidate)
            {
                if (!candidate.Finite || !Contains(Primary, candidate) ||
                    Secondary.HasValue && !Contains(Secondary.Value, candidate) ||
                    UsePlane && HeldReachVector.Dot(Normal, candidate) < Plane - Tolerance) return;
                double distance = candidate.LengthSquared;
                if (!HeldReachVector.Number(distance) || Found && distance >= BestDistance) return;
                Found = true; Best = candidate; BestDistance = distance;
            }
        }

        private static bool Contains(HeldReachArm arm, HeldReachVector point)
        {
            double distance = (point - arm.Center).LengthSquared;
            double tolerance = Tolerance * Math.Max(1, arm.Maximum * arm.Maximum);
            return distance >= arm.Minimum * arm.Minimum - tolerance && distance <= arm.Maximum * arm.Maximum + tolerance;
        }

        private static bool Find(HeldReachArm primary, HeldReachArm? secondary, HeldReachVector normal,
            double plane, bool usePlane, out HeldReachVector translation)
        {
            var search = new Search { Primary = primary, Secondary = secondary, Normal = normal, Plane = plane, UsePlane = usePlane };
            search.Consider(default);
            if (search.Found) { translation = default; return true; }
            if (usePlane) search.Consider(normal * plane);
            ArmCandidates(primary, ref search);
            if (secondary.HasValue)
            {
                var other = secondary.Value;
                ArmCandidates(other, ref search);
                for (int a = 0; a < 2; a++)
                {
                    double radiusA = a == 0 ? primary.Maximum : primary.Minimum;
                    if (radiusA < Epsilon) continue;
                    for (int b = 0; b < 2; b++)
                    {
                        double radiusB = b == 0 ? other.Maximum : other.Minimum;
                        if (radiusB >= Epsilon) SpherePair(primary.Center, radiusA, other.Center, radiusB, ref search);
                    }
                }
            }
            translation = search.Best;
            return search.Found;
        }

        private static void ArmCandidates(HeldReachArm arm, ref Search search)
        {
            SphereCandidates(arm.Center, arm.Maximum, ref search);
            if (arm.Minimum >= Epsilon && arm.Minimum != arm.Maximum) SphereCandidates(arm.Center, arm.Minimum, ref search);
        }

        private static void SphereCandidates(HeldReachVector center, double radius, ref Search search)
        {
            double length = center.Length;
            search.Consider(length > Epsilon ? center - center * (radius / length) : center + search.Normal * radius);
            if (!search.UsePlane) return;
            double distance = search.Plane - HeldReachVector.Dot(search.Normal, center);
            double radiusSquared = radius * radius - distance * distance;
            if (radiusSquared >= -Tolerance)
                Circle(center + search.Normal * distance, search.Normal, Math.Sqrt(Math.Max(0, radiusSquared)), ref search);
        }

        private static void SpherePair(HeldReachVector a, double radiusA, HeldReachVector b, double radiusB, ref Search search)
        {
            var between = b - a;
            double length = between.Length;
            if (length < Epsilon || length > radiusA + radiusB + Tolerance || length < Math.Abs(radiusA - radiusB) - Tolerance) return;
            var normal = between * (1 / length);
            double along = (radiusA * radiusA - radiusB * radiusB + length * length) / (2 * length);
            double radiusSquared = radiusA * radiusA - along * along;
            if (radiusSquared < -Tolerance) return;
            Circle(a + normal * along, normal, Math.Sqrt(Math.Max(0, radiusSquared)), ref search);
        }

        private static HeldReachVector Perpendicular(HeldReachVector normal)
        {
            // Cross the least parallel coordinate axis for stable coincident/axial targets.
            var axis = Math.Abs(normal.X) <= Math.Abs(normal.Y) && Math.Abs(normal.X) <= Math.Abs(normal.Z)
                ? new HeldReachVector(1, 0, 0) : Math.Abs(normal.Y) <= Math.Abs(normal.Z)
                ? new HeldReachVector(0, 1, 0) : new HeldReachVector(0, 0, 1);
            var cross = HeldReachVector.Cross(normal, axis);
            return cross * (1 / cross.Length);
        }

        private static void Circle(HeldReachVector center, HeldReachVector normal, double radius, ref Search search)
        {
            var radial = -center + normal * HeldReachVector.Dot(center, normal);
            double length = radial.Length;
            search.Consider(center + (length > Epsilon ? radial * (1 / length) : Perpendicular(normal)) * radius);
            if (!search.UsePlane || radius < Epsilon) return;
            // Intersection of the boundary circle and the front plane gives up to two points.
            // These also cover a nearest-circle point rejected by the third active constraint.
            var projected = search.Normal - normal * HeldReachVector.Dot(search.Normal, normal);
            double projectedSquared = projected.LengthSquared;
            if (projectedSquared < Epsilon * Epsilon) return;
            double distance = search.Plane - HeldReachVector.Dot(search.Normal, center);
            double along = distance / projectedSquared;
            double remaining = radius * radius - distance * along;
            if (remaining < -Tolerance) return;
            var middle = center + projected * along;
            var line = HeldReachVector.Cross(normal, search.Normal);
            line = line * (Math.Sqrt(Math.Max(0, remaining)) / Math.Sqrt(projectedSquared));
            search.Consider(middle + line);
            search.Consider(middle - line);
        }
    }
}
