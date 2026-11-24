using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Seats the stock game lacks: the OlTruck's front middle and right seats and four seats on the
    // bed floor at the back (clear of an installed tank), the SmollATV's two rider seats on the rear
    // rack (left and right, facing forward), plus bar stools with no native chair.
    // Sitting uses the native furniture-chair state (the walker is parented to the seat, so it rides
    // along with the truck); the host owns occupancy, and observers pin the sitter to the seat.
    internal sealed partial class SharedExtraSeats : IDisposable
    {
        internal enum SeatKind : byte { Cab, Ground, Stool, Rider, Bench }

        private sealed class Seat
        {
            public ulong Id;
            public ulong PurchasedVehicleEntity;
            public string Identity = "";
            public SeatKey Key;
            public SeatKind Kind;
            public VehiclePassengerSeatState State;
            public Transform Parent = null!;
            // The hips (bed and stools) or the vehicle-seat origin (cab) observers pin the body to.
            public Transform Anchor = null!;
            // Where the native chair state puts the walker; placed so the camera sits at the eyes.
            public Transform SeatLocation = null!;
            public Furniture_Chair[] Chairs = Array.Empty<Furniture_Chair>();
            public GameObject? Created;
            public VehicleController? Truck;
            public int Slot;
            public bool ExitPending;
            public bool NativeExitPending;
            public float NextExitRetry;
            public bool Placed;
            // Vehicle-local point on the cushion, rack or bed floor the player looks at to pick the seat.
            public Vector3 AimLocal;
            public bool AimKnown;
            // Bed/rack seats have no upholstery surface, so a cushion-sized pad shows their hover.
            public MeshRenderer? Pad;
            // Cab and bus cushions glow on the existing surface; only bed/rack seats use pads.
            public SeatSurfaceGlow? Glow;
            public int GlowRegion;
#if MONMP_DEVELOPMENT
            public string HighlightSource = "existing pad unchanged";
#endif
        }
        private sealed class VehicleSeatGroup
        {
            public VehicleController Truck = null!;
            public readonly List<Seat> Seats = new List<Seat>();
            public bool HasBench;
        }

        private const float CabEyeHeight = 1.6f, GroundEyeHeight = 0.8f;
        private static readonly FieldInfo? ChairSeatField = AccessTools.Field(typeof(Furniture_Chair), "SeatLocation");

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedVehicles vehicles;
        private readonly SharedSchoolBusPassengers bus;
        private readonly SeatRegistry registry = new SeatRegistry();
        private readonly Dictionary<ulong, Seat> seats = new Dictionary<ulong, Seat>();
        private readonly HashSet<Seat> occupiedStools = new HashSet<Seat>();
        private Seat[] occupiedStoolSnapshot = Array.Empty<Seat>();
        private bool occupiedStoolsDirty;
        private readonly List<SeatSurfaceGlow> surfaceGlows = new List<SeatSurfaceGlow>();
        private readonly Dictionary<VehicleController, VehicleSeatGroup> seatsByTruck = new Dictionary<VehicleController, VehicleSeatGroup>();
        private readonly List<VehicleSeatGroup> seatGroups = new List<VehicleSeatGroup>();
        private readonly Dictionary<Furniture_Chair, Seat> byChair = new Dictionary<Furniture_Chair, Seat>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly List<string> rows = new List<string> { "id\tkind\tidentity\tstatus" };
        private readonly string diagnostics;
        private readonly NativeEntityRegistry identities;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private float standingCameraHeight = 1.6f;
        private int enteredFrame = -1;

        internal bool TransfersBlocked { get; set; }
        internal SharedSeatAdmission? SeatAdmission { get; set; }
        internal string LastRefusal { get; private set; } = "";
        internal bool Applying => applying;
        internal int Count => seats.Count;
        internal bool Contains(ulong id) => seats.ContainsKey(id);
        internal uint[] Owners => seats.Values.Select(seat => seat.State.Owner).Where(owner => owner != SeatOccupancy.Unoccupied).Distinct().ToArray();
        internal bool LocalFlushComplete => session == null || !IsOwner(session.LocalPeerId) &&
            !seats.Values.Any(seat => seat.NativeExitPending || IsLocalSeated(seat));
        internal bool IsOwner(uint peer) => SeatOwnedBy(peer) != null;
        internal VehicleController? VehicleForPeer(uint peer) => !disposed && Ready ? SeatOwnedBy(peer)?.Truck : null;
        internal bool HasPassengerOccupant(VehicleController vehicle) => !disposed && seats.Values.Any(seat =>
            seat.Truck == vehicle && (seat.State.Occupied || IsLocalSeated(seat)));
        internal bool PeerSeatedNear(uint peer, Vector3 position, float range) => host && Ready && seats.Values.Any(seat =>
            seat.Kind == SeatKind.Stool && seat.State.Owner == peer && seat.Placed && seat.Anchor != null &&
            (seat.Anchor.position - position).sqrMagnitude <= range * range);

        private bool IsLocalSeated(Seat seat) => gameplay.Get_PlayerState == PlayerState.InFurnitureChair &&
            gameplay.PlayerWalking != null && gameplay.PlayerWalking.transform.parent == seat.SeatLocation;
        private bool TowingAllowsEntry(Seat seat) => seat.Truck == null ||
            MonMPPlugin.Instance?.World?.TowingService.AllowVehicleEntry(gameplay, seat.Truck) != false;

        private Seat? SeatOwnedBy(uint peer)
        {
            foreach (var seat in seats.Values)
                if (seat.State.Owner == peer) return seat;
            return null;
        }
        private bool Ready => channel?.Ready == true && !disposed;

        internal SharedExtraSeats(Gameplay gameplay, bool host, string diagnostics, SharedVehicles vehicles,
            SharedSchoolBusPassengers bus, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.vehicles = vehicles; this.bus = bus; this.diagnostics = diagnostics;
            this.identities = identities;
            if (ChairSeatField == null) { rows.Add("-\t-\t-\tnative-chair-api-missing"); Write(); return; }

            // Canonical scene trucks/ATVs keep their historic path identities. Exact sale
            // clones use the same sale-root registration at startup and after a later purchase.
            RegisterInitialVehicleSeats();

            // The school bus: rows of seats down both sides of the aisle behind its two native passenger
            // seats (#177). Created per slot whether or not they fit, so host and guest build one catalog.
            var busVehicle = AccessTools.Field(typeof(AI_SchoolBus), "vc");
            foreach (var schoolBus in UnityEngine.Object.FindObjectsOfType<AI_SchoolBus>(true)
                .Where(b => b != null && b.gameObject.scene.IsValid())
                .OrderBy(b => RuntimeProbe.CanonicalPath(b.transform), StringComparer.Ordinal))
            {
                if (busVehicle?.GetValue(schoolBus) is not VehicleController vehicle) continue;
                string path = RuntimeProbe.CanonicalPath(vehicle.transform);
                for (int slot = 0; slot < BusSlotRows * 2; slot++)
                {
                    var root = new GameObject("MonMP Bus Seat " + slot);
                    root.transform.SetParent(vehicle.transform, false);
                    var anchor = new GameObject("Anchor").transform; anchor.SetParent(root.transform, false);
                    var location = new GameObject("SeatLocation").transform; location.SetParent(root.transform, false);
                    // No collider (picked by aim), like the truck and ATV seats.
                    var chair = root.AddComponent<Furniture_Chair>();
                    chair.Name = "Sit";
                    ChairSeatField.SetValue(chair, location);
                    Add(new Seat
                    {
                        Kind = SeatKind.Bench, Parent = vehicle.transform, Anchor = anchor, SeatLocation = location,
                        Chairs = new[] { chair }, Created = root, Truck = vehicle, Slot = slot
                    }, path + "/monmp-bus-seat-v1/" + slot, identities);
                }
            }

            // Bar stools with no native chair of their own.
            foreach (var bar in UnityEngine.Object.FindObjectsOfType<Transform>(true)
                .Where(t => t != null && t.name == "Bar" && t.parent != null && t.parent.name == "Buildings" && t.gameObject.scene.IsValid())
                .OrderBy(t => RuntimeProbe.CanonicalPath(t), StringComparer.Ordinal))
                foreach (Transform stool in bar)
                {
                    if (!stool.name.StartsWith("BarStool", StringComparison.Ordinal)) continue;
                    string path = RuntimeProbe.CanonicalPath(stool);
                    var native = stool.GetComponentsInChildren<Furniture_Chair>(true);
                    var location = stool.Find("SeatLocation");
                    if (native.Length != 0) { rows.Add("-\tstool\t" + path + "\tnative-chair"); continue; }
                    if (location == null) { rows.Add("-\tstool\t" + path + "\tno-seat-location"); continue; }
                    var colliders = stool.GetComponentsInChildren<Collider>(true)
                        .Where(c => c != null && !c.isTrigger && c.GetComponentInParent<Actor>() == null && c.GetComponent<Interactable>() == null).ToArray();
                    if (colliders.Length == 0) { rows.Add("-\tstool\t" + path + "\tno-collider"); continue; }
                    var anchor = new GameObject("MonMP Seat Anchor").transform;
                    anchor.SetParent(stool, false);
                    // The native hover glows the chair's own renderer; without one the stool never turns blue.
                    var cushion = stool.GetComponentsInChildren<MeshRenderer>(true).OrderByDescending(r => r.name.Contains("Assise"))
                        .ThenByDescending(r => r.bounds.size.sqrMagnitude).FirstOrDefault();
                    var chairs = colliders.Select(c =>
                    {
                        var chair = c.gameObject.AddComponent<Furniture_Chair>();
                        chair.Name = "Sit";
                        var own = c.GetComponent<MeshRenderer>();
                        chair.meshRenderer = own != null ? own : cushion;
                        ChairSeatField.SetValue(chair, location);
                        return chair;
                    }).ToArray();
                    Add(new Seat { Kind = SeatKind.Stool, Parent = stool, Anchor = anchor, SeatLocation = location, Chairs = chairs, Created = anchor.gameObject },
                        path + "/monmp-extra-stool-v1", identities);
                }
            BindSurfaceGlows();
            Write();
        }

        private void Add(Seat seat, string identity, NativeEntityRegistry identities)
        {
            seat.Identity = identity;
            seat.Id = seat.PurchasedVehicleEntity == 0 ? StaticId.Hash(identity) : PurchasedSeatEntityId(identity);
            if (seat.Id == 0 || seats.ContainsKey(seat.Id) || identities.Resolve(seat.Id) != null)
            {
                rows.Add("-\t" + seat.Kind + "\t" + identity + "\tidentity-collision");
                foreach (var chair in seat.Chairs) if (chair != null) UnityEngine.Object.Destroy(chair);
                if (seat.Created != null) UnityEngine.Object.Destroy(seat.Created);
                return;
            }
            seat.Key = new SeatKey(seat.Id, "extra-seat");
            var occupancy = registry.Register(seat.Key);
            seat.State = new VehiclePassengerSeatState(occupancy.Peer, occupancy.Generation);
            identities.Bind(seat.Id, VehiclePassengerSeatState.Adapter, seat.Anchor.gameObject, identity,
                seat.PurchasedVehicleEntity == 0, EntityPersistence.SessionOnly);
            seats.Add(seat.Id, seat);
            GroupSeat(seat);
            foreach (var chair in seat.Chairs) byChair[chair] = seat;
            SetInteractionEnabled(seat);
            Place(seat);
            if (seat.Kind == SeatKind.Bench && BenchNote != null) { rows.Add("-\tbench\t-\t" + BenchNote); BenchNote = null; }
            rows.Add(seat.Id.ToString("X16") + "\t" + seat.Kind + "\t" + identity + "\t" + (seat.Placed ? "placed " + (seat.Kind == SeatKind.Stool ? seat.Anchor : seat.Anchor.parent).localPosition.ToString("F2") : "unplaced"));
        }

        private void GroupSeat(Seat seat)
        {
            if (seat.Truck == null) return;
            if (!seatsByTruck.TryGetValue(seat.Truck, out var group))
            {
                group = new VehicleSeatGroup { Truck = seat.Truck };
                seatsByTruck.Add(seat.Truck, group);
                seatGroups.Add(group);
            }
            group.Seats.Add(seat);
            if (seat.Kind == SeatKind.Bench) group.HasBench = true;
        }

        private static Mesh? padMesh;
        private void AddPad(GameObject root, Vector3 size)
        {
            var seat = seats.Values.FirstOrDefault(s => s.Created == root);
            var material = Singleton<GameAssets>.i != null ? Singleton<GameAssets>.i.Interactable_Hover : null;
            if (seat == null || material == null) return;
            if (padMesh == null)
            {
                // The built-in cube, without the collider CreatePrimitive adds (no collider may touch a vehicle body).
                var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                padMesh = probe.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(probe);
            }
            MeshRenderer Make(string name, Vector3 scale)
            {
                var pad = new GameObject(name);
                pad.transform.SetParent(root.transform, false);
                pad.transform.localScale = scale;
                pad.AddComponent<MeshFilter>().sharedMesh = padMesh;
                var renderer = pad.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = false;
                return renderer;
            }
            seat.Pad = Make("MonMP Seat Hover", size);
            PlacePad(seat);
        }

        private void PlacePad(Seat seat)
        {
            if (seat.Pad == null || seat.Truck == null || !seat.AimKnown) return;
            var t = seat.Truck.transform;
            var cushion = t.TransformPoint(seat.AimLocal);
            seat.Pad.transform.position = cushion - t.up * (seat.Pad.transform.lossyScale.y * 0.5f);
            seat.Pad.transform.rotation = seat.Anchor.rotation;
        }

        // What the player sees of a seat: the surface under it, found from a hand above the seat origin
        // (the origin itself is at hip height, up to half a metre under a cab cushion or the ATV rack).
        private static Vector3 AimSurface(VehicleController vehicle, Vector3 origin)
        {
            var t = vehicle.transform;
            return t.InverseTransformPoint(TruckHit(vehicle, origin + t.up * 1.0f, -t.up, 1.4f, out var surface)
                ? surface.point + t.up * 0.03f : origin + t.up * 0.3f);
        }

        private Interactable? hovered;
        private int hoverFrame;
        private void ShowHover(Interactable? pick)
        {
            hoverFrame = Time.frameCount;
            if (pick != hovered)
            {
                SetPad(hovered, false);
                hovered = pick;
            }
            // Native hover/material initialization can run after the first selection. Refresh
            // an unchanged pick too, so a guest does not retain a detached or reset highlight.
            SetPad(pick, true);
        }
        private void SetPad(Interactable? chair, bool on)
        {
            if (!(chair is Furniture_Chair own) || !byChair.TryGetValue(own, out var seat)) return;
            on &= SeatEnabled(seat);
            if (seat.Glow != null) { seat.Glow.Set(seat.GlowRegion, on); return; }
            if (seat.Pad != null) seat.Pad.enabled = on;
        }

        private void Write()
        {
            try {
#if MONMP_DEVELOPMENT
Directory.CreateDirectory(diagnostics);

 File.WriteAllLines(Path.Combine(diagnostics, "shared-extra-seats.tsv"), rows);
#endif
 }
            catch { /* diagnostics only */ }
        }

        internal IEnumerable<WorldRecord> InitialRecords() => seats.Values
            .Where(seat => host || seat.PurchasedVehicleEntity == 0)
            .Select(seat => new WorldRecord(seat.Id, 1, VehiclePassengerSeatState.Adapter, seat.State.Encode()));
        internal bool OwnsPassengerSeatRecord(ulong id) => seats.ContainsKey(id);

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            connected.PoseReceived += OnPose; connected.PeerLeft += OnLeft;
            connected.SettingsChanged += SeatSettingsChanged;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command;
            foreach (var purchase in purchasedSeatVehicles.Keys) HydratePurchasedSeats(purchase);
            BindSurfaceGlows();
            SeatSettingsChanged(connected.Settings);
        }

        // The seat anchor a peer sits at, and how they sit there.
        internal VehicleController? CurrentLocalPassengerVehicle
        {
            get
            {
                if (session == null || gameplay == null || gameplay.Get_PlayerState != PlayerState.InFurnitureChair || gameplay.PlayerWalking == null) return null;
                var seat = SeatOwnedBy(session.LocalPeerId);
                return seat?.Truck != null && gameplay.PlayerWalking.transform.parent == seat.SeatLocation ? seat.Truck : null;
            }
        }

        internal Transform? SeatFor(uint peer, out SeatKind kind)
        {
            foreach (var seat in seats.Values)
                if (seat.State.Owner == peer && seat.Placed) { kind = seat.Kind; return seat.Anchor; }
            kind = default; return null;
        }

        // Presentation calibration uses the placed anchor without changing the native
        // walker/camera seat location, occupancy, or collider-based placement rules.
        internal IEnumerable<(VehicleController Vehicle, Transform Anchor, SeatKind Kind, int Slot)> VehicleSeatAnchors()
        {
            if (disposed) yield break;
            foreach (var seat in seats.Values)
                if (seat.Placed && seat.Truck != null && seat.Anchor != null && seat.Kind != SeatKind.Stool)
                    yield return (seat.Truck, seat.Anchor, seat.Kind, seat.Slot);
        }


