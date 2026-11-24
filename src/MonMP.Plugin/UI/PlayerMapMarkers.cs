using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    /// <summary>
    /// Draws session players on the game's own in-hand map. Markers are transient children of the
    /// native paper renderer and contain only meshes, materials, and text (no colliders or input).
    /// </summary>
    internal sealed class PlayerMapMarkers : IDisposable
    {
        private sealed class PaperSurface
        {
            public GameObject MapRoot = null!;
            public Transform Transform = null!;
            public Renderer Renderer = null!;
            public Mesh Mesh = null!;
            public int SubMeshIndex;
            public int[] TriangleIndices = Array.Empty<int>();
            public Vector3[] Vertices = Array.Empty<Vector3>();
            public Vector2[] UV = Array.Empty<Vector2>();
            public Material Material = null!;
            public Texture Texture = null!;
            public Vector2 TextureScale, TextureOffset;
            public Vector2 TextureMin, TextureMax;
            public Vector3 Origin, AxisU, AxisV, Normal;
            public float Width, Height;
            public string GeometryFingerprint = "";

            public bool TryPoint(float u, float v, out Vector3 point, out Vector3 normal, out Vector3 up)
            {
                point = default; normal = Normal; up = AxisV.normalized;
                if (!Finite(u) || !Finite(v) || u < 0 || u > 1 || v < 0 || v > 1) return false;
                Vector2 target = new Vector2((TextureMin.x + u * (TextureMax.x - TextureMin.x) - TextureOffset.x) / TextureScale.x,
                    (TextureMin.y + v * (TextureMax.y - TextureMin.y) - TextureOffset.y) / TextureScale.y);
                for (int i = 0; i + 2 < TriangleIndices.Length; i += 3)
                {
                    int ia = TriangleIndices[i], ib = TriangleIndices[i + 1], ic = TriangleIndices[i + 2];
                    Vector2 a = UV[ia], b = UV[ib], c = UV[ic];
                    Vector2 ab = b - a, ac = c - a, from = target - a;
                    float determinant = ab.x * ac.y - ab.y * ac.x;
                    if (Mathf.Abs(determinant) <= 1e-10f) continue;
                    float wb = (from.x * ac.y - from.y * ac.x) / determinant;
                    float wc = (ab.x * from.y - ab.y * from.x) / determinant;
                    if (wb < -0.00001f || wc < -0.00001f || wb + wc > 1.00001f) continue;
                    Vector3 edgeB = Vertices[ib] - Vertices[ia], edgeC = Vertices[ic] - Vertices[ia];
                    point = Vertices[ia] + edgeB * wb + edgeC * wc;
                    normal = Vector3.Cross(edgeB, edgeC).normalized;
                    up = ((edgeC * ab.x - edgeB * ac.x) / determinant * TextureScale.y).normalized;
                    return normal.sqrMagnitude > 0.5f && up.sqrMagnitude > 0.5f;
                }
                return false;
            }

            public string MetadataPrefix()
            {
                Bounds bounds = Mesh.bounds;
                return "M1|" + SessionOptions.SupportedGameHash + "|" + Texture.name + "|" + Texture.width.ToString(CultureInfo.InvariantCulture) + "|" +
                    Texture.height.ToString(CultureInfo.InvariantCulture) + "|" + Vec3(bounds.center) + "," + Vec3(bounds.size) + "|" +
                    Vec2(TextureScale) + "|" + Vec2(TextureOffset) + "|" + Vec2(TextureMin) + "," + Vec2(TextureMax) + "|" +
                    SubMeshIndex.ToString(CultureInfo.InvariantCulture) + ":" + GeometryFingerprint;
            }

            public bool IsCurrent(GameObject map)
            {
                if (MapRoot != map || Renderer == null || Mesh == null || Texture == null) return false;
                MeshFilter? filter = Renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh != Mesh || SubMeshIndex >= Mesh.subMeshCount) return false;
                Material[] materials = Renderer.sharedMaterials;
                bool found = false;
                if (SubMeshIndex < materials.Length)
                {
                    Material material = materials[SubMeshIndex];
                    found = material != null && material == Material && material.mainTexture == Texture &&
                        material.mainTextureScale == TextureScale && material.mainTextureOffset == TextureOffset;
                }
                return found;
            }

            private static string Vec2(Vector2 v) => v.x.ToString("R", CultureInfo.InvariantCulture) + "," + v.y.ToString("R", CultureInfo.InvariantCulture);
            private static string Vec3(Vector3 v) => v.x.ToString("R", CultureInfo.InvariantCulture) + "," + v.y.ToString("R", CultureInfo.InvariantCulture) + "," + v.z.ToString("R", CultureInfo.InvariantCulture);

            public bool TryPick(Camera camera, Vector2 pixel, out Vector2 paperPoint)
            {
                paperPoint = default;
                Vector3[] vertices;
                Vector2[] uv;
                int[] triangles;
                try
                {
                    vertices = Vertices; uv = UV; triangles = TriangleIndices;
                }
                catch { return false; }
                if (vertices.Length == 0 || uv.Length != vertices.Length || triangles.Length < 3) return false;

                Ray ray = camera.ScreenPointToRay(new Vector3(pixel.x, pixel.y, 0));
                float nearest = float.PositiveInfinity;
                Vector2 hitUv = default;
                Transform target = Transform;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
                    if ((uint)ia >= vertices.Length || (uint)ib >= vertices.Length || (uint)ic >= vertices.Length) continue;
                    Vector3 a = target.TransformPoint(vertices[ia]);
                    Vector3 b = target.TransformPoint(vertices[ib]);
                    Vector3 c = target.TransformPoint(vertices[ic]);
                    if (!RayTriangle(ray, a, b, c, out float distance, out float baryB, out float baryC) || distance >= nearest) continue;
                    nearest = distance;
                    float baryA = 1 - baryB - baryC;
                    hitUv = uv[ia] * baryA + uv[ib] * baryB + uv[ic] * baryC;
                }
                if (!Finite(nearest)) return false;
                float textureU = hitUv.x * TextureScale.x + TextureOffset.x;
                float textureV = hitUv.y * TextureScale.y + TextureOffset.y;
                float rangeU = TextureMax.x - TextureMin.x, rangeV = TextureMax.y - TextureMin.y;
                if (rangeU <= 1e-6f || rangeV <= 1e-6f) return false;
                paperPoint = new Vector2((textureU - TextureMin.x) / rangeU, (textureV - TextureMin.y) / rangeV);
                return Finite(paperPoint.x) && Finite(paperPoint.y) && paperPoint.x >= 0 && paperPoint.x <= 1 && paperPoint.y >= 0 && paperPoint.y <= 1;
            }

            private static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c,
                out float distance, out float baryB, out float baryC)
            {
                const float epsilon = 1e-7f;
                Vector3 edge1 = b - a, edge2 = c - a;
                Vector3 p = Vector3.Cross(ray.direction, edge2);
                float determinant = Vector3.Dot(edge1, p);
                if (Mathf.Abs(determinant) <= epsilon) { distance = baryB = baryC = 0; return false; }
                float inverse = 1 / determinant;
                Vector3 fromA = ray.origin - a;
                baryB = Vector3.Dot(fromA, p) * inverse;
                if (baryB < 0 || baryB > 1) { distance = baryC = 0; return false; }
                Vector3 q = Vector3.Cross(fromA, edge1);
                baryC = Vector3.Dot(ray.direction, q) * inverse;
                if (baryC < 0 || baryB + baryC > 1) { distance = 0; return false; }
                distance = Vector3.Dot(edge2, q) * inverse;
                return distance >= 0;
            }

            public bool TryFitPlane()
            {
                Vector3[] vertices;
                Vector2[] uv;
                try { vertices = Vertices; uv = UV; }
                catch { return false; }
                if (vertices.Length < 3 || uv.Length != vertices.Length || TriangleIndices.Length < 3) return false;
                if (!Finite(TextureScale.x) || !Finite(TextureScale.y) || Mathf.Abs(TextureScale.x) <= 1e-6f || Mathf.Abs(TextureScale.y) <= 1e-6f) return false;

                float minU = float.PositiveInfinity, minV = float.PositiveInfinity;
                float maxU = float.NegativeInfinity, maxV = float.NegativeInfinity;
                var used = new HashSet<int>();
                foreach (int index in TriangleIndices)
                {
                    if ((uint)index >= vertices.Length) return false;
                    used.Add(index);
                }
                if (used.Count < 3) return false;
                foreach (int i in used)
                {
                    float u = uv[i].x * TextureScale.x + TextureOffset.x;
                    float v = uv[i].y * TextureScale.y + TextureOffset.y;
                    if (!Finite(u) || !Finite(v) || !Finite(vertices[i].x) || !Finite(vertices[i].y) || !Finite(vertices[i].z)) return false;
                    minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                    minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
                }
                if (maxU - minU < 1e-6f || maxV - minV < 1e-6f) return false;
                TextureMin = new Vector2(minU, minV); TextureMax = new Vector2(maxU, maxV);

                // Fit each local position component from the material-transformed mesh UVs. A
                // planar map mesh therefore keeps its authored UV orientation, flips and aspect.
                double meanU = 0, meanV = 0;
                var points = new List<Vector2>(used.Count);
                var usedVertices = new List<Vector3>(used.Count);
                foreach (int i in used)
                {
                    points.Add(new Vector2((uv[i].x * TextureScale.x + TextureOffset.x - minU) / (maxU - minU),
                        (uv[i].y * TextureScale.y + TextureOffset.y - minV) / (maxV - minV)));
                    usedVertices.Add(vertices[i]);
                    meanU += points[points.Count - 1].x; meanV += points[points.Count - 1].y;
                }
                meanU /= points.Count; meanV /= points.Count;
                double uu = 0, uvSum = 0, vv = 0;
                double[] pu = new double[3], pv = new double[3], p0 = new double[3];
                Vector3 meanPosition = Vector3.zero;
                for (int i = 0; i < usedVertices.Count; i++) meanPosition += usedVertices[i];
                meanPosition /= usedVertices.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    double du = points[i].x - meanU, dv = points[i].y - meanV;
                    uu += du * du; uvSum += du * dv; vv += dv * dv;
                    Vector3 dp = usedVertices[i] - meanPosition;
                    pu[0] += du * dp.x; pu[1] += du * dp.y; pu[2] += du * dp.z;
                    pv[0] += dv * dp.x; pv[1] += dv * dp.y; pv[2] += dv * dp.z;
                }
                double determinant = uu * vv - uvSum * uvSum;
                if (determinant <= Math.Max(1, uu * vv) * 1e-12) return false;
                Vector3 axisU = Vector3.zero, axisV = Vector3.zero;
                for (int axis = 0; axis < 3; axis++)
                {
                    double du = (pu[axis] * vv - pv[axis] * uvSum) / determinant;
                    double dv = (pv[axis] * uu - pu[axis] * uvSum) / determinant;
                    double origin = Coordinate(meanPosition, axis) - du * meanU - dv * meanV;
                    SetCoordinate(ref axisU, axis, (float)du); SetCoordinate(ref axisV, axis, (float)dv);
                    p0[axis] = origin;
                }
                AxisU = axisU; AxisV = axisV;
                Origin = new Vector3((float)p0[0], (float)p0[1], (float)p0[2]);
                Width = AxisU.magnitude; Height = AxisV.magnitude;
                Vector3 cross = Vector3.Cross(AxisU, AxisV);
                if (Width <= 1e-5f || Height <= 1e-5f || cross.sqrMagnitude <= 1e-12f) return false;
                // Axes estimate label size only. Picking and placement use the authored triangles,
                // including the folds in the shipped portable map, rather than this affine fit.
                Normal = cross.normalized;
                GeometryFingerprint = Fingerprint(vertices, uv, TriangleIndices, used);
                return true;
            }

            private static string Fingerprint(Vector3[] vertices, Vector2[] uv, int[] triangles, HashSet<int> used)
            {
                uint hash = 2166136261;
                void AddInt(int value)
                {
                    unchecked
                    {
                        for (int byteIndex = 0; byteIndex < 4; byteIndex++)
                        { hash ^= (byte)(value >> (byteIndex * 8)); hash *= 16777619; }
                    }
                }
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (!used.Contains(i)) continue;
                    AddInt(i);
                    AddInt(BitConverter.ToInt32(BitConverter.GetBytes(vertices[i].x), 0));
                    AddInt(BitConverter.ToInt32(BitConverter.GetBytes(vertices[i].y), 0));
                    AddInt(BitConverter.ToInt32(BitConverter.GetBytes(vertices[i].z), 0));
                    AddInt(BitConverter.ToInt32(BitConverter.GetBytes(uv[i].x), 0));
                    AddInt(BitConverter.ToInt32(BitConverter.GetBytes(uv[i].y), 0));
                }
                foreach (int index in triangles) AddInt(index);
                return hash.ToString("X8", CultureInfo.InvariantCulture);
            }

            private static double Coordinate(Vector3 point, int axis) => axis == 0 ? point.x : axis == 1 ? point.y : point.z;
            private static void SetCoordinate(ref Vector3 point, int axis, float value)
            { if (axis == 0) point.x = value; else if (axis == 1) point.y = value; else point.z = value; }
            private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private sealed class Marker
        {
            public GameObject Root = null!;
            public Transform DotOutline = null!;
            public Transform Dot = null!;
            public TextMesh Name = null!;
            public TextMesh Shadow = null!;
            public Renderer DotRenderer = null!;
            public Renderer OutlineRenderer = null!;
            public Material DotMaterial = null!;
            public Material OutlineMaterial = null!;
            public string MeasuredName = "";
            public float GlyphWidth, GlyphHeight;
        }

        private readonly struct MarkerLayout
        {
            public readonly float Radius, CharacterSize, LabelOffset, MarginU, MarginV;

            public MarkerLayout(float radius, float characterSize, float labelOffset, float marginU, float marginV)
            { Radius = radius; CharacterSize = characterSize; LabelOffset = labelOffset; MarginU = marginU; MarginV = marginV; }
        }

        private static MarkerLayout CalculateMarkerLayout(float width, float height, string name, int fontSize)
        {
            float minSpan = Mathf.Min(width, height);
            float radius = Mathf.Clamp(minSpan * 0.006f, 0.001f, 0.008f);
            int characterCount = Mathf.Max(1, name.Length);
            int safeFontSize = Mathf.Max(1, fontSize);
            float characterHeight = Mathf.Clamp(minSpan * 0.84f / characterCount, minSpan * 0.035f, minSpan * 0.075f);
            float characterSize = characterHeight / safeFontSize;
            float textWidth = characterCount * characterHeight * 0.5f;
            float halfTextHeight = characterHeight * 0.35f;
            float labelOffset = radius * 1.35f + halfTextHeight;
            float marginU = Mathf.Clamp(textWidth / (2f * width) + radius / width, 0, 0.45f);
            float marginV = Mathf.Clamp((labelOffset + halfTextHeight) / height, 0.01f, 0.45f);
            return new MarkerLayout(radius, characterSize, labelOffset, marginU, marginV);
        }

        private static Vector3 MarkerForward(Vector3 cameraPoint, Vector3 localPoint, Vector3 surfaceNormal)
        {
            Vector3 viewDirection = cameraPoint - localPoint;
            return viewDirection.sqrMagnitude <= 1e-8f ? surfaceNormal : viewDirection;
        }

        private static string FormatMarkerName(string displayName, uint peerId, bool extrapolated)
        {
            string name = string.IsNullOrWhiteSpace(displayName) ? "Peer " + peerId : displayName;
            int nameLimit = extrapolated ? 23 : 24;
            if (name.Length > nameLimit) name = name.Substring(0, nameLimit);
            if (extrapolated) name = "~" + name;
            return name;
        }

        private const float MarkerSurfaceLift = 0.004f;

        private static readonly FieldInfo? NativeMapField = typeof(PlayerVisualHands).GetField("Map", BindingFlags.Instance | BindingFlags.NonPublic);
