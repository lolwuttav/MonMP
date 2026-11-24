using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;
using NWH.VehiclePhysics2;

namespace MonMP.Plugin.Diagnostics
{
    // Host stages exact catalogued suspension parts on the canonical Main/Prinz/Scrapyard FL
    // sockets. The guest invokes the game's native height/camber knobs and compares immediate
    // wheel-manager geometry before and after the host echo. This deliberately does not refresh
    // the guest wheel managers itself: an echo whose part state is already equal must not hide a
    // missed native mainVehicles callback.
    internal sealed class PairedShellSuspensionTest
    {
        private enum ProbeKind { Coilover, TunedControlArm }

        private sealed class CarProbe
        {
            internal Vehicle_Type Type;
            internal VehicleController Vehicle = null!;
            internal Vehicle_PartsManager Manager = null!;
        }

        private sealed class Probe
        {
            internal CarProbe Car = null!;
            internal ProbeKind Kind;
            internal ulong SocketId;
            internal ushort Prefab;
            internal Vehicle_PartSocket Socket = null!;
            internal VehiclePartState Original;
            internal BoltHoleState[] OriginalHoles = Array.Empty<BoltHoleState>();
            internal VehiclePartState Seed;
            internal VehiclePartState LastHostRecord;
            internal int AcceptedChanges;
        }

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly List<CarProbe> cars = new List<CarProbe>();
        private readonly List<Probe> probes = new List<Probe>();
        private readonly List<string> errors = new List<string>();
        private int phase, operation;
        private float deadline;
        private VehiclePartState pendingEcho;
        private Probe? pendingProbe;
        private bool targetsReady, hostRestored;

        internal string Status => "shell suspension phase=" + phase + "; op=" + operation + "/" + probes.Count +
            "; accepted=" + probes.Sum(probe => probe.AcceptedChanges) + "; peers=" +
            (plugin.Session?.Peers.Count.ToString() ?? "none") + "; mainVehicles=" + MainVehiclesSummary();

        internal PairedShellSuspensionTest(MonMPPlugin plugin, Gameplay gameplay)
        { this.plugin = plugin; this.gameplay = gameplay; }

        internal bool Tick()
        {
            try
            {
                var parts = plugin.World!.VehicleParts;
                if (!parts.Ready) return false;
                if (!targetsReady) BuildTargets(parts);
                return plugin.TestOptions.IsHost ? TickHost(parts) : TickGuest(parts);
            }
            catch (Exception failure)
            {
                if (plugin.TestOptions.IsHost && !hostRestored && targetsReady)
                {
                    try { Abort(); }
                    catch (Exception cleanup) { throw new AggregateException("Shell suspension failed and its host native baseline could not be restored.", failure, cleanup); }
                }
                throw;
            }
        }

