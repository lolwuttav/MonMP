using System;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Never registered with the game's save system; exists only during this synchronous test.
    internal sealed class WheelbarrowCollisionTestItem : InteractableItems
    {
        public override bool IgnoreSaveSystem => true;
        public override void Awake() { }
        public override void Start() { }
        public override void OnDestroy() { }
    }

    internal static class NativeWheelbarrowCollisionTest
    {
        internal static void Run()
        {
            var native = UnityEngine.Object.FindObjectsOfType<Wheelbarrow>(true);
            foreach (var barrow in native)
                Require(new WheelbarrowItemCollision(barrow.transform).TestShapeCount != 0, "A native wheelbarrow has no solid collision shapes: " + barrow.name);
            var group = new GameObject("MonMP temporary wheelbarrow collision test");
            group.transform.position = new Vector3(1000, -1000, 1000);
            try
            {
                var player = new GameObject("temporary pusher").transform;
                player.SetParent(group.transform, false);
                var tray = new GameObject("temporary tray").transform;
                tray.SetParent(player, false); tray.localPosition = Vector3.forward;
                var shape = tray.gameObject.AddComponent<BoxCollider>(); shape.size = new Vector3(0.8f, 0.4f, 1f);
                var prop = new GameObject("temporary loose replica");
                prop.transform.SetParent(group.transform, false); prop.transform.position = tray.position + Vector3.forward * 0.9f;
                var obstacle = prop.AddComponent<BoxCollider>(); obstacle.size = Vector3.one * 0.4f;
                var body = prop.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
                var item = prop.AddComponent<WheelbarrowCollisionTestItem>(); item.rb = body; item.itemCollider = obstacle;
                var untouched = prop.transform.position;
                Physics.SyncTransforms();
                var guard = new WheelbarrowItemCollision(tray);
                player.position += Vector3.forward * 1.6f; Physics.SyncTransforms();
                Require(guard.Constrain(player) && tray.position.z < prop.transform.position.z, "Swept tray passed through a kinematic loose item.");
                Require(prop.transform.position == untouched && item.rb == body && body.isKinematic && !obstacle.isTrigger && prop.transform.parent == group.transform,
                    "Collision guard changed item physics, ownership or cargo parenting.");
                player.position -= Vector3.forward * 0.5f; Physics.SyncTransforms();
                Require(!guard.Constrain(player), "Pusher could not back away from contact.");
                obstacle.isTrigger = true;
                guard = new WheelbarrowItemCollision(tray);
                player.position += Vector3.forward * 2f; Physics.SyncTransforms();
                Require(!guard.Constrain(player), "A held/trigger item blocked the tray.");
                obstacle.isTrigger = false; prop.transform.SetParent(tray, true);
                guard = new WheelbarrowItemCollision(tray);
                player.position += Vector3.forward * 0.5f; Physics.SyncTransforms();
                Require(!guard.Constrain(player), "Cargo in this tray blocked its own carrier.");
                prop.transform.SetParent(group.transform, true);
                prop.transform.position = tray.position + Vector3.right * 0.75f;
                shape.size = new Vector3(0.8f, 0.4f, 1.8f); tray.rotation = Quaternion.identity;
                Physics.SyncTransforms(); guard = new WheelbarrowItemCollision(tray);
                tray.rotation = Quaternion.Euler(0, 90, 0); Physics.SyncTransforms();
                Require(guard.Constrain(player) && Quaternion.Angle(Quaternion.identity, tray.rotation) < 90f,
                    "Turning in place swept the tray through a loose item.");
                Debug.Log("MonMP native wheelbarrow collision passed: " + native.Length + " native shape sets; swept translation and rotation, kinematic item unchanged, backing away, held/trigger exclusion and own cargo exclusion.");
            }
            finally { UnityEngine.Object.DestroyImmediate(group); Physics.SyncTransforms(); }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
