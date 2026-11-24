using System.Collections.Generic;

namespace MonMP.Core.World
{
    public enum MapCalibrationState
    {
        Idle,
        Collecting,
        Paused
    }

    /// <summary>Small explicit state holder for a landmark calibration that may pause between map openings.</summary>
    public sealed class MapCalibrationProgress
    {
        private readonly List<MapControlPoint> points = new List<MapControlPoint>();

        public MapCalibrationState State { get; private set; }
        public IReadOnlyList<MapControlPoint> Points => points;
        public int Count => points.Count;
        public bool IsPending => State != MapCalibrationState.Idle;

        public void Begin()
        {
            points.Clear();
            State = MapCalibrationState.Collecting;
        }

        public void Begin(IEnumerable<MapControlPoint> existingPoints)
        {
            points.Clear();
            State = MapCalibrationState.Collecting;
            foreach (MapControlPoint point in existingPoints)
            {
                if (points.Count == MapProjection.MaximumControlPoints) break;
                points.Add(point);
            }
        }

        public bool TryAdd(MapControlPoint point)
        {
            if (State != MapCalibrationState.Collecting || points.Count >= MapProjection.MaximumControlPoints) return false;
            points.Add(point);
            return true;
        }

        public bool RemoveLast()
        {
            if (State != MapCalibrationState.Collecting || points.Count == 0) return false;
            points.RemoveAt(points.Count - 1);
            return true;
        }

        public bool Pause()
        {
            if (State != MapCalibrationState.Collecting) return false;
            State = MapCalibrationState.Paused;
            return true;
        }

        public bool Resume()
        {
            if (State != MapCalibrationState.Paused) return false;
            State = MapCalibrationState.Collecting;
            return true;
        }

        public void Discard()
        {
            points.Clear();
            State = MapCalibrationState.Idle;
        }
    }
}
