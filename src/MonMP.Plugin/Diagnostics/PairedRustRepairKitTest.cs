using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises the guest's real equipped Tool_RustRepairKit.MouseDown entry point.
    // A successful native repair consumes the first leased kit; a second kit is
    // used to prove stale leases and already-repaired panels leave it untouched.
    internal sealed class PairedRustRepairKitTest
    {
        private static readonly System.Reflection.FieldInfo NativeTargetField =
            AccessTools.Field(typeof(Tool_RustRepairKit), "vehicle_PaintablePanel") ??
            throw new InvalidOperationException("Native rust repair target field is unavailable.");
        private static readonly System.Reflection.FieldInfo NativeCameraField =
            AccessTools.Field(typeof(Tool_RustRepairKit), "mainCamera") ??
            throw new InvalidOperationException("Native rust repair camera field is unavailable.");

        private sealed class Target
        {
            public ulong Id;
            public Vehicle_PaintablePanel Panel = null!;
            public Collider Collider = null!;
            public PaintPanelState Baseline = null!;
            public bool Associated;
            public PaintPanelState Addressed(PaintPanelState state) => Associated ? state.Associated! : state;
        }

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly SharedPaintPanels panels;
        private readonly Target target;
        private readonly bool truckGrilleOnly;
        private readonly Vector3 startingPlayerPosition;
        private readonly Quaternion startingCameraRotation;
        private readonly int initialKitCount;
        private readonly float started = Time.unscaledTime;
        private PaintPanelState? repairedState;
        private Tool_RustRepairKit? kit;
        private ulong firstKitId, noChangeKitId;
        private int hostAcceptedBefore, hostRejectedBefore;
        private readonly int localCueBaseline, remoteCueBaseline;
        private int grabAttempts;
        private bool requestedEquip;
        private float equipDeadline;
        private float next;
        private int aimAttempts;
        private Vector3 lastAimSide;
        private float lastAimDistance;
        private int phase;
        private string lastNativeRay = "not-sampled", lastSurfaceAim = "not-sampled";
        private string lastEquipment = "not-sampled";
        private bool diagnosticsPreserved;

        internal string Status => "rust repair phase=" + phase + "; panel=" + target.Id.ToString("X16") +
            "; firstKit=" + firstKitId + "; noChangeKit=" + noChangeKitId +
            "; pending=" + panels.TestPending + "; accepted=" + panels.TestAccepted +
            "; rejected=" + panels.TestRejected + "; kitEquipped=" + (kit != null && kit.isEquiped) +
            "; aimAttempts=" + aimAttempts + "; lastAim=" + lastAimSide.ToString("F1") + "@" + lastAimDistance.ToString("F1") +
            "; ray=" + PreservedRayDiagnostics() + "; surfaceAim=" + lastSurfaceAim +
            "; hostGates=" + panels.TestLastRepairHostDiagnostic + "; equipment=" + EquipmentDiagnostics();

        private string EquipmentDiagnostics()
        {
            if (diagnosticsPreserved) return lastEquipment;
            string second = "not-selected";
            if (noChangeKitId != 0)
            {
                var state = items.TestState(noChangeKitId);
                second = NativeItemInfo(items.TestItem(noChangeKitId)) + "/exists=" + state.Exists +
                    "/leaseOwner=" + state.Lease.Owner + "/generation=" + state.Lease.Generation +
                    "/pickup=" + items.TestPickupStatus(noChangeKitId);
            }
            lastEquipment = "holding=" + NativeItemInfo(gameplay.Player.Get_HoldingItem) +
                "; active=" + NativeItemInfo(gameplay.Player.Get_ActiveTool) + "; fixtureKit=" + NativeItemInfo(kit) +
                "; secondKit=" + second + "; requestedEquip=" + requestedEquip + "; grabAttempts=" + grabAttempts;
            if (lastEquipment.Length > 1800) lastEquipment = lastEquipment.Substring(0, 1800);
            return lastEquipment;
        }

        private static string NativeItemInfo(InteractableItems? item)
        {
            if (item is null) return "managedNull=True/UnityNull=True";
            string nativeType = item.GetType().Name;
            if (item == null) return "type=" + nativeType + "/managedNull=False/UnityNull=True";
            return "type=" + nativeType + "/name=" + ShortName(item.name) +
                "/managedNull=False/UnityNull=False/equipped=" + (item is InteractableTools tool && tool.isEquiped);
        }

        private string PreservedRayDiagnostics()
        {
            if (!diagnosticsPreserved && kit != null)
            {
                lastNativeRay = RayDiagnostics();
                lastSurfaceAim = NativeRepairFixtureAim.LastDiagnostics;
            }
            return lastNativeRay;
        }

        internal PairedRustRepairKitTest(MonMPPlugin plugin, Gameplay gameplay, bool associatedAccessory = false, bool truckGrilleOnly = false)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            this.truckGrilleOnly = truckGrilleOnly;
            items = plugin.World!.Items;
            panels = plugin.World.PaintPanels;
            localCueBaseline = RepairToolPresentation.TestLocal(RepairToolPresentation.Rust);
            remoteCueBaseline = RepairToolPresentation.TestRemote(RepairToolPresentation.Rust);
            panels.TestRepairDiagnosticsActive = plugin.TestOptions.Enabled;
            if (associatedAccessory && truckGrilleOnly)
                throw new InvalidOperationException("Rust fixture cannot combine accessory and exact truck grille modes.");
            target = FindRustedTarget(panels, associatedAccessory, truckGrilleOnly);
            startingPlayerPosition = gameplay.PlayerWalking.transform.position;
            startingCameraRotation = gameplay.Player.mainCamera.transform.rotation;
            initialKitCount = items.LiveItems.Count(entry => entry.Item is Tool_RustRepairKit);
        }

        internal bool Tick()
        {
            if (Time.unscaledTime - started > 180)
                throw new InvalidOperationException("Rust repair kit test timed out: " + Status);
            if (Time.unscaledTime < next) return false;
            // EndPairedTestSession verifies that the world/session are detached.
            // The terminal guest phase must inspect restoration after that teardown.
            if (!plugin.TestOptions.IsHost && phase == 7) return Guest();
            if (plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                Require(target.Addressed(target.Baseline).IsRusted, "Host rusted panel baseline changed before the repair fixture started.");
                hostAcceptedBefore = panels.TestAccepted;
                hostRejectedBefore = panels.TestRejected;
                firstKitId = items.TestSpawnRustRepairKit(KitPosition());
                phase = 1;
            }
            else if (phase == 1 && panels.TestAccepted == hostAcceptedBefore + 1 &&
                !target.Addressed(panels.TestState(target.Id)).IsRusted && !items.TestState(firstKitId).Exists)
            {
                repairedState = panels.TestNativeState(target.Id);
                if (target.Associated) RepairAccessoryFixture.AssertOnlyAccessoryChanged(target.Baseline, repairedState);
                Require(Same(repairedState!, panels.TestState(target.Id)), "Host native panel did not match the published repair state.");
                noChangeKitId = items.TestSpawnRustRepairKit(KitPosition());
                Require(noChangeKitId > firstKitId, "Host did not allocate a distinct second native repair kit.");
                phase = 2;
            }
            else if (phase == 2 && plugin.Session!.Peers.Count == 1 && plugin.RemoteAvatarCount == 0)
            {
                Require(panels.TestAccepted == hostAcceptedBefore + 1,
                    "A no-change or stale-generation guest request was counted as another applied repair.");
                Require(panels.TestRejected == hostRejectedBefore + 1,
                    "The host did not reject exactly one stale-generation repair-kit request.");
                Require(RepairToolPresentation.TestRemote(RepairToolPresentation.Rust) == remoteCueBaseline + 1 &&
                    RepairToolPresentation.TestLocal(RepairToolPresentation.Rust) == localCueBaseline,
                    "Host repair cue was missing, duplicated, local, or emitted for stale/no-change requests.");
                Require(!items.TestState(firstKitId).Exists, "Successfully used host repair kit did not publish its tombstone.");
                Require(items.TestState(noChangeKitId).Exists && items.TestItem(noChangeKitId) is Tool_RustRepairKit,
                    "No-change repair did not preserve the second host repair kit.");
                Require(repairedState != null && Same(panels.TestNativeState(target.Id), repairedState) &&
                    Same(panels.TestState(target.Id), repairedState), "Guest cleanup changed the repaired host panel before rollback.");
                panels.TestRestoreHostPanel(target.Id, target.Baseline);
                items.TestDestroy(noChangeKitId);
                phase = 3;
            }
            else if (phase == 3 && !items.TestState(noChangeKitId).Exists)
            {
                Require(Same(panels.TestNativeState(target.Id), target.Baseline),
                    "Host fixture did not restore the rusted panel's exact native baseline.");
                Require(items.LiveItems.Count(entry => entry.Item is Tool_RustRepairKit) == initialKitCount,
                    "Host fixture left a spawned rust repair kit behind.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                Require(Same(panels.TestNativeState(target.Id), target.Baseline),
                    "Guest did not start from the native host rusted-panel snapshot.");
                Require(plugin.World!.Events?.TestRepairHistorySilent() == true, "Initial repair presentation history replayed a cue.");
                StageCamera(1.5f, PreferredSide());
                phase = 1;
                next = Time.unscaledTime + 0.7f;
                return false;
            }

            if (phase == 1)
            {
                if (!TryEquipNewestKit(afterId: 0, out firstKitId)) return false;
                kit = items.TestItem(firstKitId) as Tool_RustRepairKit ??
                    throw new InvalidOperationException("Guest leased item is not a native rust repair kit.");
                if (!AimAtTarget()) return false;
                kit.MouseDown();
                Require(panels.TestPending == 1, "Native MouseDown did not send a host-authoritative rust repair request.");
                phase = 2;
                return false;
            }

            if (phase == 2 && panels.TestPending == 0 && !Same(panels.TestState(target.Id), target.Baseline) &&
                !items.TestState(firstKitId).Exists)
            {
                repairedState = panels.TestState(target.Id);
                if (target.Associated) RepairAccessoryFixture.AssertOnlyAccessoryChanged(target.Baseline, repairedState);
                Require(!target.Addressed(repairedState).IsRusted && Same(panels.TestNativeState(target.Id), repairedState),
                    "Successful repair did not update the guest's host-authoritative native panel state.");
                Require(items.TestItem(firstKitId) == null, "Consumed guest kit replica remained active after its tombstone.");
                Require(RepairToolPresentation.TestLocal(RepairToolPresentation.Rust) == localCueBaseline + 1 &&
                    RepairToolPresentation.TestRemote(RepairToolPresentation.Rust) == remoteCueBaseline,
                    "Accepted rust repair did not play exactly one actor cue without observer echo.");
                requestedEquip = false;
                grabAttempts = 0;
                equipDeadline = 0;
                aimAttempts = 0;
                phase = 3;
                next = Time.unscaledTime + 0.4f;
                return false;
            }

            if (phase == 3)
            {
                var secondKit = noChangeKitId != 0 ? items.TestItem(noChangeKitId) : null;
                if ((gameplay.Player.Get_HoldingItem != null && gameplay.Player.Get_HoldingItem != secondKit) ||
                    (gameplay.Player.Get_ActiveTool != null && gameplay.Player.Get_ActiveTool != secondKit)) return false;
                if (!TryEquipNewestKit(afterId: firstKitId, out noChangeKitId)) return false;
                kit = items.TestItem(noChangeKitId) as Tool_RustRepairKit ??
                    throw new InvalidOperationException("Second guest item is not a native rust repair kit.");
                if (!AimAtTarget()) return false;
                SendStaleLeaseProbe();
                phase = 4;
                next = Time.unscaledTime + 0.6f;
                return false;
            }

            if (phase == 4)
            {
                Require(Same(panels.TestState(target.Id), repairedState!) &&
                    Same(panels.TestNativeState(target.Id), repairedState!) &&
                    items.TestState(noChangeKitId).Exists && items.TestItem(noChangeKitId) == kit,
                    "Stale-generation request changed the panel or consumed the leased kit.");
                Require(AimAtTarget(), "Native rust repair kit lost the target before the no-change click.");
                kit!.MouseDown();
                Require(panels.TestPending == 1, "Already-repaired native MouseDown did not reach the host no-change path.");
                phase = 5;
                return false;
            }

            if (phase == 5 && panels.TestPending == 0)
            {
                Require(RepairToolPresentation.TestLocal(RepairToolPresentation.Rust) == localCueBaseline + 1 &&
                    RepairToolPresentation.TestRemote(RepairToolPresentation.Rust) == remoteCueBaseline,
                    "Stale/no-change rust request replayed a repair cue.");
                Require(plugin.World!.Events?.TestRepairHistorySilent() == true, "Repair snapshot or duplicate log replayed a cue.");
                Require(Same(panels.TestState(target.Id), repairedState!) &&
                    Same(panels.TestNativeState(target.Id), repairedState!),
                    "Clicking an already-repaired panel changed guest state.");
                Require(items.TestState(noChangeKitId).Exists && items.TestItem(noChangeKitId) == kit &&
                    items.TestState(noChangeKitId).Lease.Owner == plugin.Session!.LocalPeerId && kit!.isEquiped,
                    "No-change repair consumed, dropped, or unequipped the guest's leased kit.");
                items.TestPutAwayTool();
                phase = 6;
                next = Time.unscaledTime + 0.2f;
                return false;
            }

            if (phase == 6 && items.TestState(noChangeKitId).Lease.Owner == ItemLease.Unowned &&
                items.TestState(noChangeKitId).Exists)
            {
                Move(startingPlayerPosition);
                NativeRepairFixtureAim.SetLook(gameplay, gameplay.Player.mainCamera, startingCameraRotation * Vector3.forward);
                plugin.EndPairedTestSession();
                phase = 7;
                return false;
            }

            if (phase == 7 && plugin.World == null)
            {
                Require(panels.TestRestored, "Guest rust repair fixture did not restore every touched panel exactly on disconnect.");
                Require(items.TestRestored, "Guest rust repair fixture did not remove its spawned item replicas on disconnect.");
                return true;
            }
            return false;
        }

        private bool TryEquipNewestKit(ulong afterId, out ulong id)
        {
            id = noChangeKitId > afterId ? noChangeKitId : 0;
            if (id == 0)
                id = items.LiveItems.Where(entry => entry.Id > afterId && entry.Item is Tool_RustRepairKit &&
                    Math.Abs(entry.Item.transform.localScale.x - 0.731f) < 0.001f)
                    .Select(entry => entry.Id).DefaultIfEmpty().Max();
            if (id == 0) return false;
            if (items.TestState(id).Lease.Owner != plugin.Session!.LocalPeerId)
            {
                if (!items.TestPickupPending(id))
                {
                    if (grabAttempts >= 6) throw new InvalidOperationException("Guest could not lease the rust repair kit: " + items.TestPickupStatus(id));
                    items.TestGrab(id);
                    grabAttempts++;
                    next = Time.unscaledTime + 0.8f;
                }
                return false;
            }

            var native = items.TestItem(id) as Tool_RustRepairKit;
            if (native == null) return false;
            if (native.isEquiped && gameplay.Player.Get_ActiveTool == native) { kit = native; return true; }
            if (gameplay.Player.Get_HoldingItem == native && !requestedEquip)
            {
                items.TestUse(id);
                requestedEquip = true;
                equipDeadline = Time.unscaledTime + 3f;
                next = Time.unscaledTime + 0.2f;
            }
            else if (requestedEquip && Time.unscaledTime >= equipDeadline)
                throw new InvalidOperationException("Guest native Use did not equip the leased rust repair kit: " + items.TestPickupStatus(id));
            return false;
        }

        private bool AimAtTarget()
        {
            if (NativeTargetField.GetValue(kit) as Vehicle_PaintablePanel == target.Panel)
            {
                aimAttempts = 0;
                return true;
            }
            if (truckGrilleOnly || target.Associated)
            {
                if (aimAttempts >= 3 || !NativeRepairFixtureAim.TryStage(gameplay, CameraForStaging(), target.Collider, AimSides(), NativeRepairRayMask(), out lastAimSide, out lastAimDistance))
                    throw new InvalidOperationException("Exact repair target has no unobstructed native-mask surface ray: " + Status);
                aimAttempts++;
                next = Time.unscaledTime + 0.3f;
                return false;
            }
            Vector3[] sides = AimSides();
            if (aimAttempts >= sides.Length * AimDistances.Length)
                throw new InvalidOperationException("Native CheckActivePannel did not resolve the selected panel after bounded camera staging: " + Status);
            int sideIndex = aimAttempts % sides.Length;
            int distanceIndex = aimAttempts / sides.Length;
            lastAimSide = sides[sideIndex];
            lastAimDistance = AimDistances[distanceIndex];
            StageCamera(lastAimDistance, lastAimSide);
            aimAttempts++;
            next = Time.unscaledTime + 0.2f;
            return false;
        }

        private void SendStaleLeaseProbe()
        {
            var lease = items.TestState(noChangeKitId).Lease;
            uint staleGeneration = lease.Generation > 1 ? lease.Generation - 1 : lease.Generation + 1;
            Camera camera = CameraForStaging() ?? throw new InvalidOperationException("Native kit camera is unavailable for stale-lease probe.");
            Vector3 origin = camera.transform.position;
            Vector3 angles = camera.transform.eulerAngles;
            var command = new RustRepairToolCommand(Guid.NewGuid(), noChangeKitId, staleGeneration,
                origin.x, origin.y, origin.z, angles.y, Mathf.DeltaAngle(0, angles.x));
            plugin.World!.Channel.Request(target.Id, command.Encode(), useLease: true);
        }

        private static Target FindRustedTarget(SharedPaintPanels shared, bool associatedAccessory, bool truckGrilleOnly)
        {
            if (truckGrilleOnly)
            {
                int truckMask = NativeRepairRayMask();
                var grille = shared.TestPanels.Where(id => !shared.TestDynamicPanels.Contains(id))
                    .Select(id => (Id: id, Panel: shared.TestNative(id)))
                    .Where(entry => entry.Panel != null && entry.Panel.gameObject.activeInHierarchy &&
                        entry.Panel.name == "Truck_FrontGrill" && entry.Panel.Get_PanelType == (int)Vehicle_PanelType.FrontGrill &&
                        entry.Panel.GetComponentInParent<Vehicle_PartsManager>(true)?.Get_Vehicle?.vehicle_type == Vehicle_Type.OlTruck &&
                        entry.Panel.PannelAssociatedToSave == null && entry.Panel.Get_IsRusted && HasMaterial(entry.Panel))
                    .Select(entry => (entry.Id, entry.Panel, Collider: entry.Panel.GetComponent<Collider>()))
                    .Where(entry => entry.Collider != null && entry.Collider.enabled && entry.Collider.gameObject.activeInHierarchy &&
                        (truckMask & (1 << entry.Collider.gameObject.layer)) != 0).ToArray();
                if (grille.Length != 1)
                    throw new InvalidOperationException("Exact truck grille rust fixture requires one native rusted OlTruck Truck_FrontGrill direct collider; found " + grille.Length + ". No generic panel fallback.");
                return new Target { Id = grille[0].Id, Panel = grille[0].Panel, Collider = grille[0].Collider!,
                    Baseline = shared.TestNativeState(grille[0].Id) };
            }
            if (associatedAccessory)
            {
                var accessory = RepairAccessoryFixture.Find(shared, panel => panel.Get_IsRusted && HasMaterial(panel));
                return new Target { Id = accessory.Id, Panel = accessory.Panel, Collider = accessory.Collider,
                    Baseline = shared.TestNativeState(accessory.Id), Associated = true };
            }
            int mask = NativeRepairRayMask();
            var candidates = shared.TestPanels.Select(id => (Id: id, Panel: shared.TestNative(id)))
                .Where(entry => !shared.TestDynamicPanels.Contains(entry.Id) && entry.Panel != null &&
                    entry.Panel.gameObject.activeInHierarchy && entry.Panel.PannelAssociatedToSave == null &&
                    entry.Panel.Get_IsRusted && HasMaterial(entry.Panel))
                .Select(entry => (entry.Id, entry.Panel, Collider: entry.Panel.GetComponent<Collider>()))
                .Where(entry => entry.Collider != null && entry.Collider.enabled && entry.Collider.gameObject.activeInHierarchy &&
                    (mask & (1 << entry.Collider.gameObject.layer)) != 0)
                .OrderBy(entry => entry.Id).FirstOrDefault();
            if (candidates.Panel == null || candidates.Collider == null)
                throw new InvalidOperationException("No active registered rusted panel has a direct collider on the native repair-kit ray mask.");
            return new Target
            {
                Id = candidates.Id,
                Panel = candidates.Panel,
                Collider = candidates.Collider,
                Baseline = shared.TestState(candidates.Id)
            };
        }

        private Vector3 KitPosition() => target.Collider.bounds.center + PreferredSide() * 0.3f + Vector3.up * 0.35f;

        private static readonly float[] AimDistances = { 1.5f, 1.1f, 1.9f };

        private static Vector3[] AimSidesFor(Transform panel)
        {
            Vector3[] options = { panel.forward, -panel.forward, panel.right, -panel.right,
                Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
            var sides = new System.Collections.Generic.List<Vector3>();
            foreach (Vector3 option in options)
            {
                Vector3 side = Vector3.ProjectOnPlane(option, Vector3.up);
                if (side.sqrMagnitude < 0.5f) continue;
                side.Normalize();
                if (!sides.Any(existing => Vector3.Dot(existing, side) > 0.999f)) sides.Add(side);
            }
            return sides.ToArray();
        }

        private Vector3[] AimSides() => AimSidesFor(target.Panel.transform);
        private Vector3 PreferredSide() => AimSides()[0];

        private void StageCamera(float distance, Vector3 side)
        {
            Camera camera = CameraForStaging() ?? throw new InvalidOperationException("Native player camera is unavailable in rust repair fixture.");
            Vector3 eyeOffset = camera.transform.position - gameplay.PlayerWalking.transform.position;
            Vector3 desiredCameraPosition = target.Collider.bounds.center + side * distance;
            Move(desiredCameraPosition - eyeOffset);
            Physics.SyncTransforms();
            camera.transform.rotation = Quaternion.LookRotation(target.Collider.bounds.center - camera.transform.position, Vector3.up);
            Physics.SyncTransforms();
        }

        private void Move(Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private Camera? CameraForStaging() => kit == null
            ? gameplay.Player.mainCamera ?? Camera.main
            : NativeCameraField.GetValue(kit) as Camera ?? gameplay.Player.mainCamera ?? Camera.main;

        private string RayDiagnostics()
        {
            Camera? camera = CameraForStaging();
            if (camera == null) return "camera=null; Camera.main=" + CameraInfo(Camera.main);
            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            int mask = NativeRepairRayMask();
            string hitStatus = "hit=<none>";
            if (Physics.Raycast(ray, out var hit, 2.4f, mask))
            {
                var direct = hit.collider.GetComponent<Vehicle_PaintablePanel>();
                var generated = hit.collider.GetComponent<Generated_Part>();
                Vehicle_PaintablePanel? resolved = direct;
                if (resolved == null && generated != null && generated.carPartSocket != null &&
                    generated.carPartSocket.isInstalled && generated.carPartSocket.partInstalled != null)
                    resolved = generated.carPartSocket.partInstalled.GetComponent<Vehicle_PaintablePanel>();
                hitStatus = "hit=" + ShortName(hit.collider.name) + "/layer=" + LayerLabel(hit.collider.gameObject.layer) +
                    "/distance=" + hit.distance.ToString("F2") + "/direct=" + ShortName(direct == null ? "null" : direct.name) +
                    "/generated=" + ShortName(generated == null ? "null" : generated.name) +
                    "/resolved=" + ShortName(resolved == null ? "null" : resolved.name) +
                    "/resolvedPath=" + ShortPath(resolved == null ? null : resolved.transform);
            }
            var nativeTarget = kit == null ? null : NativeTargetField.GetValue(kit) as Vehicle_PaintablePanel;
            var toolCamera = kit == null ? null : NativeCameraField.GetValue(kit) as Camera;
            float panelDistance = Vector3.Distance(camera.transform.position, target.Collider.bounds.center);
            return "camera=" + camera.transform.position.ToString("F2") + "/forward=" + camera.transform.forward.ToString("F2") +
                "/rayOrigin=" + ray.origin.ToString("F2") + "/rayDirection=" + ray.direction.ToString("F2") +
                "/toolCamera=" + CameraInfo(toolCamera) + "/Camera.main=" + CameraInfo(Camera.main) +
                "/mask=" + mask + "; " + hitStatus + "; panelCollider=" + ShortName(target.Collider.name) +
                "/layer=" + LayerLabel(target.Collider.gameObject.layer) + "/distance=" + panelDistance.ToString("F2") +
                "/panel=" + ShortName(target.Panel.name) + "/panelPath=" + ShortPath(target.Panel.transform) +
                "/field=" + ShortName(nativeTarget == null ? "null" : nativeTarget.name) +
                "/fieldPath=" + ShortPath(nativeTarget == null ? null : nativeTarget.transform);
        }

        internal void Abort()
        {
            try { PreservedRayDiagnostics(); } catch { }
            try { EquipmentDiagnostics(); } catch { }
            diagnosticsPreserved = true;
            if (plugin.TestOptions.IsHost)
            {
                try
                {
                    panels.TestRestoreHostPanel(target.Id, target.Baseline);
                    foreach (ulong id in new[] { firstKitId, noChangeKitId })
                        if (id != 0 && items.TestState(id).Exists && items.TestItem(id) != null) items.TestDestroy(id);
                }
                catch { }
                return;
            }
            try
            {
                if (gameplay.Player.Get_ActiveTool == kit || gameplay.Player.Get_HoldingItem == kit) items.TestPutAwayTool();
                Move(startingPlayerPosition);
                NativeRepairFixtureAim.SetLook(gameplay, gameplay.Player.mainCamera, startingCameraRotation * Vector3.forward);
            }
            catch { }
        }

        private static int NativeRepairRayMask() => LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket", "VehiclePlayerCantGoThrough");
        private static string LayerLabel(int layer) => LayerMask.LayerToName(layer) + "(" + layer + ")";
        private static string ShortName(string value) => value.Length <= 52 ? value : value.Substring(0, 49) + "...";
        private static string CameraInfo(Camera? camera) => camera == null ? "null" :
            ShortName(camera.name) + "@" + camera.transform.position.ToString("F2") + "/" + camera.transform.forward.ToString("F2");
        private static string ShortPath(Transform? transform)
        {
            if (transform == null) return "null";
            try { return ShortName(RuntimeProbe.CanonicalPath(transform)); }
            catch { return ShortName(transform.name); }
        }

        private static bool HasMaterial(Vehicle_PaintablePanel panel)
        {
            try { return panel.Get_Material != null; }
            catch { return false; }
        }

        private static bool Same(PaintPanelState a, PaintPanelState b) => a.Encode().SequenceEqual(b.Encode());
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
