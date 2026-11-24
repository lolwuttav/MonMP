using System;
using System.Collections.Generic;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        private sealed class CargoDrop
        {
            internal InteractableItems Item = null!;
            internal ulong Carrier;
            internal uint Generation;
            internal Transform Frame = null!;
            internal VehicleCargoSpace Volume = null!;
            internal Vector3 LastPosition;
            internal Quaternion LastRotation;
            internal readonly CargoSettleWindow Settle = new CargoSettleWindow();
            internal float ArmedAt;
            internal bool Reported;
        }
        private sealed class CargoPin
        {
            internal CargoDrop Drop = null!;
            internal Rigidbody Body = null!;
            internal Rigidbody? CarrierBody;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal RigidbodyInterpolation Interpolation;
            internal CollisionDetectionMode Collision;
            internal readonly List<(Collider Item, Collider Vehicle)> Ignored = new List<(Collider, Collider)>();
            internal readonly List<Collider> Shapes = new List<Collider>();
            internal float NextPairs;
            internal int ShapesFrame = -1;
        }
        private sealed class CargoShapes
        {
            internal readonly List<Collider> Solid = new List<Collider>();
            internal float Until;
        }
        private readonly Dictionary<ulong, CargoDrop> cargoDrops = new Dictionary<ulong, CargoDrop>();
        private readonly Dictionary<ulong, CargoPin> cargoPins = new Dictionary<ulong, CargoPin>();
        // Wheel roots normally equal carrier frames. Group ownership so a wheel
        // only checks each loaded carrier's hierarchy, not every item in the world.
        // Live hierarchy checks preserve native/custom parent and trailer changes.
        private readonly Dictionary<Transform, List<CargoPin>> cargoWheelCarriers = new Dictionary<Transform, List<CargoPin>>();
        private readonly List<ulong> cargoKeys = new List<ulong>();
        private readonly Dictionary<Transform, CargoShapes> cargoShapes = new Dictionary<Transform, CargoShapes>();
        private float nextCargoSweep;

        private void ArmCargoDrop(Binding binding)
        {
            cargoDrops.Remove(binding.Id);
            if (!host || session?.Settings.FreezeVehicleCargo != true || Carriers == null || binding.Item == null) return;
            if (cargoDrops.Count >= 256) { CargoSkipped(binding.Item, "too many settling drops"); return; }
            if (!CargoEligible(binding)) { CargoSkipped(binding.Item, "not eligible (" + CargoIneligibility(binding) + ")"); return; }
            // Drops outside any compartment are ordinary; only near-misses beside a carrier are worth a line.
            if (!VehicleCargoSpace.TryDropSpace(Carriers, binding.Item, out var carrier, out var frame, out var volume)) return;
            ItemCarriers.CapturePose(binding.Item.transform, binding.Item.rb, frame, out var position, out var rotation);
            cargoDrops[binding.Id] = new CargoDrop { Item = binding.Item, Carrier = carrier, Frame = frame, Volume = volume,
                Generation = leases[binding.Id].Generation, LastPosition = position, LastRotation = rotation, ArmedAt = Time.time };
        }

        // Bounded host diagnostics for explicit drops in a compartment that do not freeze.
        private int cargoSkipLogs;
        private void CargoSkipped(InteractableItems item, string reason)
        {
            if (cargoSkipLogs >= 64) return;
            cargoSkipLogs++;
            Debug.Log("MonMP cargo freeze skipped " + (item != null ? item.name : "<destroyed>") + ": " + reason +
                (cargoSkipLogs == 64 ? " (further cargo freeze diagnostics suppressed)" : ""));
        }

        private string CargoIneligibility(Binding binding)
        {
            var item = binding.Item;
            if (!binding.State.Exists || binding.Absorbed) return "absorbed";
            if (binding.State.ContainerEntity != 0) return "in container";
            if (leases[binding.Id].Owner != ItemLease.Unowned) return "held";
            if (item == null || !item.gameObject.activeInHierarchy) return "inactive";
            if (!item.HasARigidBody || item.rb == null) return "no rigidbody";
            if (item.rb.isKinematic) return "kinematic";
            if (item.itemCollider == null) return "no item collider";
            if (LocalHeld(item)) return "held locally";
            if (Placed(item)) return "placed";
            return "fish in bucket";
        }

        private static string CargoShape(InteractableItems item)
        {
            var shapes = new List<string>();
            foreach (var collider in item.GetComponentsInChildren<Collider>(false))
                if (collider.enabled && !collider.isTrigger && collider.GetComponentInParent<InteractableItems>() == item && shapes.Count < 6)
                    shapes.Add(collider.GetType().Name + (collider is MeshCollider mesh && mesh.sharedMesh != null ? (mesh.sharedMesh.isReadable ? "/readable" : "/unreadable") : ""));
            return string.Join(",", shapes.ToArray());
        }

        private bool CargoEligible(Binding binding) => binding.State.Exists && !binding.Absorbed &&
            binding.State.ContainerEntity == 0 && leases[binding.Id].Owner == ItemLease.Unowned && binding.Item != null &&
            binding.Item.gameObject.activeInHierarchy && binding.Item.HasARigidBody && binding.Item.rb != null &&
            !binding.Item.rb.isKinematic && binding.Item.itemCollider != null && !LocalHeld(binding.Item) && !Placed(binding.Item) &&
            !(binding.Item is Item_Fish && binding.Item.IntValue1 == 1);

        private void CargoSettingsChanged(SessionSettingsState state)
        {
            if (host && !state.FreezeVehicleCargo) ReleaseFrozenCargo();
        }

        // Called only from the accepted input release path, never discovery. Candidates
        // keep the exact compartment/carrier present at that drop; a later passing car
        // cannot claim an item dropped on the road. Keep waiting while contained:
        // crowded barrels may need more than ten seconds to stop nudging each other.
        // Membership is bounded at enrollment and cancelled on escape/pickup/teardown.
        private void TickCargoFreezing()
        {
            if (!host || disposed || session == null) return;
            if (!session.Settings.FreezeVehicleCargo) { ReleaseFrozenCargo(); return; }
            if (!Ready || TransfersBlocked || Carriers == null) return;
            foreach (var pair in cargoPins)
            {
                var pin = pair.Value;
                if (pin.Drop.Item != null && pin.Drop.Frame != null && pin.Drop.Item.transform.parent == null && pin.Body != null)
                {
                    PreserveCargoBody(pin);
                }
            }
            if (Time.time < nextCargoSweep) return;
            nextCargoSweep = Time.time + 0.1f;
            cargoKeys.Clear(); cargoKeys.AddRange(cargoPins.Keys);
            foreach (var id in cargoKeys)
            {
                var pin = cargoPins[id]; var item = pin.Drop.Item;
                if (item == null || pin.Drop.Frame == null || !pin.Drop.Volume.Valid || !pin.Drop.Frame.gameObject.activeInHierarchy ||
                    Carriers.Resolve(pin.Drop.Carrier) != pin.Drop.Frame || !bindings.TryGetValue(id, out var binding) || binding.Item != item ||
                    !binding.State.Exists || binding.Absorbed || leases[id].Owner != ItemLease.Unowned || LocalHeld(item) || Placed(item) ||
                    item.transform.parent != null || item.rb != pin.Body)
                { ReleaseCargoPin(id); continue; }
                if (Time.time >= pin.NextPairs) { pin.NextPairs = Time.time + 1f; IgnoreCargoCarrier(pin); }
            }
            cargoKeys.Clear(); cargoKeys.AddRange(cargoDrops.Keys);
            foreach (var id in cargoKeys)
            {
                var drop = cargoDrops[id];
                if (drop.Item == null || drop.Frame == null || !drop.Frame.gameObject.activeInHierarchy ||
                    !bindings.TryGetValue(id, out var binding) || binding.Item != drop.Item || !CargoEligible(binding) ||
                    leases[id].Generation != drop.Generation || Carriers.Resolve(drop.Carrier) != drop.Frame ||
                    !drop.Volume.Valid || !drop.Volume.Contains(drop.Item.itemCollider.bounds.center))
                { cargoDrops.Remove(id); continue; }
                var body = drop.Item.rb;
                ItemCarriers.CapturePose(drop.Item.transform, body, drop.Frame, out var position, out var rotation);
                var carrierBody = drop.Frame.GetComponent<Rigidbody>();
                var velocity = body.velocity - (carrierBody != null ? KinematicCarrierMotion.PointVelocity(carrierBody, body.worldCenterOfMass) : Vector3.zero);
                var spin = body.angularVelocity - (carrierBody != null ? KinematicCarrierMotion.Spin(carrierBody) : Vector3.zero);
                float speedSquared = velocity.sqrMagnitude, spinSquared = spin.sqrMagnitude;
                float driftSquared = (position - drop.LastPosition).sqrMagnitude, angle = Quaternion.Angle(rotation, drop.LastRotation);
                // Keep every motion sample/timer reset, but do no hull walk or
                // support raycasts for a sample that cannot count as quiet.
                bool containedAndSupported = CargoSettleWindow.IsMotionQuiet(speedSquared, spinSquared, driftSquared, angle) &&
                    drop.Volume.FullyInside(drop.Item) && VehicleCargoSpace.Supported(drop.Item, drop.Frame, Carriers);
                bool settled = drop.Settle.Observe(Time.time, containedAndSupported, speedSquared, spinSquared, driftSquared, angle);
                if (!settled && !drop.Reported && Time.time - drop.ArmedAt > 8f)
                {
                    drop.Reported = true;
                    bool quiet = CargoSettleWindow.IsMotionQuiet(speedSquared, spinSquared, driftSquared, angle);
                    // Geometry checks only for a quiet sample, as above: a moving load stays cheap.
                    CargoSkipped(drop.Item, "still settling after 8 s (" + (quiet ? "inside=" + drop.Volume.FullyInside(drop.Item) +
                        " supported=" + VehicleCargoSpace.Supported(drop.Item, drop.Frame, Carriers) : "still moving") + " shapes=" + CargoShape(drop.Item) + ")");
                }
                drop.LastPosition = position; drop.LastRotation = rotation;
                if (!settled) continue;
                var pin = new CargoPin { Drop = drop, Body = body, Position = position, Rotation = rotation,
                    Interpolation = body.interpolation, Collision = body.collisionDetectionMode, CarrierBody = carrierBody };
                RegisterCargoPin(id, pin); cargoDrops.Remove(id);
                body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
                // Pinned loads retain solid support for later drops. Speculative CCD
                // can turn their carrier-speed sweep into false ramp contacts and
                // launch neighbouring loose cargo, so use discrete contacts here.
                body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                body.isKinematic = true; body.interpolation = RigidbodyInterpolation.Interpolate;
                // A parented kinematic body is teleported by the carrier transform:
                // contacts see no useful surface velocity. Keep its physics root
                // separate and move it through the solver on each fixed step.
                drop.Item.transform.SetParent(null, true);
                body.position = carrierBody != null ? carrierBody.position + carrierBody.rotation * Vector3.Scale(position, drop.Frame.lossyScale) : drop.Frame.TransformPoint(position);
                body.rotation = (carrierBody != null ? carrierBody.rotation : drop.Frame.rotation) * rotation;
                IgnoreCargoCarrier(pin);
                PublishIsolated(binding);
            }
            cargoKeys.Clear();
        }

        // Retain the body, unlike native wheelbarrow Destroy/AddComponent. This makes
        // same-frame pickup/toggle-off safe and lets later drops settle on pinned cargo.
        // Only the vehicle's own solid shapes are ignored, never neighbouring loose cargo.
        private void IgnoreCargoCarrier(CargoPin pin)
        {
            var item = pin.Drop.Item;
            if (item == null || pin.Drop.Frame == null) return;
            CollectCargoShapes(pin);
            if (!cargoShapes.TryGetValue(pin.Drop.Frame, out var shapes))
            { shapes = new CargoShapes(); cargoShapes.Add(pin.Drop.Frame, shapes); }
            if (Time.time >= shapes.Until)
            {
                shapes.Until = Time.time + 1f;
                pin.Drop.Frame.GetComponentsInChildren<Collider>(false, shapes.Solid);
                shapes.Solid.RemoveAll(collider => collider == null || !collider.enabled || collider.isTrigger ||
                    collider.GetComponentInParent<InteractableItems>() is InteractableItems cargo && !Placed(cargo));
            }
            foreach (var shape in pin.Shapes)
            {
                if (shape == null || !shape.enabled || shape.isTrigger || shape.GetComponentInParent<InteractableItems>() != item) continue;
                foreach (var vehicle in shapes.Solid)
                {
                    if (vehicle == null || !vehicle.enabled || vehicle.isTrigger || vehicle.transform.IsChildOf(item.transform) || Physics.GetIgnoreCollision(shape, vehicle)) continue;
                    Physics.IgnoreCollision(shape, vehicle, true); pin.Ignored.Add((shape, vehicle));
                }
            }
        }

        // Resolve solid pinned loads as carrier supports even though their Rigidbody
        // must be detached from the vehicle's render hierarchy.
        internal Transform? FrozenCargoFrame(Transform surface)
        {
            var item = surface.GetComponentInParent<InteractableItems>();
            return item != null && byItem.TryGetValue(item, out var binding) && cargoPins.TryGetValue(binding.Id, out var pin) &&
                pin.Drop.Item == item && item.rb == pin.Body && item.transform.parent == null && pin.Drop.Volume.Valid ? pin.Drop.Frame : null;
        }

        private void MoveFrozenCargoPhysics()
        {
            if (!host || disposed || !Ready || session?.Settings.FreezeVehicleCargo != true) return;
            foreach (var pin in cargoPins.Values)
            {
                if (pin.Drop.Item == null || pin.Body == null || pin.Drop.Item.rb != pin.Body ||
                    pin.Drop.Item.transform.parent != null || !pin.Drop.Volume.Valid) continue;
                PreserveCargoBody(pin);
                var carrier = pin.CarrierBody;
                var position = carrier != null ? carrier.position : pin.Drop.Frame.position;
                var rotation = carrier != null ? carrier.rotation : pin.Drop.Frame.rotation;
                var local = Vector3.Scale(pin.Position, pin.Drop.Frame.lossyScale);
                var current = position + rotation * local;
                // Hard network corrections must not sweep a support through loose
                // cargo. Ordinary motion uses MovePosition so friction sees velocity.
                if ((pin.Body.position - current).sqrMagnitude > 0.25f)
                { pin.Body.position = current; pin.Body.rotation = rotation * pin.Rotation; }
                // A remote carrier's step target is known exactly; its reported velocity is zero.
                if (carrier != null && KinematicCarrierMotion.TryTarget(carrier, out var target, out var targetRotation))
                { position = target; rotation = targetRotation; }
                else if (carrier != null)
                {
                    var spin = carrier.angularVelocity;
                    float speed = spin.magnitude;
                    var delta = speed > 0.00001f ? Quaternion.AngleAxis(speed * Time.fixedDeltaTime * Mathf.Rad2Deg, spin / speed) : Quaternion.identity;
                    var center = carrier.worldCenterOfMass;
                    position = center + carrier.velocity * Time.fixedDeltaTime + delta * (position - center);
                    rotation = delta * rotation;
                }
                pin.Body.MovePosition(position + rotation * local);
                pin.Body.MoveRotation(rotation * pin.Rotation);
            }
        }

        private static void CollectCargoShapes(CargoPin pin)
        {
            if (pin.ShapesFrame == Time.frameCount) return;
            pin.Drop.Item.GetComponentsInChildren(false, pin.Shapes);
            pin.ShapesFrame = Time.frameCount;
        }

        internal FrozenCargoWheelQuery? BeginCargoWheelQuery(Transform vehicle)
        {
            if (!host || disposed || cargoPins.Count == 0) return null;
            FrozenCargoWheelQuery? scope = null;
            try
            {
                foreach (var carrier in cargoWheelCarriers)
                {
                    var frame = carrier.Key;
                    if (frame == null || !(frame.IsChildOf(vehicle) || vehicle.IsChildOf(frame))) continue;
                    foreach (var pin in carrier.Value)
                    {
                        if (pin.Drop.Item == null || !pin.Drop.Volume.Valid) continue;
                        scope ??= FrozenCargoWheelQuery.Rent();
                        CollectCargoShapes(pin);
                        foreach (var shape in pin.Shapes)
                            if (shape != null && shape.GetComponentInParent<InteractableItems>() == pin.Drop.Item) scope.Hide(shape);
                    }
                }
                if (scope?.Any == true) return scope;
                scope?.Dispose(); return null;
            }
            catch { scope?.Dispose(); throw; }
        }

        private void RegisterCargoPin(ulong id, CargoPin pin)
        {
            cargoPins.Add(id, pin);
            if (!cargoWheelCarriers.TryGetValue(pin.Drop.Frame, out var pins))
                cargoWheelCarriers.Add(pin.Drop.Frame, pins = new List<CargoPin>());
            pins.Add(pin);
        }

        private void ReleaseCargoForPickup(ulong id)
        { cargoDrops.Remove(id); ReleaseCargoPin(id); }

        private void ReleaseCargoPin(ulong id)
        {
            if (!cargoPins.TryGetValue(id, out var pin)) return;
            cargoPins.Remove(id);
            if (cargoWheelCarriers.TryGetValue(pin.Drop.Frame, out var pins))
            {
                pins.Remove(pin);
                if (pins.Count == 0)
                {
                    cargoWheelCarriers.Remove(pin.Drop.Frame);
                    cargoShapes.Remove(pin.Drop.Frame);
                }
            }
            foreach (var pair in pin.Ignored)
                if (pair.Item != null && pair.Vehicle != null) Physics.IgnoreCollision(pair.Item, pair.Vehicle, false);
            var item = pin.Drop.Item;
            if (item == null) return;
            bool attached = item.transform.parent == null;
            var carrierBody = pin.Drop.Frame != null ? pin.Drop.Frame.GetComponent<Rigidbody>() : null;
            var position = carrierBody != null ? carrierBody.position + carrierBody.rotation *
                Vector3.Scale(pin.Position, pin.Drop.Frame!.lossyScale) : item.transform.position;
            var rotation = carrierBody != null ? carrierBody.rotation * pin.Rotation : item.transform.rotation;
            var spin = carrierBody != null ? KinematicCarrierMotion.Spin(carrierBody) : Vector3.zero;
            // Detached pinned bodies already live at the scene root.
            if (item.rb == pin.Body && pin.Body != null && attached)
            {
                // The carrier may still display an interpolated pose behind physics.
                // Restore the body at its carrier's current physics pose before it
                // becomes dynamic, rather than releasing it through the moving bed.
                item.transform.position = position; item.transform.rotation = rotation;
                pin.Body.position = position; pin.Body.rotation = rotation;
                pin.Body.isKinematic = false; pin.Body.collisionDetectionMode = pin.Collision;
                pin.Body.interpolation = pin.Interpolation;
                pin.Body.velocity = carrierBody != null ? KinematicCarrierMotion.PointVelocity(carrierBody, pin.Body.worldCenterOfMass) : Vector3.zero;
                pin.Body.angularVelocity = spin;
                pin.Body.WakeUp();
            }
        }

        internal void ReleaseFrozenCargo()
        {
            cargoDrops.Clear();
            if (cargoPins.Count == 0) { cargoShapes.Clear(); cargoWheelCarriers.Clear(); return; }
            // This can run inside a settings callback; keep its scratch independent of
            // the active sweep, including if a native side effect changes a session rule.
            foreach (var id in new List<ulong>(cargoPins.Keys)) ReleaseCargoPin(id);
        }

        private static void PreserveCargoBody(CargoPin pin)
        {
            if (pin.Body.collisionDetectionMode != CollisionDetectionMode.Discrete)
                pin.Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            if (!pin.Body.isKinematic) pin.Body.isKinematic = true;
            if (pin.Body.interpolation != RigidbodyInterpolation.Interpolate) pin.Body.interpolation = RigidbodyInterpolation.Interpolate;
            if (pin.Drop.Item.itemCollider.isTrigger) pin.Drop.Item.itemCollider.isTrigger = false;
        }

        private bool KeepCargoFrozen(Binding binding)
        {
            if (!cargoPins.TryGetValue(binding.Id, out var pin)) return false;
            if (binding.Item == pin.Drop.Item && binding.Item != null && binding.Item.rb == pin.Body && pin.Body != null && pin.Drop.Volume.Valid &&
                leases[binding.Id].Owner == ItemLease.Unowned && !LocalHeld(binding.Item) && !Placed(binding.Item) && binding.Item.transform.parent == null)
            { PreserveCargoBody(pin); return true; }
            ReleaseCargoPin(binding.Id); return false;
        }

        private bool CaptureSettlingCargo(Binding binding, out ulong carrier, out ItemPose pose)
        {
            carrier = 0; pose = default;
            if (session?.Settings.FreezeVehicleCargo != true || !cargoDrops.TryGetValue(binding.Id, out var drop) ||
                !CargoEligible(binding) || binding.Item != drop.Item || leases[binding.Id].Generation != drop.Generation ||
                !drop.Volume.Valid || Carriers?.Resolve(drop.Carrier) != drop.Frame || !drop.Volume.Contains(drop.Item.itemCollider.bounds.center)) return false;
            // A loose load bouncing briefly off its support still belongs visually
            // to this measured compartment. Avoid alternating carrier/world poses
            // on every rattle; an ejected item leaves this space immediately.
            ItemCarriers.CapturePose(drop.Item.transform, drop.Item.rb, drop.Frame, out var position, out var rotation);
            if (!ItemPose.TryLocal(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w, out pose)) return false;
            carrier = drop.Carrier;
            return true;
        }

        private bool CaptureFrozenCargo(Binding binding, ItemLease lease, out ulong carrier, out ItemPose pose)
        {
            carrier = 0; pose = default;
            if (lease.Owner != ItemLease.Unowned || !cargoPins.TryGetValue(binding.Id, out var pin) ||
                binding.Item != pin.Drop.Item || pin.Drop.Frame == null || binding.Item == null || !pin.Drop.Volume.Valid ||
                Carriers?.Resolve(pin.Drop.Carrier) != pin.Drop.Frame || binding.Item.transform.parent != null) return false;
            carrier = pin.Drop.Carrier;
            return ItemPose.TryLocal(pin.Position.x, pin.Position.y, pin.Position.z, pin.Rotation.x, pin.Rotation.y, pin.Rotation.z, pin.Rotation.w, out pose);
        }

