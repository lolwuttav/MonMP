using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Persistence;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace MonMP.Plugin.Replication
{
    internal sealed class NativePlayerProfile
    {
        private static readonly string[] needNames = { "MappleSyrup", "Poutine", "Energy", "Anxiety", "THCLevel", "Drunkness", "StomachAlcoolContent" };
        private static readonly string[] counterNames = { "DeathDebuff", "HangoverDebuff", "LastTime_Drink", "LastTime_Ate", "LastTime_Slept", "Cooldown_Vomit", "EnergyDrinkInBlood" };
        private static readonly FieldInfo[] needFields = Fields(needNames, typeof(float));
        private static readonly FieldInfo[] counterFields = Fields(counterNames, typeof(int));
        private static readonly FieldInfo? WeedJointField = AccessTools.Field(typeof(GameManager), "WeedJointAmount");
        private readonly Gameplay gameplay;
        private readonly SharedWorld world;
        private readonly BasicNeeds_Manager needs;
        internal NativePlayerProfile(Gameplay gameplay, SharedWorld world)
        { this.gameplay = gameplay; this.world = world; needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>() ?? throw new InvalidOperationException("Personal needs are not initialized."); }
        private static FieldInfo[] Fields(string[] names, Type type) => names.Select(name =>
        {
            var field = AccessTools.Field(typeof(BasicNeeds_Manager), name);
            if (field == null || field.FieldType != type) throw new InvalidOperationException("Unsupported personal field: " + name);
            return field;
        }).ToArray();
        // Application quit destroys the scene before the session's dispose: nothing is capturable then.
        internal bool Ready => needs != null && gameplay != null && gameplay.Player != null && gameplay.Player.Get_VisualHands != null &&
            world.LocalFlushComplete && !needs.Dead && !needs.Get_isSaving && !gameplay.Get_IsSleeping &&
            !gameplay.Player.Get_VisualHands.IsInvoking("GameConsole_Hide") && !needs.IsInvoking("Alchool_FinishedDrinking") &&
            (float)AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_DrinkAlcool").GetValue(needs) <= Time.timeSinceLevelLoad;
        // Reading personal fields does not require walking, empty hands, or the end
        // of a computer/sleep animation. Restoration still uses the strict gate.
        internal bool CanCapture => needs != null && gameplay != null && gameplay.Player != null && gameplay.Player.Get_VisualHands != null;
        internal bool HasPendingSaveEffect => needs != null && needs.IsInvoking("Alchool_FinishedDrinking");
        internal void SettleForSave()
        {
            if (needs == null || !needs.IsInvoking("Alchool_FinishedDrinking")) return;
            needs.CancelInvoke("Alchool_FinishedDrinking");
            AccessTools.Method(typeof(BasicNeeds_Manager), "Alchool_FinishedDrinking").Invoke(needs, null);
        }
        // Evaluated only on a failed save, rather than allocating diagnostics for each periodic sample.
        internal string CaptureBlockedReason
        {
            get
            {
                if (needs == null || gameplay == null || gameplay.Player == null || gameplay.Player.Get_VisualHands == null)
                    return "The native player or personal needs are unavailable.";
                if (!world.LocalFlushComplete)
                    return gameplay.Player.IsHoldingAnObject ? "A held item could not be released." :
                        "Player ownership or seated movement has not finished releasing.";
                if (needs.Dead) return "The player is dead.";
                if (needs.Get_isSaving) return "The native computer or console save animation has not finished.";
                if (gameplay.Get_IsSleeping) return "The player is still sleeping.";
                if (gameplay.Player.Get_VisualHands.IsInvoking("GameConsole_Hide")) return "The game console has not finished closing.";
                if (needs.IsInvoking("Alchool_FinishedDrinking") ||
                    (float)AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_DrinkAlcool").GetValue(needs) > Time.timeSinceLevelLoad)
                    return "A pending drinking effect has not finished.";
                return "";
            }
        }
        private void RequireReady()
        {
            if (!Ready) throw new InvalidOperationException("Wait for walking, empty hands and personal effects to finish before capturing or restoring a profile.");
            if (GameManager.Get_PlayerStats.Length != PlayerProfileState.StatCount || Enum.GetValues(typeof(Player_ShirtColor)).Length != 9)
                throw new InvalidOperationException("Unsupported native personal profile layout.");
        }
        internal PlayerProfileState Capture()
        {
            if (!CanCapture) throw new InvalidOperationException("The native personal profile is unavailable.");
            if (GameManager.Get_PlayerStats.Length != PlayerProfileState.StatCount || Enum.GetValues(typeof(Player_ShirtColor)).Length != 9)
                throw new InvalidOperationException("Unsupported native personal profile layout.");
            return new PlayerProfileState(needFields.Select(f => (float)f.GetValue(needs)), counterFields.Select(f => (int)f.GetValue(needs)),
                GameManager.Get_PlayerStats, gameplay.Player.Get_VisualHands.Get_ShirtColor, GameManager.Get_WeedJointAmount);
        }
        internal void Restore(PlayerProfileState state)
        {
            RequireReady();
            ApplyFields(gameplay, needs, state, false);
        }
        internal void RestoreLocalBaseline(PlayerProfileState state)
        {
            if (needs == null || gameplay == null) return;
            // Cancel the remote-session completion callback before restoring captured local values.
            needs.CancelInvoke("Alchool_FinishedDrinking");
            AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_DrinkAlcool").SetValue(needs, 0f);
            AccessTools.Field(typeof(BasicNeeds_Manager), "Cache_LastDrinkAlcoolContent").SetValue(needs, 0f);
            ApplyFields(state);
        }
        internal static void RestoreLoaded(Gameplay gameplay, PlayerProfileState state)
        {
            var needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>() ?? throw new InvalidOperationException("Loaded personal needs are missing.");
            if (GameManager.Get_PlayerStats.Length != PlayerProfileState.StatCount || Enum.GetValues(typeof(Player_ShirtColor)).Length != 9)
                throw new InvalidOperationException("Unsupported loaded personal profile layout.");
            ApplyFields(gameplay, needs, state, false);
        }
        internal static PlayerProfileState CaptureLoaded(Gameplay gameplay)
        {
            var needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>() ?? throw new InvalidOperationException("Loaded personal needs are missing.");
            if (gameplay == null || gameplay.Player == null || gameplay.Player.Get_VisualHands == null ||
                GameManager.Get_PlayerStats.Length != PlayerProfileState.StatCount || Enum.GetValues(typeof(Player_ShirtColor)).Length != 9)
                throw new InvalidOperationException("Unsupported loaded personal profile layout.");
            return new PlayerProfileState(needFields.Select(f => (float)f.GetValue(needs)), counterFields.Select(f => (int)f.GetValue(needs)),
                GameManager.Get_PlayerStats, gameplay.Player.Get_VisualHands.Get_ShirtColor, GameManager.Get_WeedJointAmount);
        }
        // The native setter dereferences the inventory HUD, which is already gone during a quit.
        private static void ApplyWeedJoints(int amount)
        {
            if (amount == PlayerProfileState.UnknownWeedJoints) return;
            if (UnityEngine.Object.FindObjectOfType<UI_ShowInventory>() != null) GameManager.Get_WeedJointAmount = amount;
            else WeedJointField?.SetValue(null, amount);
        }
        private void ApplyFields(PlayerProfileState state) => ApplyFields(gameplay, needs, state);
        private static void ApplyFields(Gameplay gameplay, BasicNeeds_Manager needs, PlayerProfileState state, bool restoreStatistics = true)
        {
            for (int i = 0; i < needFields.Length; i++) needFields[i].SetValue(needs, state.Needs[i]);
            for (int i = 0; i < counterFields.Length; i++) counterFields[i].SetValue(needs, state.Counters[i]);
            ApplyWeedJoints(state.WeedJoints);
            // Legacy profile statistics remain readable, but the selected native host
            // save owns session totals. Only restoring an offline guest baseline copies them.
            if (restoreStatistics)
                for (int i = 0; i < state.Statistics.Count; i++) GameManager.Get_PlayerStats[i] = state.Statistics[i];
            // On quit the shirt renderer can already be destroyed while the baseline is restored.
            var hands = gameplay.Player != null ? gameplay.Player.Get_VisualHands : null;
            if (hands != null && Traverse.Create(hands).Field("Mesh_Shirt").GetValue() is Renderer shirt && shirt != null) hands.ChangeShirt(state.Shirt);
            // Refresh presentation directly: ticking or replaying consumables would mutate counters or award achievements.
            AccessTools.Method(typeof(BasicNeeds_Manager), "UpdateUI").Invoke(needs, null);
            ((DrunkEffect)AccessTools.Field(typeof(BasicNeeds_Manager), "DrunkEffect").GetValue(needs)).Drunkness(needs.GetDrunkness, needs.Get_HangoverDebuff);
            var post = (PostProcessingProfile)AccessTools.Field(typeof(BasicNeeds_Manager), "PostProcessProfile").GetValue(needs);
            var vignette = post.vignette.settings; vignette.intensity = Mathf.Clamp(needs.GetTHCLevel - 0.25f, 0, 1) * 1.33f / 2;
            post.vignette.settings = vignette;
            var aberration = post.chromaticAberration.settings;
            aberration.intensity = Mathf.Clamp01(Mathf.Max(needs.Get_DeathDebuff / 90f, needs.Get_HangoverDebuff / 180f)) * 0.7f;
            post.chromaticAberration.settings = aberration;
            if (gameplay.Player != null) gameplay.Player.Set_ShakeFactor = Mathf.Clamp(needs.GetAnxiety - 0.8f, 0, 0.2f) * 5;
        }
    }
}
