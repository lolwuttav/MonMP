using BepInEx.Configuration;
using MonMP.Core.Protocol;
using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin
{
    public sealed partial class MonMPPlugin
    {
        private ConfigEntry<string> appearance = null!;
        private ConfigEntry<string> appearanceParts = null!, appearanceRgb = null!;
        private PlayerAppearance playerAppearance;
        private ConfigEntry<bool> nativeShirtTexture = null!;
        private Renderer? tintedHands;
        private PlayerVisualHands? tintedSource;
        private uint tintedRgb = uint.MaxValue;
        private static readonly System.Reflection.FieldInfo handsShirt = AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt");
        private void InitializeAppearance()
        {
            appearance = Config.Bind("Player", "Appearance", "default", "Previous whole-character choice; used to migrate existing settings.");
            appearance.Value = PlayerModelPolicy.Key(PlayerModelPolicy.Id(appearance.Value));
            appearanceParts = Config.Bind("Player", "AppearanceParts", "", "Head, torso, legs and shoes catalogue IDs, separated by commas.");
            appearanceRgb = Config.Bind("Player", "ShirtRGB", "", "Optional six-digit RGB shirt colour. Choosing a shirt in a closet clears this override.");
            nativeShirtTexture = Config.Bind("Player", "NativeShirtTexture", false, "Use the selected garment original native texture instead of plaid.");
            nativeShirtTexture.SettingChanged += (_, __) => ReadAppearance();
            ReadAppearance();
            appearanceParts.SettingChanged += (_, __) => ReadAppearance();
            appearanceRgb.SettingChanged += (_, __) => ReadAppearance();
        }
        private void ReadAppearance()
        {
            var previous = PlayerAppearance.Whole(PlayerModelPolicy.Id(appearance.Value));
            var ids = appearanceParts.Value.Split(',');
            if (ids.Length == 4 && byte.TryParse(ids[0], out byte h) && byte.TryParse(ids[1], out byte t) &&
                byte.TryParse(ids[2], out byte l) && byte.TryParse(ids[3], out byte s) && PlayerModelPolicy.Valid(h) &&
                PlayerModelPolicy.Valid(t) && PlayerModelPolicy.Valid(l) && PlayerModelPolicy.Valid(s)) previous = new PlayerAppearance(h, t, l, s);
            bool custom = appearanceRgb.Value.Length == 6 && uint.TryParse(appearanceRgb.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
            uint rgb = custom ? uint.Parse(appearanceRgb.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
            playerAppearance = new PlayerAppearance(previous.Head, previous.Torso, previous.Legs, previous.Shoes, custom, rgb, nativeShirtTexture.Value);
            tintedRgb = uint.MaxValue;
        }
        internal PlayerAppearance MenuAppearance => playerAppearance;
        internal byte MenuPlayerModel => playerAppearance.Head;
        internal bool AllowAppearanceResume() => nativeMenus?.CloseAppearance() != true;
        internal void MenuSetPlayerModel(byte model)
        {
            if (!PlayerModelPolicy.Valid(model)) return;
            MenuSetAppearance(PlayerAppearance.Whole(model));
        }
        internal void MenuSetAppearance(PlayerAppearance selected)
        {
            selected.Validate();
            nativeShirtTexture.Value = selected.NativeTexture;
            appearanceParts.Value = string.Join(",", selected.Head, selected.Torso, selected.Legs, selected.Shoes);
            appearanceRgb.Value = selected.CustomColor ? selected.Rgb.ToString("X6", CultureInfo.InvariantCulture) : "";
            ReadAppearance(); TickAppearance();
        }
        internal void ClosetSelected()
        {
            if (appearanceRgb == null || !playerAppearance.CustomColor) return;
            appearanceRgb.Value = ""; ReadAppearance();
            tintedHands = null;
        }
        internal void NativeShirtChanged(PlayerVisualHands hands)
        {
            if (!playerAppearance.CustomColor || hands != gameplay?.Player?.Get_VisualHands) return;
            // Native profile/load calls can reapply a palette colour after initial creation.
            // Only an actual closet action clears RGB; these calls retain the override.
            tintedRgb = uint.MaxValue; TickAppearance();
        }
        private void TickAppearance()
        {
            if (appearanceRgb == null) return;
            var hands = gameplay != null && gameplay.Player != null ? gameplay.Player.Get_VisualHands : null;
            uint desired = playerAppearance.CustomColor ? playerAppearance.Rgb : uint.MaxValue - 1;
            if (hands == tintedSource && tintedHands != null && tintedRgb == desired) return;
            var renderer = hands != null ? handsShirt.GetValue(hands) as Renderer : null;
            if (renderer == null) { tintedHands = null; return; }
            if (playerAppearance.CustomColor) renderer.material.color = Replication.RemotePlayers.AppearanceColor(playerAppearance, 0);
            else if (tintedHands == renderer) hands!.ChangeShirt(hands.Get_ShirtColor);
            tintedSource = hands; tintedHands = renderer; tintedRgb = desired;
        }
        internal UnityEngine.Color MenuNativeShirtTint => Replication.RemotePlayers.ShirtColor(
            (byte)UnityEngine.Mathf.Clamp(gameplay != null && gameplay.Player != null && gameplay.Player.Get_VisualHands != null
                ? gameplay.Player.Get_VisualHands.Get_ShirtColor : 0, 0, 8));
        internal UnityEngine.Color MenuShirtTint => playerAppearance.CustomColor ? Replication.RemotePlayers.AppearanceColor(playerAppearance, 0) : MenuNativeShirtTint;
#if MONMP_DEVELOPMENT
        internal UI.HostScreen TestAppearanceHost() => (nativeMenus ??= new UI.NativeMenus(this)).TestAppearanceHost();
#endif
    }
}
