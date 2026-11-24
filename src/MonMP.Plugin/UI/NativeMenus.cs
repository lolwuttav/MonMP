using System;
using System.Linq;
using HarmonyLib;
using Michsky.MUIP;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    // The player-facing multiplayer menus, built into the game's own uGUI screens: a MULTIPLAYER
    // button under NEW GAME on the main menu (join), and a MULTIPLAYER tab in the pause menu (host).
    internal sealed class NativeMenus
    {
        private static readonly System.Reflection.FieldInfo MainButtons = AccessTools.Field(typeof(EscapeMenuV2), "MainButtons");
        private readonly MonMPPlugin plugin;
        private MainMenu_Manager? mainMenu;
        private EscapeMenuV2? escapeMenu;
        private GameObject? mainButton, pauseButton;
        private MultiplayerScreen? multiplayer;
        private HostScreen? host;
        private string? pendingSteamLobbyId;
        private float nextSteamInviteAttempt;
        private string steamInviteFailure = "";
        private float nextLookup, mainSeenAt, pauseSeenAt;

#if MONMP_DEVELOPMENT
        private int dumpStep;


        private float dumpAt;

private bool pauseDumped;
#endif

        internal NativeMenus(MonMPPlugin plugin) { this.plugin = plugin; }
        internal bool PauseOpen => escapeMenu != null && escapeMenu.isOpen;
        internal bool HostOpen => host?.Open == true;
        internal bool CloseAppearance() => host?.CloseAppearance() == true || multiplayer?.CloseAppearance() == true;
        internal bool ResumeForSpectate()
        {
            if (escapeMenu == null || !escapeMenu.isOpen || host?.Open != true) return false;
            host.Close();
            // Preserve native settings/modal/save checks. Never force past them.
            escapeMenu.ResumeGame(force: false);
            return !escapeMenu.isOpen;
        }
        internal void Destroy()
        {
            multiplayer?.Destroy(); multiplayer = null; host?.Destroy(); host = null;
            if (mainButton != null) UnityEngine.Object.Destroy(mainButton);
            if (pauseButton != null) UnityEngine.Object.Destroy(pauseButton);
        }

        internal void Tick(Gameplay? gameplay)
        {
            if (Time.unscaledTime >= nextLookup)
            {
                nextLookup = Time.unscaledTime + 0.5f;
                if (SceneManager.GetActiveScene().buildIndex == 1 && mainMenu == null && (mainMenu = UnityEngine.Object.FindObjectOfType<MainMenu_Manager>()) != null)
                {
                    mainSeenAt = Time.unscaledTime;
                    multiplayer?.Destroy(); host?.Destroy(); host = null;
                    multiplayer = null; mainButton = null;
#if MONMP_DEVELOPMENT
dumpStep = 0;
#endif
                }
                if (escapeMenu == null && gameplay != null && (escapeMenu = UnityEngine.Object.FindObjectOfType<EscapeMenuV2>(true)) != null)
                {
                    pauseSeenAt = Time.unscaledTime;
                    host?.Destroy(); host = null; pauseButton = null;
#if MONMP_DEVELOPMENT
pauseDumped = false;
#endif
                }
                if (mainMenu != null && mainButton == null) Try("main menu button", AddMainButton);
                if (escapeMenu != null && pauseButton == null) Try("pause menu tab", AddPauseButton);
            }
            if (gameplay != null && mainMenu == null && multiplayer != null) { multiplayer.Destroy(); multiplayer = null; }
            if (pendingSteamLobbyId != null && SceneManager.GetActiveScene().buildIndex == 1 && mainMenu != null &&
                !plugin.MenuSessionRunning && !plugin.MenuConnecting && !plugin.LocalDisconnectPending && Time.unscaledTime >= nextSteamInviteAttempt)
            {
                nextSteamInviteAttempt = Time.unscaledTime + 1;
                try
                {
                    if (multiplayer == null) multiplayer = new MultiplayerScreen(plugin);
                    if (multiplayer.ShowSteamInvite(pendingSteamLobbyId))
                    {
                        plugin.LogSteamInvite("join screen opened for lobby " + pendingSteamLobbyId + ".");
                        pendingSteamLobbyId = null;
                    }
                }
                catch (Exception ex)
                {
                    if (steamInviteFailure != ex.Message)
                    {
                        steamInviteFailure = ex.Message;
                        plugin.LogSteamInvite("join UI is not ready; invitation retained for retry. " + ex.Message);
                    }
                }
            }
            if (pendingSteamLobbyId == null && plugin.MenuJoinFailurePending && SceneManager.GetActiveScene().buildIndex == 1 && mainMenu != null &&
                !plugin.MenuSessionRunning && !plugin.MenuConnecting && !plugin.LocalDisconnectPending)
            {
                if (multiplayer == null) multiplayer = new MultiplayerScreen(plugin);
                multiplayer.Show();
                if (multiplayer.Open) plugin.MenuJoinFailureShown();
            }
            multiplayer?.Tick();
            if (host != null)
            {
                // The host screen belongs to the pause menu; it closes with it.
                if (host.Open && escapeMenu != null && !escapeMenu.isOpen) host.Close();
                host.Tick();
            }

#if MONMP_DEVELOPMENT
            if (UiDump.Requested) DumpTick();
#endif

        }

        private void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                Debug.LogWarning("MonMP could not add the " + what + ": " + ex.Message);
                if (what.StartsWith("main", StringComparison.Ordinal)) mainButton = new GameObject("MonMP unavailable"); else pauseButton = new GameObject("MonMP unavailable");
            }
        }

        private void AddMainButton()
        {
            var newGame = mainMenu!.GetComponentsInChildren<ButtonManager>(true).FirstOrDefault(b => b.name == "Button_NewGame")
                ?? throw new InvalidOperationException("Button_NewGame is missing.");
            mainButton = Clone(newGame.gameObject, "Button_MonMP_Multiplayer", "MULTIPLAYER", OpenMultiplayer);
            mainButton.transform.SetSiblingIndex(newGame.transform.GetSiblingIndex() + 1);
            // The native column spaces sections with placeholders; keep NEW GAME and MULTIPLAYER together.
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)newGame.transform.parent);
        }

        private void AddPauseButton()
        {
            var tabs = MainButtons.GetValue(escapeMenu) as Button[];
            if (tabs == null || tabs.Length == 0) throw new InvalidOperationException("The pause menu has no main buttons.");
            var template = tabs[tabs.Length - 1];
            pauseButton = Clone(template.gameObject, "Button_MonMP_Multiplayer", "MULTIPLAYER", OpenHost);
            // First in the bottom row, ahead of QUIT GAME and MAIN MENU.
            pauseButton.transform.SetSiblingIndex(0);
        }

        // A copy of a native MUIP button with its persistent actions and localisation removed.
        private static GameObject Clone(GameObject template, string name, string text, UnityAction click)
        {
            var clone = UnityEngine.Object.Instantiate(template, template.transform.parent, false);
            clone.name = name;
            MenuKit.StripLocalization(clone);
            var button = clone.GetComponent<Button>();
            if (button != null) { button.onClick = new Button.ButtonClickedEvent(); button.interactable = true; }
            var manager = clone.GetComponent<ButtonManager>();
            if (manager != null)
            {
                manager.onClick = new UnityEvent(); manager.onDoubleClick = new UnityEvent();
                manager.isInteractable = true;
                manager.buttonText = text;
                var icon = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(sprite => sprite.name == "User");
                if (icon != null) manager.buttonIcon = icon;
                foreach (var label in new[] { manager.normalText, manager.highlightedText, manager.disabledText }) if (label != null) label.text = text;
                manager.UpdateUI();
            }
            if (button != null) button.onClick.AddListener(click);
            else if (manager != null) manager.onClick.AddListener(click);
            clone.SetActive(true);
            return clone;
        }

        // Director: the menu actor's join, on screen.

