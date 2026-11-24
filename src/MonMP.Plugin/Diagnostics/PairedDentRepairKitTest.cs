using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Uses a real shared Tool_DentRepairKit lease and its native MouseDown entry
    // point. The host stages one reversible dent so the first request repairs it;
    // the same kit then proves the server-derived rapid-click damage progression.
    internal sealed class PairedDentRepairKitTest
    {
        private static readonly System.Reflection.FieldInfo NativeTargetField =
            AccessTools.Field(typeof(Tool_DentRepairKit), "vehicle_PaintablePanel") ??
            throw new InvalidOperationException("Native dent repair target field is unavailable.");
        private static readonly System.Reflection.FieldInfo NativeCameraField =
            AccessTools.Field(typeof(Tool_DentRepairKit), "mainCamera") ??
            throw new InvalidOperationException("Native dent repair camera field is unavailable.");

        private sealed class Target
        {
            public ulong Id;
            public Vehicle_PaintablePanel Panel = null!;
            public Collider Collider = null!;
            public PaintPanelState Original = null!;
            public bool Associated;
            public PaintPanelState Addressed(PaintPanelState state) => Associated ? state.Associated! : state;
        }

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly SharedPaintPanels panels;
        private readonly Target target;
        private readonly Vector3 startingPlayerPosition;
        private readonly Quaternion startingCameraRotation;
        private readonly int initialKitCount;
        private readonly float started = Time.unscaledTime;
        private PaintPanelState? staged;
        private PaintPanelState? repaired;
        private PaintPanelState? damaged;
        private Tool_DentRepairKit? kit;
        private ulong kitId;
        private float next;
        private float equipDeadline;
        private float damageWindowStarted;
        private int aimAttempts;
        private int grabAttempts;
        private int damageClicksSent;
        private int repairClicksSent, repairSteps;
        private readonly int localCueBaseline, remoteCueBaseline, acceptedBaseline;
        private float lastRepairDent;
        private int phase;
        private string lastNativeRay = "not-sampled", lastSurfaceAim = "not-sampled";
        private bool diagnosticsPreserved;
        private bool requestedEquip;
        private Vector3 lastAimSide;
        private float lastAimDistance;

        internal string Status => "dent repair phase=" + phase + "; panel=" + target.Id.ToString("X16") +
            "; kit=" + kitId + "; dent=" + target.Addressed(panels.TestState(target.Id)).DentScale.ToString("F3") +
            "; pending=" + panels.TestPending + "; accepted=" + panels.TestAccepted + "; rejected=" + panels.TestRejected +
            "; leased=" + (kitId != 0 && items.TestState(kitId).Lease.Owner == plugin.Session?.LocalPeerId) +
            "; clicks=" + damageClicksSent + "; aim=" + aimAttempts + "/" + lastAimSide.ToString("F1") + "@" + lastAimDistance.ToString("F1") +
            "; repairStart=" + (staged != null ? target.Addressed(staged).DentScale.ToString("F3") : "not-staged") +
            "; repairStepBaseline=" + lastRepairDent.ToString("F3") + "; repairClicks=" + repairClicksSent + "; repairSteps=" + repairSteps +
            "; ray=" + PreservedRayDiagnostics() + "; surfaceAim=" + lastSurfaceAim +
            "; hostGates=" + panels.TestLastRepairHostDiagnostic;

        private string PreservedRayDiagnostics()
        {
            if (!diagnosticsPreserved && kit != null)
            {
                lastNativeRay = RayDiagnostics();
                lastSurfaceAim = NativeRepairFixtureAim.LastDiagnostics;
            }
            return lastNativeRay;
        }

        internal PairedDentRepairKitTest(MonMPPlugin plugin, Gameplay gameplay, bool associatedAccessory = false)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            items = plugin.World!.Items;
            panels = plugin.World.PaintPanels;
            localCueBaseline = RepairToolPresentation.TestLocal(RepairToolPresentation.Dent);
            remoteCueBaseline = RepairToolPresentation.TestRemote(RepairToolPresentation.Dent);
            acceptedBaseline = panels.TestAccepted;
            panels.TestRepairDiagnosticsActive = plugin.TestOptions.Enabled;
            target = FindTarget(panels, associatedAccessory);
            startingPlayerPosition = gameplay.PlayerWalking.transform.position;
            startingCameraRotation = gameplay.Player.mainCamera.transform.rotation;
            initialKitCount = items.LiveItems.Count(entry => entry.Item is Tool_DentRepairKit);
        }

        internal bool Tick()
        {
            if (Time.unscaledTime - started > 180)
                throw new InvalidOperationException("Dent repair kit test timed out: " + Status);
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
                if (target.Panel.Get_CantBeDent)
                    throw new InvalidOperationException("Selected native panel became ineligible for dent repair.");
                // Seed only the isolated host panel via the native API. The original
                // PaintPanelState is retained and restored after the guest disconnects.
                int stagingClicks = 0;
                while (target.Panel.Get_ActualDantScale < 0.25f && stagingClicks++ < 3)
                    if (!target.Panel.RepairDent(doDamage: true)) break;
                staged = panels.TestNativeState(target.Id);
                lastRepairDent = target.Addressed(staged).DentScale;
                if (target.Associated) RepairAccessoryFixture.AssertOnlyAccessoryChanged(target.Original, staged);
                Require(target.Addressed(staged).DentScale >= 0.25f,
                    "Could not seed a reversible native dent on the selected panel.");
                kitId = items.TestSpawnDentRepairKit(KitPosition());
                Require(items.TestItem(kitId) is Tool_DentRepairKit,
                    "Host did not spawn a native dent repair kit.");
                phase = 1;
                return false;
            }

            if (phase == 1 && staged != null &&
                target.Addressed(panels.TestState(target.Id)).DentScale <= 0.001f &&
                Same(panels.TestNativeState(target.Id), panels.TestState(target.Id)))
            {
                repaired = panels.TestState(target.Id);
                if (target.Associated) RepairAccessoryFixture.AssertOnlyAccessoryChanged(target.Original, repaired);
                Require(items.TestState(kitId).Exists && items.TestState(kitId).Lease.Owner != ItemLease.Unowned,
                    "The guest repair changed the panel without retaining its native kit lease.");
                phase = 2;
                return false;
            }

            if (phase == 2 && repaired != null &&
                target.Addressed(panels.TestState(target.Id)).DentScale > target.Addressed(repaired).DentScale + 0.1f &&
                Same(panels.TestNativeState(target.Id), panels.TestState(target.Id)))
            {
                damaged = panels.TestState(target.Id);
                if (target.Associated) RepairAccessoryFixture.AssertOnlyAccessoryChanged(target.Original, damaged);
                Require(items.TestState(kitId).Exists && items.TestState(kitId).Lease.Owner != ItemLease.Unowned,
                    "The rapid damage progression consumed or released the leased kit.");
                phase = 3;
                return false;
            }

            if (phase == 3 && plugin.Session!.Peers.Count == 1 && plugin.RemoteAvatarCount == 0)
            {
                Require(RepairToolPresentation.TestRemote(RepairToolPresentation.Dent) == remoteCueBaseline + panels.TestAccepted - acceptedBaseline &&
                    RepairToolPresentation.TestLocal(RepairToolPresentation.Dent) == localCueBaseline,
                    "Host dent cues were missing, duplicated, local, or emitted for unchanged clicks.");
                Require(staged != null && repaired != null && damaged != null,
                    "Guest disconnected before dent repair and damage progression both completed.");
                Require(items.TestState(kitId).Exists && items.TestState(kitId).Lease.Owner == ItemLease.Unowned,
                    "Guest disconnect did not release the reusable dent repair kit.");
                Require(Same(panels.TestNativeState(target.Id), damaged!) && Same(panels.TestState(target.Id), damaged!),
                    "Host panel changed after the guest's final damage snapshot.");
                panels.TestRestoreHostPanel(target.Id, target.Original);
                items.TestDestroy(kitId);
                phase = 4;
                return false;
            }

            if (phase == 4 && !items.TestState(kitId).Exists)
            {
                Require(Same(panels.TestNativeState(target.Id), target.Original),
                    "Host did not restore the exact native panel baseline.");
                Require(items.LiveItems.Count(entry => entry.Item is Tool_DentRepairKit) == initialKitCount,
                    "Host fixture left a spawned dent repair kit behind.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                // The host's staging update must be visible before the native click.
                if (target.Addressed(panels.TestState(target.Id)).DentScale < 0.25f) return false;
                staged = panels.TestState(target.Id);
                Require(plugin.World!.Events?.TestRepairHistorySilent() == true, "Initial dent presentation history replayed a cue.");
                lastRepairDent = target.Addressed(staged).DentScale;
                StageCamera(1.5f, AimSides()[0]);
                phase = 1;
                next = Time.unscaledTime + 0.4f;
                return false;
            }

            if (phase == 1)
            {
                if (!TryEquipKit()) return false;
                if (!AimAtTarget()) return false;
                ClickNative();
                repairClicksSent++;
                phase = 2;
                return false;
            }

            if (phase == 2 && panels.TestPending == 0 && staged != null &&
                target.Addressed(panels.TestState(target.Id)).DentScale < lastRepairDent - 0.001f)
            {
                var repairStep = panels.TestState(target.Id);
                Require(Mathf.Abs(target.Addressed(repairStep).DentScale - Mathf.Max(0f, lastRepairDent - 0.25f)) < 0.001f,
                    "Native repair step did not subtract the exact bounded quarter-scale dent.");
                if (target.Associated) RepairAccessoryFixture.AssertOnlyAccessoryChanged(target.Original, repairStep);
                Require(Same(panels.TestNativeState(target.Id), repairStep),
                    "Guest native panel did not match the host repair state.");
                Require(LeasedKitRemainsEquipped(),
                    "Successful native repair did not preserve the guest's exact leased kit.");
                lastRepairDent = target.Addressed(repairStep).DentScale;
                repairSteps++;
                Require(RepairToolPresentation.TestLocal(RepairToolPresentation.Dent) == localCueBaseline + repairSteps &&
                    RepairToolPresentation.TestRemote(RepairToolPresentation.Dent) == remoteCueBaseline,
                    "Dent repair did not play exactly one actor cue per successful native step.");
                if (lastRepairDent <= 0.001f)
                {
                    repaired = repairStep;
                    phase = 3;
                }
                else phase = 20;
                next = Time.unscaledTime + 1.65f; // host enforces the native 1.5-second success gate
                return false;
            }

            if (phase == 20)
            {
                Require(repairClicksSent < 20, "Native bounded repair clicks did not fully remove the seeded dent: " + Status);
                Require(LeasedKitRemainsEquipped(), "Native repair step lost the exact leased kit.");
                Require(AimStillHitsTarget(), "Native center ray left the target between successful repair steps: " + Status);
                ClickNative();
                repairClicksSent++;
                phase = 2;
                return false;
            }

            if (phase == 3)
            {
                if (!AimStillHitsTarget())
                    throw new InvalidOperationException("Native center ray lost the staged dent panel before damage progression: " + Status);
                damageWindowStarted = Time.unscaledTime;
                damageClicksSent = 1; // first request starts the host's three-second click window
                ClickNative();
                // Stay below the unchanged eight-request/second host budget. Ten
                // clicks span 1.62 seconds, within the native three-second window.
                next = Time.unscaledTime + 0.18f;
                phase = 4;
                return false;
            }

            if (phase == 4 && panels.TestPending == 0)
            {
                float currentDent = target.Addressed(panels.TestState(target.Id)).DentScale;
                int damageCues = repaired != null && currentDent > target.Addressed(repaired).DentScale + 0.1f ? 1 : 0;
                Require(RepairToolPresentation.TestLocal(RepairToolPresentation.Dent) == localCueBaseline + repairSteps + damageCues &&
                    RepairToolPresentation.TestRemote(RepairToolPresentation.Dent) == remoteCueBaseline,
                    "Dent damage/no-change click had a missing, duplicated, or observer actor cue.");
                if (repaired != null && currentDent > target.Addressed(repaired).DentScale + 0.1f)
                {
                    damaged = panels.TestState(target.Id);
                if (target.Associated) RepairAccessoryFixture.AssertOnlyAccessoryChanged(target.Original, damaged);
                    Require(Same(panels.TestNativeState(target.Id), damaged),
                        "Guest did not apply the host's native damage panel snapshot.");
                    Require(LeasedKitRemainsEquipped(),
                        "Server-derived damage progression consumed or dropped the reusable kit.");
                    phase = 5;
                    return false;
                }

                if (Time.unscaledTime - damageWindowStarted > 2.8f || damageClicksSent >= 11)
                    throw new InvalidOperationException("Host did not complete the 10-click dent progression within its 3-second window: " + Status);
                if (!AimStillHitsTarget())
                    throw new InvalidOperationException("Native center ray left the target during dent progression: " + Status);
                damageClicksSent++;
                ClickNative();
                next = Time.unscaledTime + 0.18f;
                return false;
            }

            if (phase == 5 && damaged != null)
            {
                Require(plugin.World!.Events?.TestRepairHistorySilent() == true, "Dent snapshot or duplicate log replayed a cue.");
                items.TestPutAwayTool();
                phase = 6;
                next = Time.unscaledTime + 0.3f;
                return false;
            }

            if (phase == 6 && items.TestState(kitId).Lease.Owner == ItemLease.Unowned &&
                items.TestState(kitId).Exists)
            {
                Move(startingPlayerPosition);
                NativeRepairFixtureAim.SetLook(gameplay, gameplay.Player.mainCamera, startingCameraRotation * Vector3.forward);
                plugin.EndPairedTestSession();
                phase = 7;
                return false;
            }

            if (phase == 7 && plugin.World == null)
            {
                Require(panels.TestRestored, "Guest dent panel native state did not roll back on disconnect.");
                Require(items.TestRestored, "Guest dent repair kit replica did not disappear on disconnect.");
                return true;
            }
            return false;
        }

        private bool TryEquipKit()
        {
            if (kitId == 0)
                kitId = items.LiveItems.Where(entry => entry.Item is Tool_DentRepairKit &&
                    Math.Abs(entry.Item.transform.localScale.x - 0.736f) < 0.001f)
                    .Select(entry => entry.Id).DefaultIfEmpty().Max();
            if (kitId == 0) return false;
            if (items.TestState(kitId).Lease.Owner != plugin.Session!.LocalPeerId)
            {
                if (!items.TestPickupPending(kitId))
                {
                    if (grabAttempts >= 6)
                        throw new InvalidOperationException("Guest could not lease the dent repair kit: " + items.TestPickupStatus(kitId));
                    items.TestGrab(kitId);
                    grabAttempts++;
                    next = Time.unscaledTime + 0.7f;
                }
                return false;
            }

            kit = items.TestItem(kitId) as Tool_DentRepairKit;
            if (kit == null) return false;
            if (kit.isEquiped && gameplay.Player.Get_ActiveTool == kit) return true;
            if (gameplay.Player.Get_HoldingItem == kit && !requestedEquip)
            {
                items.TestUse(kitId);
                requestedEquip = true;
                equipDeadline = Time.unscaledTime + 3f;
                next = Time.unscaledTime + 0.2f;
            }
            else if (requestedEquip && Time.unscaledTime >= equipDeadline)
                throw new InvalidOperationException("Guest native Use did not equip the dent repair kit: " + items.TestPickupStatus(kitId));
            return false;
        }

        private void ClickNative()
        {
            Require(kit != null && kit.isEquiped && gameplay.Player.Get_ActiveTool == kit,
                "Dent repair click requires the real equipped native kit.");
            kit!.MouseDown();
            Require(panels.TestPending == 1, "Native dent repair MouseDown did not send exactly one host request: " + Status);
        }

        private bool LeasedKitRemainsEquipped() => kit != null && items.TestState(kitId).Exists &&
            items.TestState(kitId).Lease.Owner == plugin.Session!.LocalPeerId &&
            items.TestItem(kitId) == kit && kit.isEquiped && gameplay.Player.Get_ActiveTool == kit;

        private bool TryAimAtTarget()
        {
            if (AimStillHitsTarget()) return true;
            if (target.Associated)
            {
                if (aimAttempts >= 3 || !NativeRepairFixtureAim.TryStage(gameplay, CameraForStaging(),
                    target.Collider, AimSides(), NativeRepairRayMask(), out lastAimSide, out lastAimDistance))
                    throw new InvalidOperationException("Exact dent accessory has no unobstructed native-mask surface ray: " + Status);
                aimAttempts++;
                next = Time.unscaledTime + 0.3f;
                return false;
            }
            if (aimAttempts >= AimSides().Length * AimDistances.Length)
                throw new InvalidOperationException("Native dent kit could not acquire target with bounded center-ray staging: " + Status);
            Vector3[] sides = AimSides();
            lastAimSide = sides[aimAttempts % sides.Length];
            lastAimDistance = AimDistances[aimAttempts / sides.Length];
            StageCamera(lastAimDistance, lastAimSide);
            aimAttempts++;
            next = Time.unscaledTime + 0.18f;
            return false;
        }

        private bool AimAtTarget() => TryAimAtTarget();

        private bool AimStillHitsTarget()
        {
            Camera? camera = CameraForStaging();
            if (camera == null) return false;
            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(ray, out var hit, 2.4f, NativeRepairRayMask())) return false;
            return hit.collider.GetComponent<Vehicle_PaintablePanel>() == target.Panel &&
                NativeTargetField.GetValue(kit) as Vehicle_PaintablePanel == target.Panel;
        }

        private void StageCamera(float distance, Vector3 side)
        {
            Camera camera = CameraForStaging() ?? throw new InvalidOperationException("Native dent kit camera is unavailable.");
            Vector3 eyeOffset = camera.transform.position - gameplay.PlayerWalking.transform.position;
            Vector3 cameraPosition = target.Collider.bounds.center + side * distance;
            Move(cameraPosition - eyeOffset);
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

        private static Target FindTarget(SharedPaintPanels shared, bool associatedAccessory)
        {
            if (associatedAccessory)
            {
                // Native RepairDent uses PannelsRelated/_BumpScale, not a _BumpMap texture gate.
                // Keep the genuine associated target; native staging below proves it can mutate.
                var accessory = RepairAccessoryFixture.Find(shared, panel => !panel.Get_CantBeDent && panel.Get_Material != null);
                return new Target { Id = accessory.Id, Panel = accessory.Panel, Collider = accessory.Collider,
                    Original = shared.TestNativeState(accessory.Id), Associated = true };
            }
            int mask = NativeRepairRayMask();
            var candidate = shared.TestPanels.Select(id => (Id: id, Panel: shared.TestNative(id)))
                .Where(entry => !shared.TestDynamicPanels.Contains(entry.Id) && entry.Panel != null &&
                    entry.Panel.gameObject.activeInHierarchy && entry.Panel.PannelAssociatedToSave == null &&
                    !entry.Panel.Get_CantBeDent && HasDentMaterial(entry.Panel))
                .Select(entry => (entry.Id, entry.Panel, Collider: entry.Panel.GetComponent<Collider>()))
                .Where(entry => entry.Collider != null && entry.Collider.enabled && entry.Collider.gameObject.activeInHierarchy &&
                    (mask & (1 << entry.Collider.gameObject.layer)) != 0)
                .OrderBy(entry => entry.Id).FirstOrDefault();
            if (candidate.Panel == null || candidate.Collider == null)
                throw new InvalidOperationException("No active registered dentable panel has a direct collider on the native repair-kit ray mask.");
            return new Target
            {
                Id = candidate.Id,
                Panel = candidate.Panel,
                Collider = candidate.Collider,
                Original = shared.TestNativeState(candidate.Id)
            };
        }

        private Vector3 KitPosition() => target.Collider.bounds.center + AimSides()[0] * 0.3f + Vector3.up * 0.35f;
        private static readonly float[] AimDistances = { 1.5f, 1.1f, 1.9f };

        private Vector3[] AimSides()
        {
            Vector3[] options = { target.Panel.transform.forward, -target.Panel.transform.forward,
                target.Panel.transform.right, -target.Panel.transform.right, Vector3.forward, Vector3.back,
                Vector3.right, Vector3.left };
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

        private string RayDiagnostics()
        {
            Camera? camera = CameraForStaging();
            if (camera == null) return "camera=null; Camera.main=" + CameraInfo(Camera.main);
            Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            string hitStatus = "hit=<none>";
            if (Physics.Raycast(ray, out var hit, 2.4f, NativeRepairRayMask()))
                hitStatus = "hit=" + ShortName(hit.collider.name) + "/layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer) +
                    "/distance=" + hit.distance.ToString("F2") + "/panel=" +
                    ShortName((hit.collider.GetComponent<Vehicle_PaintablePanel>() is Vehicle_PaintablePanel panel ? panel.name : "null"));
            var nativeTarget = kit == null ? null : NativeTargetField.GetValue(kit) as Vehicle_PaintablePanel;
            return "camera=" + camera.transform.position.ToString("F2") + "/forward=" + camera.transform.forward.ToString("F2") +
                "/toolCamera=" + CameraInfo(kit == null ? null : NativeCameraField.GetValue(kit) as Camera) +
                "/Camera.main=" + CameraInfo(Camera.main) + "; " + hitStatus + "; panel=" + ShortName(target.Panel.name) +
                "/path=" + ShortPath(target.Panel.transform) + "; field=" + ShortName(nativeTarget == null ? "null" : nativeTarget.name);
        }

        internal void Abort()
        {
            try { PreservedRayDiagnostics(); } catch { }
            diagnosticsPreserved = true;
            if (plugin.TestOptions.IsHost)
            {
                try
                {
                    panels.TestRestoreHostPanel(target.Id, target.Original);
                    if (kitId != 0 && items.TestState(kitId).Exists && items.TestItem(kitId) != null) items.TestDestroy(kitId);
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

        private static bool HasDentMaterial(Vehicle_PaintablePanel panel)
        {
            try { return panel.Get_Material != null && panel.Get_Material.GetTexture("_BumpMap") != null; }
            catch { return false; }
        }

        private static int NativeRepairRayMask() => LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket", "VehiclePlayerCantGoThrough");
        private static string ShortName(string value) => value.Length <= 52 ? value : value.Substring(0, 49) + "...";
        private static string CameraInfo(Camera? camera) => camera == null ? "null" :
            ShortName(camera.name) + "@" + camera.transform.position.ToString("F2") + "/" + camera.transform.forward.ToString("F2");
        private static string ShortPath(Transform transform)
        {
            try { return ShortName(RuntimeProbe.CanonicalPath(transform)); }
            catch { return ShortName(transform.name); }
        }
        private static bool Same(PaintPanelState a, PaintPanelState b) => a.Encode().SequenceEqual(b.Encode());
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
