using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Joining from an existing world skips GuestWorldLoader. Borrow only the mode
    // flags and their presentation; never run NewSave or change selected-save data.
    internal sealed class GuestModeLease : IDisposable
    {
        private readonly Gameplay game;
        private readonly bool hard, perma;
        private readonly List<Action> restoreUi = new List<Action>();
        private bool disposed;

        internal GuestModeLease(Gameplay game, bool hostHard, bool hostPerma)
        {
            this.game = game;
            hard = game.Get_isHardMode; perma = game.Get_isPermadeath;
            try
            {
                game.Get_isHardMode = hostHard; game.Get_isPermadeath = hostPerma;
                // Existing patches cache this flag in Start; do not rerun Start (it
                // subscribes native minute/day events and rebuilds plant visuals).
                var gardenMode = AccessTools.Field(typeof(Garden_DirtPatch), "isHardMode");
                foreach (var patch in UnityEngine.Object.FindObjectsOfType<Garden_DirtPatch>(true))
                {
                    bool original = gardenMode?.GetValue(patch) is true;
                    gardenMode?.SetValue(patch, hostHard);
                    restoreUi.Add(() => { if (patch != null) gardenMode?.SetValue(patch, original); });
                }
                foreach (var credit in UnityEngine.Object.FindObjectsOfType<UI_GameCreditVersion>(true))
                {
                    var text = AccessTools.Field(typeof(UI_GameCreditVersion), "gameVersion")?.GetValue(credit) as TextMeshProUGUI;
                    var icon = AccessTools.Field(typeof(UI_GameCreditVersion), "GO_Permadeath")?.GetValue(credit) as GameObject;
                    if (text == null) continue;
                    bool gradient = text.enableVertexGradient, iconActive = icon != null && icon.activeSelf;
                    var colors = text.colorGradient;
                    restoreUi.Add(() =>
                    {
                        if (text != null) { text.colorGradient = colors; text.enableVertexGradient = gradient; }
                        if (icon != null) icon.SetActive(iconActive);
                    });
                    if (hostHard &&
                        AccessTools.Field(typeof(UI_GameCreditVersion), "HardModeColor_Top")?.GetValue(credit) is Color top &&
                        AccessTools.Field(typeof(UI_GameCreditVersion), "HardModeColor_Bottom")?.GetValue(credit) is Color bottom)
                        text.colorGradient = new VertexGradient(top, top, bottom, bottom);
                    text.enableVertexGradient = hostHard || GameManager.SETTING_Cheats;
                    if (icon != null) icon.SetActive(hostHard && hostPerma);
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (game != null) { game.Get_isHardMode = hard; game.Get_isPermadeath = perma; }
            foreach (var restore in restoreUi) restore();
            restoreUi.Clear();
        }
    }
}