#if MONMP_DEVELOPMENT
        internal MultiplayerScreen DirectorMultiplayer() { OpenMultiplayer(); return multiplayer!; }


        internal void TestOpenHostForSpectate() => OpenHost();
        internal HostScreen TestAppearanceHost() { OpenHost(); return host!; }
#endif


        private void OpenMultiplayer()
        {
            if (multiplayer == null) multiplayer = new MultiplayerScreen(plugin);
            multiplayer.Show();
        }

        // An accepted Steam invitation uses the same name, password, cancellation and
        // protected world-loading flow as a typed lobby ID. Never replace an open local world.
        internal void ShowSteamInvite(string lobbyId)
        {
            pendingSteamLobbyId = lobbyId; nextSteamInviteAttempt = 0; steamInviteFailure = "";
            plugin.LogSteamInvite("queued join UI; scene=" + SceneManager.GetActiveScene().buildIndex +
                "; mainMenu=" + (mainMenu != null) + "; session=" + plugin.MenuSessionRunning +
                "; connecting=" + plugin.MenuConnecting + "; disconnecting=" + plugin.LocalDisconnectPending + ".");
        }

        private void OpenHost()
        {
            if (host == null) host = new HostScreen(plugin);
            host.Show();
        }

        // ui-dump.request lines: "mp" opens the Direct browser, "mp-steam" the Steam screen,
        // "mp-add" the Direct add dialog; "host-steam" selects the Steam host setup.
        // "invite" captures the screen opened by +connect_lobby without opening it here. Each is
        // screenshotted after the plain main menu dump. The pause menu is dumped once a world is ready.