#if MONMP_DEVELOPMENT
        internal ulong[] TestSeats(SeatKind kind) => seats.Values.Where(seat => seat.Kind == kind && seat.Placed)
            .OrderBy(seat => seat.Identity, StringComparer.Ordinal).Select(seat => seat.Id).ToArray();


        internal ulong[] TestAllSeats(SeatKind kind) => seats.Values.Where(seat => seat.Kind == kind)
            .OrderBy(seat => seat.Identity, StringComparer.Ordinal).Select(seat => seat.Id).ToArray();


        internal Transform TestAnchor(ulong id) => seats[id].Anchor;
        internal bool TestNpcOccupied(ulong id) => NpcNear(seats[id]);
#endif

        // Why a bed seat is or isn't placed: the truck's live colliders and the bed measurement.

#if MONMP_DEVELOPMENT
        internal string TestBed(ulong id)
        {
            var seat = seats[id];
            if (seat.Truck == null) return "no truck";
            var colliders = seat.Truck.GetComponentsInChildren<Collider>();
            bool measured = MeasureBed(seat.Truck, null, out float floorY, out float wallX, out float rearZ, out float forward, out _);
            return "placed=" + seat.Placed + " colliders=" + colliders.Count(c => c != null && !c.isTrigger && c.enabled) + "/" + colliders.Length +
                " bed=" + measured + " floor=" + floorY.ToString("F2") + " wall=" + wallX.ToString("F2") + " rear=" + rearZ.ToString("F2") + " fwd=" + forward;
        }
