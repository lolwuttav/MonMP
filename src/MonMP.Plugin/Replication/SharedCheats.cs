using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Michsky.MUIP;
using MonMP.Core.Persistence;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using NWH;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.Replication
{
    // Native cheats are a session setting; permission is a separate host-owned grant.
    // The guest's private setting and temporary native movement state have explicit leases.
    internal sealed class SharedCheats : IDisposable
    {
        private static readonly FieldInfo CheatWindow = Field(typeof(EscapeMenu_GeneralV2), "CheatWindow");
        private static readonly FieldInfo FriendshipWindow = Field(typeof(EscapeMenu_GeneralV2), "FriendshipWindow");
        private static readonly FieldInfo ActivateButton = Field(typeof(EscapeMenu_GeneralV2), "Button_Cheats");
        private static readonly FieldInfo GodSpeedToggle = Field(typeof(SpecialMenu), "Toggle_GodSpeed");
        private static readonly FieldInfo SpawnObjects = Field(typeof(SpecialMenu), "SpawnObjects");
        private static readonly FieldInfo VehicleTypes = Field(typeof(SpecialMenu), "VehicleIDtoType");
        private static readonly FieldInfo VehicleDropdown = Field(typeof(SpecialMenu), "Dropdown_Vehicles");
        private static readonly FieldInfo SpawnDropdown = Field(typeof(SpecialMenu), "Dropdown_SpawnObjects");
        private static readonly FieldInfo WeatherDropdown = Field(typeof(SpecialMenu), "Dropdown_Weather");
        private static readonly FieldInfo GravityDropdown = Field(typeof(SpecialMenu), "Dropdown_Gravity");
        private static readonly FieldInfo CreditText = Field(typeof(UI_GameCreditVersion), "gameVersion");
        private readonly MonMPPlugin plugin;
        private readonly NetworkSession session;
        private readonly bool originalEnabled;
        private readonly Vector3 originalGravity;
        private RememberedCheatPermissions? rememberedPermissions;
        private CMF.AdvancedWalkerController? walker;
        private float originalSpeed, originalWalkerGravity, normalSpeed;
        private bool speedOwned, disposed, hosted, lastCanUse;
        private uint executingPeer;
        private SharedCheatAction executingAction;
        private CheatFeedback? capturedFeedback;
        private static int applying, executing;
        private readonly HashSet<string> loggedActions = new HashSet<string>();
        private float nextWindowAudit;
#if MONMP_DEVELOPMENT
        internal int TestRefused, TestAllowed;
#endif
        internal static bool Applying => applying > 0;
        internal static bool ExecutingRemote => executing > 0;
        internal bool CanUse => session.IsReady && session.Cheats.Permitted(session.LocalPeerId) &&
            plugin.World != null && plugin.World.Channel.FullySynchronized && !plugin.World.SaveGate.Active &&
            plugin.GuestWorld?.HoldPresentation != true;

        internal SharedCheats(MonMPPlugin plugin, NetworkSession session)
        {
            this.plugin = plugin; this.session = session;
            originalEnabled = GameManager.SETTING_Cheats; originalGravity = Physics.gravity;
            session.CheatsChanged += Apply;
            session.CheatActionReceived += Execute;
            session.CheatFeedbackReceived += Feedback;
        }
        internal void Started()
        {
            hosted = session.IsHost;
            if (hosted)
            {
                session.SetCheatGravity(Physics.gravity.y); session.SetCheatsEnabled(originalEnabled);
                try
                {
                    string directory = SaveSlot.GetSaveParentPath(NativeSaveWriter.PersistentRoot, NativeSaveWriter.SelectedSave);
                    var store = CheatPermissionStore.Open(directory, message => Debug.LogWarning("MonMP " + message));
                    rememberedPermissions = new RememberedCheatPermissions(session, store, plugin.World!.SavedProfileIdentities);
                }
                catch (Exception ex) { Debug.LogWarning("MonMP could not load optional cheat permissions: " + ex.Message); }
            }
            Apply(session.Cheats);
        }
        internal void Tick()
        {
            if (disposed || !session.IsRunning) return;
            var current = plugin.Gameplay?.PlayerWalking;
            if (current != null && walker != current)
            {
                walker = current; originalSpeed = normalSpeed = current.movementNormalSpeed; originalWalkerGravity = current.gravity;
                var menu = UnityEngine.Object.FindObjectOfType<SpecialMenu>(true);
                if (!session.IsHost && menu != null && (GodSpeedToggle.GetValue(menu) as Toggle)?.isOn == true)
                { normalSpeed /= 15f; speedOwned = true; }
            }
            if (!session.IsHost)
            {
                if (GameManager.SETTING_Cheats != session.Cheats.Enabled) Apply(session.Cheats);
                Physics.gravity = new Vector3(0, session.Cheats.GravityY, 0);
                if (walker != null) walker.gravity = -session.Cheats.GravityY * 3.058103f;
            }
            if (!CanUse) RevokeSpeed();
            AuditCheatWindows();
            bool use = CanUse;
            if (use != lastCanUse)
            {
                lastCanUse = use;
                foreach (var page in UnityEngine.Object.FindObjectsOfType<EscapeMenu_GeneralV2>(true)) RefreshMenu(page);
            }
        }
        private void Apply(SessionCheatState state)
        {
            if (disposed) return;
            applying++;
            try { if (GameManager.SETTING_Cheats != state.Enabled) GameManager.SETTING_Cheats = state.Enabled; }
            finally { applying--; }
            if (!session.IsHost) Physics.gravity = new Vector3(0, state.GravityY, 0);
            if (!state.Permitted(session.LocalPeerId)) RevokeSpeed();
            foreach (var page in UnityEngine.Object.FindObjectsOfType<EscapeMenu_GeneralV2>(true))
            {
                AccessTools.Method(typeof(EscapeMenu_GeneralV2), "Update_CheatFriendshipWindow").Invoke(page, null);
                RefreshMenu(page);
            }
            if (!state.Enabled && plugin.Gameplay?.Get_isHardMode != true)
                foreach (var credit in UnityEngine.Object.FindObjectsOfType<UI_GameCreditVersion>(true))
                    if (CreditText.GetValue(credit) is TextMeshProUGUI text) text.enableVertexGradient = false;
        }
        // Independent of what activated it (a guest's own save, native UI refresh
        // or another mod), a visible native cheat window without permission is
        // hidden on the next audit. Its actions remain host-checked regardless.
        private void AuditCheatWindows()
        {
            if (Time.unscaledTime < nextWindowAudit) return;
            nextWindowAudit = Time.unscaledTime + 0.5f;
            if (CanUse && plugin.Gameplay?.Get_isHardMode != true) return;
            foreach (var page in UnityEngine.Object.FindObjectsOfType<EscapeMenu_GeneralV2>(true))
            {
                if (CheatWindow.GetValue(page) is not RectTransform window || !window.gameObject.activeSelf) continue;
                LogAction("hid unpermitted native cheat window (native=" + GameManager.SETTING_Cheats + ")");
                RefreshMenu(page);
            }
        }
        internal void RefreshMenu(EscapeMenu_GeneralV2 page)
        {
            if (disposed || !session.IsRunning) return;
            bool use = CanUse && plugin.Gameplay?.Get_isHardMode != true;
            if (CheatWindow.GetValue(page) is RectTransform window) window.gameObject.SetActive(use);
            if (FriendshipWindow.GetValue(page) is RectTransform friends) friends.sizeDelta = new Vector2(800, use ? 500 : 675);
            if (!session.IsHost && ActivateButton.GetValue(page) is Button button)
                button.GetComponent<ButtonManager>()?.Interactable(false);
        }

        internal bool RequestEnableFromSession()
        {
            if (disposed || !session.IsHost || !session.IsRunning || session.Cheats.Enabled || plugin.Gameplay?.Get_isHardMode != false) return false;
            var page = UnityEngine.Object.FindObjectOfType<EscapeMenu_GeneralV2>(true);
            if (page == null || ActivateButton.GetValue(page) is not Button button) return false;
            // Use the native button's serialized warning/confirmation flow, not Cheats_Activate.
            button.onClick.Invoke();
            return true;
        }
        internal void BeforeGodSpeed(bool enabled)
        {
            Tick();
            if (enabled && walker != null && !speedOwned) { normalSpeed = walker.movementNormalSpeed; speedOwned = true; }
            if (!enabled) speedOwned = false;
        }
        private void RevokeSpeed()
        {
            if (!speedOwned) return;
            if (walker != null) walker.movementNormalSpeed = normalSpeed;
            speedOwned = false;
            var menu = UnityEngine.Object.FindObjectOfType<SpecialMenu>(true);
            if (menu != null && GodSpeedToggle.GetValue(menu) is Toggle toggle) toggle.SetIsOnWithoutNotify(false);
        }
        internal bool NativeAction(SpecialMenu menu, string name, object[] arguments)
        {
            if (ExecutingRemote || !session.IsRunning) return true;
            int selection = arguments.Length > 0 && arguments[0] is int index ? index : 0;
            if (arguments.Length > 0 && arguments[0] is int && selection == 0) return true;
            if (!CanUse)
            {
#if MONMP_DEVELOPMENT
                TestRefused++;
#endif
                plugin.ReportItemUseUnavailable("Cheats require the host's permission."); LogAction("refused local " + name); return false;
            }
#if MONMP_DEVELOPMENT
            TestAllowed++;
#endif
            LogAction("allowed local " + name);
            plugin.Activity.RecordAction();
            if (name == "GodSpeed") BeforeGodSpeed((bool)arguments[0]);
            if (session.IsHost || name == "GodSpeed" || name == "TPPlayer" || name == "ResetNeeds") return true;
            if (name == "Save") { plugin.ReportSaveBlocked(); return false; }
            SharedCheatAction action;
            switch (name)
            {
                case "Add1Thousand": action = SharedCheatAction.Cash; break;
                case "AddOneHour": action = SharedCheatAction.Hour; break;
                case "AddOneDay": action = SharedCheatAction.Day; break;
                case "Plants_Water": action = SharedCheatAction.WaterPlants; break;
                case "Plants_Desinfect": action = SharedCheatAction.DisinfectPlants; break;
                case "RefillAllCars": action = SharedCheatAction.Refuel; break;
                case "ChangeWeather": action = SharedCheatAction.Weather; break;
                case "ChangeGravity": action = SharedCheatAction.Gravity; break;
                case "SpawnThisObject": action = SharedCheatAction.Spawn; break;
                case "TPVehicle":
                    action = SharedCheatAction.TeleportVehicle;
                    var types = (List<Vehicle_Type>)VehicleTypes.GetValue(menu);
                    if (selection < 1 || selection > types.Count) return false;
                    selection = (int)types[selection - 1]; break;
                default: return false;
            }
            if (selection < 0 || selection > 512) return false;
            bool sent = session.RequestCheatAction(new CheatActionRequest(action, (ushort)selection));
            // A rate-limited attempt must also reset so the same option can be retried.
            ResetDropdown(menu, action);
            if (!sent) plugin.ReportItemUseUnavailable("Wait before another cheat action.");
            return false;
        }
        private static void ResetDropdown(SpecialMenu menu, SharedCheatAction action)
        {
            FieldInfo? field = action == SharedCheatAction.TeleportVehicle ? VehicleDropdown :
                action == SharedCheatAction.Spawn ? SpawnDropdown : action == SharedCheatAction.Weather ? WeatherDropdown :
                action == SharedCheatAction.Gravity ? GravityDropdown : null;
            (field?.GetValue(menu) as Dropdown)?.SetValueWithoutNotify(0);
        }
        private static readonly string[] CompletionTexts =
        {
            "Cheat action completed.", "Advancing time by one hour.", "Advancing time by one day.",
            "Object spawned.", "Vehicle teleported.", "$1,000 added."
        };
        private void Feedback(CheatFeedback feedback)
        {
            // Only failures and the native menu's own notices are shown; MonMP's
            // completion texts are logged.
            if (feedback.Error) Singleton<UI_ErrorMessage>.i?.Show(feedback.Text, 4f);
            else if (Array.IndexOf(CompletionTexts, feedback.Text) >= 0) LogAction("completed " + feedback.Action + ": " + feedback.Text);
            else Singleton<UI_EventMessage>.i?.Show(feedback.Text, "Cheat Menu", 4f);
            // Queuing an action is not its native completion. Only successful actions
            // which resume the stock menu close the requester's pause menu.
            if (NativeCheatMenuPolicy.ResumesAfter(feedback))
            {
                var pause = UnityEngine.Object.FindObjectOfType<EscapeMenuV2>(true);
                if (pause?.isOpen == true) pause.ResumeGame();
            }
        }
        // Called from the existing native notification patches during the synchronous action.
        // Native counts, labels and errors go to the requester without taking over host UI.
        internal bool CaptureFeedback(string text, bool error)
        {
            if (!ExecutingRemote || executingPeer == 0 || !session.IsHost) return false;
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (System.Text.Encoding.UTF8.GetByteCount(text) > CheatFeedback.MaxTextBytes)
                text = error ? "The cheat action could not be completed." : "Cheat action completed.";
            // Native notifications can occur before an action finishes (weather's
            // change event, for example). Send the result after execution so a failed
            // action cannot prematurely resume the requesting player's menu.
            capturedFeedback = new CheatFeedback(executingAction, error, text);
            return true;
        }
        private void Execute(uint peer, CheatActionRequest request)
        {
            // Recheck the host's current grant, world lifecycle and sender pose at dispatch.
            if (!session.IsHost || !session.Cheats.Permitted(peer) || !CanUse ||
                !session.TryGetSocialPose(peer, out var pose) || pose.Rest != PlayerRest.None || pose.Spectating)
            { LogAction("refused peer " + peer + " " + request.Action); return; }
            LogAction("executing peer " + peer + " " + request.Action);
            var menu = UnityEngine.Object.FindObjectOfType<SpecialMenu>(true);
            if (menu == null) return;
            executingPeer = peer; executingAction = request.Action; capturedFeedback = null;
            bool completed = false;
            string result = "Cheat action completed.";
            executing++;
            try
            {
                switch (request.Action)
                {
                    case SharedCheatAction.Hour: UniStorm.UniStormSystem.Instance.AddOneHour(); completed = true; result = "Advancing time by one hour."; return;
                    case SharedCheatAction.Day: UniStorm.UniStormSystem.Instance.AddOneDay(); completed = true; result = "Advancing time by one day."; return;
                    case SharedCheatAction.Spawn:
                        var prefabs = (GameObject[])SpawnObjects.GetValue(menu);
                        if (request.Selection == 0 || request.Selection > prefabs.Length || prefabs[request.Selection - 1] == null) return;
                        var origin = new Vector3(pose.X, pose.Y, pose.Z);
                        var rotation = Quaternion.Euler(0, pose.Yaw, 0);
                        UnityEngine.Object.Instantiate(prefabs[request.Selection - 1], origin + rotation * Vector3.forward * 2 + Vector3.up, rotation);
                        completed = true; result = "Object spawned.";
                        return;
                    case SharedCheatAction.TeleportVehicle:
                        menu.BuildLists(); var types = (List<Vehicle_Type>)VehicleTypes.GetValue(menu);
                        int index = types.IndexOf((Vehicle_Type)request.Selection);
                        if (index < 0 || plugin.Gameplay!.Store_PoliceInterceptor.Get_IsThisCarSeizeByThePolice((Vehicle_Type)request.Selection)) return;
                        var vehicle = Singleton<GlobalClassManager>.i.GetAllVehicles().FirstOrDefault(v => v.vehicle_type == (Vehicle_Type)request.Selection);
                        if (vehicle == null) return;
                        var dropdown = (Dropdown)VehicleDropdown.GetValue(menu);
                        dropdown.SetValueWithoutNotify(index + 1);
                        Invoke(menu, "TPVehicle", index + 1);
                        vehicle.transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z) + Quaternion.Euler(0, pose.Yaw, 0) * Vector3.forward * 4 + Vector3.up, Quaternion.Euler(0, pose.Yaw, 0));
                        completed = true; result = "Vehicle teleported.";
                        return;
                    case SharedCheatAction.Weather:
                        if (request.Selection == 0 || request.Selection > UniStorm.UniStormSystem.Instance.AllWeatherTypes.Count) return;
                        Invoke(menu, "ChangeWeather", (int)request.Selection); completed = true; return;
                    case SharedCheatAction.Gravity:
                        if (!NativeCheatGravity.TryGet(request.Selection, out float gravity, out string gravityLabel)) return;
                        plugin.Gameplay!.PlayerWalking.gravity = -gravity * 3.058103f;
                        Physics.gravity = new Vector3(0, gravity, 0);
                        session.SetCheatGravity(gravity);
                        CaptureFeedback("Gravity changed to <b>" + gravityLabel + "</b>", false);
                        completed = true; return;
                    case SharedCheatAction.Cash: Invoke(menu, "Add1Thousand"); completed = true; result = "$1,000 added."; return;
                    case SharedCheatAction.WaterPlants: Invoke(menu, "Plants_Water"); completed = true; return;
                    case SharedCheatAction.DisinfectPlants: Invoke(menu, "Plants_Desinfect"); completed = true; return;
                    case SharedCheatAction.Refuel: Invoke(menu, "RefillAllCars"); completed = true; return;
                }
            }
            catch (Exception ex) { Debug.LogWarning("MonMP authorized cheat failed: " + ex.GetBaseException().Message); }
            finally
            {
                executing--;
                var feedback = capturedFeedback ?? new CheatFeedback(request.Action, !completed,
                    completed ? result : "The cheat action is unavailable right now.");
                if (!completed && !feedback.Error)
                    feedback = new CheatFeedback(request.Action, true, "The cheat action could not be completed.");
                session.SendCheatFeedback(peer, feedback);
                capturedFeedback = null;
                executingPeer = 0;
            }
        }
        // Bounded evidence for permission reports: one line per distinct outcome.
        private void LogAction(string outcome)
        {
            if (loggedActions.Count >= 64 || !loggedActions.Add(outcome)) return;
            Debug.Log("MonMP cheat " + outcome + " (host=" + session.IsHost + ", enabled=" + session.Cheats.Enabled +
                ", granted=[" + string.Join(",", session.Cheats.Allowed) + "], local=" + session.LocalPeerId + ")");
        }
        private static void Invoke(SpecialMenu menu, string name, params object[] arguments) => AccessTools.Method(typeof(SpecialMenu), name).Invoke(menu, arguments);
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new InvalidOperationException("Missing native cheat field: " + name);
        public void Dispose()
        {
            if (disposed) return;
            session.CheatsChanged -= Apply; session.CheatActionReceived -= Execute;
            session.CheatFeedbackReceived -= Feedback;
            rememberedPermissions?.Dispose(); rememberedPermissions = null;
            RevokeSpeed();
            disposed = true;
            if (!hosted)
            {
                Physics.gravity = originalGravity;
                if (walker != null) { walker.movementNormalSpeed = originalSpeed; walker.gravity = originalWalkerGravity; }
                applying++;
                try { GameManager.SETTING_Cheats = originalEnabled; }
                finally { applying--; }
                if (!originalEnabled && plugin.Gameplay?.Get_isHardMode != true)
                    foreach (var credit in UnityEngine.Object.FindObjectsOfType<UI_GameCreditVersion>(true))
                        if (CreditText.GetValue(credit) is TextMeshProUGUI text) text.enableVertexGradient = false;
            }
            foreach (var page in UnityEngine.Object.FindObjectsOfType<EscapeMenu_GeneralV2>(true))
                AccessTools.Method(typeof(EscapeMenu_GeneralV2), "Update_CheatFriendshipWindow").Invoke(page, null);
        }
    }
}