#if MONMP_DEVELOPMENT
        private static readonly MethodInfo? NativeMapToggle = typeof(PlayerVisualHands).GetMethod("Map_Toggle", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
#endif
        private readonly Dictionary<uint, Marker> markers = new Dictionary<uint, Marker>();
        private readonly Dictionary<uint, float> poseTimes = new Dictionary<uint, float>();
        private readonly HashSet<uint> liveIds = new HashSet<uint>();
        private readonly List<uint> removeIds = new List<uint>();
        private readonly List<(PeerInfo Peer, ProjectedMapPoint Point, float Alpha)> candidates = new List<(PeerInfo, ProjectedMapPoint, float)>();
        private readonly List<PlayerMapClusters.Point> clusterPoints = new List<PlayerMapClusters.Point>();
        private readonly PlayerMapClusters clusters = new PlayerMapClusters();
        private NetworkSession? session;
        private Func<PlayerVisualHands?>? getNativeHands;
        private Func<uint, Transform?>? remoteRoot;
        private Func<uint, Vector3?>? remoteMapPosition;
        private Func<Vector3>? localPosition;
        private Func<bool>? ready;
        private Action<string>? log;
        private ConfigEntry<string>? calibrationSetting;
#if MONMP_DEVELOPMENT
        private ConfigEntry<KeyCode>? calibrationKey;
        private MapProjection? candidateProjection;
        private readonly List<MapControlPoint> savedPoints = new List<MapControlPoint>();
        private readonly MapCalibrationProgress calibrationProgress = new MapCalibrationProgress();
#endif
        private MapProjection? projection;
        private string savedIdentity = "";
        private PlayerVisualHands? nativeHands;
        private GameObject? nativeMap;
        private PaperSurface? paper;
        private GameObject? overlayRoot;
        private Mesh? markerMesh;
        private bool disposed;
#if MONMP_DEVELOPMENT
        private bool calibrationActive, cursorChanged, refiningCalibration;
        private bool cursorRestorePending;
        private CursorLockMode previousCursorLock;
        private bool previousCursorVisible;
        private int calibrationSceneHandle;
        private NetworkSession? calibrationSession;
        private PlayerVisualHands? calibrationHands;
        private NetworkSession? progressSession;
        private PlayerVisualHands? progressHands;
        private GameObject? progressMap;
        private int progressSceneHandle;
        private string progressIdentity = "";
#endif
        private string uiStatus = "";
        private string surfaceFailure = "";
        private string reportedFailure = "";
        private bool initialized;
        private float nextSurfaceDiscovery, nextSurfaceCheck;
#if MONMP_DEVELOPMENT
        private bool setupRequested;
        private PlayerVisualHands? setupHands;
        private int setupSceneHandle = -1;
        private float setupDeadline;

        internal string SetupStatus => calibrationProgress.State == MapCalibrationState.Paused
            ? "Map setup paused at " + calibrationProgress.Count + " landmarks. Resume at your next landmark."
            : uiStatus.Length != 0 ? uiStatus : projection != null && !projection.UsesPiecewiseCalibration
                ? "Map has a provisional legacy calibration. Refine with five widely spaced landmarks."
                : projection != null ? "Map player markers use the default or saved landmark calibration."
                : "No matching map calibration is available. Match five widely spaced world landmarks to the paper map.";

        internal bool RequestSetup(out string message)
        {
            message = "Map setup requires a synchronized multiplayer world with other menus closed.";
            if (!initialized || disposed || session?.IsReady != true || ready?.Invoke() != true) return false;
            if (!session.Settings.ShowPlayersOnMap)
            { message = "The host has hidden players on the map. Enable Show players on map in session settings before calibrating markers."; return false; }
            var hands = getNativeHands?.Invoke();
            var scene = SceneManager.GetActiveScene();
            if (hands == null || !hands.isActiveAndEnabled || !scene.IsValid() || !scene.isLoaded ||
                hands.gameObject.scene.handle != scene.handle || NativeMapField == null || NativeMapToggle == null)
            { message = "The player's native map is unavailable."; return false; }
            if (!hands.Get_IsMapOpen)
            {
                try { NativeMapToggle.Invoke(hands, null); }
                catch (Exception) { message = "The native map could not open. Put down held items and try again."; return false; }
            }
            if (!hands.Get_IsMapOpen || NativeMapField.GetValue(hands) == null)
            { message = "The native map cannot open right now. Put down held items and close hand menus first."; return false; }
            setupRequested = true;
            setupHands = hands; setupSceneHandle = scene.handle;
            setupDeadline = Time.unscaledTime + 3f;
            message = calibrationProgress.State == MapCalibrationState.Paused
                ? "Map opened; preparing to resume landmark setup." : "Map opened; preparing click setup on the native paper.";
            return true;
        }
#endif

        internal void Bind(NetworkSession networkSession, Func<PlayerVisualHands?> resolveNativeHands, Func<uint, Transform?> resolveRemoteRoot,
            Func<Vector3> getLocalWorldPosition, Func<bool> canDisplay,
            ConfigEntry<string> savedCalibration,
#if MONMP_DEVELOPMENT
            ConfigEntry<KeyCode> setCalibrationKey,
#endif
            Action<string> writeLog,
            Func<uint, Vector3?>? resolveRemoteMapPosition = null)
        {
            DisposeBinding();
            session = networkSession ?? throw new ArgumentNullException(nameof(networkSession));
            getNativeHands = resolveNativeHands ?? throw new ArgumentNullException(nameof(resolveNativeHands));
            remoteRoot = resolveRemoteRoot ?? throw new ArgumentNullException(nameof(resolveRemoteRoot));
            remoteMapPosition = resolveRemoteMapPosition;
            localPosition = getLocalWorldPosition ?? throw new ArgumentNullException(nameof(getLocalWorldPosition));
            ready = canDisplay ?? throw new ArgumentNullException(nameof(canDisplay));
            calibrationSetting = savedCalibration ?? throw new ArgumentNullException(nameof(savedCalibration));
#if MONMP_DEVELOPMENT
            calibrationKey = setCalibrationKey ?? throw new ArgumentNullException(nameof(setCalibrationKey));
#endif
            log = writeLog;
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            LoadCalibration();
            initialized = true;
            disposed = false;
        }

        private void LoadCalibration()
        {
            projection = null; savedIdentity = "";
#if MONMP_DEVELOPMENT
            savedPoints.Clear(); candidateProjection = null; calibrationProgress.Discard();
#endif
            string encoded = calibrationSetting?.Value ?? "";
            if (!BuiltInMapCalibration.TryResolve(encoded, SessionOptions.SupportedGameHash, null,
                out MapCalibrationSnapshot? loaded, out string status))
            {
                uiStatus = status;
                log?.Invoke("Map player markers: " + status);
                return;
            }
            ApplyCalibration(loaded!);
            uiStatus = CalibrationStatus(loaded!);
            if (loaded!.PreferenceIssue.Length != 0) log?.Invoke("Map player markers: " + status);
        }

        private void ApplyCalibration(MapCalibrationSnapshot loaded)
        {
            projection = loaded.Projection;
            savedIdentity = loaded.IdentityPrefix;
#if MONMP_DEVELOPMENT
            savedPoints.Clear(); savedPoints.AddRange(loaded.Points);
#endif
        }

        private static string CalibrationStatus(MapCalibrationSnapshot loaded)
        {
            string source = loaded.IsBuiltIn ? "built-in default" : "saved override";
            string use = loaded.Projection.UsesPiecewiseCalibration
                ? "local interpolation from " + loaded.Points.Count.ToString(CultureInfo.InvariantCulture) + " landmarks"
                : "provisional legacy affine fit from " + loaded.Points.Count.ToString(CultureInfo.InvariantCulture) + " landmarks";
            return loaded.PreferenceIssue.Length == 0
                ? "Map markers use the " + source + " calibration: " + use + ". Refine with additional widely spaced landmarks."
                : loaded.PreferenceIssue + " " + "Map markers use the built-in default: " + use + ".";
        }

#if MONMP_DEVELOPMENT
        private static string Serialize(PaperSurface surface, IReadOnlyList<MapControlPoint> points)
        {
            var values = new string[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                MapControlPoint point = points[i];
                values[i] = point.WorldX.ToString("R", CultureInfo.InvariantCulture) + "," +
                    point.WorldZ.ToString("R", CultureInfo.InvariantCulture) + "," +
                    point.PaperU.ToString("R", CultureInfo.InvariantCulture) + "," +
                    point.PaperV.ToString("R", CultureInfo.InvariantCulture);
            }
            return surface.MetadataPrefix() + "|" + string.Join(";", values);
        }
#endif

        internal void Tick()
        {
            if (!initialized || disposed) return;
            try
            {
#if MONMP_DEVELOPMENT
                TickCore();
#else
                TickRuntime();
#endif
            }
            catch (Exception ex)
            {
                if (reportedFailure != ex.GetType().Name + ": " + ex.Message)
                {
                    reportedFailure = ex.GetType().Name + ": " + ex.Message;
                    log?.Invoke("Map player markers paused after an error: " + reportedFailure);
                }
#if MONMP_DEVELOPMENT
                StopCalibration();
#endif
                ClearDisplay();
            }
        }

#if MONMP_DEVELOPMENT
        private void TickCore()
        {
            if (cursorRestorePending) TryRestoreCursor();
            if (session?.IsReady != true || ready?.Invoke() != true)
            { CancelForUi(); ClearDisplay(); poseTimes.Clear(); nativeHands = null; nativeMap = null; return; }
            if (!session.Settings.ShowPlayersOnMap)
            { CancelForUi(); ClearDisplay(); nextSurfaceDiscovery = 0; return; }

            PlayerVisualHands? hands = getNativeHands?.Invoke();
            GameObject? map = hands == null || NativeMapField == null ? null : NativeMapField.GetValue(hands) as GameObject;
            if (setupRequested && (hands != setupHands || SceneManager.GetActiveScene().handle != setupSceneHandle || Time.unscaledTime > setupDeadline))
            {
                uiStatus = "Map setup could not start: " + (surfaceFailure.Length == 0 ? "the native map changed or was unavailable." : surfaceFailure);
                setupRequested = false; setupHands = null;
            }
            if (calibrationProgress.IsPending && !IsProgressOwner(hands, map))
            {
                StopCalibration();
                uiStatus = "Calibration was discarded because the session, scene, player hands, or native map changed.";
            }
            if (hands == null || map == null || !hands.Get_IsMapOpen || !map.activeInHierarchy)
            {
                if (calibrationActive)
                    PauseCalibrationForMapClose();
                ClearDisplay(); nativeHands = hands; nativeMap = map; nextSurfaceDiscovery = 0;
                return;
            }
            if (nativeHands != hands || nativeMap != map)
            {
                if (!calibrationProgress.IsPending) StopCalibration();
                ClearDisplay(); nativeHands = hands; nativeMap = map;
                nextSurfaceDiscovery = 0;
            }

            if (paper == null && Time.unscaledTime >= nextSurfaceDiscovery)
            {
                nextSurfaceDiscovery = Time.unscaledTime + 1f;
                paper = FindPaperSurface(map);
                if (paper == null) SetSurfaceFailure("Native PortableMaps paper geometry is unavailable; click setup could not start.");
                else
                {
                    surfaceFailure = ""; reportedFailure = "";
                    ValidateSavedIdentity();
                    CreateOverlay(paper);
                    if (calibrationProgress.IsPending && !string.Equals(progressIdentity, paper.MetadataPrefix(), StringComparison.Ordinal))
                    {
                        StopCalibration();
                        uiStatus = "The native map surface changed; paused landmarks were discarded. Start calibration again.";
                    }
                }
            }
            if (paper == null) { ClearOverlay(); if (calibrationProgress.State != MapCalibrationState.Paused) HandleCalibrationKeys(); return; }
            if (paper.MapRoot != map || paper.Renderer == null || paper.Mesh == null || Time.unscaledTime >= nextSurfaceCheck)
            {
                nextSurfaceCheck = Time.unscaledTime + 0.5f;
                if (!paper.IsCurrent(map))
                {
                    if (calibrationProgress.IsPending)
                    {
                        StopCalibration();
                        uiStatus = "The native paper geometry changed; paused landmarks were discarded. Start calibration again.";
                    }
                    ClearOverlay(); paper = null; nextSurfaceDiscovery = 0;
                    return;
                }
            }
            if (overlayRoot == null) CreateOverlay(paper);
            if (setupRequested)
            {
                setupRequested = false; setupHands = null;
                StartCalibration();
            }
            HandleCalibrationKeys();
            if (calibrationActive) return;
            if (overlayRoot != null) overlayRoot.SetActive(true);
            UpdateMarkers();
        }

        private void HandleCalibrationKeys()
        {
            var keyboard = Keyboard.current;
            if (calibrationActive && keyboard?.escapeKey.wasPressedThisFrame == true)
            { StopCalibration(); uiStatus = "Calibration canceled; saved calibration is unchanged."; return; }
            if (!calibrationActive && calibrationKey != null && WasCalibrationKeyPressed(calibrationKey.Value))
            {
                if (HasNativeBindingConflict(calibrationKey.Value))
                uiStatus = "The calibration key is already bound in the native player controls. Change MapCalibrationKey in MonMP.cfg.";
                else if (calibrationProgress.State == MapCalibrationState.Paused) ResumeCalibration();
                else StartCalibration();
            }
        }

        private static bool TryGetInputSystemKey(KeyCode configured, out Key key)
        {
            string name = configured.ToString();
            if (name.StartsWith("Alpha", StringComparison.Ordinal)) name = "Digit" + name.Substring(5);
            else if (name.StartsWith("Keypad", StringComparison.Ordinal)) name = "Numpad" + name.Substring(6);
            else if (name == "Return") name = "Enter";
            else if (name == "LeftControl") name = "LeftCtrl";
            else if (name == "RightControl") name = "RightCtrl";
            else if (name == "LeftWindows") name = "LeftMeta";
            else if (name == "RightWindows") name = "RightMeta";
            return Enum.TryParse(name, true, out key) && key != Key.None;
        }

        private static bool WasCalibrationKeyPressed(KeyCode configured)
        {
            Keyboard? keyboard = Keyboard.current;
            return keyboard != null && TryGetInputSystemKey(configured, out Key key) && keyboard[key].wasPressedThisFrame;
        }

        private static bool HasNativeBindingConflict(KeyCode configured)
        {
            if (!TryGetInputSystemKey(configured, out Key key)) return true;
            GameAssets? gameAssets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            if (gameAssets == null) return false;
            string pathSuffix = "/" + key.ToString().ToLowerInvariant();
            bool Binds(InputActionMap? map)
            {
                if (map == null) return false;
                foreach (InputAction action in map.actions)
                foreach (InputBinding binding in action.bindings)
                {
                    string path = binding.effectivePath;
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            if (Binds(gameAssets.inputs.Player.Get())) return true;
            InputActionMap? vehicleMap = gameAssets.vehicleInputs?.asset?.FindActionMap("Vehicle Controls", false);
            if (Binds(vehicleMap)) return true;
            foreach (var ship in UnityEngine.Object.FindObjectsOfType<NWH.DWP2.ShipController.InputSystemShipInputProvider>())
            {
                var asset = ship.shipInputActions?.asset;
                if (asset == null) continue;
                foreach (InputActionMap actionMap in asset.actionMaps) if (Binds(actionMap)) return true;
            }
            return false;
        }

        private void StartCalibration()
        {
            if (calibrationProgress.State == MapCalibrationState.Paused) { ResumeCalibration(); return; }
            if (paper == null || nativeHands == null || !nativeHands.Get_IsMapOpen)
            { uiStatus = "Open the native map before starting calibration."; return; }
            if (localPosition == null) { uiStatus = "Local player position is unavailable."; return; }
            refiningCalibration = savedPoints.Count > 0;
            if (refiningCalibration) calibrationProgress.Begin(savedPoints);
            else calibrationProgress.Begin();
            candidateProjection = null;
            CaptureProgressOwner();
            BeginCursorCalibration();
            ClearMarkers();
            if (refiningCalibration) EvaluateCalibrationCandidate();
            else uiStatus = "Landmark 1: stand at a recognizable place, then click that place on the native paper map. Use widely spaced places.";
        }

        private void ResumeCalibration()
        {
            if (paper == null || nativeHands == null || !nativeHands.Get_IsMapOpen || !IsProgressOwner(nativeHands, nativeMap) ||
                !string.Equals(progressIdentity, paper.MetadataPrefix(), StringComparison.Ordinal))
            {
                StopCalibration();
                uiStatus = "The paused calibration belongs to a different map or session. Start again at landmark 1.";
                return;
            }
            if (!calibrationProgress.Resume()) return;
            BeginCursorCalibration();
            uiStatus = "Calibration resumed with " + calibrationProgress.Count + " landmarks. Stand at a different recognizable place, then click it on the paper.";
        }

        private void CaptureProgressOwner()
        {
            progressSession = session; progressHands = nativeHands; progressMap = nativeMap;
            progressSceneHandle = SceneManager.GetActiveScene().handle;
            progressIdentity = paper?.MetadataPrefix() ?? "";
        }

        private bool IsProgressOwner(PlayerVisualHands? hands, GameObject? map) =>
            ReferenceEquals(session, progressSession) && session?.IsRunning == true &&
            SceneManager.GetActiveScene().handle == progressSceneHandle &&
            progressHands != null && hands == progressHands && map != null && map == progressMap;

        private void BeginCursorCalibration()
        {
            previousCursorLock = Cursor.lockState; previousCursorVisible = Cursor.visible;
            calibrationSceneHandle = SceneManager.GetActiveScene().handle;
            calibrationSession = session; calibrationHands = nativeHands; cursorRestorePending = false;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true; cursorChanged = true;
            calibrationActive = true;
        }

        private void StopCalibration()
        {
            calibrationProgress.Discard();
            candidateProjection = null; refiningCalibration = false;
            calibrationActive = false;
            progressSession = null; progressHands = null; progressMap = null; progressIdentity = "";
            TryRestoreCursor();
        }

        private void PauseCalibrationForMapClose()
        {
            if (!calibrationProgress.Pause()) return;
            calibrationActive = false;
            TryRestoreCursor();
            uiStatus = "Calibration paused with " + calibrationProgress.Count + " landmarks. Reopen the map and press " +
                (calibrationKey?.Value.ToString() ?? "the calibration key") + " to resume, or use Resume.";
        }

        private void TryRestoreCursor()
        {
            if (!cursorChanged) { cursorRestorePending = false; return; }
            bool sameOwner = ReferenceEquals(session, calibrationSession) && session?.IsRunning == true &&
                SceneManager.GetActiveScene().handle == calibrationSceneHandle &&
                calibrationHands != null && getNativeHands?.Invoke() == calibrationHands;
            if (!sameOwner)
            { cursorChanged = false; cursorRestorePending = false; calibrationSession = null; calibrationHands = null; return; }
            if (ready?.Invoke() != true)
            { cursorRestorePending = true; return; }
            if (Cursor.lockState == CursorLockMode.None && Cursor.visible)
            { Cursor.lockState = previousCursorLock; Cursor.visible = previousCursorVisible; }
            cursorChanged = false; cursorRestorePending = false; calibrationSession = null; calibrationHands = null;
        }

        private void ResetCalibration()
        {
            StopCalibration();
            if (calibrationSetting != null && !BuiltInMapCalibration.TryResetPreference(
                () => calibrationSetting.Value, value => calibrationSetting.Value = value,
                () => calibrationSetting.ConfigFile.Save(), out string error))
            {
                uiStatus = "Map calibration could not be reset: " + error;
                log?.Invoke("Map player markers: calibration reset failed: " + error);
                return;
            }
            projection = null; savedIdentity = ""; ClearMarkers(); savedPoints.Clear();
            LoadCalibration();
            if (paper != null) ValidateSavedIdentity();
            if (projection != null) uiStatus = "Built-in map calibration restored from " +
                savedPoints.Count.ToString(CultureInfo.InvariantCulture) + " landmarks. Refine it to save a personal override.";
        }

        internal void CancelForUi()
        {
            setupRequested = false; setupHands = null;
            if (calibrationProgress.State == MapCalibrationState.Paused) { TryRestoreCursor(); return; }
            if (!calibrationActive && !calibrationProgress.IsPending) return;
            StopCalibration();
            uiStatus = "Calibration canceled because another interface opened; saved calibration is unchanged.";
        }
#else
        private void TickRuntime()
        {
            if (session?.IsReady != true || ready?.Invoke() != true)
            { ClearDisplay(); poseTimes.Clear(); nativeHands = null; nativeMap = null; nextSurfaceDiscovery = 0; return; }
            if (!session.Settings.ShowPlayersOnMap) { ClearDisplay(); nextSurfaceDiscovery = 0; return; }
            var hands = getNativeHands?.Invoke();
            var map = hands == null || NativeMapField == null ? null : NativeMapField.GetValue(hands) as GameObject;
            if (hands == null || map == null || !hands.Get_IsMapOpen || !map.activeInHierarchy)
            { ClearDisplay(); nativeHands = hands; nativeMap = map; nextSurfaceDiscovery = 0; return; }
            if (nativeHands != hands || nativeMap != map)
            { ClearDisplay(); nativeHands = hands; nativeMap = map; nextSurfaceDiscovery = 0; }
            if (paper == null && Time.unscaledTime >= nextSurfaceDiscovery)
            {
                nextSurfaceDiscovery = Time.unscaledTime + 1f;
                paper = FindPaperSurface(map);
                if (paper == null) SetSurfaceFailure("Native PortableMaps paper geometry is unavailable.");
                else { surfaceFailure = ""; reportedFailure = ""; ValidateSavedIdentity(); CreateOverlay(paper); }
            }
            if (paper == null) return;
            if (Time.unscaledTime >= nextSurfaceCheck)
            {
                nextSurfaceCheck = Time.unscaledTime + 0.5f;
                if (!paper.IsCurrent(map)) { ClearDisplay(); nextSurfaceDiscovery = 0; return; }
            }
            if (overlayRoot == null) CreateOverlay(paper);
            UpdateMarkers();
        }
#endif

        private void ValidateSavedIdentity()
        {
            if (paper == null) return;
            string paperIdentity = paper.MetadataPrefix();
            if (projection != null && savedIdentity.Length != 0 &&
                string.Equals(savedIdentity, paperIdentity, StringComparison.Ordinal)) return;
#if MONMP_DEVELOPMENT
            StopCalibration();
#endif
            ClearMarkers();
            if (BuiltInMapCalibration.TryResolveForPaperIdentity(calibrationSetting?.Value ?? "", SessionOptions.SupportedGameHash,
                paperIdentity, out MapCalibrationSnapshot? compatible, out string status))
            {
                ApplyCalibration(compatible!);
                uiStatus = CalibrationStatus(compatible!);
                if (compatible!.IsBuiltIn)
                {
                    uiStatus = status + " Map markers use the built-in calibration.";
                    if (compatible.PreferenceIssue.Length != 0) log?.Invoke("Map player markers: " + status);
                }
                return;
            }

            projection = null; savedIdentity = "";
#if MONMP_DEVELOPMENT
            savedPoints.Clear();
#endif
            uiStatus = status + " The saved calibration preference was preserved.";
            log?.Invoke("Map player markers: " + uiStatus);
        }

#if MONMP_DEVELOPMENT
        internal void Draw()
        {
            if (!initialized || disposed || session?.IsReady != true || ready?.Invoke() != true || nativeHands == null || !nativeHands.Get_IsMapOpen) return;
            if (paper == null)
            {
                GUI.Box(new Rect(14, 14, 500, 56), "MonMP Map Markers");
                string status = calibrationProgress.State == MapCalibrationState.Paused
                    ? "Calibration paused with " + calibrationProgress.Count + " landmarks. Waiting for the same native paper renderer."
                    : surfaceFailure.Length != 0 ? surfaceFailure : "Inspecting native paper renderer…";
                GUI.Label(new Rect(26, 40, 480, 22), status);
                return;
            }
            if (!calibrationActive)
            {
                GUI.Box(new Rect(14, 14, 510, 76), "MonMP Map Markers");
                string status = calibrationProgress.State == MapCalibrationState.Paused
                    ? "Calibration paused with " + calibrationProgress.Count + " landmarks. " + uiStatus
                    : projection == null ? (uiStatus.Length == 0 ? "No calibration saved. Match five widely spaced landmarks." : uiStatus) : uiStatus;
                GUI.Label(new Rect(26, 38, 490, 24), status);
                GUI.Label(new Rect(26, 60, 270, 22), "Calibration key: " + (calibrationKey?.Value.ToString() ?? "unbound"));
                if (GUI.Button(new Rect(310, 62, 92, 22), calibrationProgress.State == MapCalibrationState.Paused ? "Resume" : projection != null ? "Refine" : "Calibrate")) StartCalibration();
                if (projection != null && GUI.Button(new Rect(406, 62, 108, 22), "Use default")) ResetCalibration();
                return;
            }

            GUI.Box(new Rect(14, 14, 600, 102), "MonMP Map Calibration");
            GUI.Label(new Rect(26, 40, 574, 46), uiStatus + "\nLandmarks: " + calibrationProgress.Count + "/" + MapProjection.MaximumControlPoints + ". Click widely spaced recognizable places; save when ready.");
            if (GUI.Button(new Rect(378, 88, 108, 22), "Use default")) ResetCalibration();
            if (GUI.Button(new Rect(492, 88, 104, 22), "Cancel")) { StopCalibration(); uiStatus = "Calibration canceled; saved calibration is unchanged."; }
            if (calibrationProgress.Count > 0 && GUI.Button(new Rect(270, 88, 102, 22), "Undo last"))
            {
                calibrationProgress.RemoveLast();
                EvaluateCalibrationCandidate();
            }
            if (CanSaveCandidate() && GUI.Button(new Rect(166, 88, 96, 22), "Save map")) SaveCalibration();
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && !new Rect(14, 14, 600, 102).Contains(current.mousePosition))
            {
                Vector2 pixel = Mouse.current == null ? new Vector2(current.mousePosition.x, Screen.height - current.mousePosition.y) : Mouse.current.position.ReadValue();
                Camera? camera = ResolvePaperCamera(pixel);
                if (camera != null && paper.TryPick(camera, pixel, out Vector2 paperPoint))
                {
                    RecordCalibrationPoint(paperPoint);
                    current.Use();
                }
                else uiStatus = "That click missed the visible paper. Click a landmark outside this panel.";
            }
        }

        private void RecordCalibrationPoint(Vector2 paperPoint)
        {
            if (localPosition == null || calibrationSetting == null) return;
            Vector3 position = localPosition();
            if (!Finite(position.x) || !Finite(position.z)) { uiStatus = "Local position is invalid; move to the landmark and try again."; return; }
            if (!calibrationProgress.TryAdd(new MapControlPoint(position.x, position.z, paperPoint.x, paperPoint.y)))
            { uiStatus = "Sixteen landmarks is the limit. Undo a point or save the current map."; return; }
            EvaluateCalibrationCandidate();
        }

        private void EvaluateCalibrationCandidate()
        {
            candidateProjection = null;
            if (calibrationProgress.Count < MapProjection.MinimumPiecewiseControlPoints)
            {
                uiStatus = "Landmark " + calibrationProgress.Count + " of five minimum: choose a different, widely spaced place.";
                return;
            }
            if (!MapProjection.TryCreatePiecewise(calibrationProgress.Points, out MapProjection? fitted, out string error))
            {
                uiStatus = "Map fit needs wider, well-shaped coverage. Add or undo a landmark. " + error;
                return;
            }
            candidateProjection = fitted;
            double maximum = fitted!.MaximumAffineError * 100;
            string diagnostic = "Global affine max error " + maximum.ToString("0.0", CultureInfo.InvariantCulture) + "% of the paper.";
            uiStatus = refiningCalibration && !HasCalibrationChanges()
                ? "Current calibration fits. " + diagnostic + " Add a landmark to refine, or cancel to keep it. Outside the landmark area, positions are estimates."
                : "Fit ready from " + fitted.ControlPointCount + " landmarks. " + diagnostic + " Add controls or save. Outside the landmark area, positions are estimates.";
        }

        private bool HasCalibrationChanges()
        {
            if (!refiningCalibration) return true;
            if (calibrationProgress.Count != savedPoints.Count) return true;
            for (int i = 0; i < savedPoints.Count; i++)
            {
                MapControlPoint current = calibrationProgress.Points[i], prior = savedPoints[i];
                if (current.WorldX != prior.WorldX || current.WorldZ != prior.WorldZ ||
                    current.PaperU != prior.PaperU || current.PaperV != prior.PaperV) return true;
            }
            return false;
        }

        private bool CanSaveCandidate() => candidateProjection != null && HasCalibrationChanges();

        private void SaveCalibration()
        {
            if (!CanSaveCandidate() || paper == null || calibrationSetting == null) return;
            MapProjection accepted = candidateProjection!;
            var points = new List<MapControlPoint>(calibrationProgress.Points);
            string priorValue = calibrationSetting.Value;
            try
            {
                calibrationSetting.Value = Serialize(paper, points);
                calibrationSetting.ConfigFile.Save();
            }
            catch (Exception ex)
            {
                calibrationSetting.Value = priorValue;
                uiStatus = "Map calibration could not be saved: " + ex.Message;
                log?.Invoke("Map player markers: calibration save failed: " + ex.Message);
                return;
            }
            projection = accepted; savedIdentity = paper.MetadataPrefix();
            savedPoints.Clear(); savedPoints.AddRange(points);
            StopCalibration(); ClearMarkers();
            uiStatus = "Map calibrated from " + accepted.ControlPointCount + " landmarks. Outside their area, markers are estimates.";
            log?.Invoke("Map player markers calibrated: points=" + accepted.ControlPointCount.ToString(CultureInfo.InvariantCulture) +
                "; globalAffineRmsPaperFraction=" + accepted.RootMeanSquareError.ToString("0.000000", CultureInfo.InvariantCulture) +
                "; globalAffineMaxPaperFraction=" + accepted.MaximumAffineError.ToString("0.000000", CultureInfo.InvariantCulture));
        }

#endif
        private PaperSurface? FindPaperSurface(GameObject map)
        {
            Renderer[] renderers;
            try { renderers = map.GetComponentsInChildren<Renderer>(true); }
            catch { return null; }
            PaperSurface? best = null;
            int bestRank = int.MaxValue;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                MeshFilter? filter = renderer.GetComponent<MeshFilter>();
                Mesh? mesh = filter == null ? null : filter.sharedMesh;
                if (filter == null || mesh == null) continue;
                Material[] materials = renderer.sharedMaterials;
                int materialCount = Math.Min(materials.Length, mesh.subMeshCount);
                for (int subMesh = 0; subMesh < materialCount; subMesh++)
                {
                    Material material = materials[subMesh];
                    if (material == null || material.mainTexture == null) continue;
                    int rank = NativeMapGeometry.Matches(mesh, material) ? 0 : int.MaxValue;
                    if (rank >= bestRank) continue;
                    var candidate = new PaperSurface
                    {
                        MapRoot = map, Transform = filter.transform, Renderer = renderer,
                        Mesh = mesh, SubMeshIndex = subMesh, TriangleIndices = NativeMapGeometry.Triangles,
                        Vertices = NativeMapGeometry.Vertices, UV = NativeMapGeometry.UV,
                        Material = material, Texture = material.mainTexture,
                        TextureScale = material.mainTextureScale, TextureOffset = material.mainTextureOffset
                    };
                    if (!candidate.TryFitPlane()) continue;
                    best = candidate; bestRank = rank;
                }
            }
            if (best == null) SetSurfaceFailure("Native PortableMaps paper geometry is unavailable; click setup could not start.");
            return best;
        }

        private void SetSurfaceFailure(string message)
        {
            surfaceFailure = message;
            if (reportedFailure == message) return;
            reportedFailure = message;
            log?.Invoke("Map player markers: " + message);
        }

        private Camera? ResolvePaperCamera(Vector2? pixel = null)
        {
            if (paper?.Renderer == null) return null;
            Camera? best = null;
            int layer = paper.Renderer.gameObject.layer;
            // The in-hand map is rendered by the foreground camera. Camera.main can have a
            // different FOV and exclude its layer, giving a different ray from the visible paper.
            foreach (Camera camera in Camera.allCameras)
            {
                if (camera == null || !camera.isActiveAndEnabled || camera.targetTexture != null ||
                    (camera.cullingMask & (1 << layer)) == 0 ||
                    camera.gameObject.scene.handle != paper.Renderer.gameObject.scene.handle ||
                    (pixel.HasValue && !camera.pixelRect.Contains(pixel.Value))) continue;
                // Resolve equal-depth cameras consistently instead of letting camera enumeration decide.
                if (best == null || camera.depth > best.depth ||
                    (camera.depth == best.depth && camera.GetInstanceID() < best.GetInstanceID())) best = camera;
            }
            return best;
        }

        private void CreateOverlay(PaperSurface surface)
        {
            ClearOverlay();
            overlayRoot = new GameObject("MonMP transient map player markers");
            overlayRoot.layer = surface.Renderer.gameObject.layer;
            overlayRoot.transform.SetParent(surface.Transform, false);
            markerMesh = BuildMarkerMesh();
        }

        private void UpdateMarkers()
        {
            if (session == null || paper == null || projection == null || overlayRoot == null) return;
            Camera? camera = ResolvePaperCamera();
            liveIds.Clear();
            candidates.Clear(); clusterPoints.Clear();
            foreach (PeerInfo peer in session.Peers.Values)
            {
                Vector3 world;
                float alpha = 1;
                if (peer.Id == session.LocalPeerId)
                {
                    if (localPosition == null) continue;
                    world = localPosition();
                }
                else
                {
                    if (!poseTimes.TryGetValue(peer.Id, out float receivedAt)) continue;
                    if (!PlayerMarkerFreshness.TryGetOpacity(Time.unscaledTime, receivedAt, out alpha))
                    { poseTimes.Remove(peer.Id); RemoveMarker(peer.Id); continue; }
                    if (remoteMapPosition != null)
                    {
                        Vector3? mapPosition = remoteMapPosition(peer.Id);
                        if (!mapPosition.HasValue) continue;
                        world = mapPosition.Value;
                    }
                    else
                    {
                        Transform? root = remoteRoot?.Invoke(peer.Id);
                        if (root == null) continue;
                        world = root.position;
                    }
                }
                if (!projection.TryProject(world.x, world.z, out ProjectedMapPoint point)) continue;
                liveIds.Add(peer.Id);
                candidates.Add((peer, point, alpha));
            }
            // Stable peer order keeps a stack from shuffling as dictionary/snapshot order changes.
            candidates.Sort((a, b) => a.Peer.Id.CompareTo(b.Peer.Id));
            foreach (var candidate in candidates)
                clusterPoints.Add(new PlayerMapClusters.Point(candidate.Peer.Id, (float)candidate.Point.ClippedU, (float)candidate.Point.ClippedV));
            clusters.Build(clusterPoints, paper.Width, paper.Height, Mathf.Min(paper.Width, paper.Height) * 0.07f);
            for (int groupIndex = 0; groupIndex < clusters.Count; groupIndex++)
            {
                var group = clusters[groupIndex];
                string widest = ""; float dotAlpha = 0, glyphWidth = 1, glyphHeight = 1;
                foreach (int index in group.Members)
                {
                    var candidate = candidates[index];
                    string label = FormatMarkerName(candidate.Peer.DisplayName, candidate.Peer.Id, candidate.Point.IsExtrapolated);
                    var measured = EnsureMarker(candidate.Peer);
                    MeasureLabel(measured, label);
                    glyphWidth = Mathf.Max(glyphWidth, measured.GlyphWidth);
                    glyphHeight = Mathf.Max(glyphHeight, measured.GlyphHeight);
                    if (label.Length > widest.Length) widest = label;
                    dotAlpha = Mathf.Max(dotAlpha, candidate.Alpha);
                }
                for (int row = 0; row < group.Members.Count; row++)
                {
                    int index = group.Members[row]; var candidate = candidates[index];
                    UpdateMarker(EnsureMarker(candidate.Peer), candidate.Peer, candidate.Point, candidate.Alpha, camera,
                        group.U, group.V, row, group.Members.Count, widest, glyphWidth, glyphHeight, index == group.Leader, dotAlpha);
                }
            }
            removeIds.Clear();
            foreach (uint id in markers.Keys) if (!liveIds.Contains(id)) removeIds.Add(id);
            foreach (uint id in removeIds) RemoveMarker(id);
        }

        private Marker EnsureMarker(PeerInfo peer)
        {
            if (markers.TryGetValue(peer.Id, out Marker? existing)) return existing;
            if (overlayRoot == null || markerMesh == null) throw new InvalidOperationException("Map marker paper overlay is unavailable.");
            var root = new GameObject("Player " + peer.Id);
            root.layer = overlayRoot.layer;
            root.transform.SetParent(overlayRoot.transform, false);
            var marker = new Marker { Root = root };
            marker.DotOutline = MakeDot(root.transform, "Player outline", markerMesh, Color.black, 3598, out marker.OutlineRenderer, out marker.OutlineMaterial);
            marker.Dot = MakeDot(root.transform, "Player dot", markerMesh, Color.white, 3599, out marker.DotRenderer, out marker.DotMaterial);
            marker.Shadow = MakeText(root.transform, "Player name shadow", Color.black, 3600);
            marker.Name = MakeText(root.transform, "Player name", Color.white, 3601);
            markers[peer.Id] = marker;
            return marker;
        }

        private static Transform MakeDot(Transform parent, string name, Mesh mesh, Color color, int order,
            out Renderer renderer, out Material material)
        {
            var dot = new GameObject(name);
            dot.layer = parent.gameObject.layer;
            dot.transform.SetParent(parent, false);
            dot.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = dot.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = order;
            material = NewColorMaterial(color);
            renderer.sharedMaterial = material;
            return dot.transform;
        }

        private static TextMesh MakeText(Transform parent, string name, Color color, int order)
        {
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null) throw new InvalidOperationException("Unity's built-in Arial font is unavailable for map player names.");
            var holder = new GameObject(name);
            holder.layer = parent.gameObject.layer;
            holder.transform.SetParent(parent, false);
            // TextMesh's readable face is local -Z; the marker parent's +Z faces the camera.
            holder.transform.localRotation = Quaternion.Euler(0, 180, 0);
            TextMesh text = holder.AddComponent<TextMesh>();
            text.font = font;
            text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
            text.fontSize = 64; text.fontStyle = FontStyle.Bold; text.richText = false;
            text.color = color;
            Renderer renderer = holder.GetComponent<Renderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.sharedMaterial = font.material;
            renderer.sortingOrder = order;
            return text;
        }

        private static void MeasureLabel(Marker marker, string label)
        {
            if (marker.MeasuredName == label) return;
            var text = marker.Name;
            text.font.RequestCharactersInTexture(label, text.fontSize, text.fontStyle);
            float width = 0, low = 0, high = 0;
            foreach (char character in label)
                if (text.font.GetCharacterInfo(character, out var glyph, text.fontSize, text.fontStyle))
                { width += glyph.advance; low = Mathf.Min(low, glyph.minY); high = Mathf.Max(high, glyph.maxY); }
            marker.GlyphWidth = Mathf.Max(width, text.fontSize * .5f);
            // TextMesh uses a full font line, including padding outside the ink bounds.
            // Ink height alone makes adjacent generated glyph quads overlap.
            marker.GlyphHeight = Mathf.Max(high - low, text.font.lineHeight * (float)text.fontSize / Mathf.Max(1, text.font.fontSize));
            marker.MeasuredName = label;
        }

        private void UpdateMarker(Marker marker, PeerInfo peer, ProjectedMapPoint point, float alpha, Camera? camera,
            float u, float v, int row, int rows, string widest, float glyphWidth, float glyphHeight, bool showDot, float dotAlpha)
        {
            if (paper == null) return;
            string name = FormatMarkerName(peer.DisplayName, peer.Id, point.IsExtrapolated);
            MarkerLayout layout = CalculateMarkerLayout(paper.Width, paper.Height, widest, marker.Name.fontSize);
            var stack = PlayerMapLabelLayout.Calculate(paper.Width, paper.Height, layout.CharacterSize * 1.6f,
                glyphWidth, glyphHeight, layout.Radius, row, rows, u, v);
            float labelU = stack.U, labelV = stack.V;

            if (!paper.TryPoint(u, v, out Vector3 localPoint, out Vector3 facing, out Vector3 up))
            { marker.Root.SetActive(false); return; }
            marker.Root.SetActive(true);
            // Keep a fixed lift from the native triangle, but orient the dot/text to the selected
            // camera. Reversing the triangle normal as the view crosses its plane caused a 180-degree flip.
            marker.Root.transform.localPosition = localPoint + facing * MarkerSurfaceLift;
            Vector3 viewDirection = camera == null ? facing : MarkerForward(
                paper.Transform.InverseTransformPoint(camera.transform.position), localPoint, facing);
            marker.Root.transform.localRotation = Quaternion.LookRotation(viewDirection, up);
            marker.Dot.localPosition = Vector3.zero;
            marker.DotOutline.localPosition = Vector3.zero;
            marker.Dot.localScale = new Vector3(layout.Radius, layout.Radius, 1);
            marker.DotOutline.localScale = new Vector3(layout.Radius * 1.45f, layout.Radius * 1.45f, 1);

            if (!paper.TryPoint(labelU, labelV, out Vector3 labelPoint, out Vector3 labelFacing, out _))
            { labelPoint = localPoint; labelFacing = facing; }
            Vector3 surfaceNameDelta = labelPoint + labelFacing * MarkerSurfaceLift - (localPoint + facing * MarkerSurfaceLift);
            Vector3 worldNameDelta = paper.Transform.TransformVector(surfaceNameDelta);
            Vector3 namePosition = marker.Root.transform.InverseTransformVector(worldNameDelta);
            marker.Name.transform.localPosition = namePosition;
            marker.Shadow.transform.localPosition = namePosition + new Vector3(layout.Radius * 0.12f, -layout.Radius * 0.12f, 0);
            marker.Name.text = marker.Shadow.text = name;
            marker.Name.characterSize = marker.Shadow.characterSize = stack.CharacterSize;
            Color color = new Color(((peer.ColorRgb >> 16) & 255) / 255f, ((peer.ColorRgb >> 8) & 255) / 255f,
                (peer.ColorRgb & 255) / 255f, alpha);
            marker.Name.color = new Color(Mathf.Lerp(color.r, 1, 0.4f), Mathf.Lerp(color.g, 1, 0.4f), Mathf.Lerp(color.b, 1, 0.4f), alpha);
            marker.Shadow.color = new Color(0, 0, 0, alpha * 0.85f);

            marker.DotMaterial.color = rows > 1 ? new Color(1, 1, 1, dotAlpha) : color;
            marker.OutlineMaterial.color = new Color(0, 0, 0, dotAlpha);
            marker.DotRenderer.enabled = marker.OutlineRenderer.enabled = showDot && dotAlpha > 0;
        }

        private static Material NewColorMaterial(Color color)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
            if (shader == null) throw new InvalidOperationException("No Unity color shader is available for map player markers.");
            // The paper writes opaque depth first; the transparent marker remains depth-tested against it.
            return new Material(shader) { color = color, renderQueue = 3000 };
        }

        private static Mesh BuildMarkerMesh()
        {
            const int segments = 24;
            var vertices = new Vector3[segments + 1];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[segments * 3];
            vertices[0] = Vector3.zero; uv[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2 / segments;
                float x = Mathf.Cos(angle), y = Mathf.Sin(angle);
                vertices[i + 1] = new Vector3(x, y, 0); uv[i + 1] = new Vector2((x + 1) * 0.5f, (y + 1) * 0.5f);
                triangles[i * 3] = 0; triangles[i * 3 + 1] = i + 1; triangles[i * 3 + 2] = (i + 1) % segments + 1;
            }
            var mesh = new Mesh { name = "MonMP transient map dot" };
            mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles; mesh.RecalculateNormals();
            return mesh;
        }

        private void OnPose(uint peer, PlayerPose pose) => poseTimes[peer] = Time.unscaledTime;

        private void OnPeerLeft(uint peer)
        {
            poseTimes.Remove(peer); RemoveMarker(peer);
        }

        private void RemoveMarker(uint peer)
        {
            if (!markers.TryGetValue(peer, out Marker? marker)) return;
            markers.Remove(peer);
            UnityEngine.Object.Destroy(marker.Root);
            UnityEngine.Object.Destroy(marker.DotMaterial); UnityEngine.Object.Destroy(marker.OutlineMaterial);
        }

        private void ClearMarkers()
        {
            candidates.Clear(); clusterPoints.Clear(); clusters.Clear();
            if (markers.Count == 0) return;
            removeIds.Clear();
            removeIds.AddRange(markers.Keys);
            foreach (uint id in removeIds) RemoveMarker(id);
            removeIds.Clear();
        }

        private void ClearOverlay()
        {
            ClearMarkers();
            if (overlayRoot != null) UnityEngine.Object.Destroy(overlayRoot);
            overlayRoot = null;
            if (markerMesh != null) UnityEngine.Object.Destroy(markerMesh);
            markerMesh = null;
        }

        private void ClearDisplay()
        {
            ClearOverlay(); paper = null;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void DisposeBinding()
        {
            if (session != null)
            {
                session.PoseReceived -= OnPose; session.PeerLeft -= OnPeerLeft;
            }
#if MONMP_DEVELOPMENT
            StopCalibration();
#endif
            session = null; getNativeHands = null; remoteRoot = null; remoteMapPosition = null; localPosition = null; ready = null;
#if MONMP_DEVELOPMENT
            setupRequested = false; setupHands = null; setupSceneHandle = -1;
            calibrationKey = null;
#endif
            calibrationSetting = null; log = null;
            poseTimes.Clear(); liveIds.Clear(); removeIds.Clear(); ClearDisplay();
            nativeHands = null; nativeMap = null; initialized = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; DisposeBinding();
        }

    }
}