#endif

        // The native crosshair ray (PlayerInteractable.CheckForInteractable) ignores these layers.
        private static int crosshairIgnore = -1;
        internal static int CrosshairIgnore
        {
            get
            {
                if (crosshairIgnore != -1) return crosshairIgnore;
                int ignore = 0;
                foreach (var layer in new[] { "Player", "PartSocket", "Ignore Raycast", "MainVehicleTrigger", "ContainerHole", "SwimmingPlayerSurface", "IgnoreByPlayerInteractable" })
                { int n = LayerMask.NameToLayer(layer); if (n >= 0) ignore |= 1 << n; }
                return crosshairIgnore = ignore;
            }
        }
        // The truck seat the player aims at. Every seat of a nearby truck, native (driver, passenger)
        // or added, competes on how directly the crosshair points at it, so the driver seat's wide
        // collider no longer swallows the bench and the truck's own body (cab blocker, bed walls,
        // installed parts) never hides a seat. A door, a loose item or anything else not part of the
        // truck in front of the seat keeps the native pick. Null leaves the native pick alone.
        private readonly Dictionary<VehicleController, (Interactable Seat, Vector3 Point)[]> nativeSeats = new Dictionary<VehicleController, (Interactable, Vector3)[]>();
        private float nextNativeSeats;
        internal Interactable? PickSeat(Ray ray)
        {
            if (gameplay.Player.IsHoldingAnObject && gameplay.Get_PlayerState == PlayerState.Walking)
            { lastPick = null; ShowHover(null); return null; }
            if (ContainerHoleAim.Target(ray)) { lastPick = null; ShowHover(null); return null; }
            if (gameplay.Get_PlayerState == PlayerState.InFurnitureChair)
            {
                // #174: the seat a click would stand up from glows like any other pick.
                lastPick = null;
                var seated = SeatedPick(ray);
                ShowHover(seated);
                return seated;
            }
            var pick = gameplay.Get_PlayerState == PlayerState.Walking ? Pick(ray) : null;
            lastPick = pick;
            ShowHover(pick);
            return pick;
        }
        private Interactable? Pick(Ray ray)
        {
            bool any = Physics.Raycast(ray, out var hit, 2.4f, ~CrosshairIgnore);
            var native = any ? hit.collider.GetComponent<Interactable>() : null;
            if (any && (hit.collider.CompareTag("ContainerHole") || hit.collider.gameObject.layer == LayerMask.NameToLayer("ContainerHole"))) return null;
            // The hoist's handle, the hoist itself and its case sit in or right beside a truck, level with
            // its seats: aimed at directly, they win over any seat (#117).
            if (native is Tool_HoistPuller_Handle || native is Vehicle_HoistPullerBox ||
                (any && hit.collider.GetComponentInParent<Tool_HoistPuller>() is Tool_HoistPuller hoist && !hoist.isEquiped)) return null;
            // Anything that can be picked up, or used, right under the crosshair wins over a seat: a
            // loose item in the bed or cab, a door handle, a fuel cap. Only the vehicle's installed
            // parts (its panels and cushions) let the aim through to a seat.
            var aimed = any ? hit.collider.GetComponentInParent<Interactable>() : null;
            if (aimed != null && !IsSeat(aimed) &&
                aimed.isActiveAndEnabled && (aimed is InteractableItems || native != null)) return null;
            if (Time.unscaledTime >= nextNativeSeats) { nextNativeSeats = Time.unscaledTime + 2f; nativeSeats.Clear(); }
            Interactable? best = null;
            float bestAngle = float.MaxValue;
            foreach (var group in seatGroups)
            {
                var truck = group.Truck;
                if (truck == null) continue;
                // A bus is long: its rear seats sit well over 8 m from its middle.
                if ((truck.transform.position - ray.origin).sqrMagnitude > (group.HasBench ? 100f : 64f)) continue;
                bool onTruck = any && hit.collider.transform.IsChildOf(truck.transform);
                // In the way: anything off the truck, a truck interactable that is neither a seat nor a part
                // (a door handle), or a closed door: its installed panel must not let the aim through to a
                // seat the player cannot see.
                bool blocks = any && (!onTruck || ClosedDoor(truck, hit.collider) || native != null && !IsSeat(native) && !(native is InteractableItems));
                foreach (var (candidate, point) in Candidates(truck))
                {
                    float along = Vector3.Dot(point - ray.origin, ray.direction);
                    if (along < 0.2f || along > 2.4f) continue;
                    if (blocks && hit.distance < along - 0.3f) continue;
                    // Where the crosshair lands on the cushion, in metres: dividing by the distance (an
                    // angle) favoured the nearest seat, so looking across the cab always gave the driver
                    // seat (#118). Looking down onto a bench, the point on the cushion's level is used.
                    float miss = (ray.origin + ray.direction * along - point).magnitude;
                    if (ray.direction.y < -0.2f)
                    {
                        float down = (point.y - ray.origin.y) / ray.direction.y;
                        if (down > 0.2f && down < 2.8f)
                        {
                            var onCushion = ray.origin + ray.direction * down - point;
                            miss = Mathf.Min(miss, new Vector2(onCushion.x, onCushion.z).magnitude);
                        }
                    }
                    if (miss > 0.45f) continue;
                    // The seat already under the crosshair keeps it until another is clearly closer.
                    float score = candidate == lastPick ? miss - 0.08f : miss;
                    if (score < bestAngle) { best = candidate; bestAngle = score; }
                }
            }
            return best;
        }
        private Interactable? lastPick;
        private static bool ClosedDoor(VehicleController truck, Collider collider) =>
            collider.GetComponentInParent<Vehicle_Door>() is Vehicle_Door door && door.vehicleController == truck &&
            (door.isDriverDoor ? truck.isDriverDoorClosed : truck.isPassengerDoorClosed);
        // Seated in one of MonMP's seats: they have no cushion collider to click, so standing up was
        // only possible with jump (#141). Looking down at the seat itself (where the crosshair meets the
        // cushion's level, within a cushion's reach of it) picks it, so a click there stands up; a click
        // anywhere else does nothing, and something else to use in the way keeps its own pick.
        private Interactable? SeatedPick(Ray ray)
        {
            if (session == null) return null;
            var mine = SeatOwnedBy(session.LocalPeerId);
            if (mine == null || mine.Chairs.Length == 0 || !DoorAllowsEntry(mine)) return null;
            if (Physics.Raycast(ray, out var hit, 2.4f, ~CrosshairIgnore))
            {
                if (hit.collider.CompareTag("ContainerHole") || hit.collider.gameObject.layer == LayerMask.NameToLayer("ContainerHole")) return null;
                if (hit.collider.GetComponentInParent<Interactable>() is Interactable other && !IsSeat(other)) return null;
            }
            if (!mine.AimKnown) Aim(mine);
            var cushion = mine.Truck != null && mine.AimKnown ? mine.Truck.transform.TransformPoint(mine.AimLocal) : mine.Anchor.position;
            if (ray.direction.y > -0.25f) return null;
            float down = (cushion.y - ray.origin.y) / ray.direction.y;
            if (down < 0.1f || down > 2.4f) return null;
            var off = ray.origin + ray.direction * down - cushion;
            return new Vector2(off.x, off.z).magnitude <= 0.4f ? mine.Chairs[0] : null;
        }
        private static bool Placed(InteractableItems item) => item is InteractableParts part ? part.isPlaced : item is Hardware_Bolt bolt && bolt.isPlaced;

        private bool IsSeat(Interactable value) => value is Vehicle_DriverSeat || value is Vehicle_PassengerSeat ||
            value is Furniture_Chair chair && byChair.ContainsKey(chair);

        // Aim points: the cushion, rack or bed floor each seat shows the player (vehicle-local, so they ride along).
        private IEnumerable<(Interactable Seat, Vector3 Point)> Candidates(VehicleController truck)
        {
            var t = truck.transform;
            if (seatsByTruck.TryGetValue(truck, out var group))
            {
                foreach (var seat in group.Seats)
                    if (SeatEnabled(seat) && seat.Truck == truck && seat.Placed && seat.Kind != SeatKind.Stool && !seat.State.Occupied && seat.Chairs.Length != 0 && DoorAllowsEntry(seat))
                    {
                        if (!seat.AimKnown) Aim(seat);
                        yield return (seat.Chairs[0], t.TransformPoint(seat.AimLocal));
                    }
            }
            if (!nativeSeats.TryGetValue(truck, out var list))
            {
                list = truck.GetComponentsInChildren<Vehicle_DriverSeat>(true).Where(d => d != null && d.vehicleController == truck && d.seatPosition != null)
                    .Select(d => ((Interactable)d, AimSurface(truck, d.seatPosition.position)))
                    .Concat(truck.GetComponentsInChildren<Vehicle_PassengerSeat>(true).Where(p => p != null && p.vehicleController == truck && p.seatPosition != null)
                        .Select(p => ((Interactable)p, AimSurface(truck, p.seatPosition.position)))).ToArray();
                nativeSeats[truck] = list;
            }
            foreach (var (seat, point) in list)
                if (seat != null && seat.isActiveAndEnabled && seat.CanUse()) yield return (seat, t.TransformPoint(point));
        }

        private void Aim(Seat seat)
        {
            if (seat.Truck == null || !seat.Placed) return;
            seat.AimLocal = AimSurface(seat.Truck, seat.Anchor.position);
            // #177: the bus benches have no collider of their own, so the probe found the floor and the
            // hover pad lay there. The rider's hips (the anchor) sit on the cushion.
            if (seat.Kind == SeatKind.Bench)
            {
                // The bus's native seat origin (which the rows share) sits near the floor, so the pad still lay
                // there (#177 retest): the cushion is at least a bench height above the floor found.
                var hips = seat.Truck.transform.InverseTransformPoint(seat.Anchor.position);
                seat.AimLocal = new Vector3(hips.x, Mathf.Max(hips.y - 0.05f, seat.AimLocal.y + 0.42f), hips.z);
            }
            seat.AimKnown = true;
            PlacePad(seat);
        }

        // What a player gets under the crosshair aiming at a seat from where one would stand: beside the
        // bed or standing in it for bed seats; outside either cab door for cab seats (the driver side
        // looks across the driver seat).

#if MONMP_DEVELOPMENT
        internal string TestCrosshair(ulong id, int view, out bool hit, string? snapshot = null)
        {
            var seat = seats[id];
            if (!seat.AimKnown) Aim(seat);
            var target = seat.Truck != null ? seat.Truck.transform.TransformPoint(seat.AimLocal) : seat.Anchor.position + seat.Anchor.up * 0.3f;
            Vector3 eye;
            if (seat.Kind == SeatKind.Rider)
            {
                // Beside the ATV on the seat's own side, or from behind it.
                var t = seat.Truck!.transform;
                var local = t.InverseTransformPoint(target);
                eye = view == 0 ? t.TransformPoint(new Vector3(Mathf.Sign(local.x) * (Mathf.Abs(local.x) + 1.0f), local.y + 0.7f, local.z))
                    : t.TransformPoint(local + (local - t.InverseTransformPoint(TestDriver(seat.Truck))).normalized * 1.2f + Vector3.up * 0.7f);
            }
            else if (seat.Kind == SeatKind.Bench && seat.Truck != null)
            {
                var t = seat.Truck.transform;
                var local = t.InverseTransformPoint(target);
                eye = t.TransformPoint(new Vector3(0, local.y + 0.65f, local.z + (view == 0 ? 0.6f : -0.6f)));
            }
            else if (seat.Truck != null)
            {
                var t = seat.Truck.transform;
                var local = t.InverseTransformPoint(target);
                var far = seats.Values.First(s => s.Truck == seat.Truck && s.Kind == SeatKind.Cab && s.Slot == 1);
                float farSide = Mathf.Sign(t.InverseTransformPoint(far.Anchor.position).x);
                if (seat.Kind == SeatKind.Cab) eye = t.TransformPoint(new Vector3((view == 0 ? farSide : -farSide) * 1.6f, local.y + 0.75f, local.z + 0.3f));
                else if (view == 0) eye = t.TransformPoint(new Vector3((local.x >= 0 ? 1 : -1) * (Mathf.Abs(local.x) + 1.1f), local.y + 0.6f, local.z));
                // Standing in the middle of the bed, a little forward of the seat.
                else eye = t.TransformPoint(new Vector3(0, local.y + 1.4f, local.z + 0.6f));
            }
            else eye = target + Vector3.up * 1.1f - seat.Anchor.forward * 0.9f;
            var ray = new Ray(eye, (target - eye).normalized);
            string what = "nothing";
            Interactable? native = null;
            if (Physics.Raycast(ray, out var info, 2.4f, ~CrosshairIgnore))
            {
                native = info.collider.GetComponent<Interactable>();
                what = info.collider.name + "@" + LayerMask.LayerToName(info.collider.gameObject.layer);
            }
            var chosen = PickSeat(ray) ?? native;
            // Picked, and lit blue the way the native hover lights a chair.
            hit = chosen != null && seat.Chairs.Contains(chosen as Furniture_Chair) && HoverLit(seat);
            if (snapshot != null && Camera.main != null)
            {
                // What the player sees from there, with the hover showing.
                var camera = new GameObject("MonMP Seat Snapshot").AddComponent<Camera>();
                try
                {
                    camera.CopyFrom(Camera.main);
                    camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
                    AvatarPreviewCapture.SaveView(camera, snapshot);
                }
                finally { UnityEngine.Object.Destroy(camera.gameObject); }
            }
            return what + "->" + (chosen == null ? "-" : chosen.name) + (HoverLit(seat) ? "" : " unlit") +
                " d=" + (target - eye).magnitude.ToString("F2");
        }
