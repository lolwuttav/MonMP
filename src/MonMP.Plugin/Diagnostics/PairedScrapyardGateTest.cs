using System;
using System.Collections.Generic;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Runs against isolated test saves. It verifies host-side police/friendship guards,
    // guest walking range, native opening animation and restoration of the guest baseline.
    internal sealed class PairedScrapyardGateTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedScrapyardGate gate;
        private int phase;
        private int refusalTarget;
        private float next, deadline;
        private bool sawDust, sawMotion;
        internal string Status => "scrapyard-gate phase=" + phase + "; requests=" + gate.TestRequests +
            "; rejects=" + gate.TestLastRejectReason + "; pose-age=" + gate.TestLastPoseAge.ToString("F2") +
            "; pose-state=" + gate.TestLastPoseState + "; distance=" + gate.TestLastDistance.ToString("F2") +
            "; missing/stale/walking/range/save=" + gate.TestMissingPose + "/" + gate.TestStalePose + "/" +
            gate.TestNonWalkingPose + "/" + gate.TestOutOfRange + "/" + gate.TestSaveBlocked +
            "; host-denials=" + gate.TestHostCriminalDenials + "/" + gate.TestHostFriendshipDenials +
            "; refusals=" + gate.TestRefusals;

        internal static void Prepare(Gameplay gameplay, bool host)
        {
            if (!host) return;
            var gate = UnityEngine.Object.FindObjectOfType<Scrapyard>() ?? throw new InvalidOperationException("Scrapyard gate test requires the native scrapyard.");
            var manager = Singleton<ToDoList_Manager>.i;
            var line = manager.Get_Lines[ToDoList_Line.Intermediate_ScrapyardGateIsOpen];
            if (line.value) gate.CloseDoor();

            // Local test instances use private copies of the game save; set a closed, first-access
            // scenario so the fixture can exercise the actual native gate and progression code.
            var left = (Transform)AccessTools.Field(typeof(Scrapyard), "FenceDoorLeft").GetValue(gate);
            var right = (Transform)AccessTools.Field(typeof(Scrapyard), "FenceDoorRight").GetValue(gate);
            left.localRotation = (Quaternion)AccessTools.Field(typeof(Scrapyard), "Left_OriginalRotation").GetValue(gate);
            right.localRotation = (Quaternion)AccessTools.Field(typeof(Scrapyard), "Right_OriginalRotation").GetValue(gate);
            ((GameObject)AccessTools.Field(typeof(Scrapyard), "Collider_Locked").GetValue(gate)).SetActive(true);
            ((GameObject)AccessTools.Field(typeof(Scrapyard), "Collider_Normal").GetValue(gate)).SetActive(false);
            ((GameObject)AccessTools.Field(typeof(Scrapyard), "DoorHandlePin").GetValue(gate)).SetActive(true);
            ((GameObject)AccessTools.Field(typeof(Scrapyard), "DustOnBuy").GetValue(gate)).SetActive(false);
            line.value = false;
            line.UpdateStrike();
            GameManager.Get_Friendship[7] = 24;

            var tickets = (List<PoliceTicketData>)AccessTools.Field(typeof(Store_PoliceInterceptor), "ActiveTickets").GetValue(gameplay.Store_PoliceInterceptor);
            tickets.RemoveAll(ticket => ticket.ConsequenceApplied);
            tickets.Add(new PoliceTicketData { TicketType = PoliceTicketData.Ticket_Type.Speeding, ConsequenceApplied = true });
        }

        internal PairedScrapyardGateTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            gate = plugin.World!.ScrapyardGate;
        }

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 75;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Scrapyard gate test stalled: " + Status);
            if (!gate.TestChannelAttached) throw new InvalidOperationException("Scrapyard gate world channel was lost: " + Status);
            if (!gate.TestRecordAvailable) return false;
            sawDust |= gate.TestRecord.Dust;
            sawMotion |= gate.TestOpenMoving;
            if (plugin.TestOptions.IsHost) return HostTick();
            return GuestTick();
        }

        private bool HostTick()
        {
            if (gate.TestHostCriminalDenials > 0 && gate.TestHasCriminalRecord) gate.TestSetCriminalRecord(false);
            if (gate.TestHostFriendshipDenials > 0 && gate.TestFriendship < 25) gate.TestSetFriendship(25);
            if (plugin.Session!.Peers.Count == 1 && gate.TestProgress && gate.TestAtOpenEndpoint && gate.TestSynchronized)
                return true;
            return false;
        }

        private bool GuestTick()
        {
            if (Time.unscaledTime < next || plugin.World?.PersonalProfileReady != true || plugin.World.Channel.FullySynchronized != true) return false;
            if (phase == 0)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking, "fixture guest must be walking.");
                Require(!gate.TestProgress && gate.TestNative.LockedCollider && !gate.TestNative.NormalCollider && gate.TestNative.Pin,
                    "late-join snapshot did not provide the host's closed first-access gate.");
                Require(gate.TestSynchronized, "initial gate state was not applied to native transforms and colliders.");
                var before = gate.TestNative;
                gate.TestGate.CloseDoor(); gate.TestGate.OpenDoorNoAnimation();
                Require(gate.TestNative.Equals(before), "guest native gate mutators changed local presentation.");
                Move(gate.TestPosition + Vector3.right * 25);
                phase = 1; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 1)
            {
                gate.TestGate.OpenDoor(); // Host must reject a stale pose that is far from the walking gate.
                phase = 2; next = Time.unscaledTime + 0.9f;
            }
            else if (phase == 2)
            {
                Require(!gate.TestProgress && gate.TestNative.LockedCollider && gate.TestSynchronized,
                    "distant guest gate request changed host state.");
                Move(gate.TestPosition + Vector3.up * 0.15f);
                // Let the host receive a fresh pose for the relocated walking guest before
                // sending the in-range request; world commands can overtake the next pose.
                next = Time.unscaledTime + 0.8f;
                phase = 3;
            }
            else if (phase == 3)
            {
                refusalTarget = gate.TestCriminalRefusals + 1;
                gate.TestGate.OpenDoor();
                phase = 4; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 4 && gate.TestCriminalRefusals >= refusalTarget)
            {
                Require(!gate.TestProgress && gate.TestNative.LockedCollider && gate.TestSynchronized,
                    "host accepted the gate request despite its native criminal-record guard.");
                refusalTarget = gate.TestFriendshipRefusals + 1;
                gate.TestGate.OpenDoor();
                phase = 5; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 5 && gate.TestFriendshipRefusals >= refusalTarget)
            {
                Require(!gate.TestProgress && gate.TestNative.LockedCollider,
                    "host did not enforce the native Jacques friendship threshold.");
                gate.TestGate.OpenDoor(); // The host raises friendship after observing this denial.
                phase = 6; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 6 && gate.TestProgress)
            {
                if (!gate.TestSynchronized) return false;
                Require(!gate.TestRecord.LockedCollider && gate.TestRecord.NormalCollider && !gate.TestRecord.Pin,
                    "host native OpenDoor did not publish the unlocked gate presentation.");
                phase = 7;
            }
            else if (phase == 7 && gate.TestAtOpenEndpoint && gate.TestProgress)
            {
                Require(sawMotion, "guest did not observe the native gate animation in progress.");
                Require(sawDust && gate.TestRecord.Dust, "opening dust presentation was not replicated.");
                Require(gate.TestSynchronized, "gate animation/collider/pin state did not converge.");
                var open = gate.TestNative;
                gate.TestGate.CloseDoor();
                Require(PresentationEquals(gate.TestNative, open), "guest-local close call bypassed host authority.");

                // CloseDoor is intentionally suppressed above. Put only this test copy into the
                // native closed presentation so OpenDoorNoAnimation has a visible forbidden effect.
                ForceClosedPresentation(gate.TestGate);
                var closed = gate.TestNative;
                gate.TestGate.OpenDoorNoAnimation();
                Require(PresentationEquals(gate.TestNative, closed), "guest-local no-animation call bypassed host authority.");
                plugin.EndPairedTestSession();
                Require(gate.TestRestored, "disconnect did not restore the guest's pre-session gate baseline.");
                return true;
            }
            return false;
        }

        private void Move(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static bool PresentationEquals(ScrapyardGateState left, ScrapyardGateState right)
        {
            var leftRotationA = new Quaternion(left.LeftX, left.LeftY, left.LeftZ, left.LeftW);
            var leftRotationB = new Quaternion(right.LeftX, right.LeftY, right.LeftZ, right.LeftW);
            var rightRotationA = new Quaternion(left.RightX, left.RightY, left.RightZ, left.RightW);
            var rightRotationB = new Quaternion(right.RightX, right.RightY, right.RightZ, right.RightW);
            return Quaternion.Angle(leftRotationA, leftRotationB) < 0.5f &&
                Quaternion.Angle(rightRotationA, rightRotationB) < 0.5f &&
                left.LockedCollider == right.LockedCollider && left.NormalCollider == right.NormalCollider &&
                left.Pin == right.Pin && left.Dust == right.Dust;
        }

        private static void ForceClosedPresentation(Scrapyard gate)
        {
            var left = (Transform)AccessTools.Field(typeof(Scrapyard), "FenceDoorLeft").GetValue(gate);
            var right = (Transform)AccessTools.Field(typeof(Scrapyard), "FenceDoorRight").GetValue(gate);
            left.localRotation = (Quaternion)AccessTools.Field(typeof(Scrapyard), "Left_OriginalRotation").GetValue(gate);
            right.localRotation = (Quaternion)AccessTools.Field(typeof(Scrapyard), "Right_OriginalRotation").GetValue(gate);
            ((GameObject)AccessTools.Field(typeof(Scrapyard), "Collider_Locked").GetValue(gate)).SetActive(true);
            ((GameObject)AccessTools.Field(typeof(Scrapyard), "Collider_Normal").GetValue(gate)).SetActive(false);
            ((GameObject)AccessTools.Field(typeof(Scrapyard), "DoorHandlePin").GetValue(gate)).SetActive(true);
        }

        private static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Scrapyard gate test: " + message); }
    }
}
