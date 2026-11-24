using System;
using System.Collections.Generic;
using System.Globalization;
using MonMP.Core.World;

namespace MonMP.Plugin.UI
{
    /// <summary>Validated calibration selected from the user's setting or the packaged game-map default.</summary>
    internal sealed class MapCalibrationSnapshot
    {
        internal string IdentityPrefix { get; }
        internal IReadOnlyList<MapControlPoint> Points { get; }
        internal MapProjection Projection { get; }
        internal bool IsBuiltIn { get; }
        internal string PreferenceIssue { get; }

        internal MapCalibrationSnapshot(string identityPrefix, IReadOnlyList<MapControlPoint> points,
            MapProjection projection, bool isBuiltIn, string preferenceIssue)
        {
            IdentityPrefix = identityPrefix;
            Points = points;
            Projection = projection;
            IsBuiltIn = isBuiltIn;
            PreferenceIssue = preferenceIssue;
        }
    }

    /// <summary>Ships the test-host survey as a guarded default and keeps malformed user settings recoverable.</summary>
    internal static class BuiltInMapCalibration
    {
        internal const string Value = "M1|A7FF10D8090B788DE54DE31F31B8B0BCD338F94FF6C943653EA53D66395A20EE|Maps|2048|2048|0,0.0134405848,0.001901947,0.5598263,0.033563856,0.389776349|1,1|0,0|0,0.1529541,1,0.889160156|0:0AC780CB|-1719.031494140625,1489.177734375,0.1541510671377182,0.12891928851604462;-1489.6925048828125,1315.779296875,0.19064037501811981,0.21638202667236328;-1087.31884765625,-721.13067626953125,0.7211037278175354,0.34698808193206787;12.842504501342773,-27.281093597412109,0.54166853427886963,0.74821138381958008;399.27734375,-525.7845458984375,0.69184249639511108,0.9156377911567688;332.12579345703125,-940.93914794921875,0.81019097566604614,0.88885527849197388;-12.237098693847656,-563.8951416015625,0.70316052436828613,0.75066155195236206;-199.52166748046875,-834.128662109375,0.76636278629302979,0.67191565036773682;-442.56756591796875,-905.871337890625,0.78863066434860229,0.57665783166885376;-1197.0513916015625,-747.40093994140625,0.72241246700286865,0.31054496765136719;-1578.76171875,282.87176513671875,0.45587372779846191,0.18286910653114319;-1137.017578125,704.0582275390625,0.34501188993453979,0.3249308168888092";

        /// <summary>
        /// Loads a matching saved override first. Empty, malformed, unsupported, or wrong-map values
        /// fall back to the packaged default without changing the user's stored setting.
        /// </summary>
        internal static bool TryResolve(string configuredValue, string supportedGameHash, string? requiredIdentity,
            out MapCalibrationSnapshot? snapshot, out string status)
        {
            snapshot = null;
            string preferenceIssue = "";
            bool hasOverride = !string.IsNullOrEmpty(configuredValue) &&
                !string.Equals(configuredValue, Value, StringComparison.Ordinal);
            if (hasOverride)
            {
                if (TryParse(configuredValue, supportedGameHash, out MapCalibrationSnapshot? saved, out string savedError))
                {
                    if (requiredIdentity == null || string.Equals(saved!.IdentityPrefix, requiredIdentity, StringComparison.Ordinal))
                    {
                        snapshot = new MapCalibrationSnapshot(saved!.IdentityPrefix, saved.Points, saved.Projection, false, "");
                        status = "Saved map calibration loaded.";
                        return true;
                    }
                    preferenceIssue = "Saved calibration belongs to a different map geometry.";
                }
                else preferenceIssue = "Saved calibration is invalid: " + savedError;
            }

            if (!TryParse(Value, supportedGameHash, out MapCalibrationSnapshot? builtIn, out string builtInError))
            {
                status = preferenceIssue.Length == 0 ? "Built-in map calibration is unavailable: " + builtInError :
                    preferenceIssue + " Built-in map calibration is unavailable: " + builtInError;
                return false;
            }
            if (requiredIdentity != null && !string.Equals(builtIn!.IdentityPrefix, requiredIdentity, StringComparison.Ordinal))
            {
                status = preferenceIssue.Length == 0
                    ? "Built-in calibration does not match the active map geometry."
                    : preferenceIssue + " Built-in calibration does not match the active map geometry.";
                return false;
            }

            snapshot = new MapCalibrationSnapshot(builtIn!.IdentityPrefix, builtIn.Points, builtIn.Projection, true, preferenceIssue);
            status = preferenceIssue.Length == 0 ? "Built-in map calibration loaded." : preferenceIssue + " Using the built-in default.";
            return true;
        }

        internal static bool TryResolveForPaperIdentity(string configuredValue, string supportedGameHash,
            string paperIdentity, out MapCalibrationSnapshot? snapshot, out string status) =>
            TryResolve(configuredValue, supportedGameHash, paperIdentity, out snapshot, out status);

        internal static bool TryParse(string encoded, string supportedGameHash,
            out MapCalibrationSnapshot? snapshot, out string error)
        {
            snapshot = null;
            error = "";
            try
            {
                string[] fields = encoded.Split('|');
                if (fields.Length != 11 || fields[0] != "M1") throw new FormatException("Unsupported calibration format.");
                if (fields[1] != supportedGameHash) throw new FormatException("Calibration belongs to a different supported game build.");
                if (fields[2].Length == 0 || fields[9].Length == 0) throw new FormatException("Calibration has no map texture or geometry identity.");
                string[] pointRows = fields[10].Split(';');
                if (pointRows.Length < 3 || pointRows.Length > MapProjection.MaximumControlPoints)
                    throw new FormatException("Calibration must contain between three and sixteen landmarks.");
                if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
                    !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new FormatException("Calibration texture dimensions are invalid.");
                for (int i = 5; i <= 8; i++)
                foreach (string number in fields[i].Split(','))
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !Finite(value))
                        throw new FormatException("Calibration geometry contains a non-finite value.");

                var points = new List<MapControlPoint>(pointRows.Length);
                foreach (string sample in pointRows)
                {
                    string[] values = sample.Split(',');
                    if (values.Length != 4) throw new FormatException("A calibration row must have four values.");
                    if (!TryFinite(values[0], out double worldX) || !TryFinite(values[1], out double worldZ) ||
                        !TryFinite(values[2], out double paperU) || !TryFinite(values[3], out double paperV))
                        throw new FormatException("A calibration row contains an invalid coordinate.");
                    points.Add(new MapControlPoint(worldX, worldZ, paperU, paperV));
                }

                bool valid = points.Count >= MapProjection.MinimumPiecewiseControlPoints
                    ? MapProjection.TryCreatePiecewise(points, out MapProjection? projection, out error)
                    : MapProjection.TryCreate(points, out projection, out error);
                if (!valid) throw new FormatException(error);
                string identityPrefix = string.Join("|", fields, 0, 10);
                snapshot = new MapCalibrationSnapshot(identityPrefix, points.ToArray(), projection!, false, "");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Clears only the stored override and restores its prior in-memory value if persistence fails.</summary>
        internal static bool TryResetPreference(Func<string> read, Action<string> write, Action save, out string error)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (save == null) throw new ArgumentNullException(nameof(save));
            string prior = read();
            try
            {
                write("");
                save();
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                try { write(prior); } catch (Exception) { }
                error = ex.Message;
                return false;
            }
        }

        private static bool TryFinite(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Finite(value);

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
