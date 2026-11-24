using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Paired gameplay fixture for the physical guest spray path. It leases a real
    // catalog Tool_SprayPaintCan, invokes its native mouse methods, and lets the
    // normal EquipThisTool coroutine establish the camera-ray target.
    internal sealed class PairedSprayPaintTest
    {
        private sealed class GeneratedPartTarget
        {
            public ulong PanelId;
            public Vehicle_PaintablePanel Panel = null!;
            public Collider SocketCollider = null!;
            public Vector3 CameraSide;
            public PaintPanelState Baseline = null!;
        }

        private const float StartingCanLevel = 0.8f;
        private static readonly System.Reflection.FieldInfo NativeTargetField =
            AccessTools.Field(typeof(Tool_SprayPaintCan), "vehicle_PaintablePanel") ??
            throw new InvalidOperationException("Native spray target field is unavailable.");
        private static readonly System.Reflection.FieldInfo NativeMouseHeldField =
            AccessTools.Field(typeof(Tool_SprayPaintCan), "isMouseLeftClickDown") ??
            throw new InvalidOperationException("Native spray mouse-held field is unavailable.");

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly SharedPaintPanels panels;
        private readonly SharedVehicleParts vehicleParts;
        private readonly ulong panelId;
        private readonly Vehicle_PaintablePanel panel;
        private readonly Collider panelCollider;
        private readonly ulong staleTargetPanelId;
        private readonly Vehicle_PaintablePanel staleTargetPanel;
        private readonly PaintPanelState staleTargetBaseline;
        private readonly Vector3 cameraSide;
        private readonly Vector3 startingPlayerPosition;
        private readonly Quaternion startingCameraRotation;
        private readonly PaintPanelState baseline;
        private readonly float started = Time.unscaledTime;
        private readonly int customColor;
        private readonly Vector3 sprayColor;
        private PaintPanelState? paintedState;
        private GameObject? blocker;
        private Tool_SprayPaintCan? can;
        private ulong canId;
        private float hostInitialCanLevel;
        private float guestInitialCanLevel;
        private float deniedCanLevel;
        private float next;
        private float equipDeadline;
        private float dropReleaseDeadline;
        private int phase;
        private int grabAttempts;
        private bool requestedGrab;
        private bool requestedEquip;
        private bool generatedPartSelectionDone;
        private GeneratedPartTarget? generatedPartTarget;

        internal string Status => "spray paint phase=" + phase + "; panel=" + panelId.ToString("X16") +
            "; stalePanel=" + staleTargetPanelId.ToString("X16") + "; can=" + canId + "; active=" + panels.TestActiveSprays +
            "; generatedPart=" + (generatedPartTarget == null ? "unavailable" : generatedPartTarget.PanelId.ToString("X16")) +
            "; rejected=" + panels.TestRejected + "; target=" + (can == null ? 0 : TargetId(can)) +
            (can == null ? "" : "; fill=" + can.Value1.ToString("F4") + "; lease=" + items.TestState(canId).Lease.Owner +
                "; pickup=" + items.TestPickupStatus(canId) + "; held=" + (gameplay.Player.Get_HoldingItem == can) +
                "; equipped=" + can.isEquiped + "; activeTool=" + (gameplay.Player.Get_ActiveTool == can) +
                "; useRequested=" + requestedEquip);

        internal PairedSprayPaintTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            panels = plugin.World!.PaintPanels;
            items = plugin.World.Items;
            vehicleParts = plugin.World.VehicleParts;
            startingPlayerPosition = gameplay.PlayerWalking.transform.position;
            startingCameraRotation = gameplay.Player.mainCamera.transform.rotation;
            var dynamicPanelIds = new HashSet<ulong>(panels.TestDynamicPanels);

            var candidate = panels.TestPanels.Select(id => (Id: id, Panel: panels.TestNative(id)))
                .Where(entry => !dynamicPanelIds.Contains(entry.Id) && entry.Panel != null && entry.Panel.gameObject.activeInHierarchy &&
                    entry.Panel.PannelAssociatedToSave == null && HasMaterial(entry.Panel))
                .Select(entry => (entry.Id, entry.Panel, Collider: entry.Panel.GetComponent<Collider>()))
                .Where(entry => entry.Collider != null && entry.Collider.enabled && entry.Collider.gameObject.activeInHierarchy)
                .Select(entry => (entry.Id, entry.Panel, entry.Collider, Side: ClearCameraSide(entry.Panel, entry.Collider)))
                .Where(entry => entry.Side.sqrMagnitude > 0.5f)
                .OrderBy(entry => entry.Id).FirstOrDefault();
            if (candidate.Panel == null || candidate.Collider == null)
                throw new InvalidOperationException("No registered static paint panel has a clear native camera ray from a paired-test position.");

            panelId = candidate.Id;
            panel = candidate.Panel;
            panelCollider = candidate.Collider;
            cameraSide = candidate.Side;
            baseline = panels.TestState(panelId);
            var staleTarget = panels.TestPanels.Select(id => (Id: id, Panel: panels.TestNative(id)))
                .Where(entry => !dynamicPanelIds.Contains(entry.Id) && entry.Id != panelId && entry.Panel != null && entry.Panel.gameObject.activeInHierarchy &&
                    entry.Panel.PannelAssociatedToSave == null && HasMaterial(entry.Panel))
                .OrderBy(entry => entry.Id).FirstOrDefault();
            if (staleTarget.Panel == null)
                throw new InvalidOperationException("No second registered static panel is available for the stale-target spray check.");
            staleTargetPanelId = staleTarget.Id;
            staleTargetPanel = staleTarget.Panel;
            staleTargetBaseline = panels.TestState(staleTargetPanelId);
            sprayColor = FarthestPrimary(baseline);
            customColor = EncodeCustomColor(sprayColor);
        }

        internal bool Tick()
        {
            if (Time.unscaledTime - started > 180)
                throw new InvalidOperationException("Spray paint test timed out: " + Status);
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true) return false;
            if (!generatedPartSelectionDone) SelectInstalledGeneratedPartTarget();
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                Vector3 lateral = Vector3.Cross(cameraSide, Vector3.up).normalized;
                Vector3 spawn = panelCollider.bounds.center + cameraSide * 0.3f + lateral * 0.42f;
                canId = items.TestSpawnSprayCan(spawn, StartingCanLevel, customColor);
                var native = items.TestItem(canId) as Tool_SprayPaintCan ??
                    throw new InvalidOperationException("Host did not create a native spray can.");
                hostInitialCanLevel = native.Value1;
                if (Math.Abs(hostInitialCanLevel - StartingCanLevel) > 0.0001f || native.IntValue1 != customColor)
                    throw new InvalidOperationException("Host spray-can fixture did not retain its native fill and selected color.");
                phase = 1;
            }
            else if (phase == 1 && canId != 0 && panels.TestActiveSprays == 0 &&
                items.TestItem(canId) is Tool_SprayPaintCan native && native.Value1 < hostInitialCanLevel - 0.001f &&
                !Same(panels.TestNativeState(panelId), baseline))
            {
                paintedState = panels.TestNativeState(panelId);
                if (!MovedToward(panels.TestNativeState(panelId), baseline, sprayColor))
                    throw new InvalidOperationException("Host native paint color did not move toward the native spray-can color.");
                if (!Same(panels.TestNativeState(staleTargetPanelId), staleTargetBaseline))
                    throw new InvalidOperationException("Guest stale panel target changed; host did not use the camera-ray panel.");
                blocker = CreateBlocker();
                Physics.SyncTransforms();
                phase = 2;
            }
            else if (phase == 2 && plugin.Session!.Peers.Count == 1)
            {
                if (items.TestItem(canId) is not Tool_SprayPaintCan remainingCan || remainingCan.Value1 >= hostInitialCanLevel - 0.002f)
                    throw new InvalidOperationException("Host native spray-can fill did not drain during the guest spray transaction.");
                if (paintedState == null || !Same(panels.TestNativeState(panelId), paintedState))
                    throw new InvalidOperationException("Distant or occluded guest spray changed the host panel after the clear-ray paint.");
                if (!Same(panels.TestNativeState(staleTargetPanelId), staleTargetBaseline))
                    throw new InvalidOperationException("Guest stale panel target changed during a later spray command.");
                if (generatedPartTarget != null)
                {
                    var nativePartState = panels.TestNativeState(generatedPartTarget.PanelId);
                    if (Same(nativePartState, generatedPartTarget.Baseline) || !MovedToward(nativePartState, generatedPartTarget.Baseline, sprayColor))
                        throw new InvalidOperationException("Host did not paint the installed Generated_Part child hit from the guest camera ray.");
                    panels.TestRestoreHostPanel(generatedPartTarget.PanelId);
                }
                panels.TestRestoreHostPanel(panelId);
                items.TestDestroy(canId);
                if (blocker != null) Object.Destroy(blocker);
                blocker = null;
                phase = 3;
            }
            else if (phase == 3 && !items.TestState(canId).Exists)
            {
                if (!Same(panels.TestNativeState(panelId), baseline))
                    throw new InvalidOperationException("Host spray fixture did not restore its original native panel state.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                StageCamera(1.5f);
                phase = 1;
                next = Time.unscaledTime + 0.75f;
                return false;
            }

            if (phase == 1)
            {
                if (canId == 0) canId = items.TestNewestSprayCan();
                if (canId == 0 || items.TestItem(canId) is not Tool_SprayPaintCan found) return false;
                can = found;
                if (items.TestState(canId).Lease.Owner != plugin.Session!.LocalPeerId)
                {
                    if (!requestedGrab || !items.TestPickupPending(canId))
                    {
                        if (grabAttempts >= 6) throw new InvalidOperationException("Guest could not lease the native spray can: " + Status);
                        items.TestGrab(canId);
                        requestedGrab = true;
                        grabAttempts++;
                        next = Time.unscaledTime + 1.2f;
                    }
                    return false;
                }
                if (!can.isEquiped || gameplay.Player.Get_ActiveTool != can)
                {
                    // Leasing and grabbing leaves the spray can in the native hand.
                    // Use the same PlayerInteractable.UseKeyDown path as a real Use
                    // press so EquipThisTool can initialize its target/raycast state.
                    if (!requestedEquip && gameplay.Player.Get_HoldingItem == can)
                    {
                        items.TestUse(canId);
                        requestedEquip = true;
                        equipDeadline = Time.unscaledTime + 3f;
                        next = Time.unscaledTime + 0.25f;
                    }
                    else if (requestedEquip && Time.unscaledTime >= equipDeadline)
                    {
                        throw new InvalidOperationException("Guest native spray-can Use did not equip the leased tool: " + Status);
                    }
                    return false;
                }
                if (TargetId(can) != panel) { StageCamera(1.5f); next = Time.unscaledTime + 0.2f; return false; }
                guestInitialCanLevel = can.Value1;
                if (items.TestState(canId).Values.IntValue1 != customColor)
                    throw new InvalidOperationException("Guest did not receive the host spray-can color value.");
                // Keep the camera ray aimed at panel, but deliberately corrupt the
                // native cached target for this MouseDown. The host must resolve the
                // command from the reported view ray and leave this stale panel alone.
                NativeTargetField.SetValue(can, staleTargetPanel);
                Require(TargetId(can) == staleTargetPanel,
                    "Could not stage a stale native panel target for the physical spray check.");
                can.MouseDown();
                Require(panels.TestClientSpraying && !NativeMouseHeld(can),
                    "Native MouseDown did not enter the host-authoritative guest spray path.");
                phase = 2;
                next = Time.unscaledTime + 0.9f;
                return false;
            }

            if (phase == 2 && Time.unscaledTime >= next)
            {
                can!.MouseUp();
                Require(!panels.TestClientSpraying && !NativeMouseHeld(can),
                    "Native MouseUp did not end the host-authoritative guest spray path.");
                phase = 3;
                next = Time.unscaledTime + 0.25f;
                return false;
            }

            if (phase == 3 && items.TestState(canId).Values.Value1 < guestInitialCanLevel - 0.001f &&
                !Same(panels.TestState(panelId), baseline))
            {
                if (!Same(panels.TestNativeState(panelId), panels.TestState(panelId)))
                    throw new InvalidOperationException("Guest native paint material did not reconcile to the host panel snapshot.");
                if (!MovedToward(panels.TestNativeState(panelId), baseline, sprayColor))
                    throw new InvalidOperationException("Guest panel color did not move toward the leased native can color.");
                if (!Same(panels.TestState(staleTargetPanelId), staleTargetBaseline) ||
                    !Same(panels.TestNativeState(staleTargetPanelId), staleTargetBaseline))
                    throw new InvalidOperationException("A stale native target changed locally or on the host instead of the camera-ray panel.");
                paintedState = panels.TestState(panelId);
                StageCamera(4.2f);
                phase = 4;
                next = Time.unscaledTime + 0.65f;
                return false;
            }

            if (phase == 4)
            {
                if (TargetId(can!) != null)
                {
                    StageCamera(4.2f);
                    next = Time.unscaledTime + 0.2f;
                    return false;
                }
                can!.MouseDown();
                Require(panels.TestClientSpraying && !NativeMouseHeld(can),
                    "Distant native MouseDown bypassed the shared spray prefix.");
                phase = 5;
                next = Time.unscaledTime + 0.55f;
                return false;
            }

            if (phase == 5 && Time.unscaledTime >= next)
            {
                can!.MouseUp();
                Require(!panels.TestClientSpraying && !NativeMouseHeld(can), "Distant spray did not release cleanly.");
                phase = 6;
                next = Time.unscaledTime + 0.35f;
                return false;
            }

            if (phase == 6 && Same(panels.TestState(panelId), paintedState!))
            {
                if (!Same(panels.TestNativeState(panelId), paintedState!))
                    throw new InvalidOperationException("A distant spray changed guest native panel state outside the host snapshot.");
                StageCamera(1.5f);
                phase = 7;
                next = Time.unscaledTime + 0.65f;
                return false;
            }

            if (phase == 7)
            {
                if (TargetId(can!) != panel)
                {
                    StageCamera(1.5f);
                    next = Time.unscaledTime + 0.2f;
                    return false;
                }
                blocker = CreateBlocker();
                Physics.SyncTransforms();
                phase = 8;
                next = Time.unscaledTime + 0.25f;
                return false;
            }

            if (phase == 8)
            {
                if (TargetId(can!) != null)
                    throw new InvalidOperationException("Default-layer blocker did not mask the native paint-panel ray.");
                can!.MouseDown();
                Require(panels.TestClientSpraying && !NativeMouseHeld(can),
                    "Occluded native MouseDown bypassed the shared spray prefix.");
                phase = 9;
                next = Time.unscaledTime + 0.55f;
                return false;
            }

            if (phase == 9 && Time.unscaledTime >= next)
            {
                can!.MouseUp();
                Require(!panels.TestClientSpraying && !NativeMouseHeld(can), "Occluded spray did not release cleanly.");
                phase = 10;
                next = Time.unscaledTime + 0.35f;
                return false;
            }

            if (phase == 10 && Same(panels.TestState(panelId), paintedState!))
            {
                if (!Same(panels.TestNativeState(panelId), paintedState!))
                    throw new InvalidOperationException("An occluded spray changed guest native panel state.");
                if (generatedPartTarget != null)
                {
                    StageCamera(generatedPartTarget.SocketCollider, generatedPartTarget.Panel.transform,
                        generatedPartTarget.CameraSide, 1.5f);
                    phase = 13;
                    next = Time.unscaledTime + 0.65f;
                }
                else BeginDeniedInput();
                return false;
            }

            if (phase == 13)
            {
                if (TargetId(can!) != generatedPartTarget!.Panel)
                {
                    StageCamera(generatedPartTarget.SocketCollider, generatedPartTarget.Panel.transform,
                        generatedPartTarget.CameraSide, 1.5f);
                    next = Time.unscaledTime + 0.2f;
                    return false;
                }
                can!.MouseDown();
                Require(panels.TestClientSpraying && !NativeMouseHeld(can),
                    "Generated_Part child hit did not enter the host-authoritative spray path.");
                phase = 14;
                next = Time.unscaledTime + 0.9f;
                return false;
            }

            if (phase == 14 && Time.unscaledTime >= next)
            {
                can!.MouseUp();
                Require(!panels.TestClientSpraying && !NativeMouseHeld(can),
                    "Generated_Part child spray did not release cleanly.");
                phase = 15;
                next = Time.unscaledTime + 0.3f;
                return false;
            }

            if (phase == 15 && !Same(panels.TestState(generatedPartTarget!.PanelId), generatedPartTarget.Baseline))
            {
                var current = panels.TestState(generatedPartTarget.PanelId);
                if (!Same(panels.TestNativeState(generatedPartTarget.PanelId), current) ||
                    !MovedToward(panels.TestNativeState(generatedPartTarget.PanelId), generatedPartTarget.Baseline, sprayColor))
                    throw new InvalidOperationException("Installed Generated_Part child paint did not reconcile to the guest native material.");
                BeginDeniedInput();
                return false;
            }

            if (phase == 16)
            {
                var lease = items.TestState(canId).Lease;
                if (lease.Owner == ItemLease.Unowned)
                {
                    Require(!can!.isEquiped && gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                        "Player interaction state remained occupied after the spray-can lease was released.");
                    // Directly invoke the native entry point without a lease. Harmony
                    // must suppress the native coroutine and no host request is sent.
                    can.MouseDown();
                    Require(!panels.TestClientSpraying && !NativeMouseHeld(can),
                        "A guest without the spray-can lease entered native spraying.");
                    phase = 17;
                    next = Time.unscaledTime + 0.4f;
                }
                else if (Time.unscaledTime >= dropReleaseDeadline)
                {
                    throw new InvalidOperationException("Spray can did not reach an unowned, unequipped state after native drop: " + Status);
                }
                return false;
            }

            if (phase == 17 && Math.Abs(items.TestState(canId).Values.Value1 - deniedCanLevel) < 0.001f)
            {
                if (blocker != null) Object.Destroy(blocker);
                blocker = null;
                Move(startingPlayerPosition);
                gameplay.Player.mainCamera.transform.rotation = startingCameraRotation;
                plugin.EndPairedTestSession();
                if (!panels.TestRestored) throw new InvalidOperationException("Guest spray panel baseline was not restored on disconnect.");
                if (!items.TestRestored) throw new InvalidOperationException("Guest spray-can test replica was not removed on disconnect.");
                return true;
            }
            return false;
        }

        private void BeginDeniedInput()
        {
            deniedCanLevel = items.TestState(canId).Values.Value1;
            Require(can != null && can.isEquiped && gameplay.Player.Get_ActiveTool == can &&
                gameplay.Player.Get_HoldingItem == null,
                "The spray can was not in the expected equipped-tool state before cleanup.");
            items.TestUse(canId);
            Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == can,
                "Native Use did not return the equipped spray can to the player's hand.");
            items.TestDrop(false);
            phase = 16;
            dropReleaseDeadline = Time.unscaledTime + 8f;
            next = Time.unscaledTime + 0.1f;
        }

        private void SelectInstalledGeneratedPartTarget()
        {
            generatedPartSelectionDone = true;
            var dynamicIds = new HashSet<ulong>(panels.TestDynamicPanels);
            foreach (var (socketId, socket) in vehicleParts.PaintSockets.OrderBy(pair => pair.Id))
            {
                if (socket == null || !socket.isInstalled || socket.partInstalled == null || socket.gameobject == null) continue;
                var collider = socket.partCollider != null ? socket.partCollider : socket.gameobject.GetComponent<Collider>();
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                var generated = collider.GetComponent<Generated_Part>();
                if (generated == null || generated.carPartSocket != socket) continue;
                var partPanel = socket.partInstalled.GetComponent<Vehicle_PaintablePanel>();
                if (partPanel == null || !partPanel.gameObject.activeInHierarchy || !HasMaterial(partPanel)) continue;
                ulong recordId = dynamicIds.Where(id => panels.TestNative(id) == partPanel).DefaultIfEmpty().First();
                if (recordId == 0) continue;
                Vector3 side = ClearCameraSide(partPanel, collider);
                if (side.sqrMagnitude < 0.5f) continue;
                generatedPartTarget = new GeneratedPartTarget
                {
                    PanelId = recordId, Panel = partPanel, SocketCollider = collider,
                    CameraSide = side, Baseline = panels.TestState(recordId)
                };
                return;
            }
        }

        private Vector3 ClearCameraSide(Vehicle_PaintablePanel candidate, Collider collider)
        {
            var camera = gameplay.Player.mainCamera;
            if (camera == null || gameplay.PlayerWalking == null) return Vector3.zero;
            Vector3 eyeOffset = camera.transform.position - gameplay.PlayerWalking.transform.position;
            Vector3 target = collider.bounds.center;
            Vector3[] options = { candidate.transform.forward, -candidate.transform.forward,
                candidate.transform.right, -candidate.transform.right, Vector3.forward, Vector3.back,
                Vector3.right, Vector3.left };
            int mask = NativeSprayMask();
            foreach (Vector3 option in options)
            {
                Vector3 side = Vector3.ProjectOnPlane(option, Vector3.up);
                if (side.sqrMagnitude < 0.5f) continue;
                side.Normalize();
                Vector3 root = candidate.transform.position + side * 1.5f;
                Vector3 origin = root + eyeOffset;
                Vector3 ray = target - origin;
                if (ray.sqrMagnitude > 2.4f * 2.4f || ray.sqrMagnitude < 0.001f) continue;
                if (Physics.Raycast(origin, ray.normalized, out var hit, 2.4f, mask) && hit.collider == collider)
                    return side;
            }
            return Vector3.zero;
        }

        private void StageCamera(float distance)
        {
            StageCamera(panelCollider, panel.transform, cameraSide, distance);
        }

        private void StageCamera(Collider targetCollider, Transform basis, Vector3 side, float distance)
        {
            var camera = gameplay.Player.mainCamera ?? throw new InvalidOperationException("Player camera is unavailable in spray fixture.");
            if (gameplay.PlayerWalking == null) throw new InvalidOperationException("Player walking anchor is unavailable in spray fixture.");
            Vector3 root = basis.position + side * distance;
            Move(root);
            camera.transform.rotation = Quaternion.LookRotation(targetCollider.bounds.center - camera.transform.position, Vector3.up);
        }

        private GameObject CreateBlocker()
        {
            var camera = gameplay.Player.mainCamera ?? throw new InvalidOperationException("Player camera is unavailable in spray fixture.");
            var walking = gameplay.PlayerWalking ?? throw new InvalidOperationException("Player walking anchor is unavailable in spray fixture.");
            Vector3 guestRoot = panel.transform.position + cameraSide * 1.5f;
            Vector3 cameraOrigin = guestRoot + (camera.transform.position - walking.transform.position);
            Vector3 toPanel = panelCollider.bounds.center - cameraOrigin;
            if (toPanel.sqrMagnitude < 0.01f) throw new InvalidOperationException("Spray fixture camera overlaps the target panel.");
            Vector3 direction = toPanel.normalized;
            var blockerObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blockerObject.name = "MonMP Spray Paint Fixture Blocker";
            blockerObject.layer = LayerMask.NameToLayer("Default");
            blockerObject.transform.position = cameraOrigin + direction * Mathf.Min(0.75f, toPanel.magnitude * 0.45f);
            blockerObject.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            blockerObject.transform.localScale = new Vector3(0.8f, 0.8f, 0.08f);
            var renderer = blockerObject.GetComponent<Renderer>();
            if (renderer != null) renderer.enabled = false;
            return blockerObject;
        }

        private void Move(Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static Vehicle_PaintablePanel? TargetId(Tool_SprayPaintCan tool) => NativeTargetField.GetValue(tool) as Vehicle_PaintablePanel;
        private static bool NativeMouseHeld(Tool_SprayPaintCan tool) => (bool)NativeMouseHeldField.GetValue(tool);
        private static int NativeSprayMask() => LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket", "VehiclePlayerCantGoThrough", "Default");
        private static bool HasMaterial(Vehicle_PaintablePanel value) { try { return value.Get_Material != null; } catch { return false; } }
        private static Vector3 FarthestPrimary(PaintPanelState state)
        {
            Vector3[] primaries = { Vector3.right, Vector3.up, Vector3.forward };
            return primaries.OrderByDescending(color => (new Vector3(state.Red, state.Green, state.Blue) - color).sqrMagnitude).First();
        }
        private static int EncodeCustomColor(Vector3 color) => 1_000_000_000 +
            Mathf.RoundToInt(color.x * 255) * 1_000_000 + Mathf.RoundToInt(color.y * 255) * 1_000 + Mathf.RoundToInt(color.z * 255);
        private static bool MovedToward(PaintPanelState current, PaintPanelState before, Vector3 target)
        {
            Vector3 oldColor = new Vector3(before.Red, before.Green, before.Blue);
            Vector3 newColor = new Vector3(current.Red, current.Green, current.Blue);
            return (newColor - oldColor).sqrMagnitude > 0.0004f &&
                (target - newColor).sqrMagnitude < (target - oldColor).sqrMagnitude;
        }
        private static bool Same(PaintPanelState a, PaintPanelState b) => a.Encode().SequenceEqual(b.Encode());
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
