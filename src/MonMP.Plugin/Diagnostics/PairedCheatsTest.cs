using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Native cheat permission: a guest without the host's grant tries a leaked private
    // cheat flag, native activation, a forced cheat window and every native SpecialMenu
    // action, first with session cheats off and then enabled. A granted guest's cash
    // action then reaches the host (positive control) and revocation hides the window.
    internal sealed class PairedCheatsTest
    {
        private static readonly FieldInfo CheatWindow = AccessTools.Field(typeof(EscapeMenu_GeneralV2), "CheatWindow");
        private static readonly FieldInfo SettingField = AccessTools.Field(typeof(GameManager), "_SETTING_Cheats");
        private static readonly MethodInfo UpdateWindow = AccessTools.Method(typeof(EscapeMenu_GeneralV2), "Update_CheatFriendshipWindow");
        private static readonly (string Name, object[] Arguments)[] Actions =
        {
            ("Add1Thousand", new object[0]), ("AddOneHour", new object[0]), ("AddOneDay", new object[0]), ("ResetNeeds", new object[0]),
            ("Plants_Water", new object[0]), ("Plants_Desinfect", new object[0]), ("RefillAllCars", new object[0]),
            ("GodSpeed", new object[] { true }), ("TPPlayer", new object[] { 1 }), ("TPVehicle", new object[] { 1 }),
            ("SpawnThisObject", new object[] { 1 }), ("ChangeWeather", new object[] { 1 }), ("ChangeGravity", new object[] { 1 })
        };
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Cash_Manager cash;
        private int phase, acknowledgment, refused, cashBefore;
        private uint guest;
        private float next, speed;
        private Vector3 position;
        internal string Status => "cheats phase=" + phase + " enabled=" + plugin.Session?.Cheats.Enabled + " native=" + GameManager.SETTING_Cheats +
            " canUse=" + plugin.Cheats?.CanUse + " refused=" + plugin.Cheats?.TestRefused + " allowed=" + plugin.Cheats?.TestAllowed + " windows=" + Windows();
        internal PairedCheatsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1) acknowledgment = Math.Max(acknowledgment, payload[0]); }
        private void Signal(byte value) => plugin.World!.Channel.Request(ClockState.Entity, new[] { value });
        private static EscapeMenu_GeneralV2[] Pages => UnityEngine.Object.FindObjectsOfType<EscapeMenu_GeneralV2>(true);
        private static string Windows() => string.Join(",", Pages.Select(p => (CheatWindow.GetValue(p) as RectTransform)?.gameObject.activeSelf == true ? "shown" : "hidden"));
        private static bool AnyWindowShown => Pages.Any(p => (CheatWindow.GetValue(p) as RectTransform)?.gameObject.activeSelf == true);
        internal bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            var session = plugin.Session!;
            if (plugin.World?.Channel.FullySynchronized != true || plugin.Cheats == null) return false;
            return plugin.TestOptions.IsHost ? Host(session) : Guest(session);
        }
        private bool Host(Core.Session.NetworkSession session)
        {
            if (phase == 0)
            {
                if (session.Peers.Count < 2) return false;
                guest = session.Peers.Keys.First(id => id != 0);
                Require(!session.Cheats.Enabled && !GameManager.SETTING_Cheats, "The host fixture save already has cheats enabled.");
                cashBefore = cash.GetCash; phase = 1;
            }
            else if (phase == 1 && acknowledgment >= 1)
            {
                Require(cash.GetCash == cashBefore, "A guest cheat changed host cash while session cheats were off.");
                GameManager.SETTING_Cheats = true;
                Require(session.Cheats.Enabled && !session.Cheats.Granted(guest), "The host could not enable session cheats without a guest grant.");
                phase = 2;
            }
            else if (phase == 2 && acknowledgment >= 2)
            {
                Require(cash.GetCash == cashBefore, "An ungranted guest cheat changed host cash.");
                plugin.MenuSetCheatPermission(guest, true); phase = 3;
            }
            else if (phase == 3 && acknowledgment >= 3)
            {
                Require(cash.GetCash > cashBefore, "The granted guest's native cash action did not reach the host.");
                plugin.MenuSetCheatPermission(guest, false); phase = 4;
            }
            else if (phase == 4 && acknowledgment >= 4) { plugin.World!.Channel.CommandRequested -= Acknowledge; phase = 5; }
            else if (phase == 5 && session.Peers.Count == 1) return true;
            return false;
        }
        private bool Guest(Core.Session.NetworkSession session)
        {
            var cheats = plugin.Cheats!;
            if (phase == 0 && session.IsReady && !session.Cheats.Enabled)
            { Require(Pages.Length > 0 && !cheats.CanUse, "Missing native pause pages or unexpected cheat permission."); Probe(); phase = 1; }
            else if (phase == 1) { Verify(false); Signal(1); phase = 2; }
            else if (phase == 2 && session.Cheats.Enabled && GameManager.SETTING_Cheats)
            { Require(!cheats.CanUse && !AnyWindowShown, "Enabled session cheats exposed the native window to an ungranted guest."); Probe(); phase = 3; }
            else if (phase == 3) { Verify(true); Signal(2); phase = 4; }
            else if (phase == 4 && cheats.CanUse)
            {
                Require(AnyWindowShown, "A granted guest did not get the native cheat window.");
                cashBefore = cash.GetCash; int allowed = cheats.TestAllowed;
                Invoke("Add1Thousand");
                Require(cheats.TestAllowed == allowed + 1, "The granted guest's native cash action was not admitted.");
                phase = 5;
            }
            else if (phase == 5 && cash.GetCash > cashBefore) { Signal(3); phase = 6; }
            else if (phase == 6 && !cheats.CanUse) { phase = 7; next = Time.unscaledTime + 1; }
            else if (phase == 7)
            {
                Require(!AnyWindowShown, "Revoking the grant left the native cheat window visible.");
                refused = cheats.TestRefused; Invoke("Add1Thousand");
                Require(cheats.TestRefused == refused + 1, "A revoked guest's native cash action was not refused.");
                Signal(4); phase = 8; next = Time.unscaledTime + 1;
            }
            else if (phase == 8) { plugin.EndPairedTestSession(); return true; }
            return false;
        }
        // Every route a guest's own state or the native UI could open cheats by.
        private void Probe()
        {
            var cheats = plugin.Cheats!;
            position = gameplay.PlayerWalking.transform.position; speed = gameplay.PlayerWalking.movementNormalSpeed;
            cashBefore = cash.GetCash; refused = cheats.TestRefused;
            // A guest's own save/profile carrying isCheatsActivated, written past the setter.
            SettingField.SetValue(null, true);
            foreach (var page in Pages)
            {
                UpdateWindow.Invoke(page, null);
                Require((CheatWindow.GetValue(page) as RectTransform)?.gameObject.activeSelf != true, "A leaked native cheat flag opened the window through the native refresh.");
                page.Cheats_Activate();
                (CheatWindow.GetValue(page) as RectTransform)?.gameObject.SetActive(true);
            }
            GameManager.SETTING_Cheats = true;
            foreach (var (name, arguments) in Actions) Invoke(name, arguments);
            Require(cheats.TestRefused == refused + Actions.Length, "Only " + (cheats.TestRefused - refused) + " of " + Actions.Length + " native cheat actions were refused.");
            next = Time.unscaledTime + 1.2f;
        }
        private void Verify(bool enabled)
        {
            Require(!AnyWindowShown, "A forced native cheat window stayed visible without permission: " + Windows());
            Require(GameManager.SETTING_Cheats == enabled, "The guest's native cheat flag did not return to the session state.");
            Require(cash.GetCash == cashBefore, "Refused cheats changed the guest's cash.");
            Require(gameplay.PlayerWalking.movementNormalSpeed == speed, "Refused god speed changed the guest's walk speed.");
            Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, position) < 1, "A refused teleport moved the guest.");
        }
        private static void Invoke(string name, params object[] arguments)
        {
            var menu = UnityEngine.Object.FindObjectOfType<SpecialMenu>(true);
            Require(menu != null, "Missing native SpecialMenu.");
            var types = arguments.Select(a => a.GetType()).ToArray();
            AccessTools.Method(typeof(SpecialMenu), name, types).Invoke(menu, arguments);
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Cheats test: " + reason); }
    }
}