#endif

        // The hoist case in the bed's back corner: aimed at from inside the bed it must win over every seat,
        // and no bed seat may sit over it (#117).

#if MONMP_DEVELOPMENT
        internal string TestHoistCase(ulong anyTruckSeat, out bool ok)
        {
            var truck = seats[anyTruckSeat].Truck!;
            var t = truck.transform;
            var box = truck.GetComponentsInChildren<Vehicle_HoistPullerBox>(true).SelectMany(b => b.GetComponentsInChildren<Collider>())
                .FirstOrDefault(c => c != null && c.enabled && c.gameObject.activeInHierarchy);
            if (box == null) { ok = true; return "no hoist case"; }
            var center = t.InverseTransformPoint(box.bounds.center);
            var eye = t.TransformPoint(new Vector3(center.x * 0.2f, center.y + 1.4f, center.z + 0.4f));
            var chosen = PickSeat(new Ray(eye, (box.bounds.center - eye).normalized));
            float clearance = seats.Values.Where(s => s.Truck == truck && s.Kind == SeatKind.Ground && s.Placed)
                .Select(s => { var a = t.InverseTransformPoint(s.Anchor.position); return new Vector2(a.x - center.x, a.z - center.z).magnitude; })
                .DefaultIfEmpty(float.PositiveInfinity).Min();
            ok = chosen == null && clearance > 0.4f;
            return "case at " + center.ToString("F2") + " pick=" + (chosen == null ? "-" : chosen.name) + " nearest bed seat=" + clearance.ToString("F2");
        }
#endif

        // The native driver seat, aimed at from outside its own door, must stay the driver seat.

#if MONMP_DEVELOPMENT
        internal string TestDriverCrosshair(ulong anyTruckSeat, out bool hit)
        {
            var truck = seats[anyTruckSeat].Truck!;
            var (driver, point) = Candidates(truck).First(c => c.Seat is Vehicle_DriverSeat);
            var t = truck.transform;
            var local = t.InverseTransformPoint(point);
            // A standing player's eyes are about 0.75 m above the cushion, looking in ahead of the door handle
            // (on the sill at the door's back edge, level with the seat; aimed at directly it rightly wins).
            var eye = t.TransformPoint(new Vector3(Mathf.Sign(local.x) * 1.6f, local.y + 0.75f, local.z + 0.3f));
            var ray = new Ray(eye, (point - eye).normalized);
            var chosen = PickSeat(ray) ?? (Physics.Raycast(ray, out var info, 2.4f, ~CrosshairIgnore) ? info.collider.GetComponent<Interactable>() : null);
            hit = chosen == driver;
            string blocker = Physics.Raycast(ray, out var first, 2.4f, ~CrosshairIgnore)
                ? " first=" + first.collider.name + " at " + t.InverseTransformPoint(first.point).ToString("F2") + " bounds " + t.InverseTransformPoint(first.collider.bounds.center).ToString("F2") + "/" + first.collider.bounds.size.ToString("F2") : "";
            return (chosen == null ? "-" : chosen.name) + " eye=" + local.ToString("F2") + "->" + t.InverseTransformPoint(eye).ToString("F2") + blocker;
        }


        // From outside a closed driver door (the passenger door open), no seat is picked through it.
        internal string TestClosedDoorCrosshair(ulong anyTruckSeat, out bool blocked)
        {
            var truck = seats[anyTruckSeat].Truck!;
            var t = truck.transform;
            float side = Mathf.Sign(t.InverseTransformPoint(TestDriver(truck)).x);
            var targets = seats.Values.Where(s => s.Truck == truck && s.Kind == SeatKind.Cab && s.Placed)
                .Select(s => { if (!s.AimKnown) Aim(s); return ((Interactable)s.Chairs[0], t.TransformPoint(s.AimLocal)); })
                .Concat(truck.GetComponentsInChildren<Vehicle_DriverSeat>(true).Where(d => d.vehicleController == truck && d.seatPosition != null)
                    .Select(d => ((Interactable)d, t.TransformPoint(AimSurface(truck, d.seatPosition.position)))))
                .Concat(truck.GetComponentsInChildren<Vehicle_PassengerSeat>(true).Where(p => p.vehicleController == truck && p.seatPosition != null)
                    .Select(p => ((Interactable)p, t.TransformPoint(AimSurface(truck, p.seatPosition.position))))).ToArray();
            blocked = truck.isDriverDoorClosed && !truck.isPassengerDoorClosed && targets.Length != 0;
            var lines = new List<string>();
            foreach (var (seat, point) in targets)
            {
                var local = t.InverseTransformPoint(point);
                var eye = t.TransformPoint(new Vector3(side * 1.6f, local.y + 0.75f, local.z + 0.3f));
                var ray = new Ray(eye, (point - eye).normalized);
                lastPick = null;
                var chosen = PickSeat(ray);
                bool door = Physics.Raycast(ray, out var first, 2.4f, ~CrosshairIgnore) && ClosedDoor(truck, first.collider);
                if (chosen != null || !door) blocked = false;
                lines.Add(seat.GetType().Name + ":" + (chosen == null ? "-" : chosen.name) + " first=" + (first.collider == null ? "none" : first.collider.name) + (door ? "(closed door)" : ""));
            }
            ShowHover(null);
            return string.Join("; ", lines.ToArray());
        }

        private static Vector3 TestDriver(VehicleController vehicle) => vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true)
            .First(d => d.vehicleController == vehicle && d.seatPosition != null).seatPosition.position;
#endif

        // Vehicle-local layout of the added seats and anything that competes with them for the crosshair.

#if MONMP_DEVELOPMENT
        internal string TestLayout()
        {
            var lines = new List<string>();
            foreach (var truck in seats.Values.Select(seat => seat.Truck).Where(truck => truck != null).Distinct())
            {
                var t = truck!.transform;
                lines.Add(truck.vehicle_type + " seats: " + string.Join(" ", seats.Values.Where(seat => seat.Truck == truck)
                    .Select(seat => seat.Kind + "" + seat.Slot + "=" + t.InverseTransformPoint(seat.Anchor.position).ToString("F2") + " aim " + seat.AimLocal.ToString("F2")).ToArray()));
                foreach (var native in truck.GetComponentsInChildren<Interactable>(true).Where(i => i is Vehicle_DriverSeat || i is Vehicle_PassengerSeat))
                {
                    var renderer = native.meshRenderer;
                    var mesh = renderer == null ? null : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    var size = mesh == null ? Vector3.zero : Vector3.Scale(mesh.bounds.size, renderer!.transform.lossyScale);
                    lines.Add("  native highlight " + native.GetType().Name + " renderer=" + (renderer == null ? "none" : RuntimeProbe.CanonicalPath(renderer.transform)) +
                        " mesh=" + (mesh == null ? "none" : mesh.name) + " size=" + size.ToString("F3") +
                        (renderer == null ? "" : " position=" + t.InverseTransformPoint(renderer.bounds.center).ToString("F3")));
                }
                foreach (var interactable in truck.GetComponentsInChildren<Interactable>(true))
                {
                    if (interactable == null || interactable is Furniture_Chair own && byChair.ContainsKey(own)) continue;
                    foreach (var collider in interactable.GetComponentsInChildren<Collider>(true))
                    {
                        if (collider == null) continue;
                        var b = collider.bounds;
                        lines.Add("  " + interactable.GetType().Name + " " + collider.name + (collider.enabled && collider.gameObject.activeInHierarchy ? "" : " (off)") +
                            " at " + t.InverseTransformPoint(b.center).ToString("F2") + " size " + b.size.ToString("F2"));
                    }
                }
            }
            return string.Join("\n", lines.ToArray());
        }


        internal Transform TestVehicle(ulong id) => seats[id].Truck!.transform;


        internal Furniture_Chair TestChair(ulong id) => seats[id].Chairs[0];


        internal VehiclePassengerSeatState TestState(ulong id) => seats[id].State;

        internal (bool Native, string Source) TestHighlight(ulong id) => (seats[id].Glow != null, seats[id].HighlightSource);


        internal bool TestSeatedHere(ulong id) => gameplay.Get_PlayerState == PlayerState.InFurnitureChair && gameplay.PlayerWalking != null &&
            gameplay.PlayerWalking.transform.parent == seats[id].SeatLocation;