#if MONMP_DEVELOPMENT
        private void DumpTick()
        {
            var lines = UiDump.Lines;
            bool wantsHost = lines.Contains("host") || lines.Contains("host-steam");
            if (mainMenu != null && mainButton != null && Time.unscaledTime - mainSeenAt > 8f && Time.unscaledTime >= dumpAt)
            {
                switch (dumpStep)
                {
                    case 0:
                        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects().Where(r => r.GetComponentsInChildren<Canvas>(true).Length > 0))

#if MONMP_DEVELOPMENT
                            UiDump.Write("main-" + root.name, root.transform);
#else
{}
#endif


#if MONMP_DEVELOPMENT
                        UiDump.Screenshot("main");
#endif

                        Next(lines.Contains("invite") ? 2 : lines.Contains("mp") || lines.Contains("mp-steam") ? 1 : wantsHost ? 5 : lines.Contains("join") ? 8 : 9);
                        break;
                    case 1:
                        OpenMultiplayer();
                        if (lines.Contains("mp-steam")) multiplayer!.TestSelectSteam(); else multiplayer!.TestSelectFirst();
                        Next(2); break;
                    case 2:

#if MONMP_DEVELOPMENT
                        UiDump.Screenshot("mp");


                        UiDump.Write("mp", multiplayer!.TestRoot);
#endif

                        Debug.Log("UI dump multiplayer rows: " + multiplayer!.TestRows);
                        Next(lines.Contains("mp-add") ? 3 : wantsHost ? 5 : lines.Contains("join") ? 8 : 9);
                        break;
                    case 3: multiplayer!.TestOpenDialog(-1); Next(4); break;
                    case 4:
#if MONMP_DEVELOPMENT
UiDump.Screenshot("mp-add");
#endif
 Next(wantsHost ? 5 : lines.Contains("join") ? 8 : 9); break;
                    // The pause menu's host screen, shown over the main menu for a layout check.
                    case 5:
                        multiplayer?.Close(); OpenHost();
                        if (lines.Contains("host-steam")) host!.TestSelectSteam();
                        Next(6); break;
                    case 6:
#if MONMP_DEVELOPMENT
UiDump.Screenshot("host");
#endif
 Next(7); break;
                    case 7: host!.Close(); Next(lines.Contains("join") ? 8 : 9); break;
                    // Joining the first (unreachable) saved server: the connecting screen, then its failure.
                    case 8: OpenMultiplayer(); multiplayer!.TestJoinFirst(); Next(10); break;
                    case 10:
#if MONMP_DEVELOPMENT
UiDump.Screenshot("mp-joining");
#endif
 dumpStep = 11; dumpAt = Time.unscaledTime + 20f; break;
                    case 11:
#if MONMP_DEVELOPMENT
UiDump.Screenshot("mp-join-failed");
#endif
 Next(9); break;
                }
            }
            if (!pauseDumped && escapeMenu != null && pauseButton != null && Patches.NativeLoadGate.Ready && Time.unscaledTime - pauseSeenAt > 10f)
            {
                if (dumpStep < 20) { escapeMenu.Open_Force(); dumpStep = 20; pauseSeenAt = Time.unscaledTime - 8f; return; }
                if (dumpStep == 20)
                {
                    var root = escapeMenu.transform;
                    while (root.parent != null) root = root.parent;

#if MONMP_DEVELOPMENT
                    UiDump.Write("pause-" + root.name, root);


                    UiDump.Screenshot("pause");
#endif

                    OpenHost();
                    if (lines.Contains("host-steam")) host!.TestSelectSteam();
                    dumpStep = 21; pauseSeenAt = Time.unscaledTime - 8f; return;
                }
                pauseDumped = true;

#if MONMP_DEVELOPMENT
                UiDump.Screenshot("pause-host");
#endif

            }
        }


        private void Next(int step) { dumpStep = step; dumpAt = Time.unscaledTime + 1.5f; }
#endif

    }
}
