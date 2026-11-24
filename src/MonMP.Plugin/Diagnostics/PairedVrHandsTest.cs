using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The optional companion test assembly drives the actual MonVR game rig.
    // Files synchronize the assertions only. All item data crosses the normal
    // world channel between independent Unity processes.
    internal sealed class PairedVrHandsTest : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay game;
        private readonly string proof;
        private readonly float started = Time.unscaledTime;
        private int phase;
        private ulong left, right, cancelled;
        private uint cancelGeneration;
        private Vector3 l, r;
        private GameObject? platform;
        private float settle, traceAt;
        private IDisposable? driver;
        private Func<Vector3, Vector3, bool>? sample;
        private Action<bool, bool, bool>? input;
        private Func<bool, bool>? held;
        internal string Status => "VR hands phase=" + phase + " left=" + left.ToString("X") + " right=" + right.ToString("X");

        internal PairedVrHandsTest(MonMPPlugin plugin, Gameplay game)
        {
            this.plugin = plugin; this.game = game;
            proof = Environment.GetEnvironmentVariable("MONVR_PAIR_EVIDENCE") ?? throw new InvalidOperationException("Missing isolated evidence directory.");
        }
        private void AddPlatform(Vector3 tools)
        {
            platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "Isolated wrist fixture floor";
            platform.transform.localScale = new Vector3(5, .2f, 5);
            platform.transform.position = tools - Vector3.up * 1.35f;
            Physics.SyncTransforms();
        }
        private string FileAt(string name) => Path.Combine(proof, name);
        private void Signal(string name) => File.WriteAllText(FileAt(name), Status);
        private bool Seen(string name) => File.Exists(FileAt(name));

        internal bool Tick()
        {
            if (Time.unscaledTime - started > 80) throw new InvalidOperationException("Paired wrist fixture stalled: " + Status);
            if (plugin.World?.Channel.FullySynchronized != true || plugin.Items?.Ready != true) return false;
            var items = plugin.Items;
            if (left != 0 && right != 0 && Time.unscaledTime >= traceAt && items.ItemOf(left) != null && items.ItemOf(right) != null)
            {
                traceAt = Time.unscaledTime + 2f;
                Debug.Log("MONMP_VR_HANDS " + Status + " left #" + items.ItemOf(left)!.gameObject.GetInstanceID() + " " + items.TestPickupStatus(left) +
                    " right #" + items.ItemOf(right)!.gameObject.GetInstanceID() + " " + items.TestPickupStatus(right));
            }
            return plugin.TestOptions.IsHost ? Host(items) : Guest(items);
        }

        private static Vector3 Position(ItemState state) => new Vector3(state.Pose.X, state.Pose.Y, state.Pose.Z);
        private void VerifyHostOcclusion(Vector3 root)
        {
            var test = plugin.World!.ToolAim.CheckSegment;
            var pose = new Core.Protocol.PlayerPose(root.x, root.y, root.z, 0, 0, 0);
            bool Fits() => ToolAimPolicy.Fits(pose, true, root.x + 1, root.y + 1.2f, root.z, 180, 0, test);
            if (!Fits()) throw new InvalidOperationException("Clear host tool ray was refused.");
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.transform.position = root + new Vector3(.5f, 1.3f, 0);
                wall.transform.localScale = new Vector3(.1f, 2, 1);
                Physics.SyncTransforms();
                if (Fits()) throw new InvalidOperationException("A real solid failed to block the host hand ray.");
                Signal("host-wall-blocked");
            }
            finally { UnityEngine.Object.Destroy(wall); }
        }

        private bool Host(SharedItems items)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                var center = game.Player.mainCamera.transform.position + Vector3.up * 7f;
                VerifyHostOcclusion(center + Vector3.up * 4f);
                AddPlatform(center);
                left = items.TestSpawnPresentationTool(typeof(Tool_AdjustableSpanner), center - Vector3.right * .32f);
                right = items.TestSpawnPresentationTool(typeof(Tool_AdjustableSpanner), center + Vector3.right * .32f);
                File.WriteAllLines(FileAt("objects.tmp"), new[] {left.ToString(CultureInfo.InvariantCulture), right.ToString(CultureInfo.InvariantCulture)});
                File.Move(FileAt("objects.tmp"), FileAt("objects"));
                phase++;
                return false;
            }
            var a = items.TestState(left); var b = items.TestState(right);
            var guest = plugin.Session!.Peers.Keys.FirstOrDefault(id => id != 0);
            if (guest == 0) return false;
            if (phase == 1 && a.Lease.Owner == guest && b.Lease.Owner == guest && Seen("both"))
            { l = Position(a); r = Position(b); Signal("move"); phase++; }
            else if (phase == 2 && a.Lease.Owner == guest && b.Lease.Owner == guest &&
                Position(a).y > l.y + .12f && Position(b).y > r.y + .12f && items.HeldItemOf(guest) == items.ItemOf(left))
            { Signal("switch"); phase++; }
            else if (phase == 3 && items.HeldItemOf(guest) == items.ItemOf(right) && a.Lease.Owner == guest && b.Lease.Owner == guest)
            { Signal("release-left"); phase++; }
            else if (phase == 4 && a.Lease.Owner == ItemLease.Unowned && b.Lease.Owner == guest)
            { Signal("release-right"); phase++; }
            else if (phase == 5 && a.Lease.Owner == ItemLease.Unowned && b.Lease.Owner == ItemLease.Unowned &&
                !items.ItemOf(left)!.rb.isKinematic && !items.ItemOf(right)!.rb.isKinematic)
            {
                cancelled = items.TestSpawnPresentationTool(typeof(Tool_AdjustableSpanner), (l + r) * .5f + Vector3.up * .3f);
                cancelGeneration = items.TestState(cancelled).Lease.Generation;
                File.WriteAllText(FileAt("cancel-tool.tmp"), cancelled.ToString(CultureInfo.InvariantCulture));
                File.Move(FileAt("cancel-tool.tmp"), FileAt("cancel-tool"));
                phase++;
            }
            else if (phase == 6 && Seen("provider-released"))
            {
                var result = items.TestState(cancelled);
                if (result.Lease.Owner == ItemLease.Unowned && result.Lease.Generation != cancelGeneration &&
                    !items.ItemOf(cancelled)!.rb.isKinematic)
                { Signal("done"); phase++; }
            }
            return phase == 7 && Seen("ack");
        }

        private bool Guest(SharedItems items)
        {
            if (!Seen("objects")) return false;
            if (phase == 0)
            {
                var ids = File.ReadAllLines(FileAt("objects"));
                left = ulong.Parse(ids[0], CultureInfo.InvariantCulture); right = ulong.Parse(ids[1], CultureInfo.InvariantCulture);
                if (items.ItemOf(left) == null || items.ItemOf(right) == null) return false;
                l = items.ItemOf(left)!.itemCollider.bounds.center; r = items.ItemOf(right)!.itemCollider.bounds.center;
                AddPlatform((l + r) * .5f);
                var pos = (l + r) * .5f - Vector3.forward * .55f - Vector3.up * .35f;
                game.PlayerWalking.transform.position = pos; game.Get_Player_Rigidbody.position = pos; game.Get_Player_Rigidbody.velocity = Vector3.zero;
                string file = Environment.GetEnvironmentVariable("MONVR_PAIR_DRIVER") ?? throw new InvalidOperationException("Missing MonVR paired test assembly.");
                string assemblyDirectory = Path.GetDirectoryName(file)!;
                Assembly.LoadFrom(Path.Combine(assemblyDirectory, "MonVR.Core.dll"));
                Assembly.LoadFrom(Path.Combine(assemblyDirectory, "MonVR.dll"));
                var type = Assembly.LoadFrom(file).GetType("MonVR.PairedHands.GuestHands", true)!;
                driver = (IDisposable)Activator.CreateInstance(type)!;
                sample = (Func<Vector3,Vector3,bool>)type.GetMethod("Step")!.CreateDelegate(typeof(Func<Vector3,Vector3,bool>), driver);
                input = (Action<bool,bool,bool>)type.GetMethod("Input")!.CreateDelegate(typeof(Action<bool,bool,bool>), driver);
                held = (Func<bool,bool>)type.GetMethod("Held")!.CreateDelegate(typeof(Func<bool,bool>), driver);
                settle = Time.unscaledTime + .6f; phase++;
            }
            if (phase == 8)
            {
                if (items.TestLocalHeld(cancelled)) throw new InvalidOperationException("Expired pickup became a native hold.");
                if (!Seen("done") || items.TestPickupPending(cancelled) || items.TestState(cancelled).Lease.Owner != ItemLease.Unowned) return false;
                Signal("ack"); Dispose(); return true;
            }
            if (!sample!(l,r) || Time.unscaledTime < settle) return false;
            if (phase == 1)
            {
                l = items.ItemOf(left)!.itemCollider.bounds.center; r = items.ItemOf(right)!.itemCollider.bounds.center;
                sample(l,r); input!(true,true,true); input(false,true,true);
                Debug.Log("MONMP_VR_HANDS requests: " + items.TestPickupStatus(left) + " / " + items.TestPickupStatus(right));
                phase++;
            }
            else if (phase == 2 && held!(true) && held(false) && items.TestLocalHeld(left) && items.TestLocalHeld(right))
            { Signal("both"); phase++; }
            else if (phase == 3 && Seen("move"))
            {
                l += Vector3.up * .25f; r += Vector3.up * .35f; sample(l,r);
                input!(true,false,true); input(true,false,false);
                if (game.Player.Get_ActiveTool != items.ItemOf(left)) throw new InvalidOperationException("Left tool failed to equip.");
                phase++;
            }
            else if (phase == 4 && Seen("switch"))
            {
                input!(false,false,true); input(false,false,false);
                if (game.Player.Get_ActiveTool != items.ItemOf(right)) throw new InvalidOperationException("Right tool failed to equip.");
                phase++;
            }
            else if (phase == 5 && Seen("release-left"))
            { input!(true,true,false); if (!held!(false)) throw new InvalidOperationException("Right tool lost on left release."); phase++; }
            else if (phase == 6 && Seen("release-right")) { input!(false,true,false); phase++; }
            else if (phase == 7 && Seen("cancel-tool"))
            {
                cancelled = ulong.Parse(File.ReadAllText(FileAt("cancel-tool")), CultureInfo.InvariantCulture);
                var target = items.ItemOf(cancelled);
                if (target == null) return false;
                l = target.itemCollider.bounds.center;
                sample(l, r); input!(true, true, true);
                if (!items.TestPickupPending(cancelled)) throw new InvalidOperationException("No pickup ticket before provider release.");
                driver!.Dispose(); driver = null;
                Signal("provider-released"); phase++;
            }
            return false;
        }
        public void Dispose() { driver?.Dispose(); driver = null; if (platform != null) UnityEngine.Object.Destroy(platform); platform = null; }
    }
}