#endif


        // --- Geometry --------------------------------------------------------------------------

        // Cab seats mirror the driver seat to the middle and the right; bed seats sit on the bed
        // floor against the side walls at the back, facing in. Measured with rays against the truck's
        // own colliders; re-measured on each sit so a moved tailgate or new part is respected.
        private void Place(Seat seat)
        {
            if (seat.Kind == SeatKind.Stool)
            {
                var surface = StoolSurface(seat.Parent);
                if (!surface.HasValue) return;
                seat.Anchor.position = surface.Value;
                seat.Anchor.rotation = Quaternion.Euler(0, seat.SeatLocation.eulerAngles.y, 0);
                seat.Placed = true;
                return;
            }
            var truck = seat.Truck;
            if (truck == null) return;
            var t = truck.transform;
            var driverPosition = truck.GetComponentsInChildren<Vehicle_DriverSeat>(true)
                .FirstOrDefault(d => d != null && d.vehicleController == truck && d.seatPosition != null)?.seatPosition;
            if (seat.Kind == SeatKind.Cab)
            {
                if (driverPosition == null) return;
                var local = t.InverseTransformPoint(driverPosition.position);
                // The driver sits on one side; the other seats go to the middle and the far side.
                local.x = seat.Slot == 0 ? 0f : -local.x;
                seat.Anchor.localPosition = Vector3.zero;
                seat.Anchor.parent.localPosition = local;
                seat.Anchor.parent.localRotation = Quaternion.Inverse(t.rotation) * driverPosition.rotation;
                seat.Anchor.localRotation = Quaternion.identity;
                // A native passenger seat already there wins.
                var at = seat.Anchor.position;
                seat.Placed = !truck.GetComponentsInChildren<Vehicle_PassengerSeat>(true)
                    .Any(p => p != null && p.seatPosition != null && Vector3.Distance(p.seatPosition.position, at) < 0.35f);
                Aim(seat);
                return;
            }
            if (seat.Kind == SeatKind.Bench)
            {
                if (!MeasureBus(truck, driverPosition, seat.Slot, out var bench, out var facing)) return;
                seat.Anchor.parent.localPosition = bench;
                seat.Anchor.parent.localRotation = facing;
                seat.Anchor.localPosition = Vector3.zero; seat.Anchor.localRotation = Quaternion.identity;
                seat.Placed = true;
                Aim(seat);
                return;
            }
            if (seat.Kind == SeatKind.Rider)
            {
                if (driverPosition == null || !MeasureRack(truck, driverPosition, seat.Slot, out var rider)) return;
                seat.Anchor.parent.localPosition = rider;
                seat.Anchor.parent.localRotation = Quaternion.Inverse(t.rotation) * driverPosition.rotation;
                seat.Anchor.localPosition = Vector3.zero; seat.Anchor.localRotation = Quaternion.identity;
                seat.Placed = true;
                Aim(seat);
                return;
            }
            if (!MeasureBed(truck, driverPosition, out var floorY, out var wallX, out var rearZ, out var forward, out var frontZ)) return;
            int index = seat.Slot - 2;
            float side = index % 2 == 0 ? -1f : 1f;
            // The back row clears the hoist case in the back corner (0.25-0.52 m from the tailgate), which a
            // seat over it hid from the crosshair (#117); the front row stays short of the cab wall or a tank.
            float first = Mathf.Clamp(Mathf.Abs(frontZ - rearZ) - 0.95f, 0.35f, 0.8f);
            float along = rearZ + forward * (first + 0.6f * (index / 2));
            var root = seat.Anchor.parent;
            root.localPosition = new Vector3(side * Mathf.Max(0.2f, wallX - 0.3f), floorY, along);
            // Backs to the side wall, facing the middle of the bed.
            root.localRotation = Quaternion.LookRotation(new Vector3(-side, 0, 0), Vector3.up);
            seat.Anchor.localPosition = Vector3.zero; seat.Anchor.localRotation = Quaternion.identity;
            seat.Placed = true;
            Aim(seat);
        }

        // Where a sitter's hips go on a stool: the seat pan of its shell (BarStool_Assise). The shell
        // includes the backrest, so its bounds top is the backrest's. A readable mesh is measured: over
        // a grid the top vertex of each cell gives the shell's upper surface, and the height most cells
        // share (the flat pan) wins. The shipped shells aren't readable (nor is their GPU copy), and
        // their pan sits a little over half way up the shell.
        private static readonly Dictionary<Transform, Vector3?> stoolSurfaces = new Dictionary<Transform, Vector3?>();
        internal static Vector3? StoolSurface(Transform stool)
        {
            if (stoolSurfaces.TryGetValue(stool, out var known)) return known;
            var filters = stool.GetComponentsInChildren<MeshFilter>(true)
                .Where(f => f != null && f.sharedMesh != null && f.GetComponentInParent<Actor>() == null).ToArray();
            var cushion = filters.Where(f => f.name.IndexOf("Assise", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (cushion.Length != 0) filters = cushion;
            var renderers = filters.Select(f => f.GetComponent<Renderer>()).Where(r => r != null).ToArray();
            if (renderers.Length == 0) return null;
            var bounds = renderers[0].bounds; foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            const int Grid = 7;
            var tops = new Dictionary<int, Vector3>();
            foreach (var f in filters.Where(f => f.sharedMesh.isReadable))
                foreach (var local in f.sharedMesh.vertices)
                {
                    var point = f.transform.TransformPoint(local);
                    int i = Mathf.Clamp((int)((point.x - bounds.min.x) / Mathf.Max(bounds.size.x, 1e-3f) * Grid), 0, Grid - 1);
                    int j = Mathf.Clamp((int)((point.z - bounds.min.z) / Mathf.Max(bounds.size.z, 1e-3f) * Grid), 0, Grid - 1);
                    if (!tops.TryGetValue(i * Grid + j, out var top) || point.y > top.y) tops[i * Grid + j] = point;
                }
            Vector3? result;
            if (tops.Count < 4) result = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.55f, bounds.center.z);
            else
            {
                var cells = tops.Values.ToArray();
                float pan = cells.Select(c => c.y).OrderByDescending(y => cells.Count(o => Mathf.Abs(o.y - y) < 0.04f)).ThenBy(y => y).First();
                var on = cells.Where(c => Mathf.Abs(c.y - pan) < 0.04f).ToArray();
                result = new Vector3(on.Average(c => c.x), on.Average(c => c.y), on.Average(c => c.z));
            }
            stoolSurfaces[stool] = result;
            return result;
        }

        // The bus has eleven bench rows a side; the first is the two vanilla passenger seats (#177).
        private const int BusBenchRows = 11;
        private const int BusRows = BusBenchRows - 1;
        // The fallback layout loses its front rows to the stairs and the vanilla seats, so it has two more
        // rows of slots behind them; the rear wall still ends the rows (#177).
        private const int BusSlotRows = BusRows + 2;
        private const float BusRowPitch = 0.9f;

        [System.Diagnostics.Conditional("MONMP_DEVELOPMENT")]
        private void RecordHighlight(Seat seat, string description)
        {
#if MONMP_DEVELOPMENT
            seat.HighlightSource = description;
            rows.Add(seat.Id.ToString("X16") + "\t" + seat.Kind + "\t" + seat.Identity + "\thighlight " + description);
#endif
        }

        // The benches along one side of the aisle, measured from the bus's own mesh triangles. The benches
        // behind the vanilla row have no colliders (a ray down the seat line only met the floor, #177), and
        // the Seats meshes cover only the vanilla row. Each 5 cm step along the seat line gets the highest
        // triangle over it within the seat's height band; a run of steps well above the floor is one bench.
        // Returns the rows behind the vanilla one, each moved by the vanilla seat's offset from its bench's
        // centre, or none when the meshes cannot be read.
        private const float BenchStep = 0.05f;
        private static readonly Dictionary<(VehicleController, int), (float[] Rows, float At)> benchRows = new Dictionary<(VehicleController, int), (float[], float)>();
        private static float[] BenchRows(VehicleController bus, int side, Vector3 nativeLocal, float rearZ)
        {
            if (benchRows.TryGetValue((bus, side), out var known) && (known.Rows.Length != 0 || Time.unscaledTime - known.At < 5f)) return known.Rows;
            var t = bus.transform;
            float dir = rearZ < nativeLocal.z ? -1f : 1f;
            float start = nativeLocal.z - dir * 0.6f;
            int count = Mathf.Clamp(Mathf.CeilToInt((rearZ - start) * dir / BenchStep), 0, 1000);
            var top = Enumerable.Repeat(float.NaN, count).ToArray();
            float bandLow = nativeLocal.y - 0.3f, bandHigh = nativeLocal.y + 1.2f;
            int readable = 0, unreadable = 0, batched = 0, used = 0;
            var unreadableNames = new List<string>();
            foreach (var filter in bus.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
                var mesh = filter != null ? filter.sharedMesh : null;
                if (renderer == null || mesh == null || filter!.name.StartsWith("MonMP", StringComparison.Ordinal)) continue;
                if (renderer.isPartOfStaticBatch) { batched++; continue; }
                if (!mesh.isReadable) { unreadable++; if (unreadableNames.Count < 12) unreadableNames.Add(filter.name); continue; }
                readable++;
                var m = t.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                var vertices = mesh.vertices; var triangles = mesh.triangles;
                var local = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++) local[i] = m.MultiplyPoint3x4(vertices[i]);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    var p0 = local[triangles[i]]; var p1 = local[triangles[i + 1]]; var p2 = local[triangles[i + 2]];
                    if (Mathf.Max(p0.x, p1.x, p2.x) < nativeLocal.x - 0.2f || Mathf.Min(p0.x, p1.x, p2.x) > nativeLocal.x + 0.2f) continue;
                    float yMax = Mathf.Max(p0.y, p1.y, p2.y);
                    if (yMax < bandLow || Mathf.Min(p0.y, p1.y, p2.y) > bandHigh) continue;
                    int from = Mathf.FloorToInt((Mathf.Min(p0.z, p1.z, p2.z) - start) * dir / BenchStep);
                    int to = Mathf.FloorToInt((Mathf.Max(p0.z, p1.z, p2.z) - start) * dir / BenchStep);
                    if (from > to) (from, to) = (to, from);
                    from = Mathf.Max(from, 0); to = Mathf.Min(to, count - 1);
                    if (from > to) continue;
                    float h = Mathf.Min(yMax, bandHigh);
                    for (int k = from; k <= to; k++) if (float.IsNaN(top[k]) || h > top[k]) top[k] = h;
                    used++;
                }
            }
            var result = Array.Empty<float>();
            string note = "bench-mesh side " + side + ": readable " + readable + ", unreadable " + unreadable +
                (unreadableNames.Count != 0 ? " (" + string.Join(",", unreadableNames) + ")" : "") + ", batched " + batched + ", triangles " + used +
                ", native z " + nativeLocal.z.ToString("F2") + ", rear z " + rearZ.ToString("F2");
            var heights = top.Where(h => !float.IsNaN(h)).ToArray();
            if (heights.Length >= 10)
            {
                float floor = heights.Min();
                // One character per step: '.' nothing, 0-9 the height above the floor in 10 cm.
                note += ", floor " + floor.ToString("F2") + ", profile " + new string(top.Select(h => float.IsNaN(h) ? '.' : (char)('0' + Mathf.Clamp(Mathf.FloorToInt((h - floor) / 0.1f), 0, 9))).ToArray());
                var segments = new List<(float Start, float End)>();
                int first = -1, last = -1, gap = 0;
                for (int k = 0; k < count; k++)
                {
                    bool bench = !float.IsNaN(top[k]) && top[k] > floor + 0.2f;
                    if (bench) { if (first < 0) first = k; last = k; gap = 0; continue; }
                    if (first < 0 || ++gap <= 1) continue;
                    segments.Add((start + dir * first * BenchStep, start + dir * (last + 1) * BenchStep)); first = -1; gap = 0;
                }
                if (first >= 0) segments.Add((start + dir * first * BenchStep, start + dir * (last + 1) * BenchStep));
                segments = segments.Where(g => Mathf.Abs(g.End - g.Start) is >= 0.25f and <= 1.4f).ToList();
                int vanilla = segments.FindIndex(g => (nativeLocal.z - g.Start) * dir >= -0.2f && (g.End - nativeLocal.z) * dir >= -0.2f);
                note += ", benches " + string.Join(" ", segments.Select(g => g.Start.ToString("F2") + ".." + g.End.ToString("F2"))) + ", vanilla " + vanilla;
                if (vanilla >= 0 && segments.Count - vanilla - 1 >= 5)
                {
                    float offset = nativeLocal.z - (segments[vanilla].Start + segments[vanilla].End) * 0.5f;
                    result = segments.Skip(vanilla + 1).Select(g => (g.Start + g.End) * 0.5f + offset).ToArray();
                }
            }
            if (result.Length == 0) result = BenchRowsFromRender(bus, nativeLocal, dir, rearZ, ref note);
            BenchNote = note + " -> rows " + string.Join(" ", result.Select(z => z.ToString("F2")));
            benchRows[(bus, side)] = (result, Time.unscaledTime);
            return result;
        }

        // The shipped bus meshes are unreadable (the benches are part of one "interior" mesh), so the
        // triangle scan found nothing and the even fallback spacing lined up with one row only (#177).
        // The GPU still draws them: a camera over the seat line renders only the bus, clipped to a thin
        // slab at seat height, and each column of the image that shows geometry is over a bench.
        private const int ProbeLayer = 31, ProbeHeight = 12;
        private static float[] BenchRowsFromRender(VehicleController bus, Vector3 nativeLocal, float dir, float rearZ, ref string note)
        {
            float front = nativeLocal.z - dir * 0.8f;
            // A side view of the seat line in 10 cm slabs. The benches are the slab with the most
            // bench-sized runs (the anchor's height is not the cushion's: they showed 0.8 m above it).
            var view = new List<string>();
            var segments = new List<(float Start, float End)>();
            float best = float.NaN;
            for (float y = nativeLocal.y + 1.0f; y > nativeLocal.y - 1.0f; y -= 0.1f)
            {
                var slab = RenderSlab(bus, nativeLocal.x, front, rearZ, y, y - 0.1f, out string error);
                if (slab == null) { note += ", render probe failed: " + error; return Array.Empty<float>(); }
                view.Add((y - nativeLocal.y).ToString("+0.0;-0.0") + ":" + new string(Enumerable.Range(0, slab.Length / 5).Select(i => slab.Skip(i * 5).Take(5).Any(o => o) ? '#' : '.').ToArray()));
                var runs = Runs(slab, front, (rearZ - front) / slab.Length);
                if (runs.Count > segments.Count) { segments = runs; best = y - nativeLocal.y; }
            }
            note += ", side view (10 cm columns from z " + front.ToString("F2") + ") " + string.Join(" | ", view) + ", bench slab " + best.ToString("+0.0;-0.0");
            int vanilla = segments.FindIndex(g => (nativeLocal.z - g.Start) * dir >= -0.2f && (g.End - nativeLocal.z) * dir >= -0.2f);
            note += ", render benches " + string.Join(" ", segments.Select(g => g.Start.ToString("F2") + ".." + g.End.ToString("F2"))) + ", vanilla " + vanilla;
            if (vanilla < 0 || segments.Count - vanilla - 1 < 5) return Array.Empty<float>();
            float offset = nativeLocal.z - (segments[vanilla].Start + segments[vanilla].End) * 0.5f;
            return segments.Skip(vanilla + 1).Select(g => (g.Start + g.End) * 0.5f + offset).ToArray();
        }

        // Bench-sized runs of drawn columns, bridging gaps of up to 4 cm.
        private static List<(float Start, float End)> Runs(bool[] occupied, float front, float step)
        {
            var segments = new List<(float Start, float End)>();
            int first = -1, last = -1, gap = 0;
            for (int k = 0; k < occupied.Length; k++)
            {
                if (occupied[k]) { if (first < 0) first = k; last = k; gap = 0; continue; }
                if (first < 0 || ++gap <= 2) continue;
                segments.Add((front + step * first, front + step * (last + 1))); first = -1; gap = 0;
            }
            if (first >= 0) segments.Add((front + step * first, front + step * (last + 1)));
            return segments.Where(g => Mathf.Abs(g.End - g.Start) is >= 0.3f and <= 1.0f).ToList();
        }

        // One slab of the bus between two local heights over the line x = const from front to rear z:
        // per 2 cm column, whether any of the bus's own geometry is drawn there. Front first.
        private static bool[]? RenderSlab(VehicleController bus, float x, float front, float rearZ, float top, float bottom, out string error)
        {
            error = "";
            var t = bus.transform;
            Vector3 a = t.TransformPoint(new Vector3(x, top, front)), b = t.TransformPoint(new Vector3(x, top, rearZ));
            float length = Vector3.Distance(a, b), thickness = t.TransformVector(new Vector3(0, top - bottom, 0)).magnitude;
            float halfWidth = t.TransformVector(new Vector3(0.12f, 0, 0)).magnitude;
            if (length < 1f || thickness <= 0f || halfWidth <= 0f) { error = "degenerate slab"; return null; }
            int width = Mathf.Clamp(Mathf.RoundToInt(length / 0.02f), 64, 1024);
            var layers = new List<(GameObject Go, int Layer)>();
            GameObject? probe = null; RenderTexture? target = null; Texture2D? read = null;
            var previous = RenderTexture.active;
            try
            {
                foreach (var renderer in bus.GetComponentsInChildren<MeshRenderer>())
                    if (renderer != null && renderer.enabled && !renderer.name.StartsWith("MonMP", StringComparison.Ordinal))
                    { layers.Add((renderer.gameObject, renderer.gameObject.layer)); renderer.gameObject.layer = ProbeLayer; }
                probe = new GameObject("MonMP Bench Probe");
                var camera = probe.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true; camera.orthographicSize = halfWidth;
                camera.aspect = length / (2f * halfWidth);
                camera.nearClipPlane = 0.01f; camera.farClipPlane = 0.01f + thickness;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(1f, 0f, 1f, 0f);
                camera.cullingMask = 1 << ProbeLayer; camera.useOcclusionCulling = false; camera.allowHDR = false; camera.allowMSAA = false;
                var rotation = Quaternion.LookRotation(-t.up, Vector3.Cross(b - a, -t.up));
                probe.transform.SetPositionAndRotation((a + b) * 0.5f + t.up * 0.01f, rotation);
                target = RenderTexture.GetTemporary(width, ProbeHeight, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                read = new Texture2D(width, ProbeHeight, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, width, ProbeHeight), 0, 0);
                var pixels = read.GetPixels32();
                camera.targetTexture = null;
                var occupied = new bool[width];
                for (int u = 0; u < width; u++)
                {
                    int hits = 0;
                    for (int v = 0; v < ProbeHeight; v++)
                    {
                        var c = pixels[v * width + u];
                        if (!(c.r > 240 && c.g < 16 && c.b > 240)) hits++;
                    }
                    occupied[u] = hits >= ProbeHeight / 3;
                }
                // The image's left edge is the front unless the camera's right axis points forward.
                return Vector3.Dot(rotation * Vector3.right, b - a) < 0f ? Enumerable.Reverse(occupied).ToArray() : occupied;
            }
            catch (Exception ex) { error = ex.Message; return null; }
            finally
            {
                foreach (var (go, layer) in layers) if (go != null) go.layer = layer;
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (read != null) UnityEngine.Object.Destroy(read);
                if (probe != null) UnityEngine.Object.Destroy(probe);
            }
        }

        // What the last bench measurement saw, for shared-extra-seats.tsv (#177).
        private static string? BenchNote;

        // Bus seat slot: side = slot % 2 (the native passenger seats' two sides of the aisle), row = slot / 2 + 1
        // counted back from the vanilla row, spaced evenly along the benches. Rows share the native seats'
        // height, side and facing; a row past the rear of the bus stays unplaced.
        private static bool MeasureBus(VehicleController bus, Transform? driver, int slot, out Vector3 local, out Quaternion facing)
        {
            local = default; facing = Quaternion.identity;
            var t = bus.transform;
            var native = bus.GetComponentsInChildren<Vehicle_PassengerSeat>(true)
                .Where(p => p != null && p.vehicleController == bus && p.seatPosition != null)
                .Select(p => p.seatPosition).OrderBy(p => t.InverseTransformPoint(p.position).x).ToArray();
            var reference = native.Length != 0 ? native[0] : driver;
            if (reference == null || !LocalBounds(bus, out var min, out var max)) return false;
            float driverZ = driver != null ? t.InverseTransformPoint(driver.position).z : max.z - 1f;
            float forward = driverZ >= (min.z + max.z) * 0.5f ? 1f : -1f;
            if (native.Length >= 2)
            {
                var nativeLocal = t.InverseTransformPoint(native[slot % 2].position);
                var benches = BenchRows(bus, slot % 2, nativeLocal, forward > 0 ? min.z : max.z);
                if (benches.Length != 0)
                {
                    // One seat per measured bench; slots past the last bench stay unplaced.
                    if (slot / 2 >= benches.Length) return false;
                    local = new Vector3(nativeLocal.x, nativeLocal.y, benches[slot / 2]);
                    facing = Quaternion.Inverse(t.rotation) * native[slot % 2].rotation;
                    return true;
                }
            }
            int side = slot % 2, row = slot / 2;
            var refLocal = t.InverseTransformPoint(reference.position);
            // Without a bench measurement: ten rows spaced evenly from the vanilla row to the rear wall
            // (the spacing that fit eight of ten rows before), starting with the row right behind it.
            if (native.Length >= 2)
            {
                var nativeSide = t.InverseTransformPoint(native[side].position);
                float wall = (forward > 0 ? min.z : max.z) + forward * 0.45f;
                float pitch = (nativeSide.z - wall) / BusRows;
                if (row >= BusRows || Mathf.Abs(pitch) < 0.5f) return false;
                local = new Vector3(nativeSide.x, nativeSide.y, nativeSide.z - pitch * (row + 1));
                facing = Quaternion.Inverse(t.rotation) * native[side].rotation;
                return true;
            }
            float x = native.Length >= 2 ? t.InverseTransformPoint(native[side].position).x : (side == 0 ? -0.55f : 0.55f);
            // The door's stairwell runs across the aisle from the driver and past the first row: the
            // first two rows on that side stood on the steps (#177).
            // The bus's driver anchor sits on the centreline, so fall back to the vehicle's right-hand side
            // (the door side of a left-hand-drive bus) when it cannot tell the sides apart.
            float driverX = driver != null ? t.InverseTransformPoint(driver.position).x : 0f;
            float doorSide = Mathf.Abs(driverX) > 0.05f ? -Mathf.Sign(driverX) : forward;
            if (row <= 1 && Mathf.Sign(x) == doorSide) return false;
            float z = driverZ - forward * (1.3f + BusRowPitch * row);
            float rear = forward > 0 ? min.z : max.z;
            if ((z - rear) * forward < 0.5f) return false;
            local = new Vector3(x, refLocal.y, z);
            var at = t.TransformPoint(local);
            if (native.Any(p => Vector3.Distance(p.position, at) < 0.6f) || (driver != null && Vector3.Distance(driver.position, at) < 0.8f)) return false;
            facing = Quaternion.Inverse(t.rotation) * reference.rotation;
            return true;
        }

        private static bool LocalBounds(VehicleController vehicle, out Vector3 min, out Vector3 max)
        {
            var t = vehicle.transform;
            min = Vector3.positiveInfinity; max = Vector3.negativeInfinity;
            foreach (var collider in vehicle.GetComponentsInChildren<Collider>())
            {
                if (collider == null || collider.isTrigger || !collider.enabled) continue;
                Bounds b; Transform? frame;
                if (collider is BoxCollider box) { b = new Bounds(box.center, box.size); frame = box.transform; }
                else if (collider is MeshCollider mesh && mesh.sharedMesh != null) { b = mesh.sharedMesh.bounds; frame = mesh.transform; }
                else { b = collider.bounds; frame = null; }
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var point = t.InverseTransformPoint(frame != null ? frame.TransformPoint(corner) : corner);
                    min = Vector3.Min(min, point); max = Vector3.Max(max, point);
                }
            }
            return min.x <= max.x;
        }

        private static bool MeasureBed(VehicleController truck, Transform? driverSeat, out float floorY, out float wallX, out float rearZ, out float forward, out float frontZ)
        {
            floorY = 0; wallX = 0.6f; rearZ = 0; forward = 1; frontZ = 0;
            var t = truck.transform;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var collider in truck.GetComponentsInChildren<Collider>())
            {
                if (collider == null || collider.isTrigger || !collider.enabled) continue;
                // In the collider's own space where it has one: a world AABB swells as the truck turns,
                // which pushed the probe past the tailgate for any heading off the axes.
                Bounds b; Transform frame;
                if (collider is BoxCollider box) { b = new Bounds(box.center, box.size); frame = box.transform; }
                else if (collider is MeshCollider mesh && mesh.sharedMesh != null) { b = mesh.sharedMesh.bounds; frame = mesh.transform; }
                else { b = collider.bounds; frame = null!; }
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = t.InverseTransformPoint(frame != null ? frame.TransformPoint(corner) : corner);
                    min = Vector3.Min(min, local); max = Vector3.Max(max, local);
                }
            }
            if (min.x > max.x) return false;
            // The bed is at the end away from the driver.
            float driverZ = driverSeat != null ? t.InverseTransformPoint(driverSeat.position).z : max.z;
            forward = driverZ >= (min.z + max.z) * 0.5f ? 1f : -1f;
            float rear = forward > 0 ? min.z : max.z;
            float probeZ = rear + forward * 0.6f;
            if (!TruckHit(truck, t.TransformPoint(new Vector3(0, max.y + 0.5f, probeZ)), -t.up, max.y - min.y + 1f, out var floor)) return false;
            floorY = t.InverseTransformPoint(floor.point).y;
            float midY = floorY + 0.25f;
            // Side walls, from the middle outwards.
            float left = TruckHit(truck, t.TransformPoint(new Vector3(0, midY, probeZ)), -t.right, 1.4f, out var hitL) ? -t.InverseTransformPoint(hitL.point).x : max.x;
            float right = TruckHit(truck, t.TransformPoint(new Vector3(0, midY, probeZ)), t.right, 1.4f, out var hitR) ? t.InverseTransformPoint(hitR.point).x : max.x;
            wallX = Mathf.Clamp(Mathf.Min(left, right), 0.35f, 1.1f);
            // The tailgate, from inside the bed towards the back.
            var inside = t.TransformPoint(new Vector3(0, midY, rear + forward * 1.4f));
            rearZ = TruckHit(truck, inside, -t.forward * forward, 1.6f, out var gate) ? t.InverseTransformPoint(gate.point).z : rear + forward * 0.1f;
            // The front of the bed: the cab's back wall, or an installed tank.
            frontZ = TruckHit(truck, inside, t.forward * forward, 2.4f, out var front) ? t.InverseTransformPoint(front.point).z : rearZ + forward * 2.0f;
            return true;
        }

        // A rider sits behind the driver, a hand's width off the middle: the driver seat's frame moved
        // back along the vehicle and raised by how much higher the rack is than the saddle under the driver.
        internal const float RiderBack = 0.5f, RiderSide = 0.24f;
        private static bool MeasureRack(VehicleController atv, Transform driverSeat, int slot, out Vector3 local)
        {
            var t = atv.transform;
            var driver = t.InverseTransformPoint(driverSeat.position);
            var back = t.InverseTransformDirection(-driverSeat.forward); back.y = 0; back.Normalize();
            var right = Vector3.Cross(Vector3.up, back).normalized;
            local = driver + back * RiderBack + right * (slot == 0 ? RiderSide : -RiderSide);
            const float Above = 1.2f;
            if (!TruckHit(atv, t.TransformPoint(driver + Vector3.up * Above), -t.up, Above + 1f, out var saddle)) return true;
            if (!TruckHit(atv, t.TransformPoint(local + Vector3.up * Above), -t.up, Above + 1f, out var rack)) return true;
            local.y += Mathf.Clamp(t.InverseTransformPoint(rack.point).y - t.InverseTransformPoint(saddle.point).y, -0.1f, 0.35f);
            return true;
        }

        private static bool TruckHit(VehicleController truck, Vector3 from, Vector3 direction, float distance, out RaycastHit best)
        {
            best = default;
            float bestDistance = float.PositiveInfinity;
            foreach (var hit in Physics.RaycastAll(from, direction, distance, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider != null && hit.collider.transform.IsChildOf(truck.transform) && hit.distance < bestDistance) { best = hit; bestDistance = hit.distance; }
            return bestDistance < float.PositiveInfinity;
        }

        // The walker's seat location goes where it puts the camera at the seated eye height.
        private void PlaceSeatLocation(Seat seat)
        {
            if (seat.Kind == SeatKind.Stool) return; // native stools already carry a tuned SeatLocation
            var anchor = seat.Anchor;
            float eye = seat.Kind == SeatKind.Ground ? GroundEyeHeight : CabEyeHeight;
            seat.SeatLocation.position = anchor.position + anchor.up * (eye - standingCameraHeight);
            seat.SeatLocation.rotation = anchor.rotation;
        }

        // --- Interaction -----------------------------------------------------------------------

        // Furniture_Chair.MouseDown on one of these seats. Returns whether native handling proceeds.
        internal bool Click(Furniture_Chair chair)
        {
            if (!byChair.TryGetValue(chair, out var seat)) return true;
            if (!Ready || session == null) { LastRefusal = "not ready"; return false; }
            if (SeatAdmission?.IsPendingLocal(this, seat.Id) == true) return false; // a repeat click keeps waiting
            if (!DoorAllowsEntry(seat)) { MonMPPlugin.Instance?.ReportItemUseUnavailable("Open the door first."); return false; }
            if (seat.State.Owner == session.LocalPeerId)
            {
                StandUp(seat);
                return false;
            }
            if (!SeatEnabled(seat)) { LastRefusal = "disabled by host"; return false; }
            if (gameplay.Get_PlayerState != PlayerState.Walking) { LastRefusal = "local " + gameplay.Get_PlayerState; return false; }
            if (TransfersBlocked || !TowingAllowsEntry(seat)) return false;
            if (!DoorAllowsEntry(seat)) { MonMPPlugin.Instance?.ReportItemUseUnavailable("Open the door first."); return false; }
            if (gameplay.Player.IsHoldingAnObject) { MonMPPlugin.Instance?.ReportItemUseUnavailable("Put down what you're holding first."); return false; }
            if (seat.State.Occupied || NpcNear(seat)) { LastRefusal = seat.State.Occupied ? "local occupied" : "local npc"; MonMPPlugin.Instance?.ReportItemUseUnavailable("Someone is already sitting there."); return false; }
            Place(seat);
            if (!seat.Placed) { MonMPPlugin.Instance?.ReportItemUseUnavailable("There's no room to sit there."); return false; }
            if (SeatAdmission?.TryBeginLocal(this, seat.Id, seat.State.Generation, session.LocalPeerId) == false)
            { LastRefusal = "another seat request is pending"; return false; }
            channel!.Request(seat.Id, new VehiclePassengerSeatCommand(VehiclePassengerSeatAction.Enter, seat.State.Generation).Encode(), true);
            return false;
        }

        internal bool Handles(Furniture_Chair chair) => byChair.ContainsKey(chair);

        // The chair whose ordinary click stands the local player up from the added seat they own
        // and sit in, or null. Used by the local VR bridge for controller exit: these seats have
        // no native vehicle seat position, and the caller re-clicks this chair natively so
        // Click's door check and StandUp/RequestExit run as for a mouse click.
        internal Furniture_Chair? LocalExitChair()
        {
            if (!Ready || session == null || SeatOwnedBy(session.LocalPeerId) is not Seat seat || !IsLocalSeated(seat)) return null;
            foreach (var chair in seat.Chairs) if (chair != null) return chair;
            return null;
        }

        private static bool DoorAllowsEntry(Seat seat) => seat.Truck == null ||
            seat.Kind != SeatKind.Cab && seat.Kind != SeatKind.Bench ||
            VehicleSeatDoors.Allows(seat.Truck, seat.Kind == SeatKind.Cab && seat.Slot == 0);

        internal bool CanInteract(Furniture_Chair chair) => !byChair.TryGetValue(chair, out var seat) || SeatEnabled(seat) && DoorAllowsEntry(seat);
        internal bool AllowChairTransition() => applying || session == null ||
            SeatOwnedBy(session.LocalPeerId) is not Seat seat || DoorAllowsEntry(seat);

        private bool TryLeaveNative(Seat seat)
        {
            if (IsLocalSeated(seat))
            {
                applying = true;
                try { gameplay.EnterExitChair(seat.SeatLocation); }
                finally { applying = false; }
            }
            return !IsLocalSeated(seat);
        }

        private void StandUp(Seat seat)
        {
            // Native wine pouring/drinking can refuse this transition. Retain the lease
            // until the walker actually stands, so a later ordinary click can retry.
            if (!TryLeaveNative(seat)) return;
            RequestExit(seat);
        }

        private void RequestExit(Seat seat)
        {
            if (session == null || !Ready || seat.State.Owner != session.LocalPeerId ||
                seat.ExitPending && Time.unscaledTime < seat.NextExitRetry) return;
            seat.ExitPending = true;
            seat.NextExitRetry = Time.unscaledTime + 0.5f;
            channel!.Request(seat.Id, new VehiclePassengerSeatCommand(VehiclePassengerSeatAction.Exit, seat.State.Generation).Encode(), true);
        }

        internal void FlushLocalOwnership()
        {
            SeatAdmission?.CancelLocal(this);
            if (!Ready || session == null) return;
            foreach (var seat in seats.Values.Where(seat => seat.State.Owner == session.LocalPeerId).ToArray()) StandUp(seat);
        }

        internal void RevokeOwnership(uint peer)
        {
            if (!host) return;
            foreach (var seat in seats.Values.Where(seat => seat.State.Owner == peer).ToArray())
            {
                if (!registry.TryRelease(seat.Key, peer, seat.State.Generation, out var released)) continue;
                Apply(seat, new VehiclePassengerSeatState(released.Peer, released.Generation));
                Publish(seat);
            }
        }

        internal void Update()
        {
            if (!Ready || session == null || gameplay == null || gameplay.Player == null) return;
            RevokeDisabledSeats();
            // NPCs can appear after a player sits (for example when the bar opens).
            // The host releases the same ordinary ownership record for real and fake guests.
            if (host)
            {
                if (occupiedStoolsDirty)
                {
                    occupiedStoolSnapshot = occupiedStools.ToArray();
                    occupiedStoolsDirty = false;
                }
                foreach (var seat in occupiedStoolSnapshot)
                {
                    if (!seat.State.Occupied || !NpcNear(seat)) continue;
                    if (!registry.TryRelease(seat.Key, seat.State.Owner, seat.State.Generation, out var released)) continue;
                    Apply(seat, new VehiclePassengerSeatState(released.Peer, released.Generation));
                    Publish(seat);
                }
            }
            RetryNativeExits();
            if (hovered != null && Time.frameCount - hoverFrame > 2) ShowHover(null);
            // The standing camera height, for putting a seated camera at the right height.
            if (gameplay.Get_PlayerState == PlayerState.Walking && gameplay.PlayerWalking != null && !gameplay.PlayerWalking.Get_IsCrouch &&
                gameplay.Player.mainCamera != null)
            {
                float height = Vector3.Dot(gameplay.Player.mainCamera.transform.position - gameplay.PlayerWalking.transform.position, gameplay.PlayerWalking.transform.up);
                if (height > 1f && height < 2.2f) standingCameraHeight = height;
            }
            var mine = SeatOwnedBy(session.LocalPeerId);
            if (mine == null) return;
            // Left the seat some other way (passing out, sleeping, a load).
            // Voluntary exit is handled only by clicking the occupied seat.
            if (gameplay.Get_PlayerState != PlayerState.InFurnitureChair && Time.frameCount > enteredFrame + 2) RequestExit(mine);
        }

        private void RetryNativeExits()
        {
            // Forced revocation can arrive during a refused native drink exit. Preserve a
            // retry obligation even though this peer no longer owns that seat.
            foreach (var seat in seats.Values)
                if (seat.NativeExitPending && TryLeaveNative(seat)) seat.NativeExitPending = false;
        }

        private bool NpcNear(Seat seat)
        {
            var position = seat.Placed ? seat.Anchor.position : seat.SeatLocation.position;
            foreach (var actor in NpcActors())
                if (actor != null && actor.isActiveAndEnabled && actor.gameObject.activeInHierarchy &&
                    Vector3.ProjectOnPlane(actor.transform.position - position, Vector3.up).sqrMagnitude < 0.5f * 0.5f &&
                    Mathf.Abs(actor.transform.position.y - position.y) < 1.6f) return true;
            return false;
        }

        private static Actor[] actors = Array.Empty<Actor>();
        private static float nextActorScan;
        internal static Actor[] NpcActors()
        {
            if (Time.unscaledTime >= nextActorScan) { actors = UnityEngine.Object.FindObjectsOfType<Actor>(); nextActorScan = Time.unscaledTime + 5f; }
            return actors;
        }

        // --- Authority -------------------------------------------------------------------------

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            if (host) RevokeOwnership(peer);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == VehiclePassengerSeatState.Adapter) ObserveSeatRecord(record);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == VehiclePassengerSeatState.Adapter) ObserveSeatRecord(record);
        }

        [MonMP.Core.World.WorldCommandAdapters(VehiclePassengerSeatState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !seats.TryGetValue(id, out var seat)) return;
            var command = VehiclePassengerSeatCommand.Decode(payload);
            if (command.Action == VehiclePassengerSeatAction.Exit)
            {
                // Interaction gates run before local exit. Lease release must also accept
                // forced save/death cleanup, whose next walking pose can arrive afterward.
                if (command.ExpectedGeneration != seat.State.Generation ||
                    !registry.TryRelease(seat.Key, peer, command.ExpectedGeneration, out var released)) return;
                Apply(seat, new VehiclePassengerSeatState(released.Peer, released.Generation));
                Publish(seat);
                return;
            }
            if (!SeatEnabled(seat)) { LastRefusal = "disabled by host"; Refuse(peer, seat, command, FurnitureSeatRefusal.NotUsable); return; }
            bool local = peer == 0;
            Vector3 location;
            if (local)
            {
                if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject)
                {
                    Refuse(peer, seat, command, gameplay.Player.IsHoldingAnObject ? FurnitureSeatRefusal.Holding : FurnitureSeatRefusal.NotWalking);
                    return;
                }
                location = gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform.position : gameplay.Player.transform.position;
            }
            else
            {
                if (!presence.TryGetValue(peer, out var current) || Time.unscaledTime - current.Time > 2f ||
                    current.Pose.State != (byte)PlayerState.Walking || MonMPPlugin.Instance?.Items?.PeerHolding(peer) != false)
                {
                    LastRefusal = seat.Kind + ": not walking or holding";
                    Refuse(peer, seat, command, MonMPPlugin.Instance?.Items?.PeerHolding(peer) == true ? FurnitureSeatRefusal.Holding : FurnitureSeatRefusal.NotWalking);
                    return;
                }
                location = new Vector3(current.Pose.X, current.Pose.Y, current.Pose.Z);
            }
            Place(seat);
            var (reason, refusal) = TransfersBlocked ? (FurnitureSeatRefusal.TransfersBlocked, "transfers blocked") :
                !TowingAllowsEntry(seat) ? (FurnitureSeatRefusal.NotUsable, "queued for towing") :
                !DoorAllowsEntry(seat) ? (FurnitureSeatRefusal.NotUsable, "door closed") :
                !seat.Placed ? (FurnitureSeatRefusal.MissingSeat, "unplaced") :
                seat.State.Occupied ? (FurnitureSeatRefusal.Occupied, "occupied") :
                command.ExpectedGeneration != seat.State.Generation ? (FurnitureSeatRefusal.Stale, "stale generation") :
                SeatAdmission?.IsSeated(peer) == true || vehicles.IsDriverOwner(peer) || vehicles.IsPassengerOwner(peer) || bus.IsOwner(peer) || MonMPPlugin.Instance?.World?.BoatMotion.IsOwner(peer) == true ? (FurnitureSeatRefusal.AlreadySeated, "already seated") :
                NpcNear(seat) ? (FurnitureSeatRefusal.Npc, "npc") :
                Vector3.Distance(location, seat.Anchor.position) > 4f ? (FurnitureSeatRefusal.TooFar, "too far (" + Vector3.Distance(location, seat.Anchor.position).ToString("F1") + " m: " + location.ToString("F1") + " vs " + seat.Anchor.position.ToString("F1") + ")") :
                (FurnitureSeatRefusal.None, "");
            if (refusal.Length != 0) { LastRefusal = seat.Kind + ": " + refusal; Refuse(peer, seat, command, reason); return; }
            if (!registry.TryClaim(seat.Key, peer, command.ExpectedGeneration, out var claimed)) { Refuse(peer, seat, command, FurnitureSeatRefusal.Occupied); return; }
            Apply(seat, new VehiclePassengerSeatState(claimed.Peer, claimed.Generation));
            Publish(seat);
        }

        private void Publish(Seat seat) => channel!.Publish(seat.Id, seat.State.Encode());
        // A slow grant still seats a player who is free and standing within 4 m of that seat.
        private bool StillWaitingAt(Transform? seat) => seat != null && gameplay != null && gameplay.PlayerWalking != null &&
            gameplay.Get_PlayerState == PlayerState.Walking && !gameplay.Player.IsHoldingAnObject &&
            (gameplay.PlayerWalking.transform.position - seat.position).sqrMagnitude <= 16f;

        private void Refuse(uint peer, Seat seat, VehiclePassengerSeatCommand command, FurnitureSeatRefusal reason) =>
            SeatRefusals.Send(channel, peer, seat.Id, command.ExpectedGeneration, reason);

        private void Apply(Seat seat, VehiclePassengerSeatState state)
        {
            if (!host && state.Generation != seat.State.Generation && !Wire.IsNewer(state.Generation, seat.State.Generation)) return;
            bool allowEntry = SeatAdmission?.AllowsLocalEntry(this, seat.Id, state.Generation, StillWaitingAt(seat.SeatLocation)) != false;
            SeatAdmission?.Observe(this, seat.Id, state.Generation);
            bool changed = seat.State.Owner != state.Owner || seat.State.Generation != state.Generation;
            if (seat.Kind == SeatKind.Stool && seat.State.Occupied != state.Occupied)
            {
                if (state.Occupied) occupiedStools.Add(seat); else occupiedStools.Remove(seat);
                occupiedStoolsDirty = true;
            }
            seat.State = state;
            if (changed) seat.ExitPending = false;
            if (session == null) return;
            bool seatedHere = IsLocalSeated(seat);
            if (state.Owner != session.LocalPeerId)
            {
                if (seatedHere) seat.NativeExitPending = !TryLeaveNative(seat);
                return;
            }
            seat.NativeExitPending = false;
            if (seatedHere || seat.ExitPending) return;
            if (!allowEntry || !SeatEnabled(seat) || TransfersBlocked || !TowingAllowsEntry(seat) || gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject) { RequestExit(seat); return; }
            Place(seat);
            PlaceSeatLocation(seat);
            applying = true;
            try
            {
                gameplay.EnterExitChair(seat.SeatLocation);
                if (seat.Truck != null && gameplay.PlayerWalking != null && gameplay.Get_PlayerState == PlayerState.InFurnitureChair)
                    ExtraSeatFirstPersonView.Enter(gameplay.PlayerWalking, seat.SeatLocation);
                enteredFrame = Time.frameCount;
            }
            finally { applying = false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            SeatAdmission?.CancelLocal(this);
            if (session != null && gameplay != null && gameplay.Get_PlayerState == PlayerState.InFurnitureChair)
                foreach (var seat in seats.Values)
                    if (gameplay.PlayerWalking != null && gameplay.PlayerWalking.transform.parent == seat.SeatLocation)
                    {
                        applying = true;
                        try { gameplay.EnterExitChair(seat.SeatLocation); }
                        catch { /* the scene may be tearing down */ }
                        finally { applying = false; }
                    }
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; session.SettingsChanged -= SeatSettingsChanged; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            var walker = gameplay != null && gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform : null;
            ShowHover(null);
            foreach (var glow in surfaceGlows) glow.Dispose();
            surfaceGlows.Clear();
            foreach (var seat in seats.Values)
            {
                // Never take the walker down with a seat it is still parented to.
                if (walker != null && seat.Created != null && walker.IsChildOf(seat.Created.transform)) walker.SetParent(null, true);
                if (seat.Kind == SeatKind.Stool) foreach (var chair in seat.Chairs) if (chair != null) UnityEngine.Object.Destroy(chair);
                if (seat.Created != null) UnityEngine.Object.Destroy(seat.Created);
            }
            seats.Clear(); byChair.Clear(); seatsByTruck.Clear(); seatGroups.Clear();
            occupiedStools.Clear(); occupiedStoolSnapshot = Array.Empty<Seat>(); occupiedStoolsDirty = false;
            purchasedSeatVehicles.Clear(); retainedPurchasedSeatStates.Clear();
            actors = Array.Empty<Actor>(); nextActorScan = 0;
            channel = null; session = null;
        }
    }
}
