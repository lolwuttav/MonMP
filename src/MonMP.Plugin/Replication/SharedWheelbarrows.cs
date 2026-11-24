using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Every wheelbarrow except the cottage's (SharedCottagePurchase owns that one with its contents).
    // The host owns the pose; a guest pushes one after the host names it the driver in the record,
    // then streams the pose it pushes to and releases it back to the host's physics settle.
    internal sealed class SharedWheelbarrows : IDisposable
    {
        public const ushort Adapter = 94;
        // Commands go revision-free (lease adapter): the host republishes the pose ten times a second, so a
        // revision-checked Move or Release was nearly always stale and rejected, and the barrow stayed the
        // guest's until the driver timeout ("another player is pushing" for seconds, #162).
        private const float Reach = 4.5f, SendInterval = 0.1f, DriverTimeout = 5f;
        private enum Action : byte { Grab = 1, Move = 2, Release = 3 }
        private sealed class Binding
        {
            public ulong Id;
            public Wheelbarrow Barrow = null!;
            public Rigidbody[] Bodies = Array.Empty<Rigidbody>();
            public bool[] OriginalKinematic = Array.Empty<bool>();
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
            // Host: the driving guest and the pose it last sent.
            public uint Driver;
            public float DriverSeen;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            // Guest: the record's driver and pose.
            public uint RecordDriver;
            public bool GrabPending;
            public float GrabPendingSince;
            // Host: the host itself is pushing it natively, and when its tray is next swept for new load.
            public bool HostPushing;
            public float NextLoadSweep;
        }
        private static readonly FieldInfo ActiveField = AccessTools.Field(typeof(Wheelbarrow), "isActive");
        private static readonly FieldInfo HandleField = AccessTools.Field(typeof(Wheelbarrow), "Handle_Position");
        private static readonly FieldInfo CottageField = AccessTools.Field(typeof(Wheelbarrow), "isAtTheCottage");
        // Required by the native layout check: the Harmony pickup replacement targets this method.
        private static readonly MethodInfo FreezeMethod = AccessTools.Method(typeof(Wheelbarrow), "FreezeContent", new[] { typeof(bool) });
        private static readonly MethodInfo ToGroundMethod = AccessTools.Method(typeof(Wheelbarrow), "BringBarrowBackToTheGround");
        private static readonly FieldInfo DetectorField = AccessTools.Field(typeof(Wheelbarrow), "ContentDetector");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Wheelbarrow, Binding> byBarrow = new Dictionary<Wheelbarrow, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Gameplay gameplay;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private Binding? driving;
        private WheelbarrowItemCollision? localCollision;
        private bool appliedReplica;
        private float nextCapture, nextSend;
        public int Count => bindings.Count;
        internal bool TransfersBlocked { get; set; }
        internal uint[] Owners => bindings.Values.Where(b => host ? b.Driver != 0 || b.HostPushing : b.RecordDriver != 0)
            .Select(b => host ? b.HostPushing ? session?.LocalPeerId ?? 0 : b.Driver : b.RecordDriver).Distinct().ToArray();
        internal void RevokeOwnership(uint peer)
        {
            if (!host) throw new InvalidOperationException("Only the host can revoke wheelbarrow drivers.");
            foreach (var b in bindings.Values)
            {
                if (b.Driver == peer && b.Driver != 0) Release(b, null);
                if (b.HostPushing && peer == session?.LocalPeerId)
                {
                    if (gameplay.Get_PlayerState == PlayerState.InWheelbarrow && gameplay.Get_CurrentWheelBarrow == b.Barrow) b.Barrow.HandleClick();
                    b.HostPushing = false;
                    b.Barrow.StartCoroutine(ReleaseWhenSettled(b.Barrow));
                    Publish(b);
                }
            }
        }
        internal bool LocalFlushComplete => session != null && driving == null && !Owners.Contains(session.LocalPeerId) &&
            !bindings.Values.Any(b => b.GrabPending || gameplay.Get_PlayerState == PlayerState.InWheelbarrow && gameplay.Get_CurrentWheelBarrow == b.Barrow);
        internal void FlushLocalOwnership()
        {
            if (session == null || channel?.Ready != true) return;
            foreach (var b in bindings.Values.ToArray())
            {
                bool here = gameplay.Get_PlayerState == PlayerState.InWheelbarrow && gameplay.Get_CurrentWheelBarrow == b.Barrow;
                if (host)
                {
                    if (!here && (!b.HostPushing || b.Driver != 0)) continue;
                    if (here) b.Barrow.HandleClick();
                    if (gameplay.Get_PlayerState == PlayerState.InWheelbarrow && gameplay.Get_CurrentWheelBarrow == b.Barrow) continue;
                    b.HostPushing = false;
                    b.Barrow.StartCoroutine(ReleaseWhenSettled(b.Barrow));
                    Publish(b);
                    continue;
                }
                bool pending = b.GrabPending;
                b.GrabPending = false;
                if (here || driving == b || b.RecordDriver == session.LocalPeerId || pending) ExitLocal(b, true);
            }
        }
        internal ItemCarriers? Carriers { get; set; }
        internal IEnumerable<(ulong Id, Transform Frame)> CarrierFrames()
        {
            foreach (var b in bindings.Values) if (b.Barrow != null) yield return (b.Id, b.Barrow.transform);
        }

        public SharedWheelbarrows(Gameplay gameplay, bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.gameplay = gameplay; this.host = host;
            var lines = new List<string>();
            if (ActiveField != null && HandleField != null && CottageField != null && FreezeMethod != null && ToGroundMethod != null)
                foreach (var barrow in UnityEngine.Object.FindObjectsOfType<Wheelbarrow>(true))
                {
                    if (!barrow.gameObject.scene.IsValid() || (bool)CottageField.GetValue(barrow)) continue;
                    string path = SharedBuildingDoors.StablePath(barrow.transform) + "/wheelbarrow-v1";
                    ulong id = StaticId.Hash(path);
                    if (bindings.ContainsKey(id)) continue;
                    var bodies = barrow.GetComponentsInChildren<Rigidbody>(true);
                    var b = new Binding { Id = id, Barrow = barrow, Bodies = bodies, OriginalKinematic = bodies.Select(r => r.isKinematic).ToArray(),
                        OriginalPosition = barrow.transform.position, OriginalRotation = barrow.transform.rotation };
                    bindings.Add(id, b); byBarrow.Add(barrow, b);
                    registry.Bind(id, Adapter, barrow.gameObject, path, true, EntityPersistence.SessionOnly);
                    lines.Add(id.ToString("X16") + "\t" + path + "\tbodies=" + bodies.Length);
                }
            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-wheelbarrows.tsv"), new[] { "id\tpath\tbodies" }.Concat(lines));
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, Adapter, Encode(Pose(b.Barrow.transform), 0)));

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            if (host) foreach (var b in bindings.Values) if (b.Driver == peer) Release(b, null);
        }
        private bool Near(uint peer, Vector3 at, float range) =>
            presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2f &&
            (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - at).sqrMagnitude <= range * range;

        private static byte[] Encode(ItemPose pose, uint driver)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream)) { pose.Write(w); w.Write(driver); }
            return stream.ToArray();
        }
        private static (ItemPose Pose, uint Driver) Decode(byte[] payload)
        {
            using var r = new BinaryReader(new MemoryStream(payload));
            var pose = ItemPose.Read(r); uint driver = r.ReadUInt32();
            if (r.BaseStream.Position != payload.Length) throw new InvalidDataException("Invalid wheelbarrow state.");
            return (pose, driver);
        }
        private static byte[] EncodeCommand(Action action, ItemPose pose)
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream)) { w.Write((byte)action); pose.Write(w); }
            return stream.ToArray();
        }
        private static ItemPose Pose(Transform t)
        {
            var p = t.position; var q = t.rotation;
            ItemPose.TryLocal(p.x, p.y, p.z, q.x, q.y, q.z, q.w, out var pose);
            return pose;
        }
        private static void SetPose(Transform t, ItemPose pose) =>
            t.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw));

        // Host: a guest pushes this barrow, so the host player cannot grab it.
        internal bool DrivenByGuest(Wheelbarrow barrow) => host && session?.IsRunning == true && byBarrow.TryGetValue(barrow, out var b) && b.Driver != 0;
        // Guest: native pushing runs only for the barrow the host granted this player.
        internal bool BlocksNativeUpdate(Wheelbarrow barrow) => !host && session?.IsRunning == true && byBarrow.TryGetValue(barrow, out var b) && driving != b;

        // Guest click on a shared barrow; true when handled here.
        internal bool GuestClick(Wheelbarrow barrow)
        {
            if (session?.IsRunning != true || !byBarrow.TryGetValue(barrow, out var b)) return false;
            if (TransfersBlocked && gameplay.Get_PlayerState == PlayerState.Walking) return true;
            if (host) return false;
            if (driving == b) { ExitLocal(b, true); return true; }
            if (channel?.Ready != true || driving != null || b.GrabPending) return true;
            if (b.RecordDriver != 0 || (bool)ActiveField.GetValue(barrow))
            { MonMPPlugin.Instance?.ReportItemUseUnavailable("Another player is pushing the wheelbarrow."); return true; }
            b.GrabPending = true; b.GrabPendingSince = Time.unscaledTime;
            channel.Request(b.Id, EncodeCommand(Action.Grab, Pose(barrow.transform)), true);
            return true;
        }

        private void EnterLocal(Binding b)
        {
            b.GrabPending = false;
            if (TransfersBlocked || gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject)
            { channel!.Request(b.Id, EncodeCommand(Action.Release, Pose(b.Barrow.transform)), true); return; }
            if (!gameplay.EnterExitWheelbarrow(b.Barrow, (Transform)HandleField.GetValue(b.Barrow)))
            { channel!.Request(b.Id, EncodeCommand(Action.Release, Pose(b.Barrow.transform)), true); return; }
            ActiveField.SetValue(b.Barrow, true);
            driving = b; nextSend = Time.unscaledTime + SendInterval;
            localCollision = new WheelbarrowItemCollision(b.Barrow.transform);
        }

        private void ExitLocal(Binding b, bool notifyHost)
        {
            if (driving == b) { driving = null; localCollision = null; }
            ActiveField.SetValue(b.Barrow, false);
            if (gameplay.Get_PlayerState == PlayerState.InWheelbarrow && gameplay.Get_CurrentWheelBarrow == b.Barrow)
                gameplay.EnterExitWheelbarrow(b.Barrow, (Transform)HandleField.GetValue(b.Barrow));
            if (b.Barrow.transform.parent != null && b.Barrow.transform.parent == gameplay.Player.transform) b.Barrow.transform.SetParent(null);
            if (notifyHost && channel?.Ready == true) channel.Request(b.Id, EncodeCommand(Action.Release, Pose(b.Barrow.transform)), true);
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedWheelbarrows.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var b) || payload.Length != 29) return;
            Action action; ItemPose pose;
            try
            {
                using var r = new BinaryReader(new MemoryStream(payload));
                action = (Action)r.ReadByte(); pose = ItemPose.Read(r);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { return; }
            var at = new Vector3(pose.X, pose.Y, pose.Z);
            switch (action)
            {
                case Action.Grab:
                    if (b.Driver == peer) { Publish(b); return; }
                    if (TransfersBlocked || b.Driver != 0 || (bool)ActiveField.GetValue(b.Barrow) || !Near(peer, b.Barrow.transform.position, Reach)) { Publish(b); return; }
                    b.Barrow.StopAllCoroutines();
                    b.Driver = peer; b.DriverSeen = Time.unscaledTime;
                    b.TargetPosition = b.Barrow.transform.position; b.TargetRotation = b.Barrow.transform.rotation;
                    FreezeLoad(b.Barrow, gameplay, false);
                    Publish(b);
                    return;
                case Action.Move:
                    if (b.Driver != peer || !Near(peer, at, 6f)) return;
                    b.DriverSeen = Time.unscaledTime;
                    b.TargetPosition = at; b.TargetRotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
                    return;
                case Action.Release:
                    if (b.Driver != peer) return;
                    Release(b, Near(peer, at, 6f) ? pose : (ItemPose?)null);
                    return;
            }
        }

        private void Release(Binding b, ItemPose? final)
        {
            b.Driver = 0;
            if (b.Barrow == null) return;
            if (final is ItemPose pose) SetPose(b.Barrow.transform, pose);
            else b.Barrow.transform.SetPositionAndRotation(b.TargetPosition, b.TargetRotation);
            if (ToGroundMethod.Invoke(b.Barrow, null) is System.Collections.IEnumerator settle) b.Barrow.StartCoroutine(settle);
            b.Barrow.StartCoroutine(ReleaseWhenSettled(b.Barrow));
            Publish(b);
        }

        // Native FreezeContent takes every item the content detector's world box touches, and a tilted
        // barrow's world box reaches well past the tray: a guest's grab swallowed items it had clipped on
        // the ground (#162). Take only what sits in the tray itself; frozen items ride as the barrow's
        // children and are shared relative to it.
        // settledOnly: a sweep during a push takes only what has come to rest, not an item still falling in.
        internal static void FreezeLoad(Wheelbarrow barrow, Gameplay gameplay, bool settledOnly)
        {
            var detector = DetectorField?.GetValue(barrow) as MeshRenderer;
            var mesh = detector == null ? null : detector.GetComponent<MeshFilter>()?.sharedMesh;
            if (detector == null || mesh == null) return;
            var tray = mesh.bounds;
            var world = detector.bounds;
            var tool = gameplay.Player.Get_ActiveTool;
            var held = gameplay.Player.Get_HoldingItem;
            foreach (var collider in Physics.OverlapBox(world.center, world.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            {
                var item = collider.GetComponent<InteractableItems>();
                if (item == null || !item.HasARigidBody || item == tool || item == held) continue;
                // Already frozen. A child that has its body back was left parented by a native unfreeze and
                // must be taken again: as a loose body it trailed the guest's barrow in world space and
                // blocked the guest's walk (#162).
                if (item.rb == null && item.transform.IsChildOf(barrow.transform)) continue;
                uint? owner = MonMPPlugin.Instance?.Items?.LeaseOwnerOf(item);
                if (owner.HasValue && owner.Value != ItemLease.Unowned) continue;
                // Kinematic: in a hand, or following a guest who holds it.
                if (item.rb != null && (item.rb.isKinematic || settledOnly && item.rb.velocity.sqrMagnitude > 4f)) continue;
                var at = item.itemCollider != null ? item.itemCollider.bounds.center : item.transform.position;
                if (!tray.Contains(detector.transform.InverseTransformPoint(at))) continue;
                // A barrow pushed into a wall reaches through it; what lies behind the wall is not in the tray.
                if (Walled(barrow, world.center, at)) continue;
                item.FreezeInContainer(barrow.transform);
            }
        }

        private static readonly RaycastHit[] wallHits = new RaycastHit[16];
        private static bool Walled(Wheelbarrow barrow, Vector3 from, Vector3 to)
        {
            var span = to - from;
            float distance = span.magnitude;
            if (distance < 0.01f) return false;
            int count = Physics.RaycastNonAlloc(from, span / distance, wallHits, distance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var hit = wallHits[i].collider;
                if (hit == null || hit.transform.IsChildOf(barrow.transform) || hit.attachedRigidbody != null && !hit.attachedRigidbody.isKinematic) continue;
                if (hit.GetComponentInParent<InteractableItems>() != null) continue;
                return true;
            }
            return false;
        }

        // Native unfreezing re-adds the bodies of what is still in the detector box but leaves every frozen
        // item parented to the barrow, so a load that slid out of the box stayed a body-less child: the
        // starter tire rode inside the barrow, out of sight (#168). Free all of it.
        // The native settle tips the barrow onto its legs over a second or two; a load freed before that
        // got its bodies back inside a tray still turning and fell through it (#162). Free it once the
        // barrow has stopped turning, unless someone took it up again meanwhile.
        private System.Collections.IEnumerator ReleaseWhenSettled(Wheelbarrow barrow)
        {
            float until = Time.time + 5f; int still = 0;
            var last = barrow.transform.rotation;
            while (barrow != null && Time.time < until && still < 6)
            {
                yield return null;
                if (barrow == null) yield break;
                var now = barrow.transform.rotation;
                still = Quaternion.Angle(now, last) < 0.01f ? still + 1 : 0;
                last = now;
            }
            if (barrow == null || (bool)ActiveField.GetValue(barrow) || byBarrow.TryGetValue(barrow, out var b) && b.Driver != 0) yield break;
            ReleaseLoad(barrow);
        }

        private static void ReleaseLoad(Wheelbarrow barrow)
        {
            foreach (var item in barrow.GetComponentsInChildren<InteractableItems>(true))
            {
                if (item == null || item.transform == barrow.transform) continue;
                item.FreezeInContainer(null);
                item.transform.SetParent(null, true);
            }
        }

        private void Publish(Binding b)
        {
            if (b.Barrow == null || channel == null) return;
            channel.Publish(b.Id, Encode(Pose(b.Barrow.transform), b.Driver));
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b)) Apply(b, record.Payload, true);
            appliedReplica = true;
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter || !bindings.TryGetValue(record.Id, out var b)) return;
            Apply(b, record.Payload, false);
        }
        private void Apply(Binding b, byte[] payload, bool snap)
        {
            if (b.Barrow == null) return;
            var (pose, driver) = Decode(payload);
            uint self = session?.LocalPeerId ?? 0;
            b.RecordDriver = driver;
            // The host's physics would otherwise fight the replicated pose.
            foreach (var body in b.Bodies) if (body != null) body.isKinematic = true;
            if (driving == b)
            {
                // The host released us (timeout, range): stop pushing and follow its pose.
                if (driver != self) ExitLocal(b, false);
                else return;
            }
            b.TargetPosition = new Vector3(pose.X, pose.Y, pose.Z); b.TargetRotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
            if (b.GrabPending && driver == self) { SetPose(b.Barrow.transform, pose); EnterLocal(b); return; }
            if (b.GrabPending && driver != 0) b.GrabPending = false;
            // Smoothed in Update; far corrections (join, reset) snap.
            if (snap || (b.Barrow.transform.position - b.TargetPosition).sqrMagnitude > 9f) SetPose(b.Barrow.transform, pose);
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true) return;
            float now = Time.unscaledTime;
            if (host)
            {
                foreach (var b in bindings.Values)
                {
                    if (b.Barrow == null) continue;
                    bool hostPushing = b.Driver == 0 && (bool)ActiveField.GetValue(b.Barrow);
                    // Native unfreezing leaves the load parented to the barrow (#168); free it as a guest release does.
                    if (b.HostPushing && !hostPushing && b.Driver == 0) b.Barrow.StartCoroutine(ReleaseWhenSettled(b.Barrow));
                    b.HostPushing = hostPushing;
                    // The barrow is moved by its transform, so a loose item put in during a push is left behind
                    // and falls out (#162). Freeze it in like the load taken at the start.
                    if ((b.Driver != 0 || hostPushing) && now >= b.NextLoadSweep) { b.NextLoadSweep = now + 0.25f; FreezeLoad(b.Barrow, gameplay, true); }
                    if (b.Driver == 0) continue;
                    if (now - b.DriverSeen > DriverTimeout) { Release(b, null); continue; }
                    // The load was frozen into the barrow on the grab and moves as its children. Carrying
                    // riders too dragged along any loose item the barrow's box clipped (#162).
                    b.Barrow.transform.SetPositionAndRotation(
                        Vector3.Lerp(b.Barrow.transform.position, b.TargetPosition, 1 - Mathf.Exp(-18 * Time.unscaledDeltaTime)),
                        Quaternion.Slerp(b.Barrow.transform.rotation, b.TargetRotation, 1 - Mathf.Exp(-18 * Time.unscaledDeltaTime)));
                }
                if (now < nextCapture) return;
                nextCapture = now + 0.1f;
                foreach (var b in bindings.Values) Publish(b);
                return;
            }
            float blend = 1 - Mathf.Exp(-14 * Time.unscaledDeltaTime);
            foreach (var b in bindings.Values)
            {
                if (b.GrabPending && now - b.GrabPendingSince > 3) b.GrabPending = false;
                if (b == driving || b.Barrow == null || !appliedReplica) continue;
                var t = b.Barrow.transform;
                if ((t.position - b.TargetPosition).sqrMagnitude < 1e-6f && Quaternion.Dot(t.rotation, b.TargetRotation) > 0.99999f) continue;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, b.TargetPosition, blend), Quaternion.Slerp(t.rotation, b.TargetRotation, blend));
            }
            if (driving != null)
            {
                if (gameplay.Get_PlayerState != PlayerState.InWheelbarrow) { ExitLocal(driving, true); return; }
                ConstrainLocalMotion();
                if (now >= nextSend)
                {
                    nextSend = now + SendInterval;
                    channel.Request(driving.Id, EncodeCommand(Action.Move, Pose(driving.Barrow.transform)), true);
                }
            }
        }

        internal void ConstrainLocalMotion()
        {
            if (!host && driving != null && gameplay.Get_CurrentWheelBarrow == driving.Barrow && gameplay.PlayerWalking != null)
                localCollision?.Constrain(gameplay.PlayerWalking.transform);
        }

        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host)
            {
                if (driving != null) ExitLocal(driving, false);
                if (appliedReplica)
                    foreach (var b in bindings.Values)
                    {
                        if (b.Barrow == null) continue;
                        for (int i = 0; i < b.Bodies.Length; i++) if (b.Bodies[i] != null) b.Bodies[i].isKinematic = b.OriginalKinematic[i];
                        b.Barrow.transform.SetPositionAndRotation(b.OriginalPosition, b.OriginalRotation);
                    }
            }
            else foreach (var b in bindings.Values) if (b.Driver != 0) Release(b, null);
            session = null; channel = null; presence.Clear();
        }
    }
}
