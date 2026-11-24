using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    internal sealed class TimeSkipPresentation : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly TimeSkipInputLease input = new TimeSkipInputLease();
        private readonly NativeTimeSkipOverlay overlay = new NativeTimeSkipOverlay();
        internal bool BlocksInput => input.Active;
        internal TimeSkipPresentation(MonMPPlugin plugin) { this.plugin = plugin; }
        internal void Tick()
        {
            bool active = plugin.Session?.IsRunning == true && plugin.World?.Clock.FastForwarding == true &&
                plugin.GuestWorld?.HoldPresentation != true && plugin.Gameplay != null;
            if (!active)
            {
                input.End();
                overlay.End();
                return;
            }
            if (!input.Active)
            {
                plugin.ReleaseSocialInput();
                plugin.Chat?.CloseForTimeSkip();
                var gameplay = plugin.Gameplay;
                var assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
                if (assets == null) return;
                int scene = SceneManager.GetActiveScene().handle;
                var player = gameplay!.Player;
                var hands = player?.Get_VisualHands;
                var windows = UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>();
                var playerActions = assets.inputs.asset;
                var vehicleActions = assets.vehicleInputs.asset;
                var escapeMap = assets.inputs.EscapeMenu.Get();
                var consoleMap = assets.inputs.GameConsole.Get();
                bool consoleOpen = false, windowOpen = false;
                var ships = new List<(NWH.DWP2.ShipController.InputSystemShipInputProvider Provider, InputActionAsset Actions)>();
                var sources = new List<InputActionAsset> { playerActions, vehicleActions };
                foreach (var ship in UnityEngine.Object.FindObjectsOfType<NWH.DWP2.ShipController.InputSystemShipInputProvider>())
                    if (ship.shipInputActions != null)
                    {
                        sources.Add(ship.shipInputActions.asset);
                        ships.Add((ship, ship.shipInputActions.asset));
                    }
                input.Begin(sources, action =>
                    plugin.GuestWorld?.HoldPresentation != true && !plugin.SpectatorUiBlocked && !plugin.Spectating &&
                    !MonMPPlugin.CleanFrame && plugin.World?.SaveGate.Active != true &&
                    // Escape keeps its prior ownership even while paused/dead.
                    (action.actionMap == escapeMap ||
                     !gameplay.Get_IsDead && !gameplay.Get_IsSleeping && !gameplay.Get_isGamePaused && !plugin.PauseMenuOpen &&
                     (action.actionMap == consoleMap
                        ? consoleOpen : !consoleOpen && !windowOpen)),
                    () =>
                    {
                        if (plugin.Gameplay != gameplay || gameplay == null || player == null || assets == null || gameplay.Player != player ||
                            SceneManager.GetActiveScene().handle != scene ||
                            GameAssets.i != assets ||
                            assets.inputs.asset != playerActions || assets.vehicleInputs.asset != vehicleActions ||
                            !ships.TrueForAll(ship => ship.Provider != null && ship.Provider.shipInputActions?.asset == ship.Actions)) return false;
                        consoleOpen = hands != null && hands.Get_isGameConsoleOpen;
                        windowOpen = windows != null && windows.Get_isOpen;
                        return true;
                    });
            }
            input.Hold();
            overlay.Show(plugin.Session!.IsHost, !MonMPPlugin.CleanFrame);
        }
        internal void Stop()
        {
            input.End();
            overlay.End();
        }
        public void Dispose()
        {
            input.Dispose();
            overlay.Dispose();
        }
    }
}
