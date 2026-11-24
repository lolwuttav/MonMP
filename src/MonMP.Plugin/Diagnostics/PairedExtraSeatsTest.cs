using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Paired acceptance fixture for the extra OlTruck, ATV rider and bar-stool seats: the guest sits on
    // the truck bed, in the cab, behind the ATV driver and on a bar stool through the native chair click, the host grants each seat and
    // pins the guest avatar to it, and standing up or disconnecting releases the claim.
    internal sealed class PairedExtraSeatsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedExtraSeats seats;
        private readonly bool host;
        private readonly ulong bed, stool, cab, rider;
        private readonly float deadline;
        private int phase;
        private float next;
        private uint guest;
        private string gaps = "";
        // The first of the bar's own stools (native chairs): the guest sits there between the cab and the extra stool.
        private readonly Furniture_Chair? nativeStool;
        private readonly Transform? nativeSeat;
        private float nativeSince;
        private bool nativeChecked;
        private bool doorsRequested, closedDoorChecked;
        private readonly HashSet<ulong> towChecked = new HashSet<ulong>();

        internal string Status => "Extra seats phase=" + phase + "; owners=" + string.Join(",", seats.Owners.Select(v => v.ToString()).ToArray()) +
            "; local=" + gameplay.Get_PlayerState + "; gaps=" + gaps + "; refused=" + seats.LastRefusal + "; me=" + (gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform.position.ToString("F1") : "-") +
            "; rider=" + (rider != 0 && seats.Contains(rider) ? seats.TestAnchor(rider).position.ToString("F1") : "-") + "; atv=" + atvTrack;
        private string atvTrack = "";
        private float nextTrack;
        private void Track()
        {
            if (Time.unscaledTime < nextTrack) return;
            nextTrack = Time.unscaledTime + 5f;
            var atv = UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>().FirstOrDefault(v => v.vehicle_type == Vehicle_Type.SmollATV);
            if (atv != null) atvTrack += "p" + phase + ":" + atv.transform.position.ToString("F0") + " ";
        }

        internal PairedExtraSeatsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            seats = plugin.World?.ExtraSeats ?? throw new InvalidOperationException("Extra seat adapter is unavailable.");
            host = plugin.TestOptions.IsHost;
            bed = seats.TestSeats(SharedExtraSeats.SeatKind.Ground).FirstOrDefault();
            stool = seats.TestSeats(SharedExtraSeats.SeatKind.Stool).FirstOrDefault();
            cab = seats.TestSeats(SharedExtraSeats.SeatKind.Cab).FirstOrDefault();
            rider = seats.TestSeats(SharedExtraSeats.SeatKind.Rider).FirstOrDefault();
            if (bed == 0 || stool == 0 || cab == 0 || rider == 0)
                throw new InvalidOperationException("Extra seats test needs a placed truck bed seat, cab seat, ATV rider seat and bar stool: " +
                    seats.TestSeats(SharedExtraSeats.SeatKind.Ground).Length + "/" + seats.TestSeats(SharedExtraSeats.SeatKind.Cab).Length + "/" +
                    seats.TestSeats(SharedExtraSeats.SeatKind.Rider).Length + "/" + seats.TestSeats(SharedExtraSeats.SeatKind.Stool).Length);
            var location = HarmonyLib.AccessTools.Field(typeof(Furniture_Chair), "SeatLocation");
            nativeStool = UnityEngine.Object.FindObjectsOfType<Furniture_Chair>().Where(c => !seats.Handles(c) &&
                c.GetComponentsInParent<Transform>().Any(t => t.name.StartsWith("BarStool", StringComparison.Ordinal)))
                .OrderBy(c => RuntimeProbe.CanonicalPath(c.transform), StringComparer.Ordinal).FirstOrDefault();
            nativeSeat = nativeStool == null ? null : location.GetValue(nativeStool) as Transform;
            if (nativeSeat == null) throw new InvalidOperationException("Extra seats test needs a native bar stool.");
            deadline = Time.unscaledTime + 150f;
            NativeWheelbarrowCollisionTest.Run();
            if (plugin.Session?.IsHost == true) NativeCargoWheelQueryProbe.Run();
        }

        internal bool Tick()
        {
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Paired extra seats test timed out: " + Status);
            Track();
            if (plugin.Session?.IsRunning != true || plugin.World?.Channel.FullySynchronized != true || Time.unscaledTime < next) return false;
            // Profile readiness precedes the final saved/default spawn placement.
            // Do not let that placement undo the fixture's first seat approach.
            if (!host && phase == 0 && plugin.World.ReconnectPositions?.Settled != true) return false;
            return host ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                // Closed doors intentionally remove cab/driver seats from crosshair
                // candidates. Set up the native doors before probing usable seats.
                var doors = plugin.Vehicles!.Doors;
                if (!doorsRequested)
                {
                    // Passenger door open, driver door closed first: no seat may be
                    // picked through the closed door's panel.
                    Move(doors.TestPositionFor(false) + Vector3.right);
                    if (!doors.TestState(false).Open) doors.TestClick(false);
                    Move(doors.TestPosition + Vector3.right);
                    if (doors.TestState().Open) doors.TestClick();
                    doorsRequested = true;
                    return false;
                }
                if (!closedDoorChecked)
                {
                    var truck = seats.TestVehicle(cab).GetComponent<NWH.VehiclePhysics2.VehicleController>();
                    if (!doors.TestState(false).Open || Mathf.Abs(doors.TestState(false).Angle) < 5f ||
                        doors.TestState().Open || !truck.isDriverDoorClosed || truck.isPassengerDoorClosed) return false;
                    var through = seats.TestClosedDoorCrosshair(cab, out bool blocked);
                    Debug.Log("MonMP extra seat closed door: " + through);
                    Require(blocked, "A seat was picked through the closed driver door: " + through);
                    Move(doors.TestPosition + Vector3.right);
                    doors.TestClick();
                    closedDoorChecked = true;
                    return false;
                }
                if (!doors.TestState().Open || !doors.TestState(false).Open ||
                    Mathf.Abs(doors.TestState().Angle) < 5f || Mathf.Abs(doors.TestState(false).Angle) < 5f) return false;
                guest = plugin.Session.Peers.Keys.First(peer => peer != 0);
                plugin.SeatPoses!.TestRuntimeLookups();
                VehicleSeatPoseCatalog.TestRuntimeCache(plugin.World!);
                seats.TestSurfaceGlow();
                plugin.SeatPoses.TestRuntimeLookups();
                VehicleSeatPoseCatalog.TestRuntimeCache(plugin.World!);
                NativeShirtStressFixture.TestBacking(gameplay, System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics"));
                foreach (ulong id in seats.TestSeats(SharedExtraSeats.SeatKind.Cab))
                {
                    var highlight = seats.TestHighlight(id);
                    Require(highlight.Native, "A cab seat lost its native highlight: " + id.ToString("X16") + " " + highlight.Source);
                }
                foreach (ulong id in seats.TestSeats(SharedExtraSeats.SeatKind.Ground).Concat(seats.TestSeats(SharedExtraSeats.SeatKind.Rider)))
                    Require(!seats.TestHighlight(id).Native, "A floor/rack seat received an artificial native seat mesh: " + id.ToString("X16"));
                // Every placed seat must be reachable by the player's own crosshair, not only by Click.
                var misses = seats.TestSeats(SharedExtraSeats.SeatKind.Ground).Concat(seats.TestSeats(SharedExtraSeats.SeatKind.Cab))
                    .Concat(seats.TestSeats(SharedExtraSeats.SeatKind.Rider))
                    .SelectMany(id => new[] { 0, 1 }.Select(view => (id: id, view, what: seats.TestCrosshair(id, view, out bool ok), ok))).ToArray();
                var driverAim = seats.TestDriverCrosshair(seats.TestSeats(SharedExtraSeats.SeatKind.Cab)[0], out bool driverOk);
                Require(driverOk, "Aiming at the driver seat no longer picks it: " + driverAim);
                var hoistAim = seats.TestHoistCase(seats.TestSeats(SharedExtraSeats.SeatKind.Cab)[0], out bool hoistOk);
                Debug.Log("MonMP extra seat hoist case: " + hoistAim);
                Require(hoistOk, "A bed seat covers or outranks the hoist case: " + hoistAim);
                Debug.Log("MonMP extra seat layout:\n" + seats.TestLayout());
                // Snapshots of the hover each kind of added seat shows the player.
                string shots = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics");
                foreach (var (kind, view) in new[] { (SharedExtraSeats.SeatKind.Ground, 1), (SharedExtraSeats.SeatKind.Ground, 0), (SharedExtraSeats.SeatKind.Cab, 0), (SharedExtraSeats.SeatKind.Cab, 1), (SharedExtraSeats.SeatKind.Rider, 0), (SharedExtraSeats.SeatKind.Rider, 1) })
                    seats.TestCrosshair(seats.TestSeats(kind)[0], view, out _, System.IO.Path.Combine(shots, "seat-hover-" + kind + "-" + view + ".bmp"));
                foreach (ulong id in seats.TestSeats(SharedExtraSeats.SeatKind.Cab))
                    foreach (int view in new[] { 0, 1 })
                        seats.TestCrosshair(id, view, out _, System.IO.Path.Combine(shots, "seat-hover-Cab-" + id.ToString("X16") + "-" + view + ".bmp"));
                var benches = seats.TestSeats(SharedExtraSeats.SeatKind.Bench);
                foreach (ulong id in benches.Take(2).Concat(benches.Skip(benches.Length / 2).Take(2)).Concat(benches.AsEnumerable().Reverse().Take(2)).Distinct())
                {
                    seats.TestCrosshair(id, 0, out bool benchHit, System.IO.Path.Combine(shots, "seat-glow-Bench-" + id.ToString("X16") + ".bmp"));
                    Require(benchHit, "A bus glow region is unreachable from the aisle: " + id.ToString("X16"));
                }
                Debug.Log("MonMP extra seat crosshair: " + string.Join("; ", misses.Select(m => m.id.ToString("X") + "/" + m.view + "=" + m.what).ToArray()));
                Require(misses.All(m => m.ok), "The crosshair can't reach every extra seat: " +
                    string.Join("; ", misses.Where(m => !m.ok).Select(m => m.id.ToString("X") + "/" + m.view + "=" + m.what).ToArray()));
                // The bar's own stools: a remote sitter goes on the cushion, not by the (high) head height.
                var location = HarmonyLib.AccessTools.Field(typeof(Furniture_Chair), "SeatLocation");
                var native = UnityEngine.Object.FindObjectsOfType<Furniture_Chair>().Where(c => !seats.Handles(c) &&
                    c.GetComponentsInParent<Transform>().Any(t => t.name.StartsWith("BarStool", StringComparison.Ordinal)))
                    .Select(c => (Transform)location.GetValue(c)).Where(l => l != null).ToArray();
                var surfaces = native.Select(l => (l, top: RemotePlayers.StoolSurface(l.position))).ToArray();
                Debug.Log("MonMP native stools: " + string.Join("; ", surfaces.Select(s => s.l.parent.name + "/" + s.l.name + " seat=" +
                    s.l.position.y.ToString("F2") + " cushion=" + (s.top.HasValue ? s.top.Value.y.ToString("F2") : "none")).ToArray()));
                Require(native.Length > 0 && surfaces.All(s => s.top.HasValue), "A native bar stool has no cushion surface for remote sitters.");
                phase = 1;
            }
            else if (phase == 1 && seats.TestState(bed).Owner == guest) { if (Pinned(bed, "bed")) phase = 2; }
            else if (phase == 2 && seats.TestState(cab).Owner == guest)
            {
                Require(!seats.TestState(bed).Occupied, "Standing up from the truck bed did not release the bed seat.");
                if (Pinned(cab, "cab")) phase = 25;
            }
            else if (phase == 25 && seats.TestState(rider).Owner == guest)
            {
                Require(!seats.TestState(cab).Occupied, "Standing up from the cab seat did not release it.");
                Require(TowAvailable(bed), "Released truck remained unavailable to towing after its last added-seat claim was released (V-03).");
                if (Pinned(rider, "atv-rider")) phase = 3;
            }
            else if (phase == 3 && !nativeChecked)
            {
                // The guest on a native stool: the observer puts its hips on the cushion, not by head height.
                var hips = plugin.RemoteAvatarHips(guest);
                if (hips == null || Vector3.ProjectOnPlane(hips.Value - nativeSeat!.position, Vector3.up).magnitude > 0.5f) { nativeSince = 0; return false; }
                if (nativeSince == 0)
                {
                    nativeSince = Time.unscaledTime;
                    // Look at the sitter from beside the stool for a picture of the pose.
                    var eye = nativeSeat!.position + nativeSeat.right * 1.8f + nativeSeat.forward * -0.6f;
                    gameplay.PlayerWalking.transform.position = eye;
                    if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = eye; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
                    var look = Quaternion.LookRotation(hips.Value - (eye + Vector3.up * 1.6f)).eulerAngles;
                    gameplay.PlayerWalking.playerCameraController.SetRotationAngles(Mathf.DeltaAngle(0, look.x), look.y);
                    return false;
                }
                if (Time.unscaledTime - nativeSince < 1.5f) return false;
                AvatarPreviewCapture.SaveView(Camera.main, System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics", "native-stool-observer.bmp"));
                var top = RemotePlayers.StoolSurface(nativeSeat.position);
                Require(top.HasValue, "The native stool has no cushion surface.");
                float lift = hips.Value.y - top!.Value.y;
                gaps += "native-stool-hips=" + lift.ToString("F2") + " ";
                Require(lift > -0.05f && lift < 0.3f, "The guest avatar sits " + lift.ToString("F2") + " m above the native stool's cushion.");
                nativeChecked = true;
            }
            else if (phase == 3 && seats.TestState(stool).Owner == guest)
            {
                Require(!seats.TestState(rider).Occupied, "Standing up from the ATV rider seat did not release it.");
                Require(TowAvailable(rider), "Released ATV remained unavailable to towing after its rider claim was released (V-03).");
                if (Pinned(stool, "stool")) phase = 4;
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                Require(seats.Owners.Length == 0, "Guest disconnect did not release its extra seat.");
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }

        // The guest avatar sits on the seat anchor (horizontally; the pose decides the height).
        private bool Pinned(ulong id, string name)
        {
            var anchor = seats.TestAnchor(id);
            var avatar = plugin.RemoteAvatarRoot(guest);
            float gap = avatar == null ? float.PositiveInfinity : Vector3.ProjectOnPlane(avatar.position - anchor.position, Vector3.up).magnitude;
            if (gap >= 0.6f) return false;
            if ((id == bed || id == rider) && towChecked.Add(id)) VerifyOccupiedTowRejected(id);
            gaps += name + "=" + gap.ToString("F2") + "/" + (avatar!.position.y - anchor.position.y).ToString("F2") + " ";
            return true;
        }

        private bool Guest()
        {
            uint local = plugin.Session!.LocalPeerId;
            switch (phase)
            {
                case 0: case 4: case 8:
                    {
                        if (phase == 0) seats.TestSurfaceGlow();
                        // Walk up to the next seat, then click it once the host has the new position.
                        ulong id = phase == 0 ? bed : phase == 4 ? cab : stool;
                        Require(!seats.TestState(id).Occupied && gameplay.Get_PlayerState == PlayerState.Walking, "The next extra seat was not free.");
                        var anchor = seats.TestAnchor(id);
                        Move(anchor.position + Vector3.up * (phase == 4 ? 1.3f : 0.3f)); // the cab seat from the roof above it
                        phase++; next = Time.unscaledTime + 1f;
                        break;
                    }
                case 1: case 5: case 9:
                    seats.TestChair(Current).MouseDown();
                    phase++;
                    break;
                case 2: case 6: case 10:
                    if (seats.TestState(Current).Owner != local || !seats.TestSeatedHere(Current)) return false;
                    Require(gameplay.Get_PlayerState == PlayerState.InFurnitureChair, "The host grant did not seat the guest in the native chair state.");
                    if (Current != stool)
                        Require(plugin.Vehicles!.HasPassengerOccupant(seats.TestVehicle(Current).GetComponent<NWH.VehiclePhysics2.VehicleController>()),
                            "Guest's actual added-seat claim was absent from vehicle occupancy (V-03).");
                    phase++; next = Time.unscaledTime + 3f; // long enough for the host to see the avatar seated
                    break;
                case 3: case 7:
                {
                    // Clicking the seat again stands up: aimed from the seated camera, down at the lap,
                    // as a player would (the seat has no collider of its own, #141).
                    var view = gameplay.Player.mainCamera.transform;
                    var pick = seats.PickSeat(new Ray(view.position, (view.forward * 0.5f + Vector3.down).normalized));
                    Require(pick == seats.TestChair(Current), "Seated in the " + (phase == 3 ? "bed" : "cab") + " seat, aiming down did not pick it to stand up: " + (pick == null ? "nothing" : pick.name));
                    // Only the seat stands you up: looking ahead or up picks nothing (#141).
                    var ahead = seats.PickSeat(new Ray(view.position, Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized));
                    var up = seats.PickSeat(new Ray(view.position, (Vector3.up + view.forward * 0.3f).normalized));
                    Require(ahead != seats.TestChair(Current) && up != seats.TestChair(Current), "Seated, looking away from the seat still picked it to stand up (#141).");
                    pick!.MouseDown();
                }
                    phase = phase == 3 ? 31 : 71;
                    break;
                case 31: case 71:
                    if (seats.TestState(phase == 31 ? bed : cab).Occupied || gameplay.Get_PlayerState != PlayerState.Walking) return false;
                    phase = phase == 31 ? 4 : 50;
                    break;
                case 50:
                    {
                        // Beside the ATV, clear of its body, on the rider seat's side.
                        var anchor = seats.TestAnchor(rider);
                        var atv = seats.TestVehicle(rider);
                        float side = Mathf.Sign(atv.InverseTransformPoint(anchor.position).x);
                        Move(anchor.position + Vector3.ProjectOnPlane(atv.right, Vector3.up).normalized * side * 1.3f + Vector3.up * 0.3f);
                        phase = 51; next = Time.unscaledTime + 1f;
                        break;
                    }
                case 51: seats.TestChair(rider).MouseDown(); phase = 52; break;
                case 52:
                    if (seats.TestState(rider).Owner != local || !seats.TestSeatedHere(rider)) return false;
                    phase = 53; next = Time.unscaledTime + 3f;
                    break;
                case 53: seats.TestChair(rider).MouseDown(); phase = 54; break;
                case 54:
                    if (seats.TestState(rider).Occupied || gameplay.Get_PlayerState != PlayerState.Walking) return false;
                    phase = 40;
                    break;
                case 40:
                    Move(nativeSeat!.position - nativeSeat.forward * 0.8f + Vector3.up * 0.2f);
                    phase = 41; next = Time.unscaledTime + 1f;
                    break;
                case 41:
                    nativeStool!.MouseDown();
                    phase = 42;
                    break;
                case 42:
                    if (gameplay.Get_PlayerState != PlayerState.InFurnitureChair) return false;
                    phase = 43; next = Time.unscaledTime + 4f; // long enough for the host to measure the avatar
                    break;
                case 43:
                    nativeStool!.MouseDown(); // stands up
                    phase = 44;
                    break;
                case 44:
                    if (gameplay.Get_PlayerState != PlayerState.Walking) return false;
                    phase = 8;
                    break;
                case 11:
                    plugin.EndPairedTestSession();
                    Require(gameplay.Get_PlayerState == PlayerState.Walking,
                        "Leaving the session did not stand the guest up from the bar stool.");
                    return true;
            }
            return false;
        }

        private ulong Current => phase <= 3 ? bed : phase <= 7 ? cab : stool;

        private bool TowAvailable(ulong id)
        {
            var vehicle = seats.TestVehicle(id).GetComponent<NWH.VehiclePhysics2.VehicleController>();
            object?[] args = { vehicle.vehicle_type, null, null };
            return (bool)AccessTools.Method(typeof(SharedTowingService), "TryGetReplicatedAvailableTarget")!
                .Invoke(plugin.World!.TowingService, args)!;
        }

        private void VerifyOccupiedTowRejected(ulong id)
        {
            var vehicle = seats.TestVehicle(id).GetComponent<NWH.VehiclePhysics2.VehicleController>();
            Require(plugin.Vehicles!.TryGetDriverState(vehicle, out var state) && state.Driver.Owner == ItemLease.Unowned,
                "Added-seat towing regression needs an otherwise unowned driver seat.");
            Require(plugin.Vehicles.HasPassengerOccupant(vehicle) && !TowAvailable(id),
                "Occupied added seat did not make its exact vehicle unavailable to towing (V-03).");
            var towing = plugin.World!.TowingService;
            towing.TestHostOpen();
            Move(towing.TestTruckPosition + Vector3.up * 0.5f + Vector3.right * 2f);
            int rejected = towing.TestRejected;
            int accepted = towing.TestAccepted;
            int queued = towing.TestState.CurrentVehicleType;
            int cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash;
            Vector3 position = vehicle.transform.position;
            towing.TestRequest(vehicle.vehicle_type); // patched native store interaction, with the guest still seated
            Require(towing.TestRejected == rejected + 1 && towing.TestAccepted == accepted &&
                towing.TestState.CurrentVehicleType == queued && UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash == cash &&
                (vehicle.transform.position - position).sqrMagnitude < 0.000001f && seats.TestState(id).Owner == guest,
                "Native towing request changed cash, queue, vehicle pose or the guest's added-seat lease (V-03).");
            gaps += "tow-rejected-" + vehicle.vehicle_type + " ";
        }

        private void Move(Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Extra seats test: " + reason); }
    }
}
