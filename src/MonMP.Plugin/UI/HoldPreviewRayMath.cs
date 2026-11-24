using System;
using System.Numerics;

namespace MonMP.Plugin.UI
{
    // The ray parameter is preserved: callers must not normalize a direction after
    // transforming a world ray into mesh space. This keeps hits comparable across scales.
    internal static class HoldPreviewRayMath
    {
        internal static bool IsFinite(Vector3 value) => IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool TryTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
            out float distance, out Vector3 normal)
        {
            distance = 0; normal = default;
            if (!IsFinite(origin) || !IsFinite(direction) || !IsFinite(a) || !IsFinite(b) || !IsFinite(c)) return false;

            // Double intermediates retain small/local triangles under large preview scales.
            double e1x = (double)b.X - a.X, e1y = (double)b.Y - a.Y, e1z = (double)b.Z - a.Z;
            double e2x = (double)c.X - a.X, e2y = (double)c.Y - a.Y, e2z = (double)c.Z - a.Z;
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            double normalSquared = nx * nx + ny * ny + nz * nz;
            double directionSquared = (double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z;
            if (normalSquared == 0 || directionSquared == 0) return false;

            double px = direction.Y * e2z - direction.Z * e2y;
            double py = direction.Z * e2x - direction.X * e2z;
            double pz = direction.X * e2y - direction.Y * e2x;
            double determinant = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(determinant) <= 1e-10 * Math.Sqrt(normalSquared * directionSquared)) return false;
            double tx = (double)origin.X - a.X, ty = (double)origin.Y - a.Y, tz = (double)origin.Z - a.Z;
            double u = (tx * px + ty * py + tz * pz) / determinant;
            const double edgeTolerance = 1e-6;
            if (u < -edgeTolerance || u > 1 + edgeTolerance) return false;
            double qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
            double v = (direction.X * qx + direction.Y * qy + direction.Z * qz) / determinant;
            if (v < -edgeTolerance || u + v > 1 + edgeTolerance) return false;
            double hit = (e2x * qx + e2y * qy + e2z * qz) / determinant;
            if (hit < 0 || hit > float.MaxValue || double.IsNaN(hit) || double.IsInfinity(hit)) return false;

            double inverseLength = 1 / Math.Sqrt(normalSquared);
            if (nx * direction.X + ny * direction.Y + nz * direction.Z > 0) inverseLength = -inverseLength;
            distance = (float)hit;
            normal = new Vector3((float)(nx * inverseLength), (float)(ny * inverseLength), (float)(nz * inverseLength));
            return true;
        }

        internal static bool TryBounds(Vector3 origin, Vector3 direction, Vector3 minimum, Vector3 maximum,
            out float distance, out Vector3 normal)
        {
            distance = 0; normal = default;
            if (!IsFinite(origin) || !IsFinite(direction) || !IsFinite(minimum) || !IsFinite(maximum) ||
                minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z || direction == Vector3.Zero) return false;

            double entry = double.NegativeInfinity, exit = double.PositiveInfinity;
            Vector3 entryNormal = default, exitNormal = default;
            if (!Slab(origin.X, direction.X, minimum.X, maximum.X, Vector3.UnitX, ref entry, ref exit, ref entryNormal, ref exitNormal) ||
                !Slab(origin.Y, direction.Y, minimum.Y, maximum.Y, Vector3.UnitY, ref entry, ref exit, ref entryNormal, ref exitNormal) ||
                !Slab(origin.Z, direction.Z, minimum.Z, maximum.Z, Vector3.UnitZ, ref entry, ref exit, ref entryNormal, ref exitNormal)) return false;

            double hit = entry >= 0 ? entry : exit;
            if (hit < 0 || hit > float.MaxValue || double.IsNaN(hit) || double.IsInfinity(hit)) return false;
            distance = (float)hit;
            normal = entry >= 0 ? entryNormal : exitNormal;
            if (Vector3.Dot(normal, direction) > 0) normal = -normal;
            return true;
        }

        private static bool Slab(float origin, float direction, float minimum, float maximum, Vector3 axis,
            ref double entry, ref double exit, ref Vector3 entryNormal, ref Vector3 exitNormal)
        {
            if (direction == 0) return origin >= minimum && origin <= maximum;
            double first = ((double)minimum - origin) / direction, last = ((double)maximum - origin) / direction;
            Vector3 firstNormal = -axis, lastNormal = axis;
            if (first > last) { double swap = first; first = last; last = swap; firstNormal = axis; lastNormal = -axis; }
            if (first > entry) { entry = first; entryNormal = firstNormal; }
            if (last < exit) { exit = last; exitNormal = lastNormal; }
            return entry <= exit;
        }
    }
}
