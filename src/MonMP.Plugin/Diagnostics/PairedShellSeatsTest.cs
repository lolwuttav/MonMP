using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Rebuilds each native shell seat from a guest-held shared part, then enters through the native
    // Konig seat-button callback on the host and guest. The host leaves the socket empty until the
    // guest has observed the removal and installed its held part through the native callback.
    internal sealed class PairedShellSeatsTest
    {
        private sealed class SeatCase
        {
            internal Vehicle_Type Type;
            internal VehicleController Vehicle = null!;
            internal Konig_Seat Seat = null!;
            internal Vehicle_PartSocket Socket = null!;
            internal ulong SocketId;
            internal ushort Prefab;
            internal string PrefabName = "";
            internal bool Driver;
            internal bool GuestInstalls;
            internal bool GuestInstalled;
            internal Part_Seat? OfferedPart;
            internal ulong OfferedItem;
            internal VehiclePartState OriginalPart;
            internal BoltHoleState[] OriginalHoles = Array.Empty<BoltHoleState>();
            internal Vehicle_Door[] Doors = Array.Empty<Vehicle_Door>();
        }

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedVehicles vehicles;
        private readonly SharedVehicleParts parts;
        private readonly SeatCase[] cases;
        private readonly List<string> results = new List<string>();
        private readonly List<string> failures = new List<string>();
        private int caseIndex, phase;
        private uint guest;
        private float next, deadline, caseStarted, hostClickAt, guestReadyAt, guestClickAt;
        private bool guestSawEmpty, aborted, sessionWasEstablished;
        private string lastGate = "not-started";

        internal string Status => "shell-seats role=" + (plugin.TestOptions.IsHost ? "host" : "guest") +
            "; case=" + caseIndex + "/" + cases.Length + "; phase=" + phase +
            "; local=" + gameplay.Get_PlayerState + "; current=" +
            (gameplay.Get_CurrentVehicleController == null ? "none" : gameplay.Get_CurrentVehicleController.vehicle_type.ToString()) +
            "; results=" + results.Count + "; failures=" + failures.Count +
            "; peerCount=" + (plugin.Session?.Peers.Count ?? 0) + "; gate=" + lastGate;

        internal PairedShellSeatsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            vehicles = plugin.Vehicles ?? throw new InvalidOperationException("Shell seat diagnostic has no shared vehicle adapter.");
            parts = plugin.World?.VehicleParts ?? throw new InvalidOperationException("Shell seat diagnostic has no shared part adapter.");

            var found = new List<SeatCase>();
            foreach (var type in new[] { Vehicle_Type.MainVehicle, Vehicle_Type.Prinz, Vehicle_Type.ScrapyardKonig })
            {
                vehicles.TestType = type;
                var vehicle = vehicles.TestVehicle;
                var manager = vehicle.vehicle_PartsManager;
                if (manager == null) throw new InvalidOperationException("Shell has no Vehicle_PartsManager: " + type);
                var seats = vehicle.GetComponentsInChildren<Konig_Seat>(true);
                if (seats.Length != 2 || seats.Count(seat => seat != null && seat.Get_isDriverSeat) != 1 ||
                    seats.Count(seat => seat != null && !seat.Get_isDriverSeat) != 1)
                    throw new InvalidOperationException("Shell seat diagnostic needs one native driver and passenger Konig_Seat: " + type + "; found=" + seats.Length);

                foreach (bool driver in new[] { true, false })
                {
                    var seat = seats.Single(value => value.Get_isDriverSeat == driver);
                    var socket = driver ? manager.Seat_Left : manager.Seat_Right;
                    if (socket == null || socket.car_PartsType != Car_PartsType.Seat)
                        throw new InvalidOperationException("Shell fixture has no native Seat socket at " + type + "/" + (driver ? "driver" : "passenger") + ".");
                    ulong id = parts.PaintSockets.Single(entry => entry.Socket == socket).Id;
                    var original = parts.TestNative(id);
                    // Both peers must select the same registered compatible prefab even if the
                    // guest constructs its fixture after the host has already removed the part.
                    // Preserve OriginalPart independently for teardown restoration.
                    ushort prefab = parts.TestSeatPrefab(id);
                    if (prefab == 0) throw new InvalidOperationException("Shell seat has no validated compatible Part_Seat prefab: " + type + "/" + (driver ? "driver" : "passenger"));
                    var doors = vehicle.GetComponentsInChildren<Vehicle_Door>(true)
                        .Where(door => door != null && door.vehicleController == vehicle)
                        .ToArray();
                    found.Add(new SeatCase
                    {
                        Type = type, Vehicle = vehicle, Seat = seat, Socket = socket, SocketId = id,
                        Prefab = prefab, PrefabName = parts.TestPrefabName(prefab), Driver = driver,
                        GuestInstalls = true, OriginalPart = original,
                        OriginalHoles = socket.boltHolesDetails.Select(CaptureHole).ToArray(), Doors = doors
                    });
                }
            }
            cases = found.ToArray();
            if (cases.Length != 6) throw new InvalidOperationException("Shell seat diagnostic did not bind all six native seat sockets.");
            vehicles.TestEnableDriverEntryDiagnostics();
            Debug.Log("MONMP_SHELL_SEATS: fixture=" + string.Join(" | ", cases.Select(DescribeCase).ToArray()));
        }

        internal bool Tick()
        {
            if (plugin.Session?.IsRunning != true)
            {
                if (sessionWasEstablished && !aborted)
                    throw new InvalidOperationException("Paired shell seat diagnostic lost its established session before completion: " + Status);
                return false;
            }
            sessionWasEstablished = true;
            if (plugin.World?.Channel.FullySynchronized != true || !vehicles.Ready || !parts.Ready || Time.unscaledTime < next) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 240f;
            if (Time.unscaledTime > deadline)
                throw new InvalidOperationException("Paired shell seat diagnostic timed out: " + Status + "; failures=" + string.Join(" || ", failures));
            if (cases.Any(test => vehicles.TestDormant(test.Vehicle))) return false;
            if (caseIndex >= cases.Length)
            {
                return plugin.TestOptions.IsHost ? HostComplete() : GuestComplete();
            }
            return plugin.TestOptions.IsHost ? HostTick(cases[caseIndex]) : GuestTick(cases[caseIndex]);
        }

        // A native seat-slider click while the case has the seat installed.
        private uint adjustBefore;
        private bool guestAdjustTried;
        private bool adjustClicked;
        private void ClickSeatAdjustment(SeatCase test, string role)
        {
            Debug.Log("MONMP_SHELL_SEATS: adjust-before role=" + role + "; " + DescribeCase(test) + "; " +
                vehicles.TestShellSeatAdjustmentDiagnostic(test.Vehicle, test.Driver, VehicleSeatAdjustmentAxis.Forward));
            adjustBefore = vehicles.TestShellSeatAdjustmentGeneration(test.Vehicle, test.Driver);
            adjustClicked = vehicles.TestClickShellSeatAdjustment(test.Vehicle, test.Driver, VehicleSeatAdjustmentAxis.Forward);
            if (!adjustClicked) Fail(role + ":" + DescribeCase(test) + ":seat-adjustment-control-missing");
        }
        private void CheckSeatAdjustment(SeatCase test, string role)
        {
            if (!adjustClicked) return;
            adjustClicked = false;
            if (vehicles.TestShellSeatAdjustmentGeneration(test.Vehicle, test.Driver) != adjustBefore)
                results.Add(role + ":" + DescribeCase(test) + ":seat-adjusted");
            else Fail(role + ":" + DescribeCase(test) + ":seat-adjustment-refused; " +
                vehicles.TestShellSeatAdjustmentDiagnostic(test.Vehicle, test.Driver, VehicleSeatAdjustmentAxis.Forward));
        }

        private bool HostTick(SeatCase test)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guest = plugin.Session.Peers.Keys.First(peer => peer != plugin.Session.LocalPeerId);
                caseStarted = Time.unscaledTime;
                guestSawEmpty = false;
                parts.TestRemoveSeatForFresh(test.SocketId);
                Log("host-remove", test, Gate(test));
                phase = 1; next = Time.unscaledTime + 0.35f;
            }
            else if (phase == 1)
            {
                if (test.Socket.isInstalled)
                    throw new InvalidOperationException("Native RemovePart callback left shell seat installed: " + DescribeCase(test));
                if (test.OfferedPart == null)
                {
                    Vector3 at = parts.TestSocketPosition(test.SocketId) + test.Vehicle.transform.forward * 2.4f + Vector3.up * 0.45f;
                    test.OfferedPart = (Part_Seat)plugin.Items!.TestSpawn(at, test.Prefab);
                    test.OfferedPart.isSpawnFromTheSaveLoadSystem = true;
                    test.OfferedPart.CreateBasics();
                    plugin.Items.TestDiscoverSoon();
                    Log("host-offered-guest-seat", test, "prefab=" + test.PrefabName + "; item-awaiting-discovery; " + Gate(test));
                    next = Time.unscaledTime + 0.25f;
                    return false;
                }
                test.OfferedItem = plugin.Items!.TestBoundId(test.OfferedPart);
                if (test.OfferedItem == 0)
                {
                    plugin.Items.TestDiscoverSoon();
                    next = Time.unscaledTime + 0.25f;
                    return false;
                }
                if (!plugin.Items.TestState(test.OfferedItem).Exists) throw new InvalidOperationException("Offered guest seat was not published as a shared loose item: " + DescribeCase(test));
                Log("host-await-guest-seat-install", test, "prefab=" + test.PrefabName + "; item=" + test.OfferedItem.ToString("X16") + "; " + Gate(test));
                phase = 2; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 2)
            {
                if (!test.Socket.isInstalled || test.Socket.partInstalled?.GetComponent<Part_Seat>() == null)
                {
                    if (Time.unscaledTime - caseStarted < 25f) return false;
                    throw new InvalidOperationException("Guest native InstallPart callback did not install the offered seat within 25 seconds: " + DescribeCase(test) + "; item=" + test.OfferedItem.ToString("X16") + "; " + Gate(test));
                }
                if (test.Socket.partInstalled.GetComponent<Part_Seat>()!.PrefabName != test.PrefabName)
                    throw new InvalidOperationException("Seat installed into shell socket has a different registered prefab: expected=" + test.PrefabName + "; actual=" + test.Socket.partInstalled.PrefabName + "; " + DescribeCase(test));
                parts.TestTighten(test.SocketId);
                OpenNativeDoors(test);
                phase = 3; next = Time.unscaledTime + 0.65f;
            }
            else if (phase == 3)
            {
                if (!DoorsOpen(test) && Time.unscaledTime - caseStarted < 2.5f) return false;
                if (!DoorsOpen(test)) Fail("host:" + DescribeCase(test) + ":native-doors-did-not-open; " + Gate(test));
                ClickSeatAdjustment(test, "host");
                CheckSeatAdjustment(test, "host");
                lastGate = Gate(test);
                TryNativeSeatClick(test, "host");
                hostClickAt = Time.unscaledTime;
                phase = 4; next = Time.unscaledTime + 0.25f;
            }
            else if (phase == 4)
            {
                if (LocalOccupying(test))
                {
                    Log("host-entry-success", test, Gate(test));
                    results.Add("host:" + DescribeCase(test) + ":entered");
                    phase = 5; next = Time.unscaledTime + 0.35f;
                }
                else if (Time.unscaledTime - hostClickAt > 2.2f)
                {
                    Fail("host:" + DescribeCase(test) + ":native-button-did-not-enter; " + lastGate);
                    phase = 6; next = Time.unscaledTime + 0.2f;
                }
            }
            else if (phase == 5)
            {
                if (LocalOccupying(test))
                {
                    gameplay.EnterExitVehicle(test.Vehicle, test.Seat.seatPosition, test.Driver);
                    Log("host-exit-request", test, Gate(test));
                    phase = 6; next = Time.unscaledTime + 0.3f;
                }
                else phase = 6;
            }
            else if (phase == 6)
            {
                if (LocalOccupying(test) || !RoleUnoccupied(test)) return false;
                // The guest waits for host entry/release, then makes its own native callback
                // attempt. This hold also leaves time for the seat and bolt records to settle.
                if (Time.unscaledTime - hostClickAt < 4.2f) return false;
                if (RemoteGuestOccupying(test)) return false;
                parts.TestRestoreState(test.SocketId, test.OriginalPart, test.OriginalHoles);
                DestroyUninstalledOffer(test);
                Log("host-restored", test, Gate(test));
                caseIndex++; phase = 0; next = Time.unscaledTime + 0.2f;
            }
            return false;
        }

        private bool GuestTick(SeatCase test)
        {
            if (phase == 0)
            {
                if (test.Socket.isInstalled) return false;
                guestSawEmpty = true;
                caseStarted = Time.unscaledTime;
                Log("guest-saw-empty-socket", test, Gate(test));
                phase = 1;
            }
            else if (phase == 1)
            {
                if (!test.GuestInstalled)
                {
                    ulong offered = plugin.Items!.TestNewestPrefab(test.Prefab);
                    var item = offered == 0 ? null : plugin.Items.TestItem(offered);
                    if (item == null || !plugin.Items.TestState(offered).Exists || !(item is Part_Seat offeredSeat) ||
                        offeredSeat.PrefabName != test.PrefabName || offeredSeat.isPlaced)
                    {
                        if (Time.unscaledTime - caseStarted < 25f) return false;
                        Fail("guest:" + DescribeCase(test) + ":host-seat-offer-not-visible; expected=" + test.PrefabName + "; " + Gate(test));
                        caseIndex++; phase = 0; next = Time.unscaledTime + 0.2f;
                        return false;
                    }
                    test.OfferedItem = offered;
                    MoveTo(item.transform.position + Vector3.right * 0.8f);
                    if (gameplay.Player.Get_HoldingItem == null)
                    {
                        plugin.Items.TestGrab(offered);
                        Log("guest-grabbed-seat", test, "item=" + offered.ToString("X16") + "; lease=" + plugin.Items.TestState(offered).Lease.Owner + "; " + Gate(test));
                        next = Time.unscaledTime + 0.45f;
                        return false;
                    }
                    if (gameplay.Player.Get_HoldingItem != item || plugin.Items.TestState(offered).Lease.Owner != plugin.Session!.LocalPeerId)
                    {
                        if (Time.unscaledTime - caseStarted < 25f) return false;
                        Fail("guest:" + DescribeCase(test) + ":offered-seat-lease-not-held-locally; holding=" + gameplay.Player.Get_HoldingItem + "; lease=" + plugin.Items.TestState(offered).Lease.Owner + "; " + Gate(test));
                        caseIndex++; phase = 0; next = Time.unscaledTime + 0.2f;
                        return false;
                    }
                    parts.TestAimHeld(test.SocketId);
                    Log("guest-aimed-held-seat", test, "item=" + offered.ToString("X16") + "; lease=" + plugin.Items.TestState(offered).Lease.Owner + "; " + Gate(test));
                    phase = 6; next = Time.unscaledTime + 0.55f;
                    return false;
                }
                if (!guestSawEmpty || !test.Socket.isInstalled || test.Socket.partInstalled?.GetComponent<Part_Seat>() == null ||
                    !test.Socket.CheckIfAllBoltAre100Percent() || !DoorsOpen(test))
                {
                    if (Time.unscaledTime - caseStarted < 25f) return false;
                    Fail("guest:" + DescribeCase(test) + ":fresh-seat-prerequisites-did-not-converge; " + Gate(test));
                    caseIndex++; phase = 0; next = Time.unscaledTime + 0.2f;
                    return false;
                }
                guestReadyAt = Time.unscaledTime;
                lastGate = Gate(test);
                Log("guest-fresh-seat-ready", test, lastGate);
                phase = 2; next = Time.unscaledTime + 0.25f;
            }
            else if (phase == 6)
            {
                if (gameplay.Player.Get_HoldingItem != plugin.Items!.TestItem(test.OfferedItem) ||
                    plugin.Items.TestState(test.OfferedItem).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                var installed = parts.TestInstallHeld(test.SocketId);
                if (!test.Socket.isInstalled || test.Socket.partInstalled != installed || installed?.GetComponent<Part_Seat>() == null)
                    throw new InvalidOperationException("Guest native InstallPart callback did not install its held seat: " + DescribeCase(test) + "; " + Gate(test));
                test.GuestInstalled = true;
                Log("guest-native-installed-seat", test, "prefab=" + installed.PrefabName + "; item=" + test.OfferedItem.ToString("X16") + "; " + Gate(test));
                phase = 1; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 2)
            {
                // The host's own adjustment arrives first, so the two never race a generation.
                if (!guestAdjustTried && Time.unscaledTime - guestReadyAt >= 1.5f)
                {
                    guestAdjustTried = true;
                    ClickSeatAdjustment(test, "guest");
                }
                if (Time.unscaledTime - guestReadyAt < 3.5f || !RoleUnoccupied(test)) return false;
                guestAdjustTried = false;
                CheckSeatAdjustment(test, "guest");
                lastGate = Gate(test);
                TryNativeSeatClick(test, "guest");
                guestClickAt = Time.unscaledTime;
                phase = 3; next = Time.unscaledTime + 0.25f;
            }
            else if (phase == 3)
            {
                if (LocalOccupying(test))
                {
                    Log("guest-entry-success", test, Gate(test));
                    results.Add("guest:" + DescribeCase(test) + ":entered");
                    phase = 4; next = Time.unscaledTime + 0.35f;
                }
                else if (Time.unscaledTime - guestClickAt > 2.0f)
                {
                    Fail("guest:" + DescribeCase(test) + ":native-button-did-not-enter; " + lastGate + "; driverTrace=" + vehicles.TestDriverEntryDiagnostic);
                    caseIndex++; phase = 0; next = Time.unscaledTime + 0.2f;
                }
            }
            else if (phase == 4)
            {
                if (LocalOccupying(test))
                {
                    gameplay.EnterExitVehicle(test.Vehicle, test.Seat.seatPosition, test.Driver);
                    Log("guest-exit-request", test, Gate(test));
                    phase = 5; next = Time.unscaledTime + 0.3f;
                }
                else phase = 5;
            }
            else if (phase == 5 && !LocalOccupying(test) && gameplay.Get_PlayerState == PlayerState.Walking)
            {
                Log("guest-exit-confirmed", test, Gate(test));
                caseIndex++; phase = 0; next = Time.unscaledTime + 0.2f;
            }
            return false;
        }

        private void OpenNativeDoors(SeatCase test)
        {
            foreach (var door in test.Doors)
            {
                if (!door.isActiveAndEnabled || !door.gameObject.activeInHierarchy) continue;
                if (door.Get_IsOpen) continue;
                var handle = test.Vehicle.GetComponentsInChildren<Vehicle_DoorHandle>(true)
                    .FirstOrDefault(candidate => candidate != null && candidate.vehicle_Door == door &&
                        candidate.isActiveAndEnabled && candidate.gameObject.activeInHierarchy);
                if (handle == null)
                {
                    Fail("host:" + DescribeCase(test) + ":active-door-without-handle:" + RuntimeProbe.CanonicalPath(door.transform));
                    continue;
                }
                MoveTo(handle.transform.position);
                handle.MouseDown();
            }
        }

        private void TryNativeSeatClick(SeatCase test, string role)
        {
            var button = FindSeatButton(test, role);
            if (button != null)
                MoveTo(button.transform.position + test.Vehicle.transform.forward * 1.0f + Vector3.up * 0.2f, button.transform.position);
            bool canUse = false;
            string canUseText;
            if (button != null)
            {
                var canUseMethod = AccessTools.Method(button.GetType(), "CanUse", Type.EmptyTypes);
                if (canUseMethod == null) canUseText = "method-missing";
                else
                {
                    try { canUse = canUseMethod.Invoke(button, null) is bool accepted && accepted; canUseText = canUse.ToString(); }
                    catch (Exception exception) { canUseText = "throws-" + ExceptionText(exception); }
                }
            }
            else canUseText = "button-missing";

            var player = gameplay.Player;
            var target = button as Interactable;
            var tool = player.Get_ActiveTool;
            var description = Gate(test) + "; button=" + (button == null ? "missing" : button.GetType().Name + "@" + RuntimeProbe.CanonicalPath(button.transform)) +
                "; buttonCanUse=" + canUseText + "; buttonToolAllowed=" + ReflectedValue(button, "isCanBeClickedWithToolInHand") +
                "; activeTool=" + (tool == null ? "none" : tool.GetType().Name + "/" + tool.name + "/canInteractWhileHolding=" + ReflectedValue(tool, "isCanInteractWhileHolding") + "/fields=" + ToolGripFields(tool)) +
                "; held=" + (player.Get_HoldingItem == null ? "none" : player.Get_HoldingItem.GetType().Name + "/" + player.Get_HoldingItem.name) +
                "; currentTarget=" + ReflectedValue(player, "currentInteractable") + "; nativeCall=" + (canUse ? "PlayerInteractable.MouseLeftClickDown" : "blocked-before-dispatch");
            lastGate = description;
            Log(role + "-before-click", test, description);
            if (button == null) return;
            if (!canUse)
            {
                Fail(role + ":" + DescribeCase(test) + ":native-button-CanUse-false; " + description);
                return;
            }
            var mouseDown = AccessTools.Method(typeof(PlayerInteractable), "MouseLeftClickDown", Type.EmptyTypes);
            var mouseUp = AccessTools.Method(typeof(PlayerInteractable), "MouseLeftClickUp", Type.EmptyTypes);
            var currentField = AccessTools.Field(typeof(PlayerInteractable), "currentInteractable");
            var currentItemField = AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem");
            if (target == null || mouseDown == null || mouseUp == null || currentField == null || currentItemField == null)
            {
                Fail(role + ":" + DescribeCase(test) + ":native-player-click-dispatch-unavailable; " + description);
                return;
            }
            object? priorTarget = currentField.GetValue(player);
            object? priorItem = currentItemField.GetValue(player);
            CursorLockMode priorLock = Cursor.lockState;
            bool priorVisible = Cursor.visible;
            bool downInvoked = false;
            try
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                currentField.SetValue(player, target);
                currentItemField.SetValue(player, null);
                mouseDown.Invoke(player, null);
                downInvoked = true;
            }
            catch (Exception exception) { Fail(role + ":" + DescribeCase(test) + ":native-click-dispatch-threw-" + ExceptionText(exception) + "; " + description); }
            finally
            {
                try { mouseUp.Invoke(player, null); }
                catch (Exception exception) { Fail(role + ":" + DescribeCase(test) + ":native-click-up-threw-" + ExceptionText(exception) + "; " + description); }
                currentField.SetValue(player, priorTarget);
                currentItemField.SetValue(player, priorItem);
                Cursor.lockState = priorLock;
                Cursor.visible = priorVisible;
            }
            Log(role + "-after-click", test, Gate(test) + "; callback=PlayerInteractable.MouseLeftClickDown+Up; invoked=" + downInvoked + "; player=" + gameplay.Get_PlayerState +
                "; currentVehicle=" + (gameplay.Get_CurrentVehicleController == test.Vehicle) +
                "; owner=" + RoleOwner(test) + "; driverTrace=" + vehicles.TestDriverEntryDiagnostic);
        }

        private static string ReflectedValue(object? target, string name)
        {
            if (target == null) return "none";
            try
            {
                var property = AccessTools.Property(target.GetType(), name);
                if (property != null) return Convert.ToString(property.GetValue(target, null)) ?? "null";
                var field = AccessTools.Field(target.GetType(), name);
                if (field != null) return Convert.ToString(field.GetValue(target)) ?? "null";
            }
            catch (Exception exception) { return "throws-" + ExceptionText(exception); }
            return "missing";
        }

        private static string ToolGripFields(object tool)
        {
            var values = new List<string>();
            for (Type? type = tool.GetType(); type != null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.Name.IndexOf("bolt", StringComparison.OrdinalIgnoreCase) >= 0 || field.Name.IndexOf("grip", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            object? value = field.GetValue(tool);
                            values.Add(field.Name + "=" + (value == null ? "null" : value is UnityEngine.Object unity ? unity.name : Convert.ToString(value)));
                        }
                        catch { values.Add(field.Name + "=<unreadable>"); }
                    }
            return values.Count == 0 ? "none" : string.Join(",", values);
        }

        private void DestroyUninstalledOffer(SeatCase test)
        {
            if (!test.GuestInstalls || test.OfferedItem == 0 || plugin.Items?.TestItem(test.OfferedItem) == null) return;
            var item = plugin.Items.TestItem(test.OfferedItem);
            if (item is InteractableParts part && !part.isPlaced) plugin.Items.TestDestroy(test.OfferedItem);
        }

        private Component? FindSeatButton(SeatCase test, string role)
        {
            var buttonType = AccessTools.TypeByName("Konig_Seat_Button");
            var nativeButtons = buttonType == null ? Array.Empty<Component>() : test.Vehicle.GetComponentsInChildren<Component>(true)
                .Where(component => component != null && component.GetType() == buttonType).ToArray();
            var candidates = nativeButtons.Where(button => RefersToSeat(button, test.Seat)).ToArray();
            if (candidates.Length != 1)
            {
                var namedButtons = nativeButtons.Select(button => RuntimeProbe.CanonicalPath(button.transform)).ToArray();
                Fail(role + ":" + DescribeCase(test) + ":native-Konig_Seat_Button-" + (candidates.Length == 0 ? "unavailable" : "duplicate") +
                    "; typeAvailable=" + (buttonType != null) + "; matching=" + candidates.Length + "; allSeatButtons=" + string.Join(",", namedButtons) + "; " + Gate(test));
                return null;
            }
            return candidates[0];
        }

        private static bool RefersToSeat(Component component, Konig_Seat seat)
        {
            for (Type? type = component.GetType(); type != null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    try { if (ReferenceEquals(field.GetValue(component), seat)) return true; } catch { }
            return component.GetType().Name == "Konig_Seat_Button" &&
                (component.transform == seat.transform || component.transform.IsChildOf(seat.transform));
        }

        private string Gate(SeatCase test)
        {
            string seatCanUse;
            try { seatCanUse = test.Seat.CanSeat().ToString(); }
            catch (Exception exception) { seatCanUse = "throws-" + ExceptionText(exception); }
            var partSeat = test.Socket.partInstalled == null ? null : test.Socket.partInstalled.GetComponent<Part_Seat>();
            string holes = string.Join(",", test.Socket.boltHolesDetails.Select(hole =>
                (hole.isBoltInstalled ? "1" : "0") + "/" + Math.Round(Mathf.Clamp01(hole.BoltedPercent) * 100)).ToArray());
            return "vehicle=" + test.Type + "; dormant=" + vehicles.TestDormant(test.Vehicle) +
                "; nativeSeat=" + test.Seat.Get_isDriverSeat + "/active=" + test.Seat.isActiveAndEnabled +
                "/seatPosition=" + (test.Seat.seatPosition != null && test.Seat.seatPosition.gameObject.activeInHierarchy) +
                "; socket=" + test.Socket.isInstalled + "/part=" + (partSeat == null ? "missing" : partSeat.PrefabName) +
                "/partActive=" + (partSeat != null && partSeat.gameObject.activeInHierarchy) +
                "/folded=" + (partSeat == null ? "n/a" : partSeat.Get_isFolded.ToString()) +
                "/linked=" + (partSeat != null && (partSeat.transform == test.Seat.transform || partSeat.transform.IsChildOf(test.Seat.transform))) +
                "; seatCanUse=" + seatCanUse + "; solid=" + test.Socket.CheckIfSolidlyAttached() +
                "; all100=" + test.Socket.CheckIfAllBoltAre100Percent() + "; holes=" + holes +
                "; doorsClosed=" + test.Vehicle.isDriverDoorClosed + "/" + test.Vehicle.isPassengerDoorClosed +
                "; doors=" + string.Join(",", test.Doors.Select(door => (door.isDriverDoor ? "driver" : "passenger") + "/active=" + (door.isActiveAndEnabled && door.gameObject.activeInHierarchy) + "/open=" + door.Get_IsOpen).ToArray()) +
                "; player=" + gameplay.Get_PlayerState + "; holding=" + gameplay.Player.IsHoldingAnObject +
                "; transfersBlocked=" + (plugin.World?.Items.TransfersBlocked ?? true) +
                "; localSeat=" + (gameplay.Get_CurrentVehicleController == test.Vehicle) + "; owner=" + RoleOwner(test);
        }

        private bool LocalOccupying(SeatCase test) => gameplay.Get_CurrentVehicleController == test.Vehicle &&
            gameplay.Get_PlayerState == (test.Driver ? PlayerState.InVehicle_Driver : PlayerState.InVehicle_Passenger);
        private bool RoleUnoccupied(SeatCase test) => test.Driver
            ? vehicles.DrivenBy(test.Vehicle) == ItemLease.Unowned
            : !vehicles.TestPassengerStateFor(test.Vehicle).Occupied;
        private bool RemoteGuestOccupying(SeatCase test) => test.Driver
            ? vehicles.DrivenBy(test.Vehicle) == guest
            : vehicles.TestPassengerStateFor(test.Vehicle).Owner == guest;
        private string RoleOwner(SeatCase test) => test.Driver ? vehicles.DrivenBy(test.Vehicle).ToString() :
            vehicles.TestPassengerStateFor(test.Vehicle).Owner.ToString();
        private bool DoorsOpen(SeatCase test) => test.Doors.Where(door => door.isActiveAndEnabled && door.gameObject.activeInHierarchy).All(door => door.Get_IsOpen);

        private void MoveTo(Vector3 location, Vector3? lookAt = null)
        {
            if (gameplay.PlayerWalking == null) return;
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody != null)
            {
                gameplay.Get_Player_Rigidbody.position = location;
                gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
            }
            if (lookAt.HasValue)
            {
                Vector3 direction = lookAt.Value - (location + Vector3.up * 1.4f);
                if (direction.sqrMagnitude > 0.001f)
                {
                    var angles = Quaternion.LookRotation(direction.normalized).eulerAngles;
                    gameplay.PlayerWalking.playerCameraController.SetRotationAngles(Mathf.DeltaAngle(0, angles.x), angles.y);
                }
            }
        }

        private bool HostComplete()
        {
            if (plugin.Session!.Peers.Count > 1) return false;
            if (gameplay.Get_PlayerState != PlayerState.Walking)
                throw new InvalidOperationException("Host shell seat diagnostic ended while still seated: " + Status);
            plugin.EndPairedTestSession();
            if (failures.Count != 0)
                throw new InvalidOperationException("Paired shell seats host failures: " + string.Join(" || ", failures));
            Debug.Log("MONMP_SHELL_SEATS: host completed all native entry cases: " + string.Join(" | ", results));
            return true;
        }

        private bool GuestComplete()
        {
            if (gameplay.Get_PlayerState != PlayerState.Walking)
                throw new InvalidOperationException("Guest shell seat diagnostic ended while still seated: " + Status);
            plugin.EndPairedTestSession();
            if (failures.Count != 0)
                throw new InvalidOperationException("Paired shell seats guest failures: " + string.Join(" || ", failures));
            Debug.Log("MONMP_SHELL_SEATS: guest completed all native entry cases: " + string.Join(" | ", results));
            return true;
        }

        internal void Abort()
        {
            if (!plugin.TestOptions.IsHost || aborted || plugin.World?.VehicleParts == null) return;
            var current = gameplay.Get_CurrentVehicleController;
            if (current != null)
            {
                var currentCase = cases.FirstOrDefault(test => test.Vehicle == current &&
                    (test.Driver ? gameplay.Get_PlayerState == PlayerState.InVehicle_Driver :
                        gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger));
                if (currentCase != null) gameplay.EnterExitVehicle(currentCase.Vehicle, currentCase.Seat.seatPosition, currentCase.Driver);
            }

            foreach (var test in cases)
            {
                uint owner = test.Driver ? vehicles.DrivenBy(test.Vehicle) : vehicles.TestPassengerStateFor(test.Vehicle).Owner;
                if (owner != ItemLease.Unowned) vehicles.TestReleaseSeatForAbort(test.Vehicle, owner);
            }

            var errors = new List<Exception>();
            foreach (var test in cases)
            {
                try { parts.TestRestoreState(test.SocketId, test.OriginalPart, test.OriginalHoles); }
                catch (Exception exception) { errors.Add(new InvalidOperationException("Could not restore " + DescribeCase(test), exception)); }
                try { DestroyUninstalledOffer(test); }
                catch (Exception exception) { errors.Add(new InvalidOperationException("Could not destroy uninstalled offered seat for " + DescribeCase(test), exception)); }
            }
            aborted = true;
            Log("abort-restored", cases[Math.Min(caseIndex, cases.Length - 1)], "restored all original seat-part and bolt states");
            if (errors.Count != 0) throw new AggregateException("Shell seat fixture restoration failed.", errors);
        }

        private void Log(string step, SeatCase test, string detail) => Debug.Log("MONMP_SHELL_SEATS: role=" +
            (plugin.TestOptions.IsHost ? "host" : "guest") + "; case=" + DescribeCase(test) + "; phase=" + step + "; " + detail);
        private void Fail(string reason)
        {
            failures.Add(reason);
            Debug.LogError("MONMP_SHELL_SEATS_FAILURE: " + reason);
        }
        private static string DescribeCase(SeatCase test) => test.Type + "/" + (test.Driver ? "driver" : "passenger") +
            "/socket=" + test.SocketId.ToString("X16") + "/prefab=" + test.PrefabName;
        private static string ExceptionText(Exception exception) => exception is TargetInvocationException invocation && invocation.InnerException != null
            ? invocation.InnerException.GetType().Name + ":" + invocation.InnerException.Message
            : exception.GetType().Name + ":" + exception.Message;
        private static BoltHoleState CaptureHole(BoltHoleDetails hole) => new BoltHoleState(hole.isBoltInstalled,
            hole.isBoltInstalled && hole.BoltedPercent > 0 ? (byte)Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp01(hole.BoltedPercent) * 100)) : (byte)0);
    }
}
