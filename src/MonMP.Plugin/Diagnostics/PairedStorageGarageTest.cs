using System;
using System.Linq;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The host opens an empty storage garage bay, parks the shared ATV in it, closes the door (the
    // native unload hides the ATV) and opens it again (the native load brings it back). The guest
    // must mirror the door and hide/show its ATV copy, and its own door button must reach the host
    // instead of moving its door locally.
    internal sealed class PairedStorageGarageTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong door, atvId;
        private VehicleController? atv;
        private int phase;
        private float next, deadline, peerSince;
        internal string Status => "storage phase=" + phase + "; door=" + door.ToString("X16") + "; atv=" + atvId.ToString("X16") +
            (door == 0 || plugin.World == null ? "" : "; open=" + plugin.World.StorageGarages.TestOpen(door) + "; moving=" + plugin.World.StorageGarages.TestMoving(door) +
                "; record=" + Describe(plugin.World.StorageGarages.TestRecord(door))) + (atv == null ? "" : "; atvActive=" + atv.gameObject.activeSelf);
        internal PairedStorageGarageTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }

        private static string Describe(Core.World.StorageGarageDoorState? state) =>
            state == null ? "none" : "open:" + state.Value.Open + ",stored:" + state.Value.Stored + ",vehicle:" + state.Value.StoredVehicle.ToString("X16");

        internal bool Tick()
        {
            var world = plugin.World!;
            var garages = world.StorageGarages;
            if (deadline == 0) deadline = Time.unscaledTime + 150f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Storage garage test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            if (atv == null)
            {
                atv = world.Vehicles.BoundControllers.FirstOrDefault(v => v != null && v.vehicle_type == Vehicle_Type.SmollATV);
                if (atv == null) return false;
                atvId = world.Vehicles.EntityOf(atv);
            }
            if (plugin.TestOptions.IsHost)
            {
                switch (phase)
                {
                    case 0:
                        // A guest whose catalog is refused connects briefly; wait for one that stays.
                        if (plugin.Session!.Peers.Count == 0) { peerSince = 0; return false; }
                        if (peerSince == 0) peerSince = Time.unscaledTime;
                        if (Time.unscaledTime - peerSince < 8f) return false;
                        door = garages.TestDoors.OrderBy(id => id).FirstOrDefault(garages.TestBayReady);
                        if (door == 0) return false;
                        Stand(Outside());
                        garages.TestDoor(door).Door_Open();
                        phase = 1; return false;
                    case 1:
                        if (garages.TestMoving(door) || !garages.TestOpen(door)) return false;
                        var floor = garages.TestBayFloor(door);
                        atv.vehicleRigidbody.velocity = Vector3.zero; atv.vehicleRigidbody.angularVelocity = Vector3.zero;
                        atv.vehicleRigidbody.position = floor; atv.transform.position = floor;
                        phase = 2; next = Time.unscaledTime + 4f; return false;
                    case 2:
                        var parked = garages.TestDoor(door).GetVehiclesInBay;
                        Require(parked.Length == 1 && parked[0] == atv, "The ATV did not settle alone in the storage bay (" + parked.Length + " vehicles).");
                        Stand(Outside());
                        garages.TestDoor(door).Door_Close();
                        Require(garages.TestMoving(door), "The host storage door refused to close on the parked ATV.");
                        phase = 3; return false;
                    case 3:
                        if (atv.gameObject.activeSelf) return false;
                        var record = garages.TestRecord(door);
                        if (record == null || !record.Value.Stored) return false;
                        Require(!record.Value.Open && record.Value.StoredVehicle == atvId, "The stored door record does not name the shared ATV: " + Describe(record));
                        // Let the guest check its hidden copy.
                        phase = 4; next = Time.unscaledTime + 5f; return false;
                    case 4:
                        garages.TestDoor(door).Door_Open();
                        Require(atv.gameObject.activeSelf, "The native load did not bring the ATV back.");
                        phase = 5; return false;
                    case 5:
                        // The guest presses its own door button once the door has reopened.
                        if (garages.TestCommands == 0) return false;
                        phase = 6; next = Time.unscaledTime + 3f; return false;
                    case 6:
                        plugin.EndPairedTestSession(); return true;
                }
                return false;
            }
            switch (phase)
            {
                case 0:
                    door = garages.TestDoors.FirstOrDefault(id => garages.TestRecord(id)?.Open == true && (!garages.TestOpen(id) || garages.TestMoving(id)));
                    if (door == 0) return false;
                    Stand(Outside());
                    phase = 1; return false;
                case 1:
                    if (garages.TestMoving(door) || !garages.TestOpen(door)) return false;
                    phase = 2; return false;
                case 2:
                    var record = garages.TestRecord(door);
                    if (record == null || !record.Value.Stored) return false;
                    Require(record.Value.StoredVehicle == atvId, "The guest's stored record names another vehicle: " + Describe(record));
                    Require(!atv.gameObject.activeSelf, "The guest kept its ATV copy visible after the host stored it.");
                    Require(!garages.TestOpen(door), "The guest's storage door did not close with the host's.");
                    phase = 3; return false;
                case 3:
                    if (garages.TestRecord(door)?.Stored != false) return false;
                    Require(atv.gameObject.activeSelf, "The guest's ATV copy stayed hidden after the host took it out.");
                    phase = 4; return false;
                case 4:
                    if (garages.TestMoving(door) || !garages.TestOpen(door)) return false;
                    Stand(Outside());
                    // A native toggle starts the door coroutine synchronously; a routed press cannot
                    // come back from the host within this call.
                    garages.TestButton(door).Get_SwitchOn.Invoke();
                    Require(!garages.TestMoving(door) && garages.TestOpen(door), "The guest's door button moved its door locally instead of asking the host.");
                    phase = 5; next = Time.unscaledTime + 2f; return false;
                case 5:
                    plugin.EndPairedTestSession(); return true;
            }
            return false;
        }

        // Next to the door button but outside the bay, so no player blocks the door.
        private Vector3 Outside()
        {
            var garages = plugin.World!.StorageGarages;
            var button = garages.TestButton(door).transform.position;
            var floor = garages.TestBayFloor(door);
            var away = button - floor; away.y = 0;
            var at = button + (away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward) * 1.5f;
            for (int i = 0; i < 8 && garages.TestInBay(door, at + Vector3.up); i++) at += (away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward) * 1.5f;
            return at;
        }
        private void Stand(Vector3 at)
        {
            gameplay.PlayerWalking.transform.position = at;
            if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