        private void BuildTargets(SharedVehicleParts parts)
        {
            var global = UnityEngine.Object.FindObjectOfType<GlobalClassManager>();
            if (global == null) throw new InvalidOperationException("Shell suspension fixture cannot find GlobalClassManager.");
            ushort coilover = plugin.Items!.TestTypePrefab(typeof(Part_Shock_Coilover));
            ushort frontArm = plugin.Items.TestPrefab("Part/ControlArms/Part_ControlArm_Front_Tuned");
            ushort rearArm = plugin.Items.TestPrefab("Part/ControlArms/Part_ControlArm_Rear_Tuned");
            var coiloverAsset = ExactCatalogPart("Part/Shocks/Part_Shock_Coilover", typeof(Part_Shock_Coilover));
            var frontArmAsset = ExactCatalogPart("Part/ControlArms/Part_ControlArm_Front_Tuned", typeof(Part_ControlArm_Tuned));
            if (plugin.Items.TestType(coilover) != typeof(Part_Shock_Coilover) ||
                plugin.Items.TestType(frontArm) != typeof(Part_ControlArm_Tuned) ||
                plugin.Items.TestType(rearArm) != typeof(Part_ControlArm_Tuned))
                throw new InvalidOperationException("Suspension fixture catalog entries did not resolve to their exact native subtypes.");

            foreach (var type in new[] { Vehicle_Type.MainVehicle, Vehicle_Type.Prinz, Vehicle_Type.ScrapyardKonig })
            {
                if (!(global.GetVehicles(type) is VehicleController vehicle) || vehicle == null ||
                    !vehicle.gameObject.scene.IsValid() || !vehicle.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("Suspension fixture native canonical " + type + " is not active in the isolated world.");
                var manager = vehicle.GetComponent<Vehicle_PartsManager>();
                if (manager == null || manager.carPartSockets == null || manager.carPartSockets.Count == 0 ||
                    vehicle.Wheels == null || vehicle.Wheels.Count != 4 ||
                    vehicle.Wheels.Any(wheel => wheel == null || wheel.wheelController == null ||
                        wheel.wheelController.wheel == null || wheel.wheelController.wheel.Visual == null))
                    throw new InvalidOperationException("Suspension fixture " + type + " has incomplete initialized native wheels or sockets.");

                var car = new CarProbe { Type = type, Vehicle = vehicle, Manager = manager };
                var shockSocket = manager.Shock_FL;
                var armSocket = manager.ControlArm_FL;
                RequireSocket(parts, car, shockSocket, Car_PartsType.Shock, "front-left coilover");
                RequireSocket(parts, car, armSocket, Car_PartsType.ControlArm_Front, "front-left tuned arm");
                if (!SharedVehiclePartIdentityPolicy.IsCompatiblePart(type, shockSocket.car_PartsType, coiloverAsset) ||
                    !SharedVehiclePartIdentityPolicy.IsCompatiblePart(type, armSocket.car_PartsType, frontArmAsset))
                    throw new InvalidOperationException("Exact registered suspension catalog assets are not natively compatible with " + type + "; no fallback is allowed.");
                var shock = MakeProbe(parts, car, shockSocket, ProbeKind.Coilover, coilover);
                var arm = MakeProbe(parts, car, armSocket, ProbeKind.TunedControlArm, frontArm);
                cars.Add(car); probes.Add(shock); probes.Add(arm);
            }

            targetsReady = true;
            LogNativeOverview("start");
        }

        private static InteractableParts ExactCatalogPart(string path, Type expected)
        {
            var asset = Resources.Load<GameObject>(path);
            var part = asset != null ? asset.GetComponent<InteractableParts>() : null;
            if (part == null || part.GetType() != expected || part.PrefabName != path)
                throw new InvalidOperationException("Exact native suspension catalog asset is unavailable or has the wrong subtype: " + path);
            return part;
        }

        private Probe MakeProbe(SharedVehicleParts parts, CarProbe car, Vehicle_PartSocket socket, ProbeKind kind, ushort prefab)
        {
            var binding = parts.PaintSockets.SingleOrDefault(value => ReferenceEquals(value.Socket, socket));
            if (binding.Socket == null)
                throw new InvalidOperationException("Suspension fixture socket was not included in SharedVehicleParts: " + car.Type + "/" + socket.ID);
            var probe = new Probe
            {
                Car = car, Kind = kind, SocketId = binding.Id, Prefab = prefab, Socket = socket,
                Original = parts.TestNative(binding.Id),
                OriginalHoles = socket.boltHolesDetails.Select(CaptureHole).ToArray()
            };
            if (kind == ProbeKind.Coilover)
                probe.Seed = new VehiclePartState(true, prefab, false, 25f, 25f, 25, 25);
            else
                probe.Seed = new VehiclePartState(true, prefab, false, 25f);
            return probe;
        }

        private static BoltHoleState CaptureHole(BoltHoleDetails hole) =>
            new BoltHoleState(hole.isBoltInstalled, hole.isBoltInstalled && hole.BoltedPercent > 0
                ? (byte)Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp01(hole.BoltedPercent) * 100f)) : (byte)0);

        private static void RequireSocket(SharedVehicleParts parts, CarProbe car, Vehicle_PartSocket socket,
            Car_PartsType expectedType, string label)
        {
            if (socket == null || socket.gameobject == null || socket.car_PartsType != expectedType ||
                socket.vehicleAttached != car.Manager ||
                !parts.PaintSockets.Any(value => ReferenceEquals(value.Socket, socket)))
                throw new InvalidOperationException("Suspension fixture has no exact registered " + label + " socket for " + car.Type + ".");
        }

