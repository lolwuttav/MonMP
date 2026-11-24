using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedProfileTest
    {
        private readonly MonMPPlugin plugin;
        private bool tested, acknowledged;
        private bool gatePending, playerEnabled;
        private float savedScale, releaseAt;
        private byte[]? frozenProfile;
        private bool chairProbeComplete;
        private int chairPhase, chairEnteredFrame;
        private float chairExitedFixedTime;
        private GameObject? fixtureSeat;
        private Vector3 chairOriginalPosition;
        private Rigidbody? chairRigidbody;
        public string Status => "personal profile; chairPhase=" + chairPhase + "; tested=" + tested + "; guest acknowledged=" + acknowledged;
        internal PairedProfileTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.SequenceEqual(new byte[] { 71 })) acknowledged = true; }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Personal profile: " + message); }
        private static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Personal profile: unsafe native state was accepted.");
        }
        internal bool Tick()
        {
            var world = plugin.World;
            if (world?.Channel.FullySynchronized != true || !world.PersonalProfileReady ||
                plugin.GuestWorld?.HoldPresentation == true) return false;
            if (!chairProbeComplete)
            {
                if (chairPhase == 0 && !world.Profile.Ready) return false;
                if (!TickChairProbe(world)) return false;
            }
            if (!world.Profile.Ready) return false;
            if (!tested)
            {
                var original = world.Profile.Capture();
                var needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
                var stats = GameManager.Get_PlayerStats;
                byte[] cash = world.Economy.TestState.Encode(), clock = world.Clock.InitialRecords().First(r => r.Id == ClockState.Entity).Payload;
                try
                {
                    float seed = plugin.TestOptions.IsHost ? 0.125f : 0.25f;
                    for (int shirt = 0; shirt < 9; shirt++)
                    {
                        var state = new PlayerProfileState(new[] { seed, 0.375f, 0.625f, 0.875f, 0.75f, -0.002f, -0.005f },
                            new[] { 45, 90, 123, 234, 345, 6, 140 }, Enumerable.Range(0, 21).Select(i => i + seed), shirt);
                        world.Profile.Restore(PlayerProfileState.FromSection(state.ToSection()));
                        var restored = world.Profile.Capture();
                        Require(restored.Needs.SequenceEqual(state.Needs), "Native restore lost personal needs.");
                        Require(restored.Counters.SequenceEqual(state.Counters), "Native restore lost personal counters.");
                        Require(restored.Shirt == state.Shirt, "Native restore lost the requested shirt.");
                        // Session statistics belong to the native host checkpoint. Legacy
                        // profile statistics must remain readable without replacing them.
                        Require(restored.Statistics.SequenceEqual(original.Statistics), "Personal restore changed native statistics.");
                    }
                    var profileGame = UnityEngine.Object.FindObjectOfType<Gameplay>();
                    needs.Get_isSaving = true; world.Profile.Capture(); Reject(() => world.Profile.Restore(original)); needs.Get_isSaving = false;
                    AccessTools.Field(typeof(Gameplay), "isSleeping").SetValue(profileGame, true);
                    world.Profile.Capture(); Reject(() => world.Profile.Restore(original));
                    AccessTools.Field(typeof(Gameplay), "isSleeping").SetValue(profileGame, false);
                    var hands = profileGame.Player.Get_VisualHands;
                    hands.Invoke("GameConsole_Hide", 60); world.Profile.Capture(); Reject(() => world.Profile.Restore(original)); hands.CancelInvoke("GameConsole_Hide");
                    needs.Dead = true; world.Profile.Capture(); Reject(() => world.Profile.Restore(original)); needs.Dead = false;
                    // Schedule and cancel only this fixture's callback; no drinking/achievement effects execute.
                    needs.Invoke("Alchool_FinishedDrinking", 60); world.Profile.Capture();
                    Reject(() => world.Profile.Restore(original)); needs.CancelInvoke("Alchool_FinishedDrinking");
                    GameManager.Get_PlayerStats = new float[20]; Reject(() => world.Profile.Capture()); Reject(() => world.Profile.Restore(original)); GameManager.Get_PlayerStats = stats;
                    Require(world.Economy.TestState.Encode().SequenceEqual(cash) && world.Clock.InitialRecords().First(r => r.Id == ClockState.Entity).Payload.SequenceEqual(clock), "Personal restore changed world cash or clock.");
                }
                finally
                {
                    needs.Get_isSaving = false;
                    AccessTools.Field(typeof(Gameplay), "isSleeping").SetValue(UnityEngine.Object.FindObjectOfType<Gameplay>(), false);
                    UnityEngine.Object.FindObjectOfType<Gameplay>().Player.Get_VisualHands.CancelInvoke("GameConsole_Hide");
                    needs.Dead = false; needs.CancelInvoke("Alchool_FinishedDrinking"); GameManager.Get_PlayerStats = stats;
                    world.Profile.Restore(original);
                }
                Require(world.Profile.Capture().Encode().SequenceEqual(original.Encode()), "Original local profile was not restored.");
                var player = UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player;
                playerEnabled = player.enabled; savedScale = Time.timeScale;
                Reject(() => world.SaveGate.Freeze()); world.SaveGate.Begin();
                needs.Invoke("Alchool_FinishedDrinking", 60); Reject(() => world.SaveGate.Freeze()); needs.CancelInvoke("Alchool_FinishedDrinking");
                world.SaveGate.Freeze(); frozenProfile = world.Profile.Capture().Encode();
                needs.Tick(false, false);
                var game = UnityEngine.Object.FindObjectOfType<Gameplay>(); game.PauseGame(); game.ResumeGame();
                Require(!game.Get_isGamePaused, "Native resume did not finish before the freeze check.");
                Require(Time.timeScale == 0 && !player.enabled && world.Profile.Capture().Encode().SequenceEqual(frozenProfile), "Native pause/resume or personal tick escaped the freeze.");
                tested = gatePending = true; releaseAt = Time.unscaledTime + 0.75f; return false;
            }
            if (gatePending)
            {
                if (Time.unscaledTime < releaseAt) return false;
                var player = UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player;
                Require(world.SaveGate.Frozen && Time.timeScale == 0 && !player.enabled && world.Profile.Capture().Encode().SequenceEqual(frozenProfile!), "Profile changed while frozen across frames.");
                world.SaveGate.Dispose(); world.SaveGate.Dispose();
                Require(Time.timeScale == savedScale, "Freeze cleanup changed time scale: expected " + savedScale + ", actual " + Time.timeScale + ".");
                // The explicit native ResumeGame above requests enabled input. The gate
                // remembers that intent even if input was disabled before collection.
                Require(player.enabled, "Freeze cleanup did not restore the native resume input intent.");
                Require(!world.Items.TransfersBlocked, "Freeze cleanup left ownership transfers blocked.");
                Require(!world.Stores.NewPurchasesBlocked, "Freeze cleanup left new purchases blocked.");
                player.Disable(); world.SaveGate.Begin(); world.SaveGate.Freeze(); world.SaveGate.Dispose();
                Require(!player.enabled, "Originally disabled player input was enabled by cleanup.");
                if (playerEnabled) player.Enable(); else player.Disable();
                gatePending = false;
                if (!plugin.TestOptions.IsHost) world.Channel.Request(ClockState.Entity, new byte[] { 71 });
            }
            if (plugin.TestOptions.IsHost && acknowledged)
            {
                UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1071, new float[5]);
                world.Channel.CommandRequested -= Acknowledge; return true;
            }
            return !plugin.TestOptions.IsHost && world.Economy.TestState.CashCents == 1071;
        }
        private bool TickChairProbe(Replication.SharedWorld world)
        {
            var game = UnityEngine.Object.FindObjectOfType<Gameplay>();
            var needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            try
            {
                if (chairPhase == 0)
                {
                    chairOriginalPosition = game.PlayerWalking.transform.position;
                    fixtureSeat = new GameObject("MonMPProfileTestSeat");
                    fixtureSeat.transform.position = chairOriginalPosition + Vector3.up * 0.5f;
                    game.EnterExitChair(fixtureSeat.transform);
                    Require(game.Get_PlayerState == PlayerState.InFurnitureChair, "Native chair entry failed.");
                    chairEnteredFrame = Time.frameCount;
                    world.Profile.Capture(); needs.Get_isSaving = true; world.FlushLocalOwnership();
                    Require(game.Get_PlayerState == PlayerState.InFurnitureChair, "Save flush interrupted computer animation in a chair.");
                    chairPhase = 1; return false;
                }
                if (chairPhase == 1)
                {
                    // Native EnterChair schedules the walking Rigidbody for destruction.
                    // ExitChair must run after that frame, so Mover.Setup creates a fresh
                    // body instead of reusing the body still awaiting destruction.
                    if (Time.frameCount <= chairEnteredFrame) return false;
                    Require(game.Get_PlayerState == PlayerState.InFurnitureChair && needs.Get_isSaving,
                        "The saving chair state did not survive its native entry frame.");
                    needs.Get_isSaving = false; world.FlushLocalOwnership();
                    Require(game.Get_PlayerState == PlayerState.Walking && game.PlayerWalking.transform.parent == null &&
                        Vector3.Distance(game.PlayerWalking.transform.position, chairOriginalPosition) < 0.01f,
                        "Native chair flush did not restore walking at the exit position.");
                    chairRigidbody = game.Get_Player_Rigidbody;
                    Require(chairRigidbody != null, "Native chair exit did not create a live walking Rigidbody.");
                    chairExitedFixedTime = Time.fixedTime; chairPhase = 2; return false;
                }
                if (Time.fixedTime <= chairExitedFixedTime) return false;
                var mover = game.PlayerWalking.GetComponent<CMF.Mover>();
                Require(game.PlayerWalking.isActiveAndEnabled && mover != null && mover.isActiveAndEnabled &&
                    chairRigidbody != null && game.Get_Player_Rigidbody == chairRigidbody &&
                    mover.Get_RigidBody == chairRigidbody && game.PlayerWalking.GetComponent<Rigidbody>() == chairRigidbody,
                    "Native walking Rigidbody did not survive a FixedUpdate after chair exit.");
                CleanupChairProbe(); chairProbeComplete = true; chairPhase = 3; return true;
            }
            catch
            {
                CleanupChairProbe();
                throw;
            }
        }
        private void CleanupChairProbe()
        {
            var needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            if (needs != null) needs.Get_isSaving = false;
            if (fixtureSeat == null) return;
            var game = UnityEngine.Object.FindObjectOfType<Gameplay>();
            if (game?.PlayerWalking != null)
            {
                if (game.Get_PlayerState == PlayerState.InFurnitureChair && Time.frameCount > chairEnteredFrame)
                    game.EnterExitChair(fixtureSeat.transform);
                // A failed same-frame entry probe must not destroy its parented walker.
                if (game.PlayerWalking.transform.parent == fixtureSeat.transform) game.PlayerWalking.transform.SetParent(null, true);
            }
            UnityEngine.Object.Destroy(fixtureSeat); fixtureSeat = null;
        }
    }
}
