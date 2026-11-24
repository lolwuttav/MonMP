using System;
using System.Diagnostics;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace MonMP.Plugin.Diagnostics
{
    // Never registered with the game's save system; exists only during this synchronous probe.
    internal sealed class CargoWheelProbeItem : InteractableItems
    {
        public override bool IgnoreSaveSystem => true;
        public override void Awake() { }
        public override void Start() { }
        public override void OnDestroy() { }
    }

    internal sealed class CargoWheelProbeContacts : MonoBehaviour
    {
        internal int Enters, Exits;
        private void OnCollisionEnter(Collision collision) => Enters++;
        private void OnCollisionExit(Collision collision) => Exits++;
    }

    // Measures the frozen-cargo wheel query: what one hide/restore scope costs on the native
    // engine, and whether swapping a pinned load's layer around every physics step disturbs
    // the contacts of loose cargo riding on it. Logs only; it never fails its host suite.
    internal static class NativeCargoWheelQueryProbe
    {
        internal static void Run()
        {
            try { ScopeCost(); }
            catch (Exception ex) { Debug.LogWarning("MONMP_CARGO_WHEEL_QUERY_PROBE scope cost failed: " + ex); }
            try { foreach (bool swap in new[] { false, true }) Contacts(swap); }
            catch (Exception ex) { Debug.LogWarning("MONMP_CARGO_WHEEL_QUERY_PROBE contacts failed: " + ex); }
        }

        private static void ScopeCost()
        {
            var group = new GameObject("MonMP temporary cargo wheel query probe");
            group.transform.position = new Vector3(1000, -1000, 1000);
            try
            {
                foreach (int count in new[] { 1, 4, 16 })
                {
                    var root = new GameObject("cargo " + count);
                    root.transform.SetParent(group.transform, false);
                    var item = root.AddComponent<CargoWheelProbeItem>();
                    var shapes = new Collider[count];
                    for (int i = 0; i < count; i++)
                    {
                        var child = new GameObject("shape " + i);
                        child.transform.SetParent(root.transform, false); child.transform.localPosition = Vector3.right * i;
                        shapes[i] = child.AddComponent<BoxCollider>();
                    }
                    Physics.SyncTransforms();
                    const int Iterations = 20000;
                    for (int i = 0; i < 200; i++) Scope(shapes, item, false);
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < Iterations; i++) Scope(shapes, item, false);
                    double swap = watch.Elapsed.TotalMilliseconds * 1000.0 / Iterations;
                    watch.Restart();
                    for (int i = 0; i < Iterations; i++) Scope(shapes, item, true);
                    double owned = watch.Elapsed.TotalMilliseconds * 1000.0 / Iterations;
                    watch.Restart();
                    var ray = new Ray(root.transform.position + Vector3.up * 2, Vector3.down);
                    for (int i = 0; i < Iterations; i++) Physics.Raycast(ray, 4f, ~0, QueryTriggerInteraction.Ignore);
                    double raycast = watch.Elapsed.TotalMilliseconds * 1000.0 / Iterations;
                    Debug.Log("MONMP_CARGO_WHEEL_QUERY_PROBE scope shapes=" + count + " swapUs=" + swap.ToString("F3") +
                        " withOwnerLookupUs=" + owned.ToString("F3") + " referenceRaycastUs=" + raycast.ToString("F3"));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(group); }
        }

        // One wheel's HitUpdate scope: the pin's owner check per shape, hide, restore.
        private static void Scope(Collider[] shapes, InteractableItems item, bool ownerLookup)
        {
            var scope = FrozenCargoWheelQuery.Rent();
            foreach (var shape in shapes)
                if (!ownerLookup || shape.GetComponentInParent<InteractableItems>() == item) scope.Hide(shape);
            scope.Dispose();
        }

        // A kinematic bed carries a pinned kinematic load with a loose dynamic box on top,
        // accelerating gently enough that friction alone should hold the loose box. The
        // swap run applies the per-step layer change exactly as the wheel query does.
        private static void Contacts(bool swap)
        {
            var scene = SceneManager.CreateScene("MonMP cargo wheel probe " + (swap ? "swap" : "control"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var physics = scene.GetPhysicsScene();
            var root = new GameObject("probe");
            SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var bed = Body(root, "bed", new Vector3(4, 0.2f, 4), Vector3.zero, kinematic: true);
                var pinned = Body(root, "pinned", new Vector3(0.8f, 0.6f, 0.8f), new Vector3(0, 0.4f, 0), kinematic: true);
                var loose = Body(root, "loose", new Vector3(0.4f, 0.4f, 0.4f), new Vector3(0, 0.9f, 0), kinematic: false);
                loose.mass = 5;
                var contacts = loose.gameObject.AddComponent<CargoWheelProbeContacts>();
                var pinnedShape = pinned.GetComponent<Collider>();
                float dt = Time.fixedDeltaTime > 0 ? Time.fixedDeltaTime : 0.02f;
                for (int i = 0; i < 100; i++) physics.Simulate(dt);
                int settledEnters = contacts.Enters, settledExits = contacts.Exits;
                Vector3 start = loose.position - pinned.position;
                float speed = 0, maxSlip = 0, maxRelativeSpeed = 0;
                var watch = new Stopwatch();
                for (int i = 0; i < 400; i++)
                {
                    speed = Mathf.Min(speed + 3f * dt, 15f);
                    var step = Vector3.right * speed * dt;
                    bed.MovePosition(bed.position + step); pinned.MovePosition(pinned.position + step);
                    if (swap)
                    {
                        watch.Start();
                        var scope = FrozenCargoWheelQuery.Rent(); scope.Hide(pinnedShape);
                        physics.Raycast(bed.position + Vector3.up * 3 + Vector3.forward * 1.5f, Vector3.down, out _, 5f, ~(1 << 2));
                        scope.Dispose();
                        watch.Stop();
                    }
                    physics.Simulate(dt);
                    var offset = loose.position - pinned.position - start;
                    maxSlip = Mathf.Max(maxSlip, new Vector2(offset.x, offset.z).magnitude);
                    maxRelativeSpeed = Mathf.Max(maxRelativeSpeed, Mathf.Abs(loose.velocity.x - speed));
                }
                Debug.Log("MONMP_CARGO_WHEEL_QUERY_PROBE contacts swap=" + swap + " ignoreLayer0vs2=" + Physics.GetIgnoreLayerCollision(0, 2) +
                    " steps=400 dt=" + dt.ToString("F3") + " settleEnters=" + settledEnters + " settleExits=" + settledExits +
                    " rideEnters=" + (contacts.Enters - settledEnters) + " rideExits=" + (contacts.Exits - settledExits) +
                    " maxSlipMm=" + (maxSlip * 1000).ToString("F2") + " maxRelativeSpeed=" + maxRelativeSpeed.ToString("F3") +
                    " finalSpeed=" + speed.ToString("F1") + " swapMsTotal=" + watch.Elapsed.TotalMilliseconds.ToString("F3"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                SceneManager.UnloadSceneAsync(scene);
            }
        }

        private static Rigidbody Body(GameObject root, string name, Vector3 size, Vector3 position, bool kinematic)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false); go.transform.localPosition = position;
            go.AddComponent<BoxCollider>().size = size;
            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = kinematic; body.useGravity = !kinematic;
            body.interpolation = RigidbodyInterpolation.None;
            return body;
        }
    }
}