        private bool TickHost(SharedVehicleParts parts)
        {
            if (phase == 0)
            {
                if (plugin.Session?.Peers.Count < 2) return false;
                foreach (var probe in probes)
                {
                    var holes = probe.Socket.boltHolesDetails.Select(_ => new BoltHoleState(false, 0)).ToArray();
                    // This fixture-only native restore path handles an existing stock/custom part,
                    // destroys only its replaced object, installs the exact catalog part, and
                    // publishes the host's state. The guest therefore sees normal native echoes.
                    parts.TestRestoreState(probe.SocketId, probe.Seed, holes);
                    probe.LastHostRecord = probe.Seed;
                }
                deadline = Time.unscaledTime + 70f;
                phase = 1;
                LogNativeOverview("host-seeded");
                return false;
            }

            ObserveAcceptedHostChanges(parts);
            if (plugin.Session?.Peers.Count == 1)
            {
                int accepted = probes.Sum(probe => probe.AcceptedChanges);
                if (accepted != probes.Count)
                    errors.Add("Guest disconnected after " + accepted + " of " + probes.Count + " native knob edits.");
                RestoreHost(parts);
                plugin.EndPairedTestSession();
                if (errors.Count != 0)
                    throw new InvalidOperationException("Shell suspension native comparison failed: " + string.Join(" | ", errors));
                Debug.Log("MonMP shell suspension host completed six guest native knob edits and restored all host part/bolt baselines.");
                return true;
            }
            if (Time.unscaledTime >= deadline)
                throw new TimeoutException("Shell suspension host timed out waiting for guest callbacks; accepted=" +
                    probes.Sum(probe => probe.AcceptedChanges) + "; " + Status);
            return false;
        }

        private void ObserveAcceptedHostChanges(SharedVehicleParts parts)
        {
            foreach (var probe in probes)
            {
                var record = parts.TestRecord(probe.SocketId);
                if (record.Equals(probe.LastHostRecord)) continue;
                if (!record.Installed || record.Prefab != probe.Prefab)
                {
                    errors.Add(probe.Car.Type + "/" + probe.Kind + " host accepted an unexpected native part record " + Describe(record));
                    probe.LastHostRecord = record;
                    continue;
                }
                var native = parts.TestNative(probe.SocketId);
                bool nativeAccepted = native.Equals(record);
                bool geometryAccepted = NativeGeometryMatches(probe, out string geometry);
                string line = "MonMP shell suspension host-accepted car=" + probe.Car.Type + "; kind=" + probe.Kind +
                    "; record=" + Describe(record) + "; nativeAccepted=" + nativeAccepted + "; geometry=" + geometry;
                Debug.Log(line);
                if (!nativeAccepted) errors.Add(probe.Car.Type + "/" + probe.Kind + " host native state differs from its accepted record.");
                if (!geometryAccepted) errors.Add(probe.Car.Type + "/" + probe.Kind + " host wheel geometry did not follow the accepted native part state.");
                probe.LastHostRecord = record;
                probe.AcceptedChanges++;
            }
        }

        private bool TickGuest(SharedVehicleParts parts)
        {
            if (phase == 0)
            {
                if (plugin.Session?.Peers.Count < 2) return false;
                foreach (var probe in probes)
                {
                    if (!parts.TestRecordAvailable(probe.SocketId)) return false;
                    var record = parts.TestRecord(probe.SocketId);
                    if (!record.Installed || record.Prefab != probe.Prefab ||
                        !parts.TestNative(probe.SocketId).Equals(record)) return false;
                    if (!NativeGeometryMatches(probe, out string geometry))
                        errors.Add(probe.Car.Type + "/" + probe.Kind + " guest baseline manager geometry is not seeded from the host: " + geometry);
                }
                deadline = Time.unscaledTime + 70f;
                phase = 1;
                LogNativeOverview("guest-ready");
            }

            if (phase == 1)
            {
                if (operation >= probes.Count)
                {
                    plugin.EndPairedTestSession();
                    if (errors.Count != 0)
                        throw new InvalidOperationException("Shell suspension native comparison failed: " + string.Join(" | ", errors));
                    Debug.Log("MonMP shell suspension guest completed all six native knob callbacks and restored its local baseline on disconnect.");
                    return true;
                }
                InvokeNextNativeKnob(parts);
                return false;
            }

            if (phase == 2)
            {
                if (parts.TestRecord(pendingProbe!.SocketId).Equals(pendingEcho))
                {
                    bool matched = NativeGeometryMatches(pendingProbe, out string geometry);
                    Debug.Log("MonMP shell suspension guest-echo car=" + pendingProbe.Car.Type + "; kind=" +
                        pendingProbe.Kind + "; record=" + Describe(parts.TestRecord(pendingProbe.SocketId)) +
                        "; geometryMatches=" + matched + "; " + geometry);
                    if (!matched) errors.Add(pendingProbe.Car.Type + "/" + pendingProbe.Kind +
                        " native geometry remained stale after equal-state echo: " + geometry);
                    pendingProbe = null;
                    operation++;
                    phase = 1;
                    return false;
                }
                if (Time.unscaledTime >= deadline)
                    throw new TimeoutException("Shell suspension guest did not receive the host echo for operation " +
                        operation + "; pending=" + Describe(pendingEcho) + "; " + Status);
            }
            return false;
        }

