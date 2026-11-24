using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedEconomyTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Cash_Manager cash;
        private readonly float earned, spent;
        private int phase, acknowledgment, repairs;
        private float next;
        public string Status => "economy phase " + phase;
        internal static void Prepare(bool host)
        {
            UniStormSystem.Instance.TimeFlow = UniStormSystem.EnableFeature.Disabled;
            var cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            cash.LoadSaveSystem(host ? 100000 : 420, host ? new[] { 12.5f, 25f, 37.5f, 50f, 62.5f } : new[] { 1f, 2f, 3f, 4f, 5f });
            // Give the guest a distinct native spending baseline.
            if (!host) cash.Event_Spent(1f, false);
        }
        internal PairedEconomyTest(MonMPPlugin plugin)
        {
            this.plugin = plugin; cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            earned = GameManager.Get_PlayerStats[3]; spent = GameManager.Get_PlayerStats[4];
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1) acknowledgment = Math.Max(acknowledgment, payload[0]); }
        private void Signal(byte value) => plugin.World!.Channel.Request(ClockState.Entity, new[] { value });
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException("Economy test: " + reason); }
        private void Check(SharedEconomy economy)
        {
            var state = economy.TestState;
            Require(cash.GetCash == state.CashCents && cash.GetWeekCash.SequenceEqual(state.SundayPayments), "Native ledger differs from accepted host state.");
            Require(GameManager.Get_PlayerStats[3] == earned && GameManager.Get_PlayerStats[4] == spent, "Guest snapshot replayed native earning/spending effects.");
            var ui = UnityEngine.Object.FindObjectOfType<UI_ShowCash>();
            var label = AccessTools.Field(typeof(UI_ShowCash), "cashText").GetValue(ui);
            string text = (string)AccessTools.Property(label.GetType(), "text").GetValue(label);
            string expected = (state.CashCents / 100.0).ToString("0.00").Replace(',', '.') + " $";
            // A running native delta animation appends its "+x $" badge after the current total.
            Require(text == expected || text.StartsWith(expected + " <size=", StringComparison.Ordinal), "Native cash display is stale: '" + text + "', expected '" + expected + "'.");
        }
        internal bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 5) { plugin.BeginPairedTestSession(); phase = 6; return false; }
            var world = plugin.World; if (world?.Channel.FullySynchronized != true) return false;
            var economy = world.Economy;
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0 && acknowledgment >= 1)
                {
                    cash.Event_Spent(12.34f, false); cash.Event_Received(10.75f); cash.Event_Received_NowAndEachSunday(2.25f);
                    AccessTools.Method(typeof(Cash_Manager), "NewDay").Invoke(cash, new object[] { 6 });
                    Require(cash.GetCash == 101366 && cash.GetWeekCash.SequenceEqual(new[] { 27.25f, 39.75f, 52.25f, 64.75f, 0 }), "Host native rounding or Sunday shift was changed.");
                    Require(GameManager.Get_PlayerStats[3] == earned + 2600 && GameManager.Get_PlayerStats[4] == spent + 1234, "Host native effects missing."); phase = 1;
                }
                else if (phase == 1 && acknowledgment >= 2) phase = 2;
                else if (phase == 2 && acknowledgment >= 3)
                { cash.Event_Received(1); phase = 3; next = Time.unscaledTime + 0.5f; }
                else if (phase == 3) { world.Channel.CommandRequested -= Acknowledge; return true; }
                return false;
            }
            if (phase == 0)
            {
                Require(economy.TestState.CashCents == 100000 && economy.TestState.SundayPayments[4] == 62.5f, "Host ledger missing from late join."); Check(economy);
                cash.Event_Spent(100); cash.Event_Received(500); cash.Event_Received_NowAndEachSunday(250);
                cash.HardMode_NewSave(); cash.LoadSaveSystem(999, new float[5]);
                AccessTools.Method(typeof(Cash_Manager), "NewDay").Invoke(cash, new object[] { 6 });
                Require(cash.TryToBuy(null!, -1, null!) == null && !cash.isCanAfford(1), "Guest native purchase path remained active.");
                cash.TryToBuy_Vehicle(null!); Check(economy); Signal(1); phase = 1;
            }
            else if (phase == 1 && economy.TestState.CashCents == 101366)
            {
                Check(economy); Require(cash.GetWeekCash.SequenceEqual(new[] { 27.25f, 39.75f, 52.25f, 64.75f, 0 }), "Sunday payment was not shifted once.");
                AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(cash, 9); cash.GetWeekCash[0] = 9;
                UnityEngine.Object.FindObjectOfType<UI_ShowCash>().UpdateCash(9, 1f);
                repairs = world.Channel.RepairsApplied;
                Require(world.Channel.RequestResync(SnapshotScope.Section(EconomyState.Adapter), "Economy repair without payment replay"), "Repair request failed."); phase = 2;
            }
            else if (phase == 2 && world.Channel.RepairsApplied > repairs)
            { Check(economy); Signal(2); phase = 3; next = Time.unscaledTime + 6; }
            else if (phase == 3)
            {
                Check(economy); plugin.EndPairedTestSession();
                Require(economy.TestRestored && cash.GetCash == 320 && cash.GetWeekCash[0] == 1, "Offline cash/payout baseline not restored.");
                cash.Event_Received(2.75f); Require(cash.GetCash == 520 && cash.isCanAfford(1), "Offline native cash controls remain blocked.");
                // Restore counters only in this fixture so the next Check still detects replay.
                GameManager.Get_PlayerStats[3] = earned; phase = 5; next = Time.unscaledTime + 1;
            }
            else if (phase == 6)
            {
                Require(economy.TestState.CashCents == 101366, "Reconnect lost host ledger."); Check(economy); Signal(3); phase = 7;
            }
            else if (phase == 7)
            { if (economy.TestState.CashCents == 101466) { Check(economy); return true; } Signal(3); next = Time.unscaledTime + 0.3f; }
            return false;
        }
    }
}
