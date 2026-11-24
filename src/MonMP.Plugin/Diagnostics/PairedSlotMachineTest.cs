using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedSlotMachineTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedSlotMachines shared;
        private readonly Cash_Manager cash;
        private readonly ulong machineId;
        private readonly Guid distant = Guid.NewGuid();
        private readonly Guid duplicate = Guid.NewGuid();
        private readonly Guid insufficient = Guid.NewGuid();
        private readonly Guid spin = Guid.NewGuid();
        private readonly Guid refill = Guid.NewGuid();
        private readonly Guid cashOut = Guid.NewGuid();
        private int phase;
        private float next, deadline;
        private float cashOutDeadline;
        private int spinCredit;
        private bool sawSpin;

        internal string Status => "slot machine phase=" + phase + "; cash=" + cash.GetCash + "; state=" + shared.TestState(machineId).Bet + "/" + shared.TestState(machineId).Credit + "; paid=" + shared.TestState(machineId).WinnerPaid + "; loss=" + shared.TestState(machineId).LossAmount + "; spinning=" + shared.TestState(machineId).Spinning + "; revision=" + shared.TestRevision(machineId) + "; last=" + shared.TestLastReply?.Operation + "/" + shared.TestLastReply?.Status + "; applied=" + shared.TestAppliedCommands + "; duplicate=" + shared.TestDuplicateCommands + "; rejected=" + shared.TestRejectedCommands;

        internal static void Prepare(bool host)
        {
            if (UniStormSystem.Instance != null) UniStormSystem.Instance.TimeFlow = UniStormSystem.EnableFeature.Disabled;
            UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(host ? 100000 : 420, new float[5]);
            if (!host) return;
            var storeField = AccessTools.Field(typeof(SlotMachine), "StoreBar");
            foreach (var machine in UnityEngine.Object.FindObjectsOfType<SlotMachine>(true))
            {
                machine.ResetMachine();
                var store = storeField.GetValue(machine) as Store_Bar;
                if (store != null) store.isOpen = true;
            }
        }

        internal PairedSlotMachineTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; shared = plugin.World!.SlotMachines;
            cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            machineId = shared.TestMachines.FirstOrDefault(id => HasButton(id, SlotMachine_Button.ButtonType.CashIn10) &&
                HasButton(id, SlotMachine_Button.ButtonType.CashIn1000) && HasButton(id, SlotMachine_Button.ButtonType.Spin) &&
                HasButton(id, SlotMachine_Button.ButtonType.CashOut));
            Require(machineId != 0, "native slot machine with the required buttons is missing.");
            if (plugin.TestOptions.IsHost)
                Require(shared.TestState(machineId).Bet == 10 && shared.TestState(machineId).Credit == 0 && !shared.TestState(machineId).Spinning,
                    "native machine did not reset to its $10 bet and empty credit baseline.");
        }

        private bool HasButton(ulong id, SlotMachine_Button.ButtonType type) =>
            shared.TestNative(id).GetComponentsInChildren<SlotMachine_Button>(true).Any(button =>
                (SlotMachine_Button.ButtonType)AccessTools.Field(typeof(SlotMachine_Button), "Button_Type").GetValue(button)! == type);

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 120;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Slot machine test stalled: " + Status);
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            var state = shared.TestState(machineId);
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                Require(cash.GetCash == 100000, "host test wallet did not start at exactly $1,000.");
                phase = 1;
            }
            else if (phase == 1 && shared.TestRejectedCommands >= 1) phase = 2;
            else if (phase == 2 && shared.TestAppliedCommands >= 2 && shared.TestDuplicateCommands >= 1 && shared.TestRejectedCommands >= 2)
            {
                Require(cash.GetCash == 98000 && state.Credit == 20 && state.Bet == 10,
                    "native guest cash-in or its duplicate replay changed wallet/credit by the wrong amount.");
                phase = 3;
            }
            else if (phase == 3)
            {
                if (state.Spinning) sawSpin = true;
                if (sawSpin && !state.Spinning && (state.LossAmount > 0 || state.WinnerPaid > 0))
                {
                    spinCredit = state.Credit;
                    phase = 4;
                }
            }
            else if (phase == 4 && shared.TestAppliedCommands >= 4 && state.Credit == 0)
            {
                Require(cash.GetCash == 97000 + (spinCredit + 10) * 100,
                    "cash-in after spin or cash-out did not settle the machine payout exactly once.");
                phase = 5;
            }
            else if (phase == 5 && plugin.Session!.Peers.Count == 1)
            {
                Require(shared.TestAppliedCommands == 5 && shared.TestDuplicateCommands == 1 && shared.TestRejectedCommands >= 2 &&
                    state.Credit == 0 && !state.Spinning,
                    "duplicate, distant, insufficient-funds or spin settlement checks changed the final native state.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var state = shared.TestState(machineId);
            var cashButton = shared.TestButton(machineId, SlotMachine_Button.ButtonType.CashIn10);
            if (phase == 0)
            {
                Require(cash.GetCash == 100000 && state.Credit == 0,
                    "the host wallet and empty slot machine state did not reach the guest at join.");
                Move(cashButton.position + Vector3.right * 30);
                phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1)
            {
                shared.TestRequest(machineId, distant, SlotMachineAction.CashIn10);
                phase = 2;
            }
            else if (phase == 2 && shared.TestLastReply?.Operation == distant)
            {
                Require(shared.TestLastReply.Status == SlotMachineReplyStatus.Rejected && cash.GetCash == 100000 && state.Credit == 0,
                    "a distant cash-in changed the host wallet or machine credit.");
                Move(cashButton.position + Vector3.up);
                phase = 3; next = Time.unscaledTime + 1;
            }
            else if (phase == 3)
            {
                // Exercise the actual guest Harmony entry point, then replay a fixed second operation.
                shared.TestNative(machineId).ButtonPressed(SlotMachine_Button.ButtonType.CashIn10);
                shared.TestRequest(machineId, duplicate, SlotMachineAction.CashIn10);
                shared.TestRequest(machineId, duplicate, SlotMachineAction.CashIn10);
                phase = 4;
            }
            else if (phase == 4 && cash.GetCash == 98000 && state.Credit == 20)
            {
                shared.TestRequest(machineId, insufficient, SlotMachineAction.CashIn1000);
                phase = 5;
            }
            else if (phase == 5 && shared.TestLastReply?.Operation == insufficient)
            {
                Require(shared.TestLastReply.Status == SlotMachineReplyStatus.Rejected && cash.GetCash == 98000 && state.Credit == 20,
                    "an unaffordable $1,000 cash-in changed the native wallet or machine credit.");
                shared.TestRequest(machineId, spin, SlotMachineAction.Spin);
                phase = 6;
            }
            else if (phase == 6 && state.Spinning) phase = 7;
            else if (phase == 7 && !state.Spinning && (state.LossAmount > 0 || state.WinnerPaid > 0))
            {
                Require(shared.TestReelsShown(machineId), "the guest's reels did not stop on the host's spin result.");
                spinCredit = state.Credit;
                Move(cashButton.position + Vector3.up);
                phase = 8; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 8)
            {
                shared.TestRequest(machineId, refill, SlotMachineAction.CashIn10);
                phase = 9;
            }
            else if (phase == 9 && shared.TestLastReply?.Operation == refill && state.Credit >= 10 && cash.GetCash == 97000)
            {
                shared.TestRequest(machineId, cashOut, SlotMachineAction.CashOut);
                phase = 10;
            }
            else if (phase == 10 && shared.TestLastReply?.Operation == cashOut)
            {
                if (shared.TestLastReply.Status != SlotMachineReplyStatus.Applied)
                    throw new InvalidOperationException("Slot machine cash-out was rejected: status=" + shared.TestLastReply.Status + "; " + Status);
                int expected = 97000 + (spinCredit + 10) * 100;
                if (state.Credit != 0 || cash.GetCash != expected)
                {
                    if (cashOutDeadline == 0) cashOutDeadline = Time.unscaledTime + 5;
                    if (Time.unscaledTime < cashOutDeadline) return false;
                    throw new InvalidOperationException("Slot machine cash-out did not converge within five seconds: expectedCash=" + expected + "; actualCash=" + cash.GetCash + "; expectedCredit=0; " + Status);
                }
                plugin.EndPairedTestSession();
                Require(cash.GetCash == 420 && plugin.World == null && shared.TestRestored,
                    "guest cash or slot machine baseline was not restored on disconnect.");
                return true;
            }
            return false;
        }

        private void Move(Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Slot machine test: " + reason); }
    }
}