        private void InvokeNextNativeKnob(SharedVehicleParts parts)
        {
            var probe = probes[operation];
            var beforeState = parts.TestNative(probe.SocketId);
            if (!beforeState.Installed || beforeState.Prefab != probe.Prefab)
                throw new InvalidOperationException("Shell suspension guest lost the native mounted part before operation " + operation + ".");
            bool beforeMatches = NativeGeometryMatches(probe, out string beforeGeometry);
            LogProbe(probe, "before-knob", beforeState, beforeMatches, beforeGeometry);

            int direction;
            if (probe.Kind == ProbeKind.Coilover)
            {
                var shock = probe.Socket.partInstalled as Part_Shock_Coilover
                    ?? throw new InvalidOperationException("Guest front-left native socket is not a Part_Shock_Coilover.");
                direction = shock.IntValue1 < 50 ? 1 : -1;
                shock.Knob_Height(direction);
            }
            else
            {
                var arm = probe.Socket.partInstalled as Part_ControlArm_Tuned
                    ?? throw new InvalidOperationException("Guest front-left native socket is not a Part_ControlArm_Tuned.");
                direction = arm.Value1 < 50 ? 1 : -1;
                arm.Knob_Camber(direction);
            }

            var afterState = parts.TestNative(probe.SocketId);
            bool immediateMatches = NativeGeometryMatches(probe, out string immediateGeometry);
            LogProbe(probe, "immediate-after-native-knob", afterState, immediateMatches, immediateGeometry);
            if (afterState.Equals(beforeState))
                errors.Add(probe.Car.Type + "/" + probe.Kind + " native knob did not change the mounted part state.");
            if (!immediateMatches)
                errors.Add(probe.Car.Type + "/" + probe.Kind +
                    " wheel geometry did not change with its native knob before any host echo: " + immediateGeometry);
            pendingProbe = probe;
            pendingEcho = afterState;
            deadline = Time.unscaledTime + 25f;
            phase = 2;
        }

        private bool NativeGeometryMatches(Probe probe, out string description)
        {
            var wheel = ProbeWheel(probe.Car);
            if (wheel == null)
            {
                description = "native FL WheelController unavailable";
                return false;
            }
            if (probe.Kind == ProbeKind.Coilover)
            {
                if (!(probe.Socket.partInstalled is Part_Shock_Coilover shock))
                {
                    description = "native shock socket has " + (probe.Socket.partInstalled?.GetType().Name ?? "no part");
                    return false;
                }
                float expected = shock.Get_SpringLenght;
                float actual = wheel.springLength;
                description = "expectedSpring=" + expected.ToString("F4") + "; wheel.springLength=" + actual.ToString("F4") +
                    "; expectedForce=" + shock.Get_Force.ToString("F1") + "; wheel.springMaximumForce=" + wheel.springMaximumForce.ToString("F1");
                return Mathf.Abs(expected - actual) < 0.0005f &&
                    Mathf.Abs(shock.Get_Force - wheel.springMaximumForce) < 0.1f;
            }
            if (!(probe.Socket.partInstalled is Part_ControlArm_Tuned arm))
            {
                description = "native front control-arm socket has " + (probe.Socket.partInstalled?.GetType().Name ?? "no part");
                return false;
            }
            float expectedCamber = arm.Get_Camber;
            float actualTop = wheel.wheel.camberAtTop, actualBottom = wheel.wheel.camberAtBottom;
            description = "expectedCamber=" + expectedCamber.ToString("F3") + "; wheel.camberAtTop=" +
                actualTop.ToString("F3") + "; wheel.camberAtBottom=" + actualBottom.ToString("F3");
            return Mathf.Abs(expectedCamber - actualTop) < 0.001f && Mathf.Abs(expectedCamber - actualBottom) < 0.001f;
        }

        private static NWH.WheelController3D.WheelController? ProbeWheel(CarProbe car) =>
            car.Vehicle.Wheels != null && car.Vehicle.Wheels.Count > 0 ? car.Vehicle.Wheels[0]?.wheelController : null;

