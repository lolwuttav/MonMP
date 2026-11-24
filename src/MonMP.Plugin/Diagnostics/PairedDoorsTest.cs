using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedDoorsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedBuildingDoors doors;
        private readonly ulong a, b;
        private int phase, repairs, refusals;
        private float next;
        private bool sawAOpen, unlockedB, sawBOpen, presenceChecked;
        internal string Status => "doors phase=" + phase + "; count=" + doors.Count + "; A=" + Describe(a) + "; B=" + Describe(b);
        private string Describe(ulong id) => doors.TestNative(id).Open + "/" + doors.TestNative(id).Locked;
        internal PairedDoorsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; doors = plugin.World!.Doors;
            var home = UnityEngine.Object.FindObjectsOfType<Home>(true).SelectMany(h => h.HouseDoors ?? Array.Empty<Furniture_Door>()).ToArray();
            var store = UnityEngine.Object.FindObjectsOfType<Stores>(true).SelectMany(s => s.StoreDoors ?? Array.Empty<Furniture_Door>()).ToArray();
            bool Plain(Furniture_Door d) => d.gameObject.activeInHierarchy && !(bool)AccessToolsField("isCottage").GetValue(d) && !store.Contains(d);
            a = doors.TestEntity(d => home.Contains(d) && Plain(d));
            b = doors.TestEntity(d => !home.Contains(d) && Plain(d) && ExactHandles(d));
            Require(doors.Count > 10 && doors.TestHomeCount > 0, "Expected static home and world doors; found " + doors.Count + ".");
            Require(!doors.TestNative(a).Open && !doors.TestNative(b).Open, "Fixture requires closed doors.");
            if (plugin.TestOptions.IsHost) doors.TestDoor(b).CloseAndLockDoor(true);
        }
        private static System.Reflection.FieldInfo AccessToolsField(string name) => HarmonyLib.AccessTools.Field(typeof(Furniture_Door), name);
        private static bool ExactHandles(Furniture_Door d) => UnityEngine.Object.FindObjectsOfType<Furniture_DoorHandle>(true).Where(h => h.furniture_Door == d).All(h => h.GetType() == typeof(Furniture_DoorHandle));
        internal bool Tick()
        {
            return plugin.TestOptions.IsHost ? HostTick() : GuestTick();
        }
        private bool HostTick()
        {
            var stateA = doors.TestNative(a);
            if (stateA.Open && !sawAOpen)
            {
                sawAOpen = true;
                // The guest is standing at door A; a far host must still count as present for store locking.
                var far = doors.TestDoor(a).transform.position + Vector3.right * 500;
                Require(Patches.StoreDoorPresencePatch.StoreDoorDistance(far, doors.TestDoor(a).transform.position) < 6, "Store door presence ignored the nearby guest.");
                presenceChecked = true;
            }
            if (sawAOpen && !stateA.Open && !unlockedB)
            {
                Require(doors.TestNative(b).Locked && !doors.TestNative(b).Open, "Locked door changed before the host unlocked it.");
                doors.TestDoor(b).UnlockDoor(); unlockedB = true;
            }
            sawBOpen |= unlockedB && doors.TestNative(b).Open;
            if (sawBOpen && presenceChecked && plugin.Session!.Peers.Count == 1) { plugin.EndPairedTestSession(); return true; }
            return false;
        }
        private bool GuestTick()
        {
            if (Time.unscaledTime < next || plugin.World?.PersonalProfileReady != true) return false;
            if (phase == 0)
            {
                Require(doors.TestRecord(b).Locked && doors.TestNative(b).Locked && !doors.TestNative(a).Open, "Late join missed the host's locked door.");
                Require(doors.TestSynchronized, "Initial door snapshot was not applied natively.");
                Move(doors.TestHandlePosition(a) + Vector3.right * 30); phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1) { doors.TestClick(a); phase = 2; next = Time.unscaledTime + 1.5f; }
            else if (phase == 2)
            {
                Require(!doors.TestRecord(a).Open && !doors.TestNative(a).Open, "Distant door request was accepted.");
                Move(doors.TestHandlePosition(a) + Vector3.up * 0.1f); phase = 3; next = Time.unscaledTime + 1;
            }
            else if (phase == 3) { doors.TestClick(a); phase = 4; next = Time.unscaledTime + 0.3f; }
            else if (phase == 4 && doors.TestRecord(a).Open)
            {
                Require(doors.TestNative(a).Open, "Guest did not apply the open door.");
                phase = 5; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 5)
            {
                Require(doors.TestAtEndpoint(a) && doors.TestSynchronized, "Native open animation did not reach its endpoint.");
                // A stale toggle based on the closed presentation must not close the door again.
                plugin.World.Channel.Request(a, new BuildingDoorCommand(false, 0).Encode());
                var door = doors.TestDoor(b);
                door.ToggleDoor(); door.CloseAndLockDoor(true); door.UnlockDoor();
                doors.TestDoor(a).ToggleDoor(); doors.TestDoor(a).CloseAndLockDoor(true);
                Require(doors.TestNative(b).Locked && !doors.TestNative(b).Open && doors.TestNative(a).Open && !doors.TestNative(a).Locked,
                    "Guest-local door mutation bypassed host authority.");
                Move(doors.TestHandlePosition(b) + Vector3.up * 0.1f); phase = 6; next = Time.unscaledTime + 1;
            }
            else if (phase == 6)
            {
                Require(doors.TestRecord(a).Open, "Stale door command was accepted.");
                doors.TestClick(b);
                // Bypass the local lock check so the host itself must refuse and reply.
                refusals = doors.TestRefusals; doors.TestRequest(b, false);
                phase = 7; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 7)
            {
                Require(!doors.TestRecord(b).Open && doors.TestRecord(b).Locked, "Locked door opened for a guest.");
                Require(doors.TestRefusals == refusals + 1, "Host refusal did not reach the guest as locked feedback.");
                Move(doors.TestHandlePosition(a) + Vector3.up * 0.1f); phase = 8; next = Time.unscaledTime + 1;
            }
            else if (phase == 8) { doors.TestClick(a); phase = 9; next = Time.unscaledTime + 0.3f; }
            else if (phase == 9 && !doors.TestRecord(a).Open) { phase = 10; next = Time.unscaledTime + 1.5f; }
            else if (phase == 10)
            {
                Require(!doors.TestNative(a).Open && doors.TestAtEndpoint(a), "Closing animation did not synchronize.");
                phase = 11;
            }
            else if (phase == 11 && !doors.TestRecord(b).Locked)
            {
                Require(!doors.TestNative(b).Locked, "Host unlock did not reach the guest.");
                Move(doors.TestHandlePosition(b) + Vector3.up * 0.1f); phase = 12; next = Time.unscaledTime + 1;
            }
            else if (phase == 12) { doors.TestClick(b); phase = 13; next = Time.unscaledTime + 0.3f; }
            else if (phase == 13 && doors.TestRecord(b).Open) { phase = 14; next = Time.unscaledTime + 1.5f; }
            else if (phase == 14)
            {
                Require(doors.TestNative(b).Open && doors.TestSynchronized, "Unlocked door did not open for the guest.");
                repairs = plugin.World.Channel.RepairsApplied; doors.TestDrift(a);
                Require(!doors.TestSynchronized, "Fixture did not introduce door drift.");
                Require(plugin.World.Channel.RequestResync(SnapshotScope.ForEntity(BuildingDoorState.Adapter, a)), "Door repair rejected.");
                phase = 15;
            }
            else if (phase == 15 && plugin.World.Channel.RepairsApplied > repairs)
            {
                Require(doors.TestSynchronized && !doors.TestNative(a).Open, "Door repair did not restore native state.");
                plugin.EndPairedTestSession();
                Require(doors.TestRestored, "Guest door baseline was not restored on disconnect.");
                return true;
            }
            return false;
        }
        private void Move(Vector3 p)
        { gameplay.PlayerWalking.transform.position = p; gameplay.Get_Player_Rigidbody.position = p; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
