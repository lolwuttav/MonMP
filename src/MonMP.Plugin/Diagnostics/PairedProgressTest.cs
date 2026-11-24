using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedProgressTest
    {
        private readonly MonMPPlugin plugin;
        private readonly ToDoList_Manager manager;
        private int phase, ack, repairs;
        private float next;
        public string Status => "progression phase " + phase;
        private static GameObject Popup() => (GameObject)AccessTools.Field(typeof(UI_FrontLeftPopup), "ToDoList").GetValue(UnityEngine.Object.FindObjectOfType<UI_FrontLeftPopup>());
        internal static void Prepare(bool host)
        {
            UniStormSystem.Instance.TimeFlow = UniStormSystem.Instance.RealWorldTime = UniStormSystem.EnableFeature.Disabled;
            var manager = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            foreach (var line in manager.Get_Lines.Values) { line.value = false; line.UpdateStrike(); }
            var groups = (List<ToDoList_Object>)AccessTools.Field(typeof(ToDoList_Manager), "ToDoListsPerType").GetValue(manager);
            for (int i = 0; i < groups.Count; i++) groups[i].gameObject.SetActive(i < 3);
            manager.CheckLineFromSaveSystem((host ? ToDoList_Line.Debut_UseTheAdjustableSpanner : ToDoList_Line.Garden_FindSomeSeeds).ToString());
            Popup().SetActive(false);
        }
        internal PairedProgressTest(MonMPPlugin plugin)
        {
            this.plugin = plugin; manager = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1) ack = Math.Max(ack, payload[0]); }
        private void Signal(byte stage) => plugin.World!.Channel.Request(ClockState.Entity, new[] { stage });
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException("Progress test: " + reason); }
        private static object Label(ToDoList_LineDetails line) => AccessTools.Field(typeof(ToDoList_LineDetails), "UI_text").GetValue(line);
        private void CheckPresentation()
        {
            foreach (var line in manager.Get_Lines.Values)
            {
                object label = Label(line); string text = (string)AccessTools.Property(label.GetType(), "text").GetValue(label);
                Require(text.StartsWith(line.value ? "<b>V</b> <s>" : "  ", StringComparison.Ordinal), "Native list strike/label state is stale.");
            }
            // Guests show the to-do popup for lines the host completes (SharedProgress.Checked); native
            // completion itself stays blocked, which phase 0 checks.
            Popup().SetActive(false);
            var groups = (List<ToDoList_Object>)AccessTools.Field(typeof(ToDoList_Manager), "ToDoListsPerType").GetValue(manager);
            Require(groups.Select(g => g.gameObject.activeSelf).SequenceEqual(plugin.World!.Progress.TestState.VisibleGroups), "Native category visibility differs from host state.");
        }
        internal bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 4) { plugin.BeginPairedTestSession(); phase = 5; return false; }
            var world = plugin.World; if (world?.Channel.FullySynchronized != true) return false;
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0 && ack >= 1)
                {
                    // Native save import changes completion and categories without touching account achievements.
                    manager.CheckLineFromSaveSystem(ToDoList_Line.Garden_FindSomeSeeds.ToString());
                    manager.CheckLineFromSaveSystem(ToDoList_Line.Dealer_Unlock.ToString()); phase = 1;
                }
                else if (phase == 1 && ack >= 2)
                { manager.CheckLineFromSaveSystem(ToDoList_Line.Debut_SellWoodToBrother.ToString()); phase = 2; next = Time.unscaledTime + 0.5f; }
                else if (phase == 2) { world.Channel.CommandRequested -= Acknowledge; return true; }
                return false;
            }
            if (phase == 0)
            {
                Require(manager.isDone(ToDoList_Line.Debut_UseTheAdjustableSpanner) && !manager.isDone(ToDoList_Line.Garden_FindSomeSeeds), "Late join did not replace distinct guest progression.");
                manager.Debut_TalkedToBrother(); manager.CheckLineFromSaveSystem(ToDoList_Line.Debut_TalkedToBrother.ToString());
                manager.Get_Lines[ToDoList_Line.Debut_TalkedToBrother].CheckLine();
                Require(!manager.isDone(ToDoList_Line.Debut_TalkedToBrother), "Guest native completion/import/direct line was not blocked.");
                manager.LocalizationChanged(); CheckPresentation(); Signal(1); phase = 1;
            }
            else if (phase == 1 && manager.isDone(ToDoList_Line.Dealer_Unlock))
            {
                Require(manager.isDone(ToDoList_Line.Garden_FindSomeSeeds), "Host progression update missing."); CheckPresentation();
                var label = Label(manager.Get_Lines[ToDoList_Line.Dealer_Unlock]); AccessTools.Property(label.GetType(), "text").SetValue(label, "damaged");
                repairs = world.Channel.RepairsApplied;
                Require(world.Channel.RequestResync(SnapshotScope.Section(ProgressState.Adapter), "ToDo UI repair without native completion"), "Repair request failed."); phase = 2;
            }
            else if (phase == 2 && world.Channel.RepairsApplied > repairs)
            {
                CheckPresentation(); var progress = world.Progress; plugin.EndPairedTestSession();
                Require(progress.TestRestored && manager.isDone(ToDoList_Line.Garden_FindSomeSeeds) && !manager.isDone(ToDoList_Line.Debut_UseTheAdjustableSpanner) && !manager.isDone(ToDoList_Line.Dealer_Unlock), "Offline progression baseline not restored.");
                manager.CheckLineFromSaveSystem(ToDoList_Line.Debut_TalkedToBrother.ToString()); Require(manager.isDone(ToDoList_Line.Debut_TalkedToBrother), "Offline native import remained blocked.");
                phase = 4; next = Time.unscaledTime + 1;
            }
            else if (phase == 5)
            {
                Require(manager.isDone(ToDoList_Line.Dealer_Unlock) && !manager.isDone(ToDoList_Line.Debut_TalkedToBrother), "Reconnect lost canonical progression."); CheckPresentation(); Signal(2); phase = 6;
            }
            else if (phase == 6 && manager.isDone(ToDoList_Line.Debut_SellWoodToBrother)) { CheckPresentation(); return true; }
            return false;
        }
    }
}
