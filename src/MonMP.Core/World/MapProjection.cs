using System;
using System.Collections.Generic;

namespace MonMP.Core.World
{
    /// <summary>Bounded marker visibility based on the arrival time of an existing player pose.</summary>
    public static class PlayerMarkerFreshness
    {
        public const double FullyFreshSeconds = 1.25;
        public const double DiscardAfterSeconds = 3.0;

        public static bool TryGetOpacity(double now, double receivedAt, out float opacity)
        {
            opacity = 0;
            if (!Finite(now) || !Finite(receivedAt) || receivedAt > now) return false;
            double age = now - receivedAt;
            if (age >= DiscardAfterSeconds) return false;
            opacity = age <= FullyFreshSeconds ? 1f : (float)(1 - (age - FullyFreshSeconds) / (DiscardAfterSeconds - FullyFreshSeconds));
            return opacity > 0;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>A surveyed world location and its matching normalized position on the paper map.</summary>
    public readonly struct MapControlPoint
    {
        public double WorldX { get; }
        public double WorldZ { get; }
        public double PaperU { get; }
        public double PaperV { get; }

        public MapControlPoint(double worldX, double worldZ, double paperU, double paperV)
        {
            WorldX = worldX; WorldZ = worldZ; PaperU = paperU; PaperV = paperV;
        }
    }

    /// <summary>The calibrated paper coordinate before and after clipping to the paper edge.</summary>
    public readonly struct ProjectedMapPoint
    {
        public double U { get; }
        public double V { get; }
        public double ClippedU { get; }
        public double ClippedV { get; }
        public bool IsClipped { get; }
        /// <summary>True when a piecewise calibration had to extrapolate beyond its landmark hull.</summary>
        public bool IsExtrapolated { get; }

        internal ProjectedMapPoint(double u, double v, bool isExtrapolated = false)
        {
            U = u; V = v;
            ClippedU = Clamp01(u); ClippedV = Clamp01(v);
            IsClipped = ClippedU != u || ClippedV != v;
            IsExtrapolated = isExtrapolated;
        }

        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    }

    /// <summary>
    /// XZ-to-paper projection fitted from surveyed landmarks. Legacy M1 calibrations use a global
    /// affine fit; new calibrations interpolate a piecewise-affine chart inside their landmark hull
    /// and use a boundary-anchored affine estimate outside it.
    /// Control points use paper fractions (0,0 is the lower-left edge; 1,1 the upper-right edge).
    /// Neither model assumes north-up, scale, rotation, or a terrain-derived projection.
    /// </summary>
    public sealed class MapProjection
    {
        public const int MinimumPiecewiseControlPoints = 5;
        public const int MaximumControlPoints = 16;
        private const double MaximumSpreadCondition = 40;
        private const double MinimumTriangleQuality = 0.025;

        private readonly struct Triangle
        {
            public readonly int A, B, C;
            public Triangle(int a, int b, int c) { A = a; B = b; C = c; }
        }

        private readonly struct Edge
        {
            public readonly int A, B;
            public Edge(int a, int b) { A = a; B = b; }
        }

        private readonly struct BoundarySegment
        {
            public readonly int A, B;
            public BoundarySegment(int a, int b) { A = a; B = b; }
        }

        private readonly double uX, uZ, u0, vX, vZ, v0;
        private readonly MapControlPoint[]? piecewisePoints;
        private readonly Triangle[]? triangles;
        private readonly BoundarySegment[]? boundarySegments;
        private readonly double jacobian;
        public double RootMeanSquareError { get; }
        /// <summary>Largest paper-fraction residual from the diagnostic global affine fit.</summary>
        public double MaximumAffineError { get; }
        /// <summary>Largest-to-smallest centered world landmark spread ratio.</summary>
        public double WorldSpreadCondition { get; }
        public bool UsesPiecewiseCalibration => piecewisePoints != null;
        public int ControlPointCount { get; }

        private MapProjection(double uX, double uZ, double u0, double vX, double vZ, double v0,
            double rootMeanSquareError, double maximumAffineError, double worldSpreadCondition, int count,
            MapControlPoint[]? piecewisePoints = null, Triangle[]? triangles = null,
            BoundarySegment[]? boundarySegments = null)
        {
            this.uX = uX; this.uZ = uZ; this.u0 = u0;
            this.vX = vX; this.vZ = vZ; this.v0 = v0;
            jacobian = uX * vZ - uZ * vX;
            RootMeanSquareError = rootMeanSquareError; MaximumAffineError = maximumAffineError;
            WorldSpreadCondition = worldSpreadCondition; ControlPointCount = count;
            this.piecewisePoints = piecewisePoints; this.triangles = triangles;
            this.boundarySegments = boundarySegments;
        }

        public static bool TryCreate(IEnumerable<MapControlPoint>? points, out MapProjection? projection, out string error)
        {
            projection = null;
            if (!TryFitAffine(points, out MapProjection? affine, out List<MapControlPoint>? samples, out error)) return false;
            projection = affine;
            return true;
        }

        /// <summary>
        /// Builds a local piecewise-affine calibration from five or more landmarks. Points inside
        /// their world-space hull interpolate through a deterministic Delaunay triangulation;
        /// outside points use the global affine slope anchored to the nearest hull point and are marked extrapolated.
        /// </summary>
        public static bool TryCreatePiecewise(IEnumerable<MapControlPoint>? points, out MapProjection? projection, out string error)
        {
            projection = null;
            if (!TryFitAffine(points, out MapProjection? affine, out List<MapControlPoint>? samples, out error)) return false;
            if (samples!.Count < MinimumPiecewiseControlPoints)
            { error = "At least five widely spaced landmarks are needed for local map calibration."; return false; }
            if (affine!.WorldSpreadCondition > MaximumSpreadCondition)
            { error = "World landmarks are too narrowly spread. Choose points around the area you travel."; return false; }

            var sampleArray = samples.ToArray();
            if (!TryTriangulate(sampleArray, out Triangle[]? fittedTriangles))
            { error = "Landmarks do not form a stable map. Undo a point that is too close to another."; return false; }
            BoundarySegment[] fittedBoundary = CreateBoundarySegments(fittedTriangles!);
            if (fittedBoundary.Length < 3)
            { error = "Landmarks do not form a closed map boundary. Undo a point that is too close to another."; return false; }

            double paperHullArea = 0;
            foreach (Triangle triangle in fittedTriangles!)
            {
                MapControlPoint a = sampleArray[triangle.A], b = sampleArray[triangle.B], c = sampleArray[triangle.C];
                double worldArea = Cross(b.WorldX - a.WorldX, b.WorldZ - a.WorldZ, c.WorldX - a.WorldX, c.WorldZ - a.WorldZ);
                double paperArea = Cross(b.PaperU - a.PaperU, b.PaperV - a.PaperV, c.PaperU - a.PaperU, c.PaperV - a.PaperV);
                double worldQuality = TriangleQuality(a.WorldX, a.WorldZ, b.WorldX, b.WorldZ, c.WorldX, c.WorldZ);
                double paperQuality = TriangleQuality(a.PaperU, a.PaperV, b.PaperU, b.PaperV, c.PaperU, c.PaperV);
                if (!Finite(worldArea) || !Finite(paperArea) || Math.Abs(worldArea) <= 1e-12 || Math.Abs(paperArea) <= 1e-10 ||
                    Math.Sign(worldArea) * Math.Sign(paperArea) != Math.Sign(affine.jacobian) ||
                    worldQuality < MinimumTriangleQuality || paperQuality < MinimumTriangleQuality)
                { error = "Landmarks create a folded or narrow map section. Choose points farther apart and in matching order."; return false; }
                paperHullArea += Math.Abs(paperArea) * 0.5;
            }
            if (paperHullArea < 0.015)
            { error = "Paper landmarks cover too little of the map. Choose widely spaced points around your travel area."; return false; }

            projection = new MapProjection(affine.uX, affine.uZ, affine.u0, affine.vX, affine.vZ, affine.v0,
                affine.RootMeanSquareError, affine.MaximumAffineError, affine.WorldSpreadCondition, samples.Count,
                sampleArray, fittedTriangles, fittedBoundary);
            return true;
        }

        private static bool TryFitAffine(IEnumerable<MapControlPoint>? points, out MapProjection? projection,
            out List<MapControlPoint>? samples, out string error)
        {
            projection = null; samples = null; error = "";
            if (points == null) { error = "No map calibration points were supplied."; return false; }
            samples = new List<MapControlPoint>();
            foreach (var point in points)
            {
                if (!Finite(point.WorldX) || !Finite(point.WorldZ) || !Finite(point.PaperU) || !Finite(point.PaperV) ||
                    point.PaperU < 0 || point.PaperU > 1 || point.PaperV < 0 || point.PaperV > 1)
                { error = "Map calibration points must contain finite world coordinates and paper fractions from zero to one."; return false; }
                samples.Add(point);
                if (samples.Count > MaximumControlPoints) { error = "At most sixteen map calibration landmarks are supported."; return false; }
            }
            if (samples.Count < 3) { error = "At least three map landmarks are needed to fit an affine projection."; return false; }

            // Centering keeps the least-squares solve well-conditioned for large world coordinates.
            double meanX = 0, meanZ = 0, meanU = 0, meanV = 0;
            foreach (var point in samples)
            { meanX += point.WorldX; meanZ += point.WorldZ; meanU += point.PaperU; meanV += point.PaperV; }
            meanX /= samples.Count; meanZ /= samples.Count; meanU /= samples.Count; meanV /= samples.Count;

            double xx = 0, xz = 0, zz = 0, uu = 0, uv = 0, vv = 0, xu = 0, zu = 0, xv = 0, zv = 0;
            foreach (var point in samples)
            {
                double x = point.WorldX - meanX, z = point.WorldZ - meanZ;
                double u = point.PaperU - meanU, v = point.PaperV - meanV;
                xx += x * x; xz += x * z; zz += z * z;
                uu += u * u; uv += u * v; vv += v * v;
                xu += x * u; zu += z * u; xv += x * v; zv += z * v;
            }
            double determinant = xx * zz - xz * xz;
            double scale = Math.Max(1, xx * zz);
            if (!Finite(determinant) || determinant <= scale * 1e-12)
            { error = "Map calibration landmarks are collinear or too close together."; return false; }
            double eigenDelta = Math.Sqrt(Math.Max(0, (xx - zz) * (xx - zz) + 4 * xz * xz));
            double largestEigenvalue = (xx + zz + eigenDelta) * 0.5;
            double smallestEigenvalue = (xx + zz - eigenDelta) * 0.5;
            double spreadCondition = smallestEigenvalue <= 0 ? double.PositiveInfinity : Math.Sqrt(largestEigenvalue / smallestEigenvalue);

            double targetDeterminant = uu * vv - uv * uv;
            double targetScale = Math.Max(1, uu * vv);
            if (!Finite(targetDeterminant) || targetDeterminant <= targetScale * 1e-12)
            { error = "Selected paper landmarks are collinear or too close together."; return false; }

            double ux = (xu * zz - zu * xz) / determinant;
            double uz = (zu * xx - xu * xz) / determinant;
            double vx = (xv * zz - zv * xz) / determinant;
            double vz = (zv * xx - xv * xz) / determinant;
            double jacobian = ux * vz - uz * vx;
            double jacobianScale = Math.Sqrt((ux * ux + uz * uz) * (vx * vx + vz * vz));
            if (!Finite(jacobian) || !Finite(jacobianScale) || Math.Abs(jacobian) <= Math.Max(1e-15, jacobianScale * 1e-12))
            { error = "The fitted map projection collapses the paper to a line."; return false; }
            double u0 = meanU - ux * meanX - uz * meanZ;
            double v0 = meanV - vx * meanX - vz * meanZ;

            double errorSquares = 0, maximumError = 0;
            foreach (var point in samples)
            {
                double du = ux * point.WorldX + uz * point.WorldZ + u0 - point.PaperU;
                double dv = vx * point.WorldX + vz * point.WorldZ + v0 - point.PaperV;
                errorSquares += du * du + dv * dv;
                maximumError = Math.Max(maximumError, Math.Sqrt(du * du + dv * dv));
            }
            double rms = Math.Sqrt(errorSquares / samples.Count);
            if (!Finite(ux) || !Finite(uz) || !Finite(u0) || !Finite(vx) || !Finite(vz) || !Finite(v0) ||
                !Finite(rms) || !Finite(maximumError))
            { error = "Map calibration produced invalid coefficients."; return false; }

            projection = new MapProjection(ux, uz, u0, vx, vz, v0, rms, maximumError, spreadCondition, samples.Count);
            return true;
        }

        public bool TryProject(double worldX, double worldZ, out ProjectedMapPoint point)
        {
            point = default;
            if (!Finite(worldX) || !Finite(worldZ)) return false;
            double u, v;
            bool extrapolated = false;
            if (piecewisePoints != null && triangles != null && TryInterpolate(worldX, worldZ, out u, out v))
            { }
            else if (piecewisePoints != null && boundarySegments != null &&
                TryProjectOutsideHull(worldX, worldZ, out u, out v))
            { extrapolated = true; }
            else
            {
                u = uX * worldX + uZ * worldZ + u0;
                v = vX * worldX + vZ * worldZ + v0;
                extrapolated = piecewisePoints != null;
            }
            if (!Finite(u) || !Finite(v)) return false;
            point = new ProjectedMapPoint(u, v, extrapolated);
            return true;
        }

        private bool TryProjectOutsideHull(double worldX, double worldZ, out double u, out double v)
        {
            u = v = 0;
            MapControlPoint[] points = piecewisePoints!;
            double bestDistanceSquared = double.PositiveInfinity;
            double nearestX = 0, nearestZ = 0, boundaryU = 0, boundaryV = 0;
            bool found = false;
            foreach (BoundarySegment segment in boundarySegments!)
            {
                MapControlPoint a = points[segment.A], b = points[segment.B];
                double dx = b.WorldX - a.WorldX, dz = b.WorldZ - a.WorldZ;
                double lengthSquared = dx * dx + dz * dz;
                if (!Finite(lengthSquared) || lengthSquared <= 0) continue;
                double t = ((worldX - a.WorldX) * dx + (worldZ - a.WorldZ) * dz) / lengthSquared;
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                double candidateX = a.WorldX + t * dx, candidateZ = a.WorldZ + t * dz;
                double offsetX = worldX - candidateX, offsetZ = worldZ - candidateZ;
                double distanceSquared = offsetX * offsetX + offsetZ * offsetZ;
                if (!Finite(distanceSquared) || distanceSquared >= bestDistanceSquared) continue;
                bestDistanceSquared = distanceSquared;
                nearestX = candidateX; nearestZ = candidateZ;
                boundaryU = a.PaperU + t * (b.PaperU - a.PaperU);
                boundaryV = a.PaperV + t * (b.PaperV - a.PaperV);
                found = true;
            }
            if (!found) return false;

            // Carry the nearest boundary's local residual outwards with the global affine slope.
            // This matches the piecewise chart at the hull, while nearest-point changes at hull
            // vertices stay continuous because adjoining segments share the same control point.
            u = boundaryU + uX * (worldX - nearestX) + uZ * (worldZ - nearestZ);
            v = boundaryV + vX * (worldX - nearestX) + vZ * (worldZ - nearestZ);
            return Finite(u) && Finite(v);
        }

        private bool TryInterpolate(double worldX, double worldZ, out double u, out double v)
        {
            u = v = 0;
            MapControlPoint[] points = piecewisePoints!;
            foreach (Triangle triangle in triangles!)
            {
                MapControlPoint a = points[triangle.A], b = points[triangle.B], c = points[triangle.C];
                double denominator = Cross(b.WorldX - a.WorldX, b.WorldZ - a.WorldZ, c.WorldX - a.WorldX, c.WorldZ - a.WorldZ);
                double wb = Cross(worldX - a.WorldX, worldZ - a.WorldZ, c.WorldX - a.WorldX, c.WorldZ - a.WorldZ) / denominator;
                double wc = Cross(b.WorldX - a.WorldX, b.WorldZ - a.WorldZ, worldX - a.WorldX, worldZ - a.WorldZ) / denominator;
                double wa = 1 - wb - wc;
                if (wa < -1e-9 || wb < -1e-9 || wc < -1e-9) continue;
                u = wa * a.PaperU + wb * b.PaperU + wc * c.PaperU;
                v = wa * a.PaperV + wb * b.PaperV + wc * c.PaperV;
                return true;
            }
            return false;
        }

        private static bool TryTriangulate(MapControlPoint[] points, out Triangle[]? result)
        {
            result = null;
            int count = points.Length;
            double minX = points[0].WorldX, maxX = minX, minZ = points[0].WorldZ, maxZ = minZ;
            for (int i = 1; i < count; i++)
            { minX = Math.Min(minX, points[i].WorldX); maxX = Math.Max(maxX, points[i].WorldX); minZ = Math.Min(minZ, points[i].WorldZ); maxZ = Math.Max(maxZ, points[i].WorldZ); }
            double span = Math.Max(maxX - minX, maxZ - minZ);
            if (!Finite(span) || span <= 1e-9) return false;
            double centerX = minX + (maxX - minX) * 0.5, centerZ = minZ + (maxZ - minZ) * 0.5;
            double radius = span * 64;
            if (!Finite(radius) || !Finite(centerX + radius) || !Finite(centerZ + radius)) return false;
            var x = new double[count + 3]; var z = new double[count + 3];
            for (int i = 0; i < count; i++) { x[i] = points[i].WorldX; z[i] = points[i].WorldZ; }
            x[count] = centerX - radius; z[count] = centerZ - radius;
            x[count + 1] = centerX + radius; z[count + 1] = centerZ - radius;
            x[count + 2] = centerX; z[count + 2] = centerZ + radius;
            var work = new List<Triangle> { new Triangle(count, count + 1, count + 2) };
            for (int inserted = 0; inserted < count; inserted++)
            {
                var boundary = new Dictionary<long, (Edge edge, int count)>();
                var bad = new List<int>();
                for (int t = 0; t < work.Count; t++)
                {
                    Triangle triangle = work[t];
                    if (!InCircumcircle(x, z, triangle, inserted)) continue;
                    bad.Add(t);
                    AddEdge(triangle.A, triangle.B); AddEdge(triangle.B, triangle.C); AddEdge(triangle.C, triangle.A);
                }
                if (bad.Count == 0) return false;
                for (int i = bad.Count - 1; i >= 0; i--) work.RemoveAt(bad[i]);
                foreach (var item in boundary.Values)
                {
                    if (item.count != 1) continue;
                    double area = Cross(x[item.edge.B] - x[item.edge.A], z[item.edge.B] - z[item.edge.A],
                        x[inserted] - x[item.edge.A], z[inserted] - z[item.edge.A]);
                    double tolerance = span * span * 1e-12;
                    if (Math.Abs(area) <= tolerance) continue;
                    work.Add(area > 0 ? new Triangle(item.edge.A, item.edge.B, inserted) : new Triangle(item.edge.B, item.edge.A, inserted));
                }

                void AddEdge(int a, int b)
                {
                    int low = Math.Min(a, b), high = Math.Max(a, b);
                    long key = ((long)low << 32) | (uint)high;
                    if (boundary.TryGetValue(key, out var existing)) boundary[key] = (existing.edge, existing.count + 1);
                    else boundary.Add(key, (new Edge(a, b), 1));
                }
            }

            var final = new List<Triangle>();
            var connected = new bool[count];
            foreach (Triangle triangle in work)
            {
                if (triangle.A >= count || triangle.B >= count || triangle.C >= count) continue;
                if (TriangleQuality(x[triangle.A], z[triangle.A], x[triangle.B], z[triangle.B], x[triangle.C], z[triangle.C]) < MinimumTriangleQuality)
                    return false;
                connected[triangle.A] = connected[triangle.B] = connected[triangle.C] = true;
                final.Add(triangle);
            }
            if (final.Count == 0) return false;
            for (int i = 0; i < connected.Length; i++) if (!connected[i]) return false;
            result = final.ToArray();
            return true;
        }

        private static BoundarySegment[] CreateBoundarySegments(Triangle[] triangles)
        {
            var edges = new Dictionary<long, (Edge edge, int count)>();
            foreach (Triangle triangle in triangles)
            {
                AddEdge(triangle.A, triangle.B);
                AddEdge(triangle.B, triangle.C);
                AddEdge(triangle.C, triangle.A);
            }
            var boundary = new List<BoundarySegment>();
            foreach (var item in edges.Values)
                if (item.count == 1) boundary.Add(new BoundarySegment(item.edge.A, item.edge.B));
            return boundary.ToArray();

            void AddEdge(int a, int b)
            {
                int low = Math.Min(a, b), high = Math.Max(a, b);
                long key = ((long)low << 32) | (uint)high;
                if (edges.TryGetValue(key, out var existing)) edges[key] = (existing.edge, existing.count + 1);
                else edges.Add(key, (new Edge(a, b), 1));
            }
        }

        private static bool InCircumcircle(double[] x, double[] z, Triangle triangle, int point)
        {
            double ax = x[triangle.A] - x[point], az = z[triangle.A] - z[point];
            double bx = x[triangle.B] - x[point], bz = z[triangle.B] - z[point];
            double cx = x[triangle.C] - x[point], cz = z[triangle.C] - z[point];
            double determinant = (ax * ax + az * az) * (bx * cz - bz * cx) -
                (bx * bx + bz * bz) * (ax * cz - az * cx) +
                (cx * cx + cz * cz) * (ax * bz - az * bx);
            double span = Math.Max(Math.Max(Math.Abs(ax), Math.Abs(az)), Math.Max(Math.Max(Math.Abs(bx), Math.Abs(bz)), Math.Max(Math.Abs(cx), Math.Abs(cz))));
            return determinant > span * span * span * span * 1e-12;
        }

        private static double TriangleQuality(double ax, double az, double bx, double bz, double cx, double cz)
        {
            double area2 = Math.Abs(Cross(bx - ax, bz - az, cx - ax, cz - az));
            double ab = (bx - ax) * (bx - ax) + (bz - az) * (bz - az);
            double bc = (cx - bx) * (cx - bx) + (cz - bz) * (cz - bz);
            double ca = (ax - cx) * (ax - cx) + (az - cz) * (az - cz);
            double sum = ab + bc + ca;
            return sum <= 0 ? 0 : 2 * Math.Sqrt(3) * area2 / sum;
        }

        private static double Cross(double ax, double az, double bx, double bz) => ax * bz - az * bx;

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