#if MONMP_DEVELOPMENT
        // Every freeze check for one item, without arming or the session setting (cargo-bounds editor).
        internal string TestCargoVerdict(InteractableItems item)
        {
            if (item == null || !byItem.TryGetValue(item, out var binding)) return "unbound";
            if (Carriers == null) return "no carriers";
            string text = "eligible=" + CargoEligible(binding) + (CargoEligible(binding) ? "" : "(" + CargoIneligibility(binding) + ")") +
                " shapes=" + CargoShape(item);
            if (!VehicleCargoSpace.TryDropSpace(Carriers, item, out _, out var frame, out var volume)) return text + " space=False";
            var body = item.rb;
            return text + " space=True frame=" + frame.name + " inside=" + volume.FullyInside(item) +
                " supported=" + VehicleCargoSpace.Supported(item, frame, Carriers) +
                " speed=" + (body != null ? body.velocity.magnitude.ToString("F3") : "-") +
                " frozen=" + cargoPins.ContainsKey(binding.Id) + " settling=" + cargoDrops.ContainsKey(binding.Id);
        }
#endif

        internal bool AllowNativeCargoFreeze(InteractableItems item, Transform? container) => !host || container == null ||
            !byItem.TryGetValue(item, out var binding) ||
            !cargoPins.ContainsKey(binding.Id) && (!cargoDrops.TryGetValue(binding.Id, out var drop) || drop.Frame == null || !container.IsChildOf(drop.Frame));
    }
}
