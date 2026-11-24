using System;
using System.Globalization;
using System.Linq;
using MonMP.Core.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    // Pause menu > MULTIPLAYER: open the loaded game to other players, or see who is connected.
    internal sealed class HostScreen
    {
        private readonly MonMPPlugin plugin;
        private readonly AppearancePicker appearance;
        private readonly ErrorCopyButton copyErrors;
        private readonly RectTransform root, box, setup, directSetup, running, sessionPage, playerPage;
        private readonly TMP_InputField nameField, portField, passwordField;
        private readonly Toggle lan, router, showPlayersOnMap, truckExtraSeats, freezeCargo, cheatsEnabled;
        private readonly TextMeshProUGUI status, title, summary, steamHint, addressHint;
        private readonly RectTransform players;
        private readonly Button repair;
        private readonly Button sessionTab, playersTab;
        private bool showingPlayers = true;
        private readonly System.Collections.Generic.Dictionary<uint, Toggle> cheatPermissions = new System.Collections.Generic.Dictionary<uint, Toggle>();
        private string playerKey = "";
        private readonly Button start, stop, steamMode, directMode, copyAddress, invite;
        private bool attempted;
        private string? validationError;
        private float nextRefresh;
        private readonly System.Collections.Generic.Dictionary<uint, TMPro.TextMeshProUGUI> playerNotes = new System.Collections.Generic.Dictionary<uint, TMPro.TextMeshProUGUI>();
        private readonly System.Collections.Generic.Dictionary<uint, Button> spectateButtons = new System.Collections.Generic.Dictionary<uint, Button>();
        private string spectateNotice = "";
        private float spectateNoticeUntil;
        private bool spectateNoticeError;

        internal bool Open => root != null && root.gameObject.activeSelf;

        internal HostScreen(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            root = MenuKit.Canvas("MonMP Host", 32000);
            MenuKit.Fill(MenuKit.Rect("Backdrop", root).Stretch(), new Color(0, 0, 0, 0.55f), rounded: false);
            box = MenuKit.Rect("Box", root).Center(1040, 800);
            MenuKit.Fill(box, new Color32(0x1E, 0x24, 0x2B, 0xF8), roundness: 4);
            MenuKit.Layout<VerticalLayoutGroup>(box, 14, 50, 50, 36, 36).childForceExpandWidth = true;
            var header = MenuKit.Rect("Header", box).Size(height: 60);
            MenuKit.Layout<HorizontalLayoutGroup>(header, 16).childForceExpandHeight = true;
            title = MenuKit.Label(header, "HOST THIS GAME", 40, MenuKit.Bold, align: TextAlignmentOptions.Center).Size(flexWidth: 1);
            MenuKit.Button(header, "Appearance", OpenAppearance, size: 22).Size(190);

            setup = MenuKit.Rect("Setup", box).Size(flexHeight: 1);
            MenuKit.Layout<VerticalLayoutGroup>(setup, 14).childForceExpandWidth = true;
            var transports = MenuKit.Rect("Connection method", setup).Size(height: 48);
            MenuKit.Layout<HorizontalLayoutGroup>(transports, 16).childForceExpandHeight = true;
            steamMode = MenuKit.Button(transports, "Steam", () => SelectTransport(true), size: 24).Size(flexWidth: 1);
            directMode = MenuKit.Button(transports, "Direct IP / LAN", () => SelectTransport(false), size: 24).Size(flexWidth: 1);
            nameField = Field(setup, "Player name", "Your name", 48);
            passwordField = Field(setup, "Password", "Optional", 64);
            passwordField.contentType = TMP_InputField.ContentType.Password;
            steamHint = MenuKit.Label(setup, "", 24, color: MenuKit.Muted).Size(height: 124);
            steamHint.enableWordWrapping = true;
            directSetup = MenuKit.Rect("Direct IP setup", setup);
            MenuKit.Layout<VerticalLayoutGroup>(directSetup, 10).childForceExpandWidth = true;
            portField = Field(directSetup, "UDP port", "27020", 5);
            portField.contentType = TMP_InputField.ContentType.IntegerNumber;
            nameField.onValueChanged.AddListener(_ => ClearValidation());
            portField.onValueChanged.AddListener(_ => ClearValidation());
            passwordField.onValueChanged.AddListener(_ => ClearValidation());
            lan = MenuKit.Check(directSetup, "Show this game to players on my local network", true, _ => { }).Size(height: 44);
            router = MenuKit.Check(directSetup, "Ask my router to open the port automatically", false, _ => { }).Size(height: 44);
            var hostingHint = MenuKit.Label(directSetup, "Players outside your network need your public address and the UDP port forwarded.", 20, color: MenuKit.Muted).Size(height: 58);
            hostingHint.enableWordWrapping = true;

            running = MenuKit.Rect("Running", box).Size(flexHeight: 1);
            MenuKit.Layout<VerticalLayoutGroup>(running, 10).childForceExpandWidth = true;
            var tabs = MenuKit.Rect("Pages", running).Size(height: 48);
            MenuKit.Layout<HorizontalLayoutGroup>(tabs, 16).childForceExpandHeight = true;
            playersTab = MenuKit.Button(tabs, "Players", () => SelectPage(true), size: 24).Size(flexWidth: 1);
            sessionTab = MenuKit.Button(tabs, "Session", () => SelectPage(false), size: 24).Size(flexWidth: 1);
            playerPage = MenuKit.Rect("Players page", running).Size(flexHeight: 1);
            MenuKit.Layout<VerticalLayoutGroup>(playerPage, 10).childForceExpandWidth = true;
            MenuKit.Scroll(playerPage, out players).Size(flexHeight: 1);
            sessionPage = MenuKit.Rect("Session page", running).Size(flexHeight: 1);
            MenuKit.Layout<VerticalLayoutGroup>(sessionPage, 16).childForceExpandWidth = true;
            summary = MenuKit.Label(sessionPage, "", 26, MenuKit.Semibold, MenuKit.Good).Size(height: 50);
            summary.enableWordWrapping = true;
            var actions = MenuKit.Rect("Session actions", sessionPage).Size(height: 52);
            MenuKit.Layout<HorizontalLayoutGroup>(actions, 16).childForceExpandHeight = true;
            copyAddress = MenuKit.Button(actions, "Copy address", CopyAddress, size: 22).Size(flexWidth: 1);
            invite = MenuKit.Button(actions, "Invite friends", plugin.MenuSteamInvite, size: 22).Size(flexWidth: 1);
            repair = MenuKit.Button(actions, "Repair shared world", plugin.MenuRepair, size: 22).Size(flexWidth: 1);
            addressHint = MenuKit.Label(sessionPage, "", 20, color: MenuKit.Muted).Size(height: 52);
            addressHint.enableWordWrapping = true;
            MenuKit.Label(sessionPage, "Session options", 24, MenuKit.Semibold).Size(height: 34);
            MenuKit.Scroll(sessionPage, out var sessionOptions).Size(flexHeight: 1);
            showPlayersOnMap = MenuKit.Check(sessionOptions, "Show players on map", true, plugin.MenuSetShowPlayersOnMap).Size(height: 44);
            truckExtraSeats = MenuKit.Check(sessionOptions, "Truck bed and front middle seats", true, plugin.MenuSetTruckExtraSeatsEnabled).Size(height: 44);
            freezeCargo = MenuKit.Check(sessionOptions, "Freeze settled cargo from player drops", false, plugin.MenuSetFreezeVehicleCargo).Size(height: 44);
            cheatsEnabled = MenuKit.Check(sessionOptions, "Cheats", false, RequestCheats).Size(height: 44);
            SelectPage(true);

            status = MenuKit.Label(box, "", 22, color: MenuKit.Muted, align: TextAlignmentOptions.Center).Size(height: 72);
            status.enableWordWrapping = true;
            status.enableAutoSizing = true; status.fontSizeMin = 18; status.fontSizeMax = 22;
            copyErrors = new ErrorCopyButton(plugin, box);
            var buttons = MenuKit.Rect("Buttons", box).Size(height: 58);
            MenuKit.Layout<HorizontalLayoutGroup>(buttons, 16).childForceExpandHeight = true;
            start = MenuKit.Button(buttons, "Start hosting", Start, primary: true).Size(flexWidth: 1);
            stop = MenuKit.Button(buttons, "Stop hosting", () =>
            {
                attempted = false;
                if (plugin.CanLeaveWithoutSaving) plugin.LeaveWithoutSaving(); else plugin.MenuDisconnect();
            }).Size(flexWidth: 1);
            MenuKit.Button(buttons, "Back", Close).Size(flexWidth: 1);
            appearance = new AppearancePicker(plugin, root);
        }

        private void RequestCheats(bool enabled)
        {
            // A cancelled warning leaves this off. Enabled cheats cannot be switched
            // back off for this save, matching the native menu.
            cheatsEnabled.SetIsOnWithoutNotify(plugin.MenuCheatsEnabled);
            if (enabled && plugin.MenuRequestEnableCheats()) Close();
        }

        private static TMP_InputField Field(Transform parent, string label, string hint, int limit)
        {
            var row = MenuKit.Rect(label, parent).Size(height: 56);
            MenuKit.Layout<HorizontalLayoutGroup>(row, 16, align: TextAnchor.MiddleLeft).childForceExpandHeight = true;
            MenuKit.Label(row, label, 24, color: MenuKit.Muted).Size(190);
            return MenuKit.Input(row, hint, limit).Size(flexWidth: 1);
        }

        private void OpenAppearance() => appearance.Show();
        internal bool CloseAppearance() => appearance.CloseForBack();
#if MONMP_DEVELOPMENT
        internal AppearancePicker TestAppearance => appearance;
        internal void TestOpenAppearance() => OpenAppearance();
#endif
        internal void Show()
        {
            nameField.text = plugin.MenuPlayerName;
            portField.text = plugin.MenuPort.ToString(CultureInfo.InvariantCulture);
            lan.isOn = plugin.MenuAdvertiseLan;
            router.isOn = plugin.MenuMapRouter;
            attempted = false; validationError = null; nextRefresh = 0; spectateNotice = ""; spectateNoticeUntil = 0;
            root.gameObject.SetActive(true);
            SelectPage(true);
            Tick();
        }

        internal void Close() { appearance.Close(); if (Open) root.gameObject.SetActive(false); }
        internal void Destroy() { appearance.Dispose(); if (root != null) UnityEngine.Object.Destroy(root.gameObject); }

#if MONMP_DEVELOPMENT
        internal void TestSelectSteam() => SelectTransport(true);
#endif

        internal void Tick()
        {
            if (!Open) return;
            copyErrors.Tick();
            if (appearance.Open) { appearance.Tick(); return; }
            MenuKit.FitWidth(box, 1040);
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;
            bool session = plugin.MenuSessionRunning, host = plugin.MenuIsHost, disconnecting = plugin.LocalDisconnectPending;
            bool steam = plugin.MenuUseSteam;
            bool connecting = plugin.MenuConnecting;
            directSetup.gameObject.SetActive(!steam);
            steamHint.gameObject.SetActive(steam);
            steamHint.text = plugin.MenuSteamAvailable
                ? "Start hosting, then invite your friends through Steam. Steam chooses a direct connection or Valve relay when available."
                : plugin.MenuSteamStatus;
            steamMode.SetEnabled(!session && !connecting && !disconnecting);
            directMode.SetEnabled(!session && !connecting && !disconnecting);
            steamMode.GetComponentInChildren<TextMeshProUGUI>().color = steam ? MenuKit.Accent : Color.white;
            directMode.GetComponentInChildren<TextMeshProUGUI>().color = steam ? Color.white : MenuKit.Accent;
            setup.gameObject.SetActive(!session);
            running.gameObject.SetActive(session);
            start.gameObject.SetActive(!session && !connecting);
            stop.gameObject.SetActive(session || connecting);
            stop.SetText(disconnecting ? "Disconnecting…" : !session && connecting ? "Cancel hosting" : host ? plugin.CanLeaveWithoutSaving ? "Leave without saving" : "Stop hosting" : "Disconnect");
            stop.SetEnabled(!disconnecting);
            title.text = disconnecting ? "DISCONNECTING" : !session && connecting ? "STARTING HOST" : !session ? "HOST THIS GAME" : host ? "HOSTING" : "CONNECTED";
            start.SetEnabled(plugin.MenuCanHost && !disconnecting && !connecting && (!steam || plugin.MenuSteamAvailable));
            if (session)
            {
                showPlayersOnMap.SetIsOnWithoutNotify(plugin.MenuShowPlayersOnMap);
                showPlayersOnMap.interactable = host && !disconnecting;
                truckExtraSeats.SetIsOnWithoutNotify(plugin.MenuTruckExtraSeatsEnabled);
                truckExtraSeats.interactable = host && !disconnecting;
                freezeCargo.SetIsOnWithoutNotify(plugin.MenuFreezeVehicleCargo);
                freezeCargo.interactable = host && !disconnecting;
                var peers = plugin.Session!.Peers.Values.OrderBy(p => p.Id).ToArray();
                cheatsEnabled.SetIsOnWithoutNotify(plugin.MenuCheatsEnabled);
                cheatsEnabled.interactable = host && !disconnecting && plugin.MenuCanEnableCheats;
                cheatsEnabled.GetComponentInChildren<TextMeshProUGUI>().text = plugin.MenuCheatsEnabled ? "Cheats (enabled for this save)" : "Cheats";
                cheatsEnabled.GetComponentInChildren<TextMeshProUGUI>().color = cheatsEnabled.interactable ? Color.white : MenuKit.Muted;
                summary.text = plugin.MenuTransportStatus + (host && !steam
                    ? "  ·  UDP " + plugin.MenuPort + (plugin.MenuAdvertiseLan ? "  ·  LAN visible" : "") : "");
                copyAddress.gameObject.SetActive(!steam);
                copyAddress.SetText("Copy address");
                copyAddress.SetEnabled(!disconnecting);
                invite.gameObject.SetActive(steam && host);
                invite.SetEnabled(!disconnecting && plugin.MenuSteamLobbyId.Length > 0 && plugin.MenuSteamAvailable);
                addressHint.text = steam ? "Invite friends using Steam. Accepted invitations open the join screen automatically."
                    : "Host address is for LAN use. Outside your network, share your public address and port.";
                string key = host + ":" + string.Join(",", peers.Select(p => p.Id + ":" + p.DisplayName));
                if (key != playerKey)
                {
                    playerKey = key;
                    playerNotes.Clear(); spectateButtons.Clear(); cheatPermissions.Clear();
                    foreach (Transform child in players) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); }
                    foreach (var peer in peers)
                    {
                        var row = MenuKit.Rect("Player", players).Size(height: 108);
                        MenuKit.Fill(row, new Color(0, 0, 0, .16f), rounded: false);
                        MenuKit.Layout<VerticalLayoutGroup>(row, 8, 14, 14, 10, 10).childForceExpandWidth = true;
                        var details = MenuKit.Rect("Details", row).Size(height: 32);
                        MenuKit.Layout<HorizontalLayoutGroup>(details, 12).childForceExpandHeight = true;
                        var name = MenuKit.Label(details, peer.DisplayName + (peer.Id == 0 ? " (host)" : ""), 24).Size(flexWidth: 1);
                        name.enableAutoSizing = true; name.fontSizeMin = 18; name.fontSizeMax = 24;
                        playerNotes[peer.Id] = MenuKit.Label(details, "", 20, color: MenuKit.Muted);
                        playerNotes[peer.Id].Size(width: 170);
                        var controls = MenuKit.Rect("Actions", row).Size(height: 38);
                        MenuKit.Layout<HorizontalLayoutGroup>(controls, 18).childForceExpandHeight = true;
                        if (peer.Id != plugin.Session!.LocalPeerId)
                            spectateButtons[peer.Id] = MenuKit.Button(controls, "Spectate", () => StartSpectate(peer.Id), size: 20).Size(width: 150);
                        if (host && peer.Id != 0)
                        {
                            cheatPermissions[peer.Id] = MenuKit.Check(controls, "Allow cheats", plugin.MenuCheatsGranted(peer.Id), allowed => plugin.MenuSetCheatPermission(peer.Id, allowed)).Size(width: 240);
                            MenuKit.Rect("Gap", controls).Size(flexWidth: 1);
                            MenuKit.Button(controls, "Kick", () => plugin.MenuKick(peer.Id), size: 20).Size(width: 100);
                        }
                        else if (peer.Id != 0) MenuKit.Label(controls, "Cheat access is controlled by the host", 20, color: MenuKit.Muted).Size(flexWidth: 1);
                    }
                }
                // Round trip and who is away, here rather than over the pause menu (#241).
                var live = plugin.Session!;
                foreach (var peer in peers)
                {
                    if (cheatPermissions.TryGetValue(peer.Id, out var permission))
                    { permission.SetIsOnWithoutNotify(plugin.MenuCheatsGranted(peer.Id)); permission.interactable = !disconnecting; }
                    if (!playerNotes.TryGetValue(peer.Id, out var note) || note == null) continue;
                    bool self = peer.Id == live.LocalPeerId;
                    string ping = self ? "you" : live.IsHost || peer.Id == 0 ? live.MetricsFor(peer.Id).RoundTripTimeMs + " ms" : "";
                    note.text = ping + (plugin.Activity.IsAfk(peer.Id, live.LocalPeerId) ? (ping.Length > 0 ? "  ·  " : "") + "AFK" : "");
                }
                foreach (var peer in peers)
                {
                    if (!spectateButtons.TryGetValue(peer.Id, out var spectate)) continue;
                    spectate.SetEnabled(!disconnecting && plugin.MenuCanQueueSpectate(peer.Id));
                }
                repair.gameObject.SetActive(!host);
                repair.SetEnabled(!disconnecting);
                status.text = !disconnecting && !steam && plugin.MenuMapRouter && host ? plugin.MenuRouterStatus : plugin.MenuStatus;
                status.color = MenuKit.Muted;
            }
            else if (validationError != null) { status.text = validationError; status.color = MenuKit.Error; }
            else if (attempted) { status.text = plugin.MenuStatus; status.color = connecting ? MenuKit.Muted : MenuKit.Error; }
            else
            {
                status.text = steam && !plugin.MenuSteamAvailable ? plugin.MenuSteamStatus : plugin.MenuCanHost ? "Everyone who joins plays in this world. It is saved on your machine."
                    : "Hosting opens up once the world has loaded (after the tutorial).";
                status.color = MenuKit.Muted;
            }
            if (session && !disconnecting && Time.unscaledTime < spectateNoticeUntil)
            { status.text = spectateNotice; status.color = spectateNoticeError ? MenuKit.Error : MenuKit.Good; }
        }

        private void SelectPage(bool playersVisible)
        {
            showingPlayers = playersVisible;
            playerPage.gameObject.SetActive(showingPlayers); sessionPage.gameObject.SetActive(!showingPlayers);
            playersTab.GetComponentInChildren<TextMeshProUGUI>().color = showingPlayers ? MenuKit.Accent : Color.white;
            sessionTab.GetComponentInChildren<TextMeshProUGUI>().color = showingPlayers ? Color.white : MenuKit.Accent;
        }

        private void StartSpectate(uint peer)
        {
            if (plugin.MenuStartSpectate(peer)) return;
            spectateNotice = "Spectating needs synchronized walking, empty hands, and no held item, tool, save, or modal window.";
            spectateNoticeError = true;
            spectateNoticeUntil = Time.unscaledTime + 8f;
        }

        private void CopyAddress()
        {
            if (plugin.MenuUseSteam) return;
            string address = plugin.MenuJoinAddress;
            if (address.Length == 0) return;
            GUIUtility.systemCopyBuffer = address;
            spectateNotice = "Copied address: " + address;
            spectateNoticeError = false;
            spectateNoticeUntil = Time.unscaledTime + 8f;
            nextRefresh = 0;
        }

        private void ClearValidation()
        {
            validationError = null; attempted = false; nextRefresh = 0;
        }

        private void SelectTransport(bool steam)
        {
            if (plugin.MenuSessionRunning || plugin.MenuConnecting || plugin.LocalDisconnectPending) return;
            if (plugin.MenuUseSteam == steam) return;
            plugin.MenuUseSteam = steam;
            ClearValidation();
        }

        private void ShowValidation(string message, TMP_InputField field)
        {
            validationError = message; status.text = message; status.color = MenuKit.Error;
            nextRefresh = 0; field.ActivateInputField();
        }

        private void Start()
        {
            string player = nameField.text.Trim();
            try { Wire.ValidateName(player); }
            catch (Exception) { ShowValidation("Use a player name of 1 to 48 UTF-8 bytes, without control characters or < >.", nameField); return; }
            if (plugin.MenuUseSteam)
            {
                if (!plugin.MenuSteamAvailable) { validationError = plugin.MenuSteamStatus; nextRefresh = 0; return; }
                validationError = null; attempted = true; nextRefresh = 0;
                plugin.MenuSteamHost(player, passwordField.text);
                return;
            }
            if (!int.TryParse(portField.text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            { ShowValidation("The port must be a number from 1 to 65535.", portField); return; }
            validationError = null; attempted = true; nextRefresh = 0;
            plugin.MenuHost(player, port, passwordField.text, lan.isOn, router.isOn);
        }
    }
}
