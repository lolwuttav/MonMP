using System;
using System.Linq;
using HarmonyLib;
using MonMP.Plugin.Patches;
using MonMP.Core.Persistence;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedInteractionAuditTest
    {
        private readonly MonMPPlugin plugin;
        private bool tested, acknowledged;
        internal string Status => "interaction audit; tested=" + tested + "; acknowledged=" + acknowledged;
        internal PairedInteractionAuditTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ProfileRestoreChannel.Entity && payload.SequenceEqual(new byte[] { 90 })) acknowledged = true; }
        internal bool Tick()
        {
            if (plugin.World?.PersonalProfileReady != true || !plugin.World.Channel.FullySynchronized) return false;
            if (!tested)
            {
                Verify(false); tested = true;
                if (!plugin.TestOptions.IsHost) plugin.World.Channel.Request(ProfileRestoreChannel.Entity, new byte[] { 90 });
            }
            if (plugin.TestOptions.IsHost)
            {
                if (acknowledged) UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1097, new float[5]);
                if (!acknowledged || plugin.Session!.Peers.Count != 1) return false;
            }
            else if (plugin.World.Economy.TestState.CashCents != 1097) return false;
            plugin.EndPairedTestSession(); Verify(true); return true;
        }
        private void Verify(bool offline)
        {
            if (!offline) NativeLightingGuardTest.Verify(plugin);
            var audit = plugin.InteractionAudit!;
            var fixture = new GameObject("MonMPPendingInteractionFixture"); fixture.SetActive(false);
            try
            {
                // Inactive fixture avoids Start/physics/audio. Its native MouseDown first
                // dereferences the deliberately absent vehicle; blocking must skip that body.
                var button = fixture.AddComponent<Vehicle_Button_HazardLights>();
                string name = typeof(Vehicle_Button_HazardLights).FullName!;
                int before = audit.Model.Entries.Where(e => e.Type == name && e.Action == "MouseDown").Select(e => e.Observed).SingleOrDefault();
                audit.TestStrictOverride = true;
                if (offline)
                {
                    ExpectNativeBody(button);
                    Require(audit.Model.Entries.Single(e => e.Type == name && e.Action == "MouseDown").Observed == before,
                        "Offline interaction entered multiplayer diagnostics.");
                }
                else
                {
                    InteractionAuditDispatch.MouseDown(button);
                    var blocked = audit.Model.Entries.Single(e => e.Type == name && e.Action == "MouseDown");
                    Require(blocked.Observed == before + 1 && blocked.Blocked > 0, "Strict pending interaction did not reach the installed prefix.");
                    var player = UnityEngine.Object.FindObjectOfType<Gameplay>().Player;
                    var target = AccessTools.Field(typeof(PlayerInteractable), "currentInteractable");
                    var itemTarget = AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem");
                    object prior = target.GetValue(player), priorItem = itemTarget.GetValue(player);
                    try
                    {
                        target.SetValue(player, button); itemTarget.SetValue(player, null);
                        AccessTools.Method(typeof(PlayerInteractable), "UseKeyDown").Invoke(player, null);
                        Require(audit.Model.Entries.Any(e => e.Type == name && e.Action == "Use" && e.Blocked > 0), "Native input dispatch bypassed strict audit.");
                        Require(!InteractionAuditDispatch.Use(button), "Blocked bool interaction did not return its safe default.");
                    }
                    finally { target.SetValue(player, prior); itemTarget.SetValue(player, priorItem); }
                    audit.TestStrictOverride = false; ExpectNativeBody(button);
                    Require(audit.Model.Entries.Single(e => e.Type == name && e.Action == "MouseDown").Observed == before + 2,
                        "Observe-only mode did not record the native call.");
                }
            }
            finally { audit.TestStrictOverride = null; audit.Flush(); UnityEngine.Object.Destroy(fixture); }
        }
        private static void ExpectNativeBody(Vehicle_Button_HazardLights button)
        {
            try { InteractionAuditDispatch.MouseDown(button); }
            catch (NullReferenceException) { return; }
            throw new InvalidOperationException("Audit unexpectedly suppressed the fixture's native body.");
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