        private void LogProbe(Probe probe, string stage, VehiclePartState state, bool geometryMatches, string geometry)
        {
            var socket = probe.Socket;
            var socketTransform = socket.gameobject!.transform;
            var part = socket.partInstalled;
            var parentScale = socketTransform.parent != null ? socketTransform.parent.localScale : Vector3.one;
            var wheel = ProbeWheel(probe.Car);
            var visual = wheel?.wheel?.Visual;
            var nativeList = gameplay.mainVehicles ?? Array.Empty<VehicleController>();
            bool nativeMember = nativeList.Any(vehicle => vehicle == probe.Car.Vehicle);
            string placement = part == null ? "part=none" :
                "part=" + part.GetType().Name + ";placed=" + part.isPlaced +
                ";partParent=" + (part.transform.parent == null ? "none" : part.transform.parent.name) +
                ";expectedParent=" + socketTransform.name +
                ";localPos=" + part.transform.localPosition.ToString("F4") +
                ";localRot=" + part.transform.localRotation.ToString("F4") +
                ";localScale=" + part.transform.localScale.ToString("F4") +
                ";nativeExpectedScale=" + parentScale.ToString("F4") +
                ";parentOK=" + (part.transform.parent == socketTransform) +
                ";poseOK=" + (part.transform.localPosition.sqrMagnitude < 0.000001f &&
                    Quaternion.Angle(part.transform.localRotation, Quaternion.identity) < 0.01f) +
                ";scaleOK=" + (Vector3.Distance(part.transform.localScale, parentScale) < 0.0001f);
            string visualSummary = wheel == null || visual == null ? "wheelVisual=missing" :
                "wheelController=" + wheel.name + ";wheelGO=" + wheel.gameObject.name +
                ";visual=" + visual.name + ";visualParent=" + (visual.transform.parent == null ? "none" : visual.transform.parent.name) +
                ";visualPos=" + visual.transform.position.ToString("F4") +
                ";wheelWorldPos=" + wheel.wheel.worldPosition.ToString("F4") +
                ";visualToWheelWorld=" + Vector3.Distance(visual.transform.position, wheel.wheel.worldPosition).ToString("F4") +
                ";cachedVisualMatches=" + (wheel.cachedVisualTransform == visual.transform) +
                ";meshRenderers=" + visual.GetComponentsInChildren<Renderer>(true).Length;
            Debug.Log("MonMP shell suspension " + stage + "; car=" + probe.Car.Type +
                "; mainVehiclesMember=" + nativeMember + "; mainVehicles=" + MainVehiclesSummary() +
                "; socket=" + socket.car_PartsType + "/" + socket.gameobject.name + "; side=" + socket.car_Side +
                "; state=" + Describe(state) + "; geometryMatches=" + geometryMatches +
                "; " + geometry + "; " + placement + "; " + visualSummary);
        }

        private void LogNativeOverview(string stage)
        {
            Debug.Log("MonMP shell suspension native overview " + stage + "; " + MainVehiclesSummary() +
                "; cacheDormant=" + string.Join(",", cars.Select(car => car.Type + ":" + plugin.World!.Vehicles.TestDormant(car.Vehicle))));
            foreach (var probe in probes)
            {
                bool matches = NativeGeometryMatches(probe, out string geometry);
                LogProbe(probe, stage, plugin.World!.VehicleParts.TestNative(probe.SocketId), matches, geometry);
            }
        }

        private string MainVehiclesSummary()
        {
            var list = gameplay.mainVehicles ?? Array.Empty<VehicleController>();
            return "[" + string.Join(",", list.Select(vehicle => vehicle == null ? "null" :
                vehicle.vehicle_type + "/" + vehicle.name + "/active=" + vehicle.gameObject.activeInHierarchy)) + "]";
        }

        private void RestoreHost(SharedVehicleParts parts)
        {
            if (hostRestored) return;
            foreach (var probe in probes)
                parts.TestRestoreState(probe.SocketId, probe.Original, probe.OriginalHoles);
            hostRestored = true;
            LogNativeOverview("host-restored");
        }

        internal void Abort()
        {
            if (!plugin.TestOptions.IsHost || hostRestored || !targetsReady || plugin.World?.VehicleParts == null) return;
            try { RestoreHost(plugin.World.VehicleParts); }
            catch (Exception ex) { errors.Add("host abort restoration failed: " + ex); throw; }
        }

        private static string Describe(VehiclePartState state) => state.Installed
            ? "installed,prefab=" + state.Prefab + ",tightened=" + state.FullyTightened +
                ",Value1=" + state.Value1 + ",Value2=" + state.Value2 +
                ",IntValue1=" + state.IntValue1 + ",IntValue2=" + state.IntValue2
            : "empty";
    }
}
