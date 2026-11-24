using System;
using MonMP.Core.Session;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Replication
{
    // A world loaded for a remote session is never an offline save candidate.
    // Keep its disk guards until the scene has actually returned to the menu.
    internal sealed class GuestWorldLoader
    {
        private readonly MonMPPlugin plugin;
        private const float ReturnTimeout = 10f;
        private bool requested, enteredWorld, returning, cancelled;
        private int originalMenu;
        private float readyAt, returnRequestedAt;
        public bool NativeReady { get; private set; }
        public bool Activated { get; private set; }
        public bool SceneLoadRequested => requested;
        public bool EnteredWorld => enteredWorld;
        public bool SuppressDisk => requested;
        public bool HoldPresentation => requested && (!Activated || cancelled);
        public bool Cancelled => cancelled;
        public bool CanDiscard => !requested || ReturnedToMenu(SceneManager.GetActiveScene());
        private bool ReturnedToMenu(Scene scene) => scene.buildIndex == 1 && (enteredWorld || scene.handle != originalMenu);
        public int BlockedSaveReads { get; private set; }
        public int PreservedSceneObjects { get; private set; }
        public int HeldInputFrames { get; private set; }
        public SessionWorld? Configuration { get; private set; }

        public GuestWorldLoader(MonMPPlugin plugin) => this.plugin = plugin;
        public void NoteBlockedRead() => BlockedSaveReads++;
        public void NotePreservedObject() => PreservedSceneObjects++;
        public void NativeFinished()
        {
            if (!NativeReady)
            {
                NativeReady = true; readyAt = Time.unscaledTime + 2;
                // The guest skipped the host's save load; rebuild its hard-mode world shape before the catalog is taken.
                if (Configuration != null) HardModeWorldLayout.HideVehicles(Configuration.HiddenVehicles);
                if (Configuration?.HardMode == true) HardModeWorldLayout.Apply(Configuration.HardModeLayout);
            }
            HoldInputs();
        }
        public void ApplyConfiguration()
        {
            if (!SuppressDisk || Configuration == null) return;
            var game = UnityEngine.Object.FindObjectOfType<Gameplay>();
            if (game == null) return;
            game.Get_isHardMode = Configuration.HardMode; game.Get_isPermadeath = Configuration.Permadeath;
        }
        internal void PrepareNativeHardMode(Gameplay game)
        {
            // NewSave schedules this native call after the loading screen can report NativeReady.
            // Keep the gate open through the loading hold, before the shared catalog is registered.
            if (!requested || cancelled || Activated || Configuration?.HardMode != true || !game.Get_isHardMode ||
                plugin.Session?.IsHost != false || plugin.Session.State != SessionState.LoadingWorld ||
                !ReferenceEquals(plugin.Session.HostWorld, Configuration) ||
                SceneManager.GetActiveScene().buildIndex != 2 ||
                UnityEngine.Object.FindObjectOfType<Gameplay>() != game) return;
            HardModeWorldLayout.PrepareNativeLoad(game, Configuration.HiddenVehicles, Configuration.StoredVehicles);
        }
        public void HoldInputs()
        {
            if (!HoldPresentation) return;
            var assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            if (assets == null) return;
            assets.inputs.Player.Disable(); assets.vehicleInputs.VehicleControls.Disable();
            AudioListener.pause = true; HeldInputFrames++;
        }
        public void Cancel()
        {
            if (cancelled) return;
            cancelled = true;
            returnRequestedAt = Time.unscaledTime;
            HoldInputs();
        }
        public void Tick(Gameplay? gameplay)
        {
            if (!requested && !cancelled && plugin.Session?.State == SessionState.LoadingWorld)
            {
                Configuration = plugin.Session.HostWorld ?? throw new InvalidOperationException("Host world missing.");
                string master = "2:" + SceneUtility.GetScenePathByBuildIndex(2);
                if (Configuration.Scene != master) throw new InvalidOperationException("Host must be in the supported master world.");
                var menu = UnityEngine.Object.FindObjectOfType<MainMenu_Manager>();
                if (menu == null || menu.Get_GameStarted) throw new InvalidOperationException("Return to the main menu before loading a host world.");
                DropStrayGameplay();
                originalMenu = SceneManager.GetActiveScene().handle;
                requested = true; menu.StartGame(false);
            }
            var scene = SceneManager.GetActiveScene();
            if (requested && scene.buildIndex == 2) enteredWorld = true;
            if (cancelled)
            {
                if (!requested || ReturnedToMenu(scene)) return;
                HoldInputs();
                if (scene.buildIndex == 2 && !returning)
                {
                    returning = true; returnRequestedAt = Time.unscaledTime; Time.timeScale = 1;
                    var loader = UnityEngine.Object.FindObjectOfType<LevelLoader>();
                    if (loader != null) loader.LoadMainMenuScene();
                    else SceneManager.LoadScene(1);
                }
                else if (Time.unscaledTime - returnRequestedAt >= ReturnTimeout)
                {
                    // Also escape a stalled menu cinematic/scene request before the master scene
                    // ever opens. A single-mode LoadScene completes preceding Unity async loads
                    // and unloads the original menu. Keep disk guards until the new menu arrives.
                    returning = true; returnRequestedAt = Time.unscaledTime; Time.timeScale = 1;
                    SceneManager.LoadScene(1);
                }
                return;
            }
            HoldInputs();
            if (!NativeReady || Time.unscaledTime < readyAt || gameplay?.Player == null) return;
            if (plugin.Session?.State == SessionState.LoadingWorld)
            {
                // Native HardMode_LoadSave can run after NativeFinished and enable tools
                // the fresh guest does not own. Match the authoritative availability
                // once that deferred initialization has settled, before catalog capture.
                if (Configuration?.HardMode == true) HardModeWorldLayout.Apply(Configuration.HardModeLayout);
                plugin.RegisterGuestWorld(gameplay);
            }
            if (!Activated && plugin.World?.Channel.FullySynchronized == true && plugin.World.PersonalProfileReady)
            {
                NativePlayerSpawn.PlaceWalking(gameplay, Configuration!.Spawn);
                Activated = true;
                AudioListener.pause = false; gameplay.ResumeGame();
                // HoldInputs disables both maps while the guest scene and snapshot load.
                // ResumeGame restores only Player; NWH reads throttle, brakes and shifting
                // from this separate map, so leave it enabled once guest play begins.
                UnityEngine.Object.FindObjectOfType<GameAssets>()?.vehicleInputs.VehicleControls.Enable();
                UnityEngine.Object.FindObjectOfType<UI_BlackScreenWaitForSaveSystem>()?.FadeBlackScreen();
                plugin.ReportGuestWorldReady(this);
            }
        }

        // Reading Singleton<Gameplay>.i in the menu creates a blank Gameplay that survives the load; every
        // world system then binds to it (no player, no terrain) and the native load dies at "Loading
        // settings". Drop any such stray before the world loads.
        internal static void DropStrayGameplay()
        {
            foreach (var stray in UnityEngine.Object.FindObjectsOfType<Gameplay>())
            {
                if (stray.gameObject.scene.name != "DontDestroyOnLoad") continue;
                Debug.LogWarning("MonMP: removed a stray menu Gameplay before loading the host world.");
                try { UnityEngine.Object.DestroyImmediate(stray.gameObject); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
