using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    // Main menu > MULTIPLAYER: Steam invitations by default, with the existing saved-server,
    // recent-host and local-network browser available in Direct IP / LAN mode.
    internal sealed class MultiplayerScreen
    {
        private enum Source { Saved, Lan, Recent }

        private sealed class Entry
        {
            internal string Key = "", Name = "", Detail = "", Tag = "";
            internal Source Source;
            internal DirectHostEndpoint Endpoint;
            internal int Saved = -1;
            internal bool Compatible = true, Password;
        }

        private sealed class SavedHostResolution
        {
            internal Task<IPAddress[]> Lookup = null!;
            internal IPAddress[] Addresses = Array.Empty<IPAddress>();
            internal float StartedAt, ResolvedAt;
            internal bool Resolved;
        }

        private sealed class RowView
        {
            internal Entry Entry = null!;
            internal Image Selection = null!;
            internal TextMeshProUGUI Name = null!, Detail = null!, Tag = null!;
        }

        private readonly MonMPPlugin plugin;
        private readonly AppearancePicker appearance;
        private readonly ErrorCopyButton copyErrors;
        private readonly RectTransform root, column, content, directList, directTop, directBottom, steamPanel, steamActions, dialog, dialogBox, connecting, connectingPanel;
        private readonly TMP_InputField nameField, passwordField, dialogName, dialogAddress;
        private readonly TextMeshProUGUI status, steamStatus, dialogTitle, dialogError, connectingTitle, connectingStatus;
        private readonly Button join, edit, delete, steamJoin, steamMode, directMode, dialogDone, cancelJoin;
        private readonly GameObject dialogNameRow;
        private readonly List<RowView> rows = new List<RowView>();
        private readonly Dictionary<string, SavedHostResolution> savedHostResolutions = new Dictionary<string, SavedHostResolution>(StringComparer.OrdinalIgnoreCase);
        private string? listKey;
        private string? nameError = PlayerNamePolicy.RequiredMessage;
        private string selected = "", joiningName = "", joiningLobbyId = "", invitedLobbyId = "", deleteArmedKey = "";
        private string retainedFailure = "";
        private DirectHostEndpoint joiningEndpoint;
        private float nextRefresh, lastClickAt, deleteArmedUntil;
        private int editing = -2; // -2 closed, -1 adding, >=0 editing that saved index, -3 direct connect
        private bool joinAttempted, wasConnecting, cancelling, passwordRejected, passwordRetry, joiningSteam;

        internal bool Open => root != null && root.gameObject.activeSelf;

        internal MultiplayerScreen(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            root = MenuKit.Canvas("MonMP Multiplayer", 500);
            MenuKit.Fill(MenuKit.Rect("Backdrop", root).Stretch(), MenuKit.Backdrop, rounded: false);

            column = MenuKit.Rect("Column", root);
            column.anchorMin = new Vector2(0.5f, 0); column.anchorMax = new Vector2(0.5f, 1); column.pivot = new Vector2(0.5f, 0.5f);
            column.sizeDelta = new Vector2(1180, 0); column.anchoredPosition = Vector2.zero;
            MenuKit.Layout<VerticalLayoutGroup>(column, 16, top: 50, bottom: 50).childForceExpandWidth = true;

            MenuKit.Label(column, "MULTIPLAYER", 46, MenuKit.Bold, align: TextAlignmentOptions.Center).Size(height: 64);

            var transports = ButtonRow(column);
            steamMode = MenuKit.Button(transports, "Steam", () => SelectTransport(true), size: 26).Size(flexWidth: 1);
            directMode = MenuKit.Button(transports, "Direct IP / LAN", () => SelectTransport(false), size: 26).Size(flexWidth: 1);

            var nameRow = FieldRow(column, "Player name", out nameField, "Your name", 48);
            MenuKit.Button(nameRow, "Appearance", OpenAppearance, size: 22).Size(190);
            nameField.onValueChanged.AddListener(value =>
            {
                nameError = PlayerNamePolicy.ValidationError(value);
                joinAttempted = false;
            });
            FieldRow(column, "Password", out passwordField, "If the host set one", 64);
            passwordField.contentType = TMP_InputField.ContentType.Password;

            steamPanel = MenuKit.Rect("Steam join", column).Size(flexHeight: 1);
            MenuKit.Layout<VerticalLayoutGroup>(steamPanel, 20, top: 16).childForceExpandWidth = true;
            var steamInstructions = MenuKit.Label(steamPanel,
                "Join a friend's invitation from the Steam overlay.\n\nTo host, load your world and open Multiplayer in the pause menu, then choose Invite friends.",
                27, color: MenuKit.Muted).Size(height: 164);
            steamInstructions.enableWordWrapping = true;
            steamStatus = MenuKit.Label(steamPanel, "", 23, color: MenuKit.Muted).Size(height: 96);
            steamStatus.enableWordWrapping = true;
            directList = MenuKit.Scroll(column, out content).GetComponent<RectTransform>().Size(flexHeight: 1);

            status = MenuKit.Label(column, "", 24, color: MenuKit.Muted, align: TextAlignmentOptions.Center).Size(height: 76);
            status.enableWordWrapping = true;
            status.enableAutoSizing = true; status.fontSizeMin = 18; status.fontSizeMax = 24;
            copyErrors = new ErrorCopyButton(plugin, column);

            steamActions = ButtonRow(column);
            steamJoin = MenuKit.Button(steamActions, "Join Steam game", JoinSteam, primary: true).Size(flexWidth: 1);
            MenuKit.Button(steamActions, "Back", Close).Size(flexWidth: 1);
            directTop = ButtonRow(column);
            join = MenuKit.Button(directTop, "Join Server", JoinSelected, primary: true).Size(flexWidth: 1);
            MenuKit.Button(directTop, "Direct Connect", () => OpenDialog(-3)).Size(flexWidth: 1);
            MenuKit.Button(directTop, "Add Server", () => OpenDialog(-1)).Size(flexWidth: 1);
            directBottom = ButtonRow(column);
            edit = MenuKit.Button(directBottom, "Edit", () => { var e = Selected; if (e?.Saved >= 0) OpenDialog(e.Saved); }).Size(flexWidth: 1);
            delete = MenuKit.Button(directBottom, "Delete", DeleteSelected).Size(flexWidth: 1);
            MenuKit.Button(directBottom, "Refresh", () => { listKey = null; nextRefresh = 0; }).Size(flexWidth: 1);
            MenuKit.Button(directBottom, "Back", Close).Size(flexWidth: 1);

            // Add / edit / direct connect dialog.
            dialog = MenuKit.Rect("Dialog", root).Stretch();
            MenuKit.Fill(dialog, new Color(0, 0, 0, 0.6f), rounded: false);
            dialogBox = MenuKit.Rect("Box", dialog).Center(820, 540);
            var box = dialogBox;
            MenuKit.Fill(box, new Color32(0x1E, 0x24, 0x2B, 0xFF), roundness: 4);
            MenuKit.Layout<VerticalLayoutGroup>(box, 12, 40, 40, 30, 30).childForceExpandWidth = true;
            dialogTitle = MenuKit.Label(box, "", 36, MenuKit.Bold, align: TextAlignmentOptions.Center).Size(height: 54);
            dialogNameRow = FieldRow(box, "Server name", out dialogName, "My server", 48).gameObject;
            FieldRow(box, "Address", out dialogAddress, "host or host:port", 300);
            var addressHint = MenuKit.Label(box, "IP address or hostname, with :port if the host changed it from " + plugin.MenuPort + ".", 20, color: MenuKit.Muted).Size(height: 52);
            addressHint.enableWordWrapping = true;
            dialogError = MenuKit.Label(box, "", 22, color: MenuKit.Error, align: TextAlignmentOptions.Center).Size(height: 72);
            dialogError.enableWordWrapping = true;
            dialogError.enableAutoSizing = true; dialogError.fontSizeMin = 18; dialogError.fontSizeMax = 22;
            MenuKit.Rect("Gap", box).Size(flexHeight: 1);
            var dialogButtons = ButtonRow(box);
            dialogDone = MenuKit.Button(dialogButtons, "Done", ConfirmDialog, primary: true).Size(flexWidth: 1);
            MenuKit.Button(dialogButtons, "Cancel", CloseDialog).Size(flexWidth: 1);
            dialog.gameObject.SetActive(false);

            // Joining: the native loader takes over once the host accepts.
            connecting = MenuKit.Rect("Connecting", root).Stretch();
            MenuKit.Fill(connecting, new Color32(0x10, 0x13, 0x17, 0xFF), rounded: false);
            connectingPanel = MenuKit.Rect("Panel", connecting).Center(900, 340);
            var panel = connectingPanel;
            MenuKit.Layout<VerticalLayoutGroup>(panel, 18, align: TextAnchor.MiddleCenter).childForceExpandWidth = true;
            connectingTitle = MenuKit.Label(panel, "", 40, MenuKit.Bold, align: TextAlignmentOptions.Center).Size(height: 110);
            connectingTitle.enableWordWrapping = true;
            connectingTitle.enableAutoSizing = true; connectingTitle.fontSizeMin = 26; connectingTitle.fontSizeMax = 40;
            connectingStatus = MenuKit.Label(panel, "", 26, color: MenuKit.Muted, align: TextAlignmentOptions.Center).Size(height: 100);
            connectingStatus.enableWordWrapping = true;
            connectingStatus.enableAutoSizing = true; connectingStatus.fontSizeMin = 20; connectingStatus.fontSizeMax = 26;
            var cancelRow = MenuKit.Rect("Cancel", panel).Size(height: 60);
            MenuKit.Layout<HorizontalLayoutGroup>(cancelRow, 0, align: TextAnchor.MiddleCenter).childForceExpandHeight = true;
            cancelJoin = MenuKit.Button(cancelRow, "Cancel", CancelJoin).Size(320);
            connecting.gameObject.SetActive(false);
            appearance = new AppearancePicker(plugin, root);
        }

        private static RectTransform ButtonRow(Transform parent)
        {
            var row = MenuKit.Rect("Buttons", parent).Size(height: 58);
            MenuKit.Layout<HorizontalLayoutGroup>(row, 16).childForceExpandHeight = true;
            return row;
        }

        private static RectTransform FieldRow(Transform parent, string label, out TMP_InputField field, string hint, int limit)
        {
            var row = MenuKit.Rect(label, parent).Size(height: 56);
            MenuKit.Layout<HorizontalLayoutGroup>(row, 16, align: TextAnchor.MiddleLeft).childForceExpandHeight = true;
            MenuKit.Label(row, label, 24, color: MenuKit.Muted).Size(170);
            field = MenuKit.Input(row, hint, limit).Size(flexWidth: 1);
            return row;
        }

        private void OpenAppearance() => appearance.Show();
        internal bool CloseAppearance() => appearance.CloseForBack();
#if MONMP_DEVELOPMENT
        internal AppearancePicker TestAppearance => appearance;
        internal void TestOpenAppearance() => OpenAppearance();
#endif
        internal void Show()
        {
            nameField.text = PlayerNamePolicy.IsDefault(plugin.MenuPlayerName) ? "" : plugin.MenuPlayerName;
            nameError = PlayerNamePolicy.ValidationError(nameField.text);
            passwordRejected = false; passwordRetry = false; wasConnecting = false; connecting.gameObject.SetActive(false);
            joinAttempted = false; cancelling = false; cancelJoin.SetEnabled(true); cancelJoin.SetText("Cancel");
            deleteArmedUntil = 0; deleteArmedKey = ""; delete.SetText("Delete");
            listKey = null; nextRefresh = 0;
            retainedFailure = plugin.MenuJoinFailureReason;
            if (retainedFailure.Length != 0)
            {
                joiningSteam = plugin.MenuRetryUsesSteam;
                plugin.MenuUseSteam = joiningSteam;
                joiningLobbyId = invitedLobbyId = plugin.MenuRetrySteamLobbyId;
                joiningEndpoint = plugin.MenuRetryDirectEndpoint;
                joiningName = joiningSteam ? "your friend's Steam game" : joiningEndpoint.Display;
                passwordRetry = true;
            }
            root.gameObject.SetActive(true);
            CloseDialog();
        }

        internal bool ShowSteamInvite(string lobbyId)
        {
            if (plugin.MenuSessionRunning || plugin.MenuConnecting || plugin.LocalDisconnectPending) return false;
            plugin.MenuDismissJoinFailure();
            plugin.MenuUseSteam = true;
            Show();
            // Invitations supply only a lobby ID; the existing name/password policy remains in
            // charge, and a failed password can be edited and retried without losing the invite.
            invitedLobbyId = lobbyId;
            if (nameError != null) nameField.ActivateInputField();
            else steamJoin.Select();
            return Open;
        }

        internal void Close()
        {
            if (!Open) return;
            appearance.Close();
            root.gameObject.SetActive(false);
            plugin.MenuStopBrowsing();
        }

        internal void Destroy() { appearance.Dispose(); if (root != null) UnityEngine.Object.Destroy(root.gameObject); }

        internal void Tick()
        {
            if (!Open) return;
            copyErrors.Tick();
            if (appearance.Open) { appearance.Tick(); return; }
            MenuKit.FitWidth(column, 1180);
            MenuKit.FitWidth(dialogBox, 820);
            MenuKit.FitWidth(connectingPanel, 900);
            bool busy = plugin.MenuConnecting;
            bool steam = plugin.MenuUseSteam;
            steamPanel.gameObject.SetActive(steam);
            steamActions.gameObject.SetActive(steam);
            directList.gameObject.SetActive(!steam);
            directTop.gameObject.SetActive(!steam);
            directBottom.gameObject.SetActive(!steam);
            bool maySelect = !busy && !plugin.MenuSessionRunning && !plugin.LocalDisconnectPending;
            steamMode.SetEnabled(maySelect); directMode.SetEnabled(maySelect);
            steamMode.GetComponentInChildren<TextMeshProUGUI>().color = steam ? MenuKit.Accent : Color.white;
            directMode.GetComponentInChildren<TextMeshProUGUI>().color = steam ? Color.white : MenuKit.Accent;
            if (busy != wasConnecting)
            {
                wasConnecting = busy; connecting.gameObject.SetActive(busy);
                if (!busy) { cancelling = false; cancelJoin.SetEnabled(true); cancelJoin.SetText("Cancel"); }
            }
            if (busy)
            {
                connectingTitle.color = Color.white;
                if (cancelling)
                {
                    connectingTitle.text = "Cancelling join…";
                    connectingStatus.text = plugin.GuestWorld?.EnteredWorld == true
                        ? "Waiting for the game to return to the main menu. Your local save is protected."
                        : "Waiting for the current scene load to finish. Your local save is protected.";
                }
                else
                {
                    connectingTitle.text = "Joining " + joiningName;
                    connectingStatus.text = plugin.MenuStatus;
                }
                return;
            }
            if (!passwordRejected && joinAttempted && plugin.MenuStatus.IndexOf("Incorrect session password.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                passwordRejected = true;
                connecting.gameObject.SetActive(true);
                connectingTitle.text = "Incorrect password";
                connectingTitle.color = MenuKit.Error;
                connectingStatus.text = "The password did not match this server. Enter the host's password and try again.";
                cancelJoin.SetEnabled(true); cancelJoin.SetText("Edit password");
            }
            if (passwordRejected)
            {
                if (Keyboard.current?.escapeKey.wasPressedThisFrame == true) CancelJoin();
                return;
            }
            if (!steam) plugin.MenuBrowseLan();
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.escapeKey.wasPressedThisFrame) { if (editing != -2) CloseDialog(); else Close(); return; }
                if (editing != -2 && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)) { ConfirmDialog(); return; }
            }
            if (steam)
            {
                steamStatus.text = plugin.MenuSteamStatus;
                steamStatus.color = plugin.MenuSteamAvailable ? MenuKit.Muted : MenuKit.Error;
                steamJoin.SetText(passwordRetry ? "Retry Steam join" : "Join Steam game");
                steamJoin.SetEnabled(maySelect && plugin.MenuSteamAvailable && nameError == null && ValidLobbyId(invitedLobbyId));
                if (nameError != null) { status.text = nameError; status.color = MenuKit.Error; }
                else if (retainedFailure.Length != 0) { status.text = Friendly(retainedFailure); status.color = MenuKit.Error; }
                else if (joinAttempted) { status.text = Friendly(plugin.MenuStatus); status.color = MenuKit.Error; }
                else if (passwordRetry) { status.text = "Enter the host's password, then retry this Steam game."; status.color = MenuKit.Muted; }
                else { status.text = ValidLobbyId(invitedLobbyId) ? "Invitation received. Ready to join your friend's game." : "Accept a friend's Steam invitation to join their game."; status.color = MenuKit.Muted; }
                return;
            }
            if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.5f; Refresh(); }
            var entry = Selected;
            join.SetText(passwordRetry ? "Retry join" : "Join Server");
            join.SetEnabled((passwordRetry || entry != null && entry.Compatible) && nameError == null);
            edit.SetEnabled(entry?.Saved >= 0);
            delete.SetEnabled(entry?.Saved >= 0);
            if (deleteArmedUntil > 0 && Time.unscaledTime > deleteArmedUntil)
            { deleteArmedUntil = 0; deleteArmedKey = ""; delete.SetText("Delete"); }
            if (nameError != null) { status.text = nameError; status.color = MenuKit.Error; }
            else if (retainedFailure.Length != 0) { status.text = Friendly(retainedFailure); status.color = MenuKit.Error; }
            else if (joinAttempted) { status.text = Friendly(plugin.MenuStatus); status.color = MenuKit.Error; }
            else if (passwordRetry) { status.text = "Retry " + joiningEndpoint.Display; status.color = MenuKit.Muted; }
            else if (entry?.Compatible == false)
            { status.text = "This server uses a different MonMP version. Match the host's version before joining."; status.color = MenuKit.Error; }
            else { status.text = entry == null ? "Pick a server, or add one. Games on your local network show up here on their own." : entry.Name + "  ·  " + entry.Endpoint.Display; status.color = MenuKit.Muted; }
        }

        private Entry? Selected => rows.Select(r => r.Entry).FirstOrDefault(e => e.Key == selected);

        private static bool ValidLobbyId(string value) =>
            ulong.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0;

        private void SelectTransport(bool steam)
        {
            if (plugin.MenuSessionRunning || plugin.MenuConnecting || plugin.LocalDisconnectPending) return;
            if (plugin.MenuUseSteam == steam) return;
            retainedFailure = ""; plugin.MenuDismissJoinFailure();
            plugin.MenuUseSteam = steam;
            plugin.MenuStopBrowsing();
            CloseDialog();
            passwordRejected = false; passwordRetry = false; joinAttempted = false;
            listKey = null; nextRefresh = 0;
        }

        private List<Entry> Entries()
        {
            var list = new List<Entry>();
            var lan = plugin.MenuLanHosts.Select(host => (host, ok: DirectHostEndpoint.TryCreate(host.Endpoint.Address.ToString(), host.Endpoint.Port, out var endpoint), endpoint))
                .Where(h => h.ok).OrderBy(h => h.host.Session.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var discovered = plugin.MenuDiscoveredHosts.ToList();
            var servers = plugin.MenuServers.Entries;
            var directProbes = new List<IPEndPoint>();
            var savedTargets = new List<(DirectHostEndpoint Endpoint, IPAddress[] Addresses)>();
            for (int i = 0; i < servers.Count; i++)
            {
                var server = servers[i];
                var addresses = ResolveSavedHost(server.Endpoint);
                savedTargets.Add((server.Endpoint, addresses));
                foreach (var address in addresses.Distinct().Take(4))
                    directProbes.Add(new IPEndPoint(address, server.Endpoint.Port));
                var matches = discovered.Where(h => SavedServerPresence.Matches(server.Endpoint,
                    h.Endpoint.Address, h.Endpoint.Port, addresses)).ToList();
                var live = matches.FirstOrDefault(h => h.Session.Compatible) ?? matches.FirstOrDefault();
                list.Add(new Entry
                {
                    Key = "s" + i + server.Endpoint.Display, Source = Source.Saved, Name = server.Name, Endpoint = server.Endpoint, Saved = i,
                    Detail = server.Endpoint.Display,
                    Tag = live != null && !live.Session.Compatible ? "Different MonMP version" :
                        SavedServerPresence.FormatTag(live != null, live?.Session.Players ?? 0, live?.Session.PasswordRequired == true),
                    Compatible = live?.Session.Compatible ?? true, Password = live?.Session.PasswordRequired == true,
                });
            }
            plugin.MenuProbeSavedServers(directProbes);
            foreach (var (host, _, endpoint) in lan)
            {
                if (savedTargets.Any(saved => SavedServerPresence.Matches(saved.Endpoint,
                    host.Endpoint.Address, endpoint.Port, saved.Addresses))) continue;
                list.Add(new Entry
                {
                    Key = "l" + host.Session.SessionId + endpoint.Display, Source = Source.Lan, Name = host.Session.Name + "'s game", Endpoint = endpoint,
                    Detail = "Local network  ·  " + endpoint.Display,
                    Tag = host.Session.Compatible ? Players(host.Session.Players) + (host.Session.PasswordRequired ? "  ·  password" : "") : "Different MonMP version",
                    Compatible = host.Session.Compatible, Password = host.Session.PasswordRequired,
                });
            }
            foreach (var endpoint in plugin.MenuRecentHosts)
            {
                if (list.Any(e => e.Endpoint == endpoint)) continue;
                list.Add(new Entry { Key = "r" + endpoint.Display, Source = Source.Recent, Name = endpoint.Display, Endpoint = endpoint, Detail = "Joined recently", Tag = "Recent" });
            }
            return list;
        }

        private IPAddress[] ResolveSavedHost(DirectHostEndpoint endpoint)
        {
            if (IPAddress.TryParse(endpoint.Host, out var address)) return new[] { address };
            if (!savedHostResolutions.TryGetValue(endpoint.Host, out var lookup))
            {
                // Keep the number of outstanding DNS jobs bounded even if saved addresses are edited
                // repeatedly. Completed lookups can be evicted for a newer saved hostname.
                if (savedHostResolutions.Count >= SavedServerList.MaxEntries)
                {
                    string? completed = savedHostResolutions.FirstOrDefault(pair => pair.Value.Lookup.IsCompleted).Key;
                    if (completed == null) return Array.Empty<IPAddress>();
                    savedHostResolutions.Remove(completed);
                }
                lookup = new SavedHostResolution
                {
                    Lookup = StartSavedHostResolution(endpoint.Host),
                    StartedAt = Time.unscaledTime,
                };
                savedHostResolutions.Add(endpoint.Host, lookup);
            }
            if (lookup.Resolved && Time.unscaledTime - lookup.ResolvedAt >= 60f)
            {
                lookup.Lookup = StartSavedHostResolution(endpoint.Host);
                lookup.Addresses = Array.Empty<IPAddress>(); lookup.Resolved = false; lookup.StartedAt = Time.unscaledTime;
            }
            if (lookup.Lookup.Status == TaskStatus.RanToCompletion)
            {
                if (!lookup.Resolved)
                {
                    lookup.Addresses = lookup.Lookup.Result; lookup.Resolved = true; lookup.ResolvedAt = Time.unscaledTime;
                }
                return lookup.Addresses;
            }
            if (lookup.Lookup.IsFaulted)
            {
                // Reading Exception marks the DNS failure observed so a rejected resolver task
                // cannot surface later as an unobserved task exception.
                _ = lookup.Lookup.Exception;
                if (Time.unscaledTime - lookup.StartedAt >= 60f)
                {
                    lookup.Lookup = StartSavedHostResolution(endpoint.Host);
                    lookup.StartedAt = Time.unscaledTime;
                }
            }
            else if (lookup.Lookup.IsCanceled && Time.unscaledTime - lookup.StartedAt >= 60f)
            {
                lookup.Lookup = StartSavedHostResolution(endpoint.Host);
                lookup.StartedAt = Time.unscaledTime;
            }
            return Array.Empty<IPAddress>();
        }

        private static Task<IPAddress[]> StartSavedHostResolution(string host)
        {
            var lookup = Task.Run(() => Dns.GetHostAddresses(host));
            _ = lookup.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return lookup;
        }

        private string Friendly(string text) =>
            !joiningSteam && text.Contains("ConnectionFailed") ? "Could not reach " + joiningName + ". Check the address, that the host is running, and for internet joins that the host forwarded its UDP port." :
            !joiningSteam && text.Contains("UnknownHost") ? "Could not find " + joiningName + ". The address could not be looked up; check it for typos (an IP address or host name, optionally with :port)." :
            text.Contains("Timeout") ? "Lost contact with " + joiningName + " (timed out)." : text;

        private static string Players(int count) => count + (count == 1 ? " player" : " players");

        private void Refresh()
        {
            var entries = Entries();
            string key = string.Join("\n", entries.Select(e => e.Key));
            if (key == listKey)
            {
                for (int i = 0; i < rows.Count; i++) UpdateRow(rows[i], entries[i]);
                UpdateSelection();
                return;
            }
            listKey = key;
            MenuKit.Clear(content);
            rows.Clear();
            Section(entries.Any(e => e.Source == Source.Saved) ? "SERVERS" : null);
            foreach (var entry in entries.Where(e => e.Source == Source.Saved)) Row(entry);
            Section("LOCAL NETWORK");
            if (!entries.Any(e => e.Source == Source.Lan)) Hint("Scanning for games on your local network...");
            foreach (var entry in entries.Where(e => e.Source == Source.Lan)) Row(entry);
            if (entries.Any(e => e.Source == Source.Recent)) Section("RECENT");
            foreach (var entry in entries.Where(e => e.Source == Source.Recent)) Row(entry);
            if (rows.All(r => r.Entry.Key != selected)) selected = "";
            UpdateSelection();
        }

        private void Section(string? title)
        {
            if (title == null) return;
            MenuKit.Label(content, title, 20, MenuKit.Semibold, MenuKit.Muted).Size(height: 36).margin = new Vector4(8, 8, 0, 0);
        }

        private void Hint(string text) =>
            MenuKit.Label(content, text, 22, color: new Color(1, 1, 1, 0.35f), align: TextAlignmentOptions.Center).Size(height: 70).fontStyle = FontStyles.Italic;

        private void Row(Entry entry)
        {
            var r = MenuKit.Rect("Row " + entry.Name, content).Size(height: 86);
            var fill = MenuKit.Fill(r, Color.white);
            var button = r.gameObject.AddComponent<Button>();
            button.targetGraphic = fill;
            var colors = button.colors;
            colors.normalColor = colors.selectedColor = new Color(1, 1, 1, 0.012f);
            colors.highlightedColor = new Color(1, 1, 1, 0.035f); colors.pressedColor = new Color(1, 1, 1, 0.06f);
            colors.fadeDuration = 0.06f;
            button.colors = colors;
            var selection = MenuKit.Fill(MenuKit.Rect("Selected", r).Stretch(), MenuKit.Accent, outlined: true);
            selection.raycastTarget = false;
            // Anchored, not laid out: TMP inside a nested layout group here never built its mesh.
            var name = MenuKit.Label(r, entry.Name, 30, MenuKit.Semibold);
            name.enableAutoSizing = true; name.fontSizeMin = 22; name.fontSizeMax = 30;
            Line(name.rectTransform, 2, 42);
            var detail = MenuKit.Label(r, entry.Detail, 21);
            Line(detail.rectTransform, -32, 30);
            var tag = MenuKit.Label(r, entry.Tag, 22,
                align: TextAlignmentOptions.Right);
            tag.rectTransform.anchorMin = new Vector2(1, 0); tag.rectTransform.anchorMax = new Vector2(1, 1); tag.rectTransform.pivot = new Vector2(1, 0.5f);
            tag.rectTransform.sizeDelta = new Vector2(340, 0); tag.rectTransform.anchoredPosition = new Vector2(-26, 0);
            var view = new RowView { Entry = entry, Selection = selection, Name = name, Detail = detail, Tag = tag };
            button.onClick.AddListener(() => Click(view.Entry));
            rows.Add(view);
            UpdateRow(view, entry);
        }

        private static void UpdateRow(RowView view, Entry entry)
        {
            view.Entry = entry;
            float alpha = entry.Compatible ? 1 : 0.4f;
            var nameColor = new Color(1, 1, 1, alpha);
            var detailColor = new Color(1, 1, 1, 0.5f * alpha);
            var tagColor = entry.Compatible && (entry.Source == Source.Lan || entry.Tag.Contains("player")) ? MenuKit.Good : MenuKit.Muted;
            if (view.Name.text != entry.Name) view.Name.text = entry.Name;
            if (view.Name.color != nameColor) view.Name.color = nameColor;
            if (view.Detail.text != entry.Detail) view.Detail.text = entry.Detail;
            if (view.Detail.color != detailColor) view.Detail.color = detailColor;
            if (view.Tag.text != entry.Tag) view.Tag.text = entry.Tag;
            if (view.Tag.color != tagColor) view.Tag.color = tagColor;
        }

        // A text line across the row's left part, its bottom at y from the row's middle.
        private static void Line(RectTransform r, float y, float height)
        {
            r.anchorMin = new Vector2(0, 0.5f); r.anchorMax = new Vector2(1, 0.5f); r.pivot = new Vector2(0, 0);
            r.offsetMin = new Vector2(26, y); r.offsetMax = new Vector2(-380, y + height);
        }

        private void Click(Entry entry)
        {
            retainedFailure = ""; plugin.MenuDismissJoinFailure();
            passwordRetry = false;
            bool again = selected == entry.Key && Time.unscaledTime - lastClickAt < 0.4f;
            selected = entry.Key; lastClickAt = Time.unscaledTime; joinAttempted = false;
            UpdateSelection();
            if (again) JoinSelected();
        }

        private void UpdateSelection()
        {
            foreach (var row in rows) row.Selection.enabled = row.Entry.Key == selected;
            if (deleteArmedUntil > 0 && selected != deleteArmedKey)
            { deleteArmedUntil = 0; deleteArmedKey = ""; delete.SetText("Delete"); }
        }

        private void JoinSelected()
        {
            if (passwordRetry) { Join(joiningName, joiningEndpoint); return; }
            var entry = Selected;
            if (entry != null && entry.Compatible) Join(entry.Name, entry.Endpoint);
        }

        private void Join(string name, DirectHostEndpoint endpoint)
        {
            if (plugin.MenuSessionRunning || plugin.MenuConnecting || plugin.LocalDisconnectPending) return;
            nameError = PlayerNamePolicy.ValidationError(nameField.text);
            if (nameError != null)
            {
                joinAttempted = false; status.text = nameError; status.color = MenuKit.Error;
                nameField.ActivateInputField();
                return;
            }
            string player = PlayerNamePolicy.RequireCustom(nameField.text);
            retainedFailure = "";
            passwordRejected = false; passwordRetry = false;
            joiningName = name;
            joiningEndpoint = endpoint;
            joiningSteam = false;
            joinAttempted = true;
            cancelling = false; cancelJoin.SetEnabled(true); cancelJoin.SetText("Cancel");
            plugin.MenuJoin(player, endpoint, passwordField.text);
        }

        private void JoinSteam()
        {
            if (plugin.MenuSessionRunning || plugin.MenuConnecting || plugin.LocalDisconnectPending || !plugin.MenuSteamAvailable) return;
            nameError = PlayerNamePolicy.ValidationError(nameField.text);
            if (nameError != null)
            {
                joinAttempted = false; status.text = nameError; status.color = MenuKit.Error;
                nameField.ActivateInputField();
                return;
            }
            string lobbyId = invitedLobbyId;
            if (!ValidLobbyId(lobbyId))
            {
                status.text = "Accept a friend's Steam invitation to join their game.";
                status.color = MenuKit.Error;
                return;
            }
            retainedFailure = "";
            joiningSteam = true; joiningLobbyId = lobbyId; joiningName = "your friend's Steam game";
            passwordRejected = false; passwordRetry = false; joinAttempted = true; cancelling = false;
            cancelJoin.SetEnabled(true); cancelJoin.SetText("Cancel");
            plugin.MenuSteamJoin(PlayerNamePolicy.RequireCustom(nameField.text), joiningLobbyId, passwordField.text);
        }

        private void CancelJoin()
        {
            if (passwordRejected)
            {
                passwordRejected = false; passwordRetry = true; joinAttempted = false;
                connecting.gameObject.SetActive(false);
                cancelJoin.SetEnabled(true); cancelJoin.SetText("Cancel");
                passwordField.Select(); passwordField.ActivateInputField();
                return;
            }
            if (cancelling) return;
            cancelling = true;
            cancelJoin.SetEnabled(false); cancelJoin.SetText("Cancelling…");
            connectingTitle.text = "Cancelling join…";
            connectingStatus.text = "Waiting for the game to finish its current scene load. Your local save is protected.";
            plugin.MenuDisconnect();
        }

        private void DeleteSelected()
        {
            var entry = Selected;
            if (entry == null || entry.Saved < 0) return;
            if (deleteArmedUntil == 0) { deleteArmedKey = selected; deleteArmedUntil = Time.unscaledTime + 3; delete.SetText("Really delete?"); return; }
            deleteArmedUntil = 0; deleteArmedKey = ""; delete.SetText("Delete");
            plugin.MenuSaveServers(plugin.MenuServers.Remove(entry.Saved));
            selected = ""; listKey = null; nextRefresh = 0;
        }

        private void OpenDialog(int mode)
        {
            retainedFailure = ""; plugin.MenuDismissJoinFailure();
            passwordRetry = false;
            editing = mode;
            dialogError.text = "";
            dialogNameRow.SetActive(mode != -3);
            var entry = Selected;
            if (mode >= 0)
            {
                var server = plugin.MenuServers.Entries[mode];
                dialogTitle.text = "EDIT SERVER"; dialogDone.SetText("Save");
                dialogName.text = server.Name; dialogAddress.text = Address(server.Endpoint);
            }
            else if (mode == -1)
            {
                dialogTitle.text = "ADD SERVER"; dialogDone.SetText("Add");
                dialogName.text = entry != null && entry.Saved < 0 && entry.Source == Source.Lan ? entry.Name : "";
                dialogAddress.text = entry != null && entry.Saved < 0 ? Address(entry.Endpoint) : "";
            }
            else
            {
                dialogTitle.text = "DIRECT CONNECT"; dialogDone.SetText("Join");
                dialogAddress.text = entry != null ? Address(entry.Endpoint) : plugin.MenuRecentHosts.Count > 0 ? Address(plugin.MenuRecentHosts[0]) : "";
            }
            dialog.gameObject.SetActive(true);
            (mode == -3 ? dialogAddress : dialogName).ActivateInputField();
        }

        private string Address(DirectHostEndpoint endpoint) =>
            endpoint.Port == plugin.MenuPort ? (endpoint.IsIPv6 ? "[" + endpoint.Host + "]" : endpoint.Host) : endpoint.Display;

        private void CloseDialog() { editing = -2; dialog.gameObject.SetActive(false); }

        private void ConfirmDialog()
        {
            if (!SavedServer.TryParseAddress(dialogAddress.text, plugin.MenuPort, out var endpoint))
            { dialogError.text = "That is not an address MonMP can reach. Try 192.168.1.20 or host.example.com:27020."; return; }
            int mode = editing;
            if (mode == -3) { CloseDialog(); Join(endpoint.Display, endpoint); return; }
            var server = new SavedServer(dialogName.text, endpoint);
            try
            {
                plugin.MenuSaveServers(mode >= 0 ? plugin.MenuServers.Replace(mode, server) : plugin.MenuServers.Add(server));
                selected = "s" + (mode >= 0 ? mode : plugin.MenuServers.Entries.Count - 1) + endpoint.Display;
            }
            // A refused save (the list is full) keeps the dialog open with the reason: the status line
            // is rewritten every frame, so an error there vanished before it could be read.
            catch (InvalidOperationException ex) { dialogError.text = ex.Message; return; }
            CloseDialog();
            listKey = null; nextRefresh = 0;
        }

        // Diagnostics: drive the screen for the UI screenshots.

#if MONMP_DEVELOPMENT
        internal void TestSelectSteam() => SelectTransport(true);

        internal void TestOpenDialog(int mode) { SelectTransport(false); OpenDialog(mode); }


        internal void TestSelectFirst() { SelectTransport(false); Refresh(); if (rows.Count > 0) { selected = rows[0].Entry.Key; UpdateSelection(); } }


        internal Transform TestRoot => root;


        internal void TestJoinFirst() { TestSelectFirst(); JoinSelected(); }


        internal string TestRows => string.Join("; ", rows.Select(r => r.Entry.Name + " [" + r.Entry.Tag + "]"));
#endif

    }
}
