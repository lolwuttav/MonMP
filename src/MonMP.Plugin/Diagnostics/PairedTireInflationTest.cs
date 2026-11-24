using System;
using System.Linq;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Real scene hose and real shared loose wheel; pressure/air writes happen only on the host.
    internal sealed class PairedTireInflationTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Vector3 originalPosition;
        private readonly Quaternion originalCameraRotation;
        private readonly float started = Time.unscaledTime;
        private ulong wheel, toolId;
        private Tool_Air_TirePump? tool;
        private float originalAir, stoppedPressure, finalTarget, until;
        private int phase, aimAttempt;
        private bool inputDown, hostObserved, heardInflate, heardDeflate, sawRemoteEquip;
        private uint guestPeer, firstGrant;
        private string rayStatus = "not sampled";
        internal string Status => "tire inflation phase=" + phase + "; wheel=" + wheel + "; tool=" + toolId +
            "; aim=" + aimAttempt + "; input=" + inputDown + "; hostObserved=" + hostObserved +
            "; accepted=" + (plugin.World?.TirePressure.TestAccepted ?? -1) + "; rejected=" + (plugin.World?.TirePressure.TestRejected ?? -1) + "; ray=" + rayStatus;
        internal PairedTireInflationTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            originalPosition = gameplay.PlayerWalking.transform.position;
            originalCameraRotation = gameplay.Player.mainCamera.transform.rotation;
        }
        internal bool Tick()
        {
            if (Time.unscaledTime - started > 100) throw new InvalidOperationException("Tire inflation stalled: " + Status);
            if (plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }
        private bool Host()
        {
            var pressure = plugin.World!.TirePressure;
            var items = plugin.World.Items;
            if (phase > 0 && guestPeer != 0)
            {
                var cue = TirePumpPresentation.TestActor(guestPeer);
                if (cue.Playing && TirePumpPresentation.TestKind(guestPeer) == TirePumpPresentation.Inflate) heardInflate = true;
                if (cue.Playing && TirePumpPresentation.TestKind(guestPeer) == TirePumpPresentation.Deflate) heardDeflate = true;
                var equipment = plugin.World!.SceneAirEquipment;
                if (equipment.TestHolder(toolId) == guestPeer && equipment.TestPresented(guestPeer))
                {
                    Require(equipment.TestPresentationOnly(guestPeer) && equipment.TestNativeHidden(toolId), "Remote nozzle is not an isolated mesh presentation.");
                    sawRemoteEquip = true;
                    if (firstGrant == 0) firstGrant = pressure.TestAirGeneration(guestPeer, toolId);
                }
            }
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                toolId = pressure.TestFirstAirTool;
                Require(toolId != 0, "No native scene air hose is registered.");
                guestPeer = plugin.Session!.Peers.Keys.First(peer => peer != 0);
                gameplay.PlayerWalking.transform.position = pressure.TestAirCompressor(toolId).transform.position + Vector3.right * 4;
                var hostRig = gameplay.PlayerWalking.GetComponent<Rigidbody>();
                if (hostRig != null) { hostRig.position = gameplay.PlayerWalking.transform.position; hostRig.velocity = Vector3.zero; }
                originalAir = pressure.TestAirCompressor(toolId).Get_AirPressure;
                Require(originalAir > 30, "Native compressor lacks air for this fixture.");
                wheel = items.TestSpawnInflationWheel(pressure.TestAirCompressor(toolId).transform.position + Vector3.right * 2 + Vector3.up);
                phase = 1;
            }
            if (phase == 1 && pressure.TestWheel(wheel).Value1 >= 24)
            {
                Require(pressure.TestAccepted >= 4 && pressure.TestAirCompressor(toolId).Get_AirPressure < originalAir,
                    "Host did not accept real inflation pulses and debit native compressor air.");
                Require(items.TestState(wheel).Values.Value1 >= 24, "Host pressure result was not published through the loose-wheel record.");
                var guest = plugin.Session!.Peers.Keys.First(peer => peer != 0);
                Require(firstGrant != 0 && pressure.TestAirGeneration(guest, toolId) != firstGrant, "No fresh scene grant after nozzle reuse.");
                Require(pressure.TestRejectWrongAirLease(guest, toolId, wheel, firstGrant), "Old released nozzle grant was accepted after reacquire.");
                Require(pressure.TestRejectWrongAirLease(guest, toolId, wheel), "Forged scene-nozzle lease was not rejected without pressure/air mutation.");
                Debug.Log("MONMP_TIRE_INFLATION: host loose-wheel native pressure=" + pressure.TestWheel(wheel).Value1 +
                    "; air=" + pressure.TestAirCompressor(toolId).Get_AirPressure + "; accepted=" + pressure.TestAccepted);
                Require(heardInflate && sawRemoteEquip, "Host did not observe the accepted remote inflation sound/nozzle presentation.");
                hostObserved = true; phase = 2;
            }
            if (phase == 2 && plugin.Session!.Peers.Count == 1)
            {
                Require(hostObserved && heardDeflate, "Host did not observe remote inflation and deflation cues.");
                Require(pressure.TestWheel(wheel).Value1 < 24 && items.TestState(wheel).Values.Value1 < 24, "Real guest deflation did not reach the host and authoritative loose-wheel record.");
                var cue = TirePumpPresentation.TestActor(guestPeer);
                Require(!cue.Playing && cue.Starts >= 3 && cue.Stops >= 3, "Remote pump loops survived release/departure or reuse did not restart a loop.");
                Require(plugin.World!.SceneAirEquipment.TestHolder(toolId) == uint.MaxValue && plugin.World.SceneAirEquipment.TestNativeFlagsRestored, "Scene hose holder/render flags survived release.");
                items.TestDestroy(wheel);
                pressure.TestAirCompressor(toolId).Get_AirPressure = originalAir;
                Restore(); phase = 3;
            }
            return phase == 3 && !items.TestState(wheel).Exists;
        }
        private bool Guest()
        {
            var pressure = plugin.World!.TirePressure;
            var items = plugin.World.Items;
            if ((phase == 2 || phase == 6 || phase == 8) && !Aim(pressure)) return false;
            if (phase == 0)
            {
                toolId = pressure.TestFirstAirTool;
                wheel = items.TestNewest(typeof(Part_Driveline_Wheel));
                if (toolId == 0 || wheel == 0 || items.TestItem(wheel) == null || Mathf.Abs(items.TestState(wheel).Values.Value1 - 20) > .01f) return false;
                tool = pressure.TestAirTool(toolId);
                originalAir = pressure.TestAirCompressor(toolId).Get_AirPressure;
                if (!Aim(pressure)) return false;
                gameplay.Player.EquipThisTool(tool);
                phase = 1; until = Time.unscaledTime + 5f;
            }
            if (phase == 1)
            {
                Require(tool != null && tool.isEquiped && gameplay.Player.Get_ActiveTool == tool, "Native scene nozzle did not equip.");
                bool granted = plugin.World!.SceneAirEquipment.TestHolder(toolId) == plugin.Session!.LocalPeerId && pressure.TestAirGeneration(plugin.Session.LocalPeerId, toolId) != 0;
                Require(granted || Time.unscaledTime < until, "Native equipped nozzle did not receive an accepted scene grant.");
                if (!granted || !Aim(pressure)) return false;
                tool!.MouseDown(); inputDown = true; phase = 2;
            }
            if (phase == 2 && pressure.TestWheel(wheel).Value1 >= 22 && items.TestState(wheel).Values.Value1 >= 22)
            {
                tool!.MouseUp(); inputDown = false;
                AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool").Invoke(gameplay.Player, null);
                Require(!tool.isEquiped && gameplay.Player.Get_ActiveTool != tool, "Native nozzle unequip did not return the hose.");
                stoppedPressure = pressure.TestWheel(wheel).Value1;
                until = Time.unscaledTime + .8f; phase = 3;
            }
            if (phase == 3 && Time.unscaledTime >= until)
            {
                // An already in-flight valid pulse may arrive after release; wait for settlement
                // before checking that no further native input continues to pump.
                stoppedPressure = pressure.TestWheel(wheel).Value1;
                until = Time.unscaledTime + .5f; phase = 4;
            }
            if (phase == 4 && Time.unscaledTime >= until)
            {
                Require(Mathf.Abs(pressure.TestWheel(wheel).Value1 - stoppedPressure) < .01f, "Inflation continued after native release/unequip.");
                finalTarget = Mathf.Max(24, stoppedPressure + 2);
                gameplay.Player.EquipThisTool(tool!);
                phase = 5; until = Time.unscaledTime + 5f;
            }
            if (phase == 5)
            {
                Require(tool != null && tool.isEquiped && gameplay.Player.Get_ActiveTool == tool, "Native nozzle could not be reused.");
                bool granted = plugin.World!.SceneAirEquipment.TestHolder(toolId) == plugin.Session!.LocalPeerId && pressure.TestAirGeneration(plugin.Session.LocalPeerId, toolId) != 0;
                Require(granted || Time.unscaledTime < until, "Native equipped nozzle did not receive an accepted scene grant.");
                if (!granted || !Aim(pressure)) return false;
                tool!.MouseDown(); inputDown = true; phase = 6;
            }
            if (phase == 6 && pressure.TestWheel(wheel).Value1 >= finalTarget && items.TestState(wheel).Values.Value1 >= finalTarget)
            {
                Require(pressure.TestAirCompressor(toolId).Get_AirPressure < originalAir, "Guest did not receive authoritative compressor debit.");
                Debug.Log("MONMP_TIRE_INFLATION: guest real nozzle reuse; loose pressure=" + pressure.TestWheel(wheel).Value1 + "; air=" + pressure.TestAirCompressor(toolId).Get_AirPressure);
                tool!.MouseUp(); inputDown = false;
                phase = 7; until = Time.unscaledTime + .8f;
            }
            if (phase == 7 && Time.unscaledTime >= until)
            {
                if (!Aim(pressure)) return false;
                tool!.MouseRightDown(); inputDown = true; phase = 8;
            }
            if (phase == 8 && pressure.TestWheel(wheel).Value1 <= 22 && items.TestState(wheel).Values.Value1 <= 22)
            {
                tool!.MouseRightUp(); inputDown = false; StopTool(); Restore();
                phase = 9; until = Time.unscaledTime + 1.2f;
            }
            if (phase == 9 && Time.unscaledTime >= until) { plugin.EndPairedTestSession(); return true; }
            return false;
        }
        private bool Aim(SharedTirePressure pressure)
        {
            var native = pressure.TestWheel(wheel);
            var target = native.itemCollider.bounds.center;
            var sides = new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            for (int i = 0; i < sides.Length; i++)
            {
                var location = target + sides[(aimAttempt + i) % sides.Length] * 1.1f - Vector3.up;
                gameplay.PlayerWalking.transform.position = location;
                var rig = gameplay.PlayerWalking.GetComponent<Rigidbody>();
                if (rig != null) { rig.position = location; rig.velocity = Vector3.zero; }
                var camera = gameplay.Player.mainCamera;
                camera.transform.rotation = Quaternion.LookRotation(target - camera.transform.position);
                Physics.SyncTransforms();
                var ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
                int mask = LayerMask.GetMask("InteractableItem") | LayerMask.GetMask("PartSocket");
                string hitName = Physics.Raycast(ray, out var hit, 2.4f, mask) ? hit.collider.name : "none";
                rayStatus = "target=" + target.ToString("F2") + "; camera=" + camera.transform.position.ToString("F2") +
                    "; distance=" + Vector3.Distance(target, camera.transform.position).ToString("F2") + "; hit=" + hitName +
                    "; wheel=" + native.transform.position.ToString("F2");
                if (pressure.TestCameraHits(camera, wheel)) { aimAttempt += i; rayStatus += "; exact=True"; return true; }
                rayStatus += "; exact=False";
            }
            Require(++aimAttempt < 20, "Native ray could not acquire this exact shared loose wheel.");
            return false;
        }
        private void StopTool()
        {
            if (tool == null) return;
            tool.MouseUp(); tool.MouseRightUp(); inputDown = false;
            if (gameplay != null && gameplay.Player != null && gameplay.Player.Get_ActiveTool == tool)
                AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool").Invoke(gameplay.Player, null);
        }
        private void Restore()
        {
            if (gameplay == null || gameplay.PlayerWalking == null) return;
            gameplay.PlayerWalking.transform.position = originalPosition;
            var rig = gameplay.PlayerWalking.GetComponent<Rigidbody>();
            if (rig != null) { rig.position = originalPosition; rig.velocity = Vector3.zero; }
            if (gameplay.Player != null) gameplay.Player.mainCamera.transform.rotation = originalCameraRotation;
        }
        internal void Cleanup() { StopTool(); Restore(); }
        private void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Tire inflation: " + message + "; " + Status); }
    }
}
