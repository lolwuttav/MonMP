using System;
using System.Numerics;

namespace MonMP.Plugin.UI
{
    // All points are in the same unscaled GUI coordinate space. Projected basis
    // vectors run from the handle center to its positive endpoint, not absolute points.
    internal static class HoldPreviewGizmoMath
    {
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector2 value) => Finite(value.X) && Finite(value.Y);

        internal static float SegmentDistance(Vector2 point, Vector2 start, Vector2 end)
        {
            if (!Finite(point) || !Finite(start) || !Finite(end)) return float.PositiveInfinity;
            double dx = (double)end.X - start.X, dy = (double)end.Y - start.Y;
            double px = (double)point.X - start.X, py = (double)point.Y - start.Y;
            double squaredLength = dx * dx + dy * dy;
            double t = squaredLength == 0 ? 0 : Math.Max(0, Math.Min(1, (px * dx + py * dy) / squaredLength));
            double x = px - t * dx, y = py - t * dy;
            return (float)Math.Sqrt(x * x + y * y);
        }

        // Returns movement in the units of worldHandleLength. Nearly end-on axes
        // cannot be reliably dragged; their exact numeric controls remain available.
        internal static bool TryAxisDrag(Vector2 displacement, Vector2 projectedAxis, float worldHandleLength, out float delta)
        {
            delta = 0;
            if (!Finite(displacement) || !Finite(projectedAxis) || !Finite(worldHandleLength) || worldHandleLength <= 0) return false;
            double squaredLength = (double)projectedAxis.X * projectedAxis.X + (double)projectedAxis.Y * projectedAxis.Y;
            if (squaredLength < 64) return false;
            double value = ((double)displacement.X * projectedAxis.X + (double)displacement.Y * projectedAxis.Y) / squaredLength * worldHandleLength;
            if (value < -float.MaxValue || value > float.MaxValue) return false;
            delta = (float)value;
            return true;
        }

        // Invert the projected circle basis rather than using a screen-circle angle:
        // a tilted world ring is an ellipse. Freeze center/bases for the entire drag.
        internal static bool TryRingAngle(Vector2 pointer, Vector2 center, Vector2 projectedBasisU, Vector2 projectedBasisV, out float angleDegrees)
        {
            angleDegrees = 0;
            if (!Finite(pointer) || !Finite(center) || !Finite(projectedBasisU) || !Finite(projectedBasisV)) return false;
            double ux = projectedBasisU.X, uy = projectedBasisU.Y, vx = projectedBasisV.X, vy = projectedBasisV.Y;
            double determinant = ux * vy - uy * vx;
            double trace = ux * ux + uy * uy + vx * vx + vy * vy;
            if (trace == 0 || determinant == 0) return false;
            double discriminant = Math.Max(0, trace * trace - 4 * determinant * determinant);
            double largest = Math.Sqrt((trace + Math.Sqrt(discriminant)) * 0.5);
            double smallest = Math.Abs(determinant) / largest;
            // A ring narrower than five GUI pixels or with an extreme aspect ratio
            // does not provide a stable angle, even if its sampled line is hit.
            if (smallest < 5 || largest > smallest * 20) return false;
            double dx = (double)pointer.X - center.X, dy = (double)pointer.Y - center.Y;
            double u = (dx * vy - dy * vx) / determinant;
            double v = (ux * dy - uy * dx) / determinant;
            if (u * u + v * v < 0.0001) return false;
            angleDegrees = (float)(Math.Atan2(v, u) * (180 / Math.PI));
            return true;
        }

        // Snap the displacement, preserving an exact initial value. Clamp after
        // snapping so the stated bounds are respected even when off the step grid.
        internal static float ClampAndSnap(float start, float delta, float minimum, float maximum, float step, bool snap)
        {
            if (!Finite(start) || !Finite(minimum) || !Finite(maximum) || minimum > maximum)
                throw new ArgumentOutOfRangeException(nameof(start), "A bounded edit requires a finite start and ordered finite limits.");
            if (!Finite(delta)) return Math.Max(minimum, Math.Min(maximum, start));
            double change = delta;
            if (snap && Finite(step) && step > 0) change = Math.Round(change / step, MidpointRounding.AwayFromZero) * step;
            return (float)Math.Max(minimum, Math.Min(maximum, start + change));
        }
    }
}
