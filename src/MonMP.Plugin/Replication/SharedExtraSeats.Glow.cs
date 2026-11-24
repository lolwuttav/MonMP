using System;
using System.Linq;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedExtraSeats
    {
        private void BindSurfaceGlows()
        {
            foreach (var group in seats.Values.Where(s => s.Glow == null && s.Placed && (s.Kind == SeatKind.Cab || s.Kind == SeatKind.Bench)).GroupBy(s => s.Truck))
            {
                var vehicle = group.Key;
                if (vehicle == null) continue;
                var targets = group.OrderBy(s => s.Slot).ToArray();
                bool cab = targets[0].Kind == SeatKind.Cab;
                MeshRenderer? surface;
                if (cab)
                {
                    var driver = vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true).FirstOrDefault(d =>
                        d != null && d.vehicleController == vehicle && d.seatPosition != null);
                    surface = driver != null ? driver.GetComponent<MeshRenderer>() : null;
                    // The audited physical Seat renderer owns the remaining bench; Seat_Visual is
                    // the driver's independent cushion and must keep vanilla's own hover handling.
                    if (surface == null || surface.name != "Seat" || surface == driver!.meshRenderer) continue;
                }
                else
                {
                    var surfaces = vehicle.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name == "interior" &&
                        r.sharedMaterials.Length == 1 && r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("SchoolBus_interior", StringComparison.Ordinal)).ToArray();
                    surface = surfaces.Length == 1 ? surfaces[0] : null;
                }
                if (surface == null || surface.GetComponent<MeshFilter>()?.sharedMesh == null) continue;
                try
                {
                    var regions = targets.Select(s => SurfaceBounds(s, vehicle)).ToArray();
                    var glow = SeatSurfaceGlow.Create(surface, vehicle.transform, regions);
                    surfaceGlows.Add(glow);
                    for (int i = 0; i < targets.Length; i++)
                    {
                        var seat = targets[i];
                        seat.Glow = glow; seat.GlowRegion = i;
                        RecordHighlight(seat, "native surface " + surface.name + " region=" + i + " bounds=" + regions[i]);
                    }
                }
                catch (Exception error)
                {
                    Debug.LogWarning("MonMP: native " + (cab ? "truck cab" : "bus") + " seat glow unavailable; seating remains usable: " + error.Message);
                }
            }
        }

        private static Bounds SurfaceBounds(Seat seat, VehicleController vehicle)
        {
            var candidates = vehicle.GetComponentsInChildren<Vehicle_PassengerSeat>(true)
                .Where(p => p != null && p.vehicleController == vehicle && p.seatPosition != null && p.meshRenderer != null)
                .Select(p => (Renderer: p.meshRenderer, Origin: p.seatPosition))
                .Concat(vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true)
                    .Where(d => d != null && d.vehicleController == vehicle && d.seatPosition != null && d.meshRenderer != null)
                    .Select(d => (Renderer: d.meshRenderer, Origin: d.seatPosition)))
                .OrderBy(p => Mathf.Abs(vehicle.transform.InverseTransformPoint(p.Origin.position).x - vehicle.transform.InverseTransformPoint(seat.Anchor.position).x));
            var native = candidates.First();
            var mesh = native.Renderer.GetComponent<MeshFilter>()?.sharedMesh ?? throw new InvalidOperationException("Native cushion mesh is missing.");
            var turn = seat.Anchor.rotation * Quaternion.Inverse(native.Origin.rotation);
            var sourceBounds = mesh.bounds;
            var bounds = new Bounds();
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                var corner = sourceBounds.center + Vector3.Scale(sourceBounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var position = seat.Anchor.position + turn * (native.Renderer.transform.TransformPoint(corner) - native.Origin.position);
                var local = vehicle.transform.InverseTransformPoint(position);
                if (first) { bounds = new Bounds(local, Vector3.zero); first = false; }
                else bounds.Encapsulate(local);
            }
            // Include the actual rolled front edge below the cushion top, without reaching the
            // backrest, floor or neighbouring row. Geometry itself never moves.
            var min = bounds.min - new Vector3(0, seat.Kind == SeatKind.Cab ? 0.08f : 0.01f, 0.015f);
            var max = bounds.max + new Vector3(0, seat.Kind == SeatKind.Cab ? 0.02f : 0.01f, 0.015f);
            bounds.SetMinMax(min, max);
            return bounds;
        }

        private static bool HoverLit(Seat seat) => seat.Glow != null ? seat.Glow.IsLit(seat.GlowRegion) : seat.Pad != null && seat.Pad.enabled;

#if MONMP_DEVELOPMENT
        internal void TestSurfaceGlow()
        {
            ShowHover(null);
            var targets = seats.Values.Where(s => s.Placed && (s.Kind == SeatKind.Cab || s.Kind == SeatKind.Bench)).ToArray();
            if (targets.Length == 0 || targets.Any(s => s.Glow == null || s.Pad != null || s.Created!.GetComponentsInChildren<MeshRenderer>(true).Length != 0))
                throw new InvalidOperationException("A truck/bus seat still uses an overlay instead of the physical surface.");
            var natives = targets.Select(s => s.Truck).Distinct().SelectMany(v => v!.GetComponentsInChildren<Interactable>(true))
                .Where(i => (i is Vehicle_DriverSeat || i is Vehicle_PassengerSeat) && i.meshRenderer != null)
                .Select(i => (Renderer: i.meshRenderer, Mesh: i.meshRenderer.GetComponent<MeshFilter>().sharedMesh,
                    Materials: i.meshRenderer.sharedMaterials,
                    Emission: i.meshRenderer.sharedMaterials.Select(m => (m.IsKeywordEnabled("_EMISSION"), m.GetColor("_EmissionColor"))).ToArray())).ToArray();
            foreach (var seat in targets)
            {
                ShowHover(seat.Chairs[0]);
                if (targets.Any(s => !s.Glow!.TestColor(s.GlowRegion, s == seat)))
                    throw new InvalidOperationException("Seat glow color or region isolation differs from native emission.");
            }
            ShowHover(null);
            foreach (var glow in surfaceGlows) glow.TestMaterialRecovery();
            foreach (var native in natives)
                if (native.Renderer.GetComponent<MeshFilter>().sharedMesh != native.Mesh ||
                    !native.Renderer.sharedMaterials.SequenceEqual(native.Materials) ||
                    !native.Renderer.sharedMaterials.Select(m => (m.IsKeywordEnabled("_EMISSION"), m.GetColor("_EmissionColor"))).SequenceEqual(native.Emission))
                    throw new InvalidOperationException("An added seat changed a native driver/front-row mesh or material.");
            foreach (var glow in surfaceGlows)
            {
                glow.Dispose();
                if (!glow.TestRestored) throw new InvalidOperationException("Seat glow did not restore its original mesh/material references.");
            }
            surfaceGlows.Clear();
            foreach (var seat in targets) seat.Glow = null;
            BindSurfaceGlows();
            if (targets.Any(s => s.Glow == null)) throw new InvalidOperationException("Seat glow failed to rebind after cleanup.");
            Debug.Log("MonMP native seat glow passed: " + targets.Length + " individual surfaces, exact native emission, no overlays; native driver/front-row state unchanged; cleanup/rebind restored original references.");
        }
#endif
    }
}
