using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization.Settings;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Uses only the isolated paired-test save. This checks private UI_EventMessage fields,
    // not rendered pixels or the complete Unity presentation stack.
    internal sealed class PairedConsoleChargerTest
    {
        private const float DeadlineSeconds = 30f;
        private static readonly FieldInfo ChargerInputActionField = AccessTools.Field(typeof(Furniture_PortableGameConsole_Charger), "inputAction");
        private static readonly FieldInfo ConsoleObjectField = AccessTools.Field(typeof(Furniture_PortableGameConsole_Charger), "GameConsole");
        private static readonly FieldInfo EventMessageField = AccessTools.Field(typeof(UI_EventMessage), "eventMessage");
        private static readonly FieldInfo EventNameField = AccessTools.Field(typeof(UI_EventMessage), "eventName");
        private static readonly FieldInfo EventCanvasGroupField = AccessTools.Field(typeof(UI_EventMessage), "Event_canvasGroup");
        private static readonly FieldInfo EventTimerField = AccessTools.Field(typeof(UI_EventMessage), "eventTimer");
        private static readonly FieldInfo OriginalStateField = AccessTools.Field(typeof(SharedPortableConsoleCharger), "original");

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private Furniture_PortableGameConsole_Charger? homeCharger;
        private Furniture_PortableGameConsole_Charger? speedwayCharger;
        private int phase;
        private float readyDeadline, nextAction, preparationDeadline;
        private bool retrieveSpeedwayFirst, guestClicked;
        private bool feedbackBaselineCaptured;
        private float feedbackTimerBefore;
        private string feedbackTextBefore = "", feedbackTitleBefore = "";
        private string feedbackObserved = "no";

        internal string Status => "console charger phase=" + phase + "; state=" +
            (plugin.World == null ? "-" : plugin.World.PortableConsoleCharger.TestState.Location.ToString()) +
            "; nativeHand=" + NativeHandForStatus() +
            "; homeCharging=" + (homeCharger?.isConsoleCharging.ToString() ?? "-") +
            "; homeObject=" + HomeObjectForStatus() + "; guestClicked=" + guestClicked +
            "; feedback=" + feedbackObserved + "; peers=" + (plugin.Session?.Peers.Count.ToString() ?? "-");

        internal PairedConsoleChargerTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            if (!plugin.TestOptions.Enabled || !MonMP.Plugin.Testing.LocalTestIsolation.Installed)
                throw new InvalidOperationException("Paired portable-console charger testing requires an isolated local test instance.");
            if (ChargerInputActionField?.FieldType != typeof(InputAction) || ConsoleObjectField?.FieldType != typeof(GameObject) ||
                EventMessageField?.FieldType != typeof(TextMeshProUGUI) || EventNameField?.FieldType != typeof(TextMeshProUGUI) ||
                EventCanvasGroupField?.FieldType != typeof(CanvasGroup) || EventTimerField?.FieldType != typeof(float) ||
                OriginalStateField?.FieldType != typeof(PortableConsoleChargerState))
                throw new InvalidOperationException("Unsupported native portable-console charger or event-message UI layout.");
        }

        internal bool Tick()
        {
            var session = plugin.Session;
            var world = plugin.World;
            bool ready = session != null && world != null && session.IsRunning && session.IsReady && world.Channel.FullySynchronized == true &&
                world.PersonalProfileReady && world.Items.Ready;
            if (!ready)
            {
                if (readyDeadline != 0)
                    throw new InvalidOperationException("Paired portable-console charger test lost its ready session/world: " + Status);
                return false;
            }

            if (session == null || world == null) throw new InvalidOperationException("Ready console test lost its session/world.");

            if (readyDeadline == 0) readyDeadline = Time.unscaledTime + DeadlineSeconds;
            if (Time.unscaledTime > readyDeadline)
                throw new InvalidOperationException("Paired portable-console charger test timed out: " + Status);

            if (homeCharger == null) FindChargers();
            if (plugin.TestOptions.IsHost && phase == 3) return Host(world);
            if (phase != 0 && session.Peers.Count != 2)
            {
                throw new InvalidOperationException("Paired portable-console charger test lost its paired peer: " + Status);
            }
            if (session.Peers.Count != 2) return false;
            if (Time.unscaledTime < nextAction) return false;
            return plugin.TestOptions.IsHost ? Host(world) : Guest(world);
        }

        private void FindChargers()
        {
            var all = Object.FindObjectsOfType<Furniture_PortableGameConsole_Charger>(true).Where(charger => charger != null).ToArray();
            var homes = all.Where(charger => charger.ChargerLocation == ConsoleLocation.Charger_Home).ToArray();
            Require(homes.Length == 1, "Expected exactly one native Home portable-console charger; found " + homes.Length + ".");
            homeCharger = homes[0];
            speedwayCharger = all.SingleOrDefault(charger => charger.ChargerLocation == ConsoleLocation.Charger_Speedway);
            Require(homeCharger.isActiveAndEnabled && homeCharger.gameObject.activeInHierarchy,
                "The registered Home portable-console charger is not active.");
        }

        private bool Host(SharedWorld world)
        {
            var shared = world.PortableConsoleCharger;
            var state = shared.TestState.Location;
            if (phase == 0)
            {
                if (state == PortableConsoleLocation.HomeCharger)
                {
                    Require(homeCharger!.isConsoleCharging, "Shared state says Home charger, but its console presentation is inactive.");
                    CaptureFeedbackBaseline();
                    phase = 2;
                    return false;
                }
                if (state == PortableConsoleLocation.Hand)
                {
                    homeCharger!.MouseDown();
                    retrieveSpeedwayFirst = false;
                    preparationDeadline = Time.unscaledTime + 2f;
                    phase = 1;
                    nextAction = Time.unscaledTime + 0.2f;
                    return false;
                }
                if (state == PortableConsoleLocation.SpeedwayCharger)
                {
                    Require(speedwayCharger != null && speedwayCharger.isActiveAndEnabled && speedwayCharger.gameObject.activeInHierarchy,
                        "The console is at Speedway, but its native charger is unavailable.");
                    speedwayCharger!.MouseDown();
                    retrieveSpeedwayFirst = true;
                    preparationDeadline = Time.unscaledTime + 2f;
                    phase = 1;
                    nextAction = Time.unscaledTime + 0.2f;
                    return false;
                }
                throw new InvalidOperationException("Unknown host portable-console location: " + state + ".");
            }
            if (phase == 1)
            {
                state = shared.TestState.Location;
                if (retrieveSpeedwayFirst && state == PortableConsoleLocation.Hand)
                {
                    homeCharger!.MouseDown();
                    retrieveSpeedwayFirst = false;
                    preparationDeadline = Time.unscaledTime + 2f;
                    nextAction = Time.unscaledTime + 0.2f;
                    return false;
                }
                if (state == PortableConsoleLocation.HomeCharger && homeCharger!.isConsoleCharging)
                {
                    CaptureFeedbackBaseline();
                    phase = 2;
                    return false;
                }
                if (Time.unscaledTime > preparationDeadline)
                    throw new InvalidOperationException("Host native MouseDown did not place the console on Home: " + Status);
                return false;
            }
            if (phase == 2)
            {
                if (shared.TestState.Location != PortableConsoleLocation.Hand) return false;
                Require(shared.TestAccepted > 0, "Host did not accept the guest's normal charger retrieval request.");
                VerifyHandheldAndHidden(homeCharger!, "host");
                VerifyNativeTip(homeCharger!, "host");
                feedbackObserved = "host UI fields match";
                phase = 3;
                nextAction = Time.unscaledTime + 0.2f;
                return false;
            }
            if (phase == 3 && plugin.Session?.Peers.Count == 1)
            {
                plugin.EndPairedTestSession();
                return true;
            }
            if (phase == 3 && plugin.Session?.Peers.Count != 2)
                throw new InvalidOperationException("Host lost the paired session before guest disconnect completed: " + Status);
            return false;
        }

        private bool Guest(SharedWorld world)
        {
            var shared = world.PortableConsoleCharger;
            if (phase == 0)
            {
                if (shared.TestState.Location != PortableConsoleLocation.HomeCharger) return false;
                Require(homeCharger!.isConsoleCharging, "Host state says Home charger, but the guest charger presentation is inactive.");
                MoveNear(homeCharger.transform);
                phase = 1;
                nextAction = Time.unscaledTime + 0.75f;
                return false;
            }
            if (phase == 1)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking, "Guest is not walking after moving near the Home charger.");
                Require(shared.TestState.Location == PortableConsoleLocation.HomeCharger && homeCharger!.isConsoleCharging,
                    "Home charger changed before the guest retrieval click.");
                if (!feedbackBaselineCaptured) CaptureFeedbackBaseline();
                if (feedbackTextBefore == ExpectedTip(homeCharger!) && feedbackTitleBefore == ExpectedTitle() &&
                    CurrentFeedbackTimer() > 0.2f)
                {
                    nextAction = Time.unscaledTime + 0.1f;
                    return false;
                }
                feedbackTimerBefore = CurrentFeedbackTimer();
                homeCharger!.MouseDown(); // Run the native method and ordinary MonMP interception exactly once.
                guestClicked = true;
                phase = 2;
                nextAction = Time.unscaledTime + 0.15f;
                return false;
            }
            if (phase == 2)
            {
                if (shared.TestState.Location != PortableConsoleLocation.Hand) return false;
                VerifyHandheldAndHidden(homeCharger!, "guest");
                VerifyNativeTip(homeCharger!, "guest");
                feedbackObserved = "guest UI fields match";
                var original = ReadOriginalState(shared);
                plugin.EndPairedTestSession();
                var restored = CaptureLocalConsole();
                Require(restored.Equals(original), "Guest personal console state did not restore after disconnect: expected " +
                    Format(original) + ", got " + Format(restored) + ".");
                phase = 3;
                return true;
            }
            return false;
        }

        private void MoveNear(Transform target)
        {
            Vector3 location = target.position + target.forward * 2.5f + Vector3.up;
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private void VerifyHandheldAndHidden(Furniture_PortableGameConsole_Charger charger, string peer)
        {
            var location = gameplay.Player.Get_VisualHands.Get_ConsoleLocation;
            Require(location == ConsoleLocation.Hand, peer + " native handheld location is " + location + ", expected Hand.");
            Require(!charger.isConsoleCharging, peer + " Home charger still reports its console presentation active.");
            var presentation = (GameObject)ConsoleObjectField.GetValue(charger);
            Require(!presentation.activeSelf && !presentation.activeInHierarchy, peer + " Home charger console object is still active: activeSelf=" +
                presentation.activeSelf + "; activeInHierarchy=" + presentation.activeInHierarchy + ".");
        }

        private void VerifyNativeTip(Furniture_PortableGameConsole_Charger charger, string peer)
        {
            string expectedText = ExpectedTip(charger);
            string expectedTitle = ExpectedTitle();

            var message = Singleton<UI_EventMessage>.i;
            var eventMessage = EventMessageField.GetValue(message) as TextMeshProUGUI ??
                throw new InvalidOperationException(peer + " native event message text component is missing.");
            var eventName = EventNameField.GetValue(message) as TextMeshProUGUI ??
                throw new InvalidOperationException(peer + " native event name text component is missing.");
            var group = EventCanvasGroupField.GetValue(message) as CanvasGroup ??
                throw new InvalidOperationException(peer + " native event canvas group is missing.");
            Require(group.gameObject.activeSelf && group.alpha > 0f, peer + " native event message group is hidden: active=" +
                group.gameObject.activeSelf + "; alpha=" + group.alpha + ".");
            Require(eventMessage.gameObject.activeSelf && eventMessage.text == expectedText,
                peer + " native pickup message differs: active=" + eventMessage.gameObject.activeSelf + "; actual='" + eventMessage.text +
                "'; expected='" + expectedText + "'.");
            Require(eventName.gameObject.activeSelf && eventName.text == expectedTitle,
                peer + " native pickup message title differs: active=" + eventName.gameObject.activeSelf + "; actual='" + eventName.text +
                "'; expected='" + expectedTitle + "'.");
            if (feedbackBaselineCaptured && feedbackTextBefore == expectedText && feedbackTitleBefore == expectedTitle)
            {
                float eventTimer = (float)EventTimerField.GetValue(message);
                Require(eventTimer > feedbackTimerBefore + 0.35f, peer + " native pickup message was not refreshed: before=" +
                    feedbackTimerBefore + "; after=" + eventTimer + ".");
            }
        }

        private void CaptureFeedbackBaseline()
        {
            var message = Singleton<UI_EventMessage>.i;
            var eventMessage = EventMessageField.GetValue(message) as TextMeshProUGUI;
            var eventName = EventNameField.GetValue(message) as TextMeshProUGUI;
            feedbackTextBefore = eventMessage == null ? "" : eventMessage.text;
            feedbackTitleBefore = eventName == null ? "" : eventName.text;
            feedbackTimerBefore = (float)EventTimerField.GetValue(message);
            feedbackBaselineCaptured = true;
        }

        private string ExpectedTip(Furniture_PortableGameConsole_Charger charger)
        {
            var inputAction = (InputAction)ChargerInputActionField.GetValue(charger);
            string bindingDisplayString = inputAction.GetBindingDisplayString(1);
            if (gameplay.Get_InputDeviceType == InputDeviceType.Keyboard)
                bindingDisplayString = inputAction.GetBindingDisplayString(0);
            return "[<color=yellow>" + bindingDisplayString + "</color>] " + TEXT.GetText("PortableGameConsole_PickupFromChargerTip");
        }

        private static string ExpectedTitle() => LocalizationSettings.StringDatabase.GetLocalizedString("UI_General", "LoadingScreen_TipsTitle", null,
            FallbackBehavior.UseProjectSettings);

        private float CurrentFeedbackTimer() => (float)EventTimerField.GetValue(Singleton<UI_EventMessage>.i);

        private string NativeHandForStatus()
        {
            try { return gameplay.Player.Get_VisualHands.Get_ConsoleLocation.ToString(); }
            catch { return "unavailable"; }
        }

        private string HomeObjectForStatus()
        {
            try
            {
                if (homeCharger == null) return "unavailable";
                var presentation = ConsoleObjectField.GetValue(homeCharger) as GameObject;
                return presentation == null ? "missing" : presentation.activeSelf + "/" + presentation.activeInHierarchy;
            }
            catch { return "unavailable"; }
        }

        private PortableConsoleChargerState CaptureLocalConsole()
        {
            var hands = gameplay.Player.Get_VisualHands;
            return new PortableConsoleChargerState(ToPortableLocation(hands.Get_ConsoleLocation), hands.GameConsole.Get_BatteryLife,
                hands.GameConsole.Battery_Charging);
        }

        private static PortableConsoleChargerState ReadOriginalState(SharedPortableConsoleCharger shared)
        {
            object original = OriginalStateField.GetValue(shared);
            return original is PortableConsoleChargerState state
                ? state
                : throw new InvalidOperationException("Guest portable-console baseline state is unavailable.");
        }

        private static string Format(PortableConsoleChargerState state) =>
            state.Location + "/battery" + state.BatteryLife + "/charge" + state.ChargingTicks;

        private static PortableConsoleLocation ToPortableLocation(ConsoleLocation location) => location switch
        {
            ConsoleLocation.Hand => PortableConsoleLocation.Hand,
            ConsoleLocation.Charger_Home => PortableConsoleLocation.HomeCharger,
            ConsoleLocation.Charger_Speedway => PortableConsoleLocation.SpeedwayCharger,
            _ => throw new InvalidOperationException("Unknown native portable-console location: " + location + ".")
        };

        private void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Paired portable-console charger test: " + reason + " (" + Status + ")");
        }
    }
}
