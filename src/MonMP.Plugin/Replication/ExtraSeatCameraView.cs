using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class ExtraSeatCameraView
    {
        // Unity camera space looks down -Z; native eye/gameplay coordinates stay intact.
        internal static Matrix4x4 Matrix(Vector3 position, Quaternion rotation) =>
            Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(position, rotation, Vector3.one).inverse;

        internal static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(a[i]) || float.IsNaN(b[i]) || Mathf.Abs(a[i] - b[i]) > .0001f) return false;
            return true;
        }
        internal static bool IsNativeView(Matrix4x4 actual, Vector3 position, Quaternion rotation, Matrix4x4 worldToLocal) =>
            NearNative(actual, Matrix(position, rotation)) || NearNative(actual, Matrix4x4.Scale(new Vector3(1, 1, -1)) * worldToLocal);

        internal static bool SameCulling(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i]) || float.IsNaN(b[i]) || float.IsInfinity(b[i]) ||
                    Mathf.Abs(a[i] - b[i]) > .0001f + .00001f * Mathf.Max(Mathf.Abs(a[i]), Mathf.Abs(b[i]))) return false;
            return true;
        }

        private static bool NearNative(Matrix4x4 a, Matrix4x4 b)
        {
            // Inverting TRS and Unity's native view path accumulate different
            // rounding at map-scale coordinates. Translation may differ by mm.
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i]) || float.IsNaN(b[i]) || float.IsInfinity(b[i]) ||
                    Mathf.Abs(a[i] - b[i]) > (i >= 12 && i <= 14 ? .01f : .0001f)) return false;
            return true;
        }
    }
}
