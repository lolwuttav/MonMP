#if MONMP_DEVELOPMENT
using System;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Plugin.Replication;
using MonMP.Plugin.UI;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Actual native uGUI, one isolated process, no network session. The preview
    // camera count is sampled before capture creates its temporary test camera.
    internal sealed class AppearanceUiFixture : IDisposable
    {
        private readonly MonMPPlugin plugin = MonMPPlugin.Instance!;
        private readonly string directory;
        private readonly byte original;
        private MultiplayerScreen? main;
        private HostScreen? host;
        private EscapeMenuV2? escape;
        private int phase, index, nextFrame, checks, cameras;
        private bool menuDone;
        private int oldShirt = -1;
        private RemotePlayers? actors;
        private float actorDeadline;
        private int buildsBeforeBurst;
        private int propIdentity;
        internal bool WorldStarted { get; private set; }
        internal AppearanceUiFixture(string directory)
        {
            if (!Testing.LocalTestIsolation.Installed) throw new InvalidOperationException("Appearance UI probe requires isolation.");
            this.directory = directory; original = plugin.MenuPlayerModel;
        }
        private static int PortraitTextures => Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t.name == "MonMP character preview");
        private void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
        private void Wait(int frames = 3) { nextFrame = Time.frameCount + frames; }
        internal bool MenuStep()
        {
            if (menuDone) return true;
            if (Time.unscaledTime < 10 || Time.frameCount < nextFrame) return false;
            if (phase == 0)
            {
                Screen.SetResolution(1920, 1080, false); main = plugin.DirectorMultiplayer(); cameras = Camera.allCamerasCount;
                phase++; Wait(8); return false;
            }
            if (phase == 1) { UiDump.ScreenshotOffscreen("appearance-main-entry"); main!.TestOpenAppearance(); phase++; Wait(); return false; }
            var picker = main!.TestAppearance;
            if (phase == 2)
            {
                Check(picker.Open && picker.TestReady, "Main-menu character preview failed.");
                Check(Camera.allCamerasCount == cameras && PortraitTextures == 4, "Preview created a camera or retained extra textures.");
                if (index < 50)
                {
                    byte id = (byte)(index / 2); picker.TestSelect(id);
                    if ((index & 1) != 0) picker.TestTurn(); else if (index > 0) picker.TestTurn();
                    // Each model has front and rear. For even indices restore front
                    // from the prior model's rear before capturing.
                    index++; phase = 3; Wait(); return false;
                }
                picker.TestSelect(13); picker.TestApply(); phase = 4; Wait(); return false;
            }
            if (phase == 3)
            {
                Check(picker.TestReady && PortraitTextures == 4, "Main-menu selection leaked/missed textures.");
                int id = (index - 1) / 2;
                if (id == 0 || id == 13 || id == 23 || id == 24)
                    UiDump.ScreenshotOffscreen("appearance-main-" + id + ((index & 1) == 0 ? "-back" : "-front"));
                phase = 2; Wait(1); return false;
            }
            if (phase == 4)
            {
                Check(plugin.MenuPlayerModel == 13 && !picker.Open && PortraitTextures == 0, "Apply or texture cleanup failed.");
                main.TestOpenAppearance(); picker.TestSelect(24); picker.Close(); phase++; Wait(); return false;
            }
            Check(plugin.MenuPlayerModel == 13 && PortraitTextures == 0, "Cancel changed the saved model or leaked textures.");
            main.Close(); main = null; menuDone = true; phase = index = 0; return true;
        }
        internal string? WorldStep(Gameplay gameplay)
        {
            WorldStarted = true;
            if (Time.frameCount < nextFrame) return null;
            if (phase == 0)
            {
                Screen.SetResolution(1920, 1080, false);
                oldShirt = gameplay.Player.Get_VisualHands.Get_ShirtColor;
                gameplay.Player.Get_VisualHands.ChangeShirt(6);
                escape = UnityEngine.Object.FindObjectOfType<EscapeMenuV2>(true); Check(escape != null, "Native pause menu missing.");
                escape!.Open_Force();
                host = plugin.TestAppearanceHost(); cameras = Camera.allCamerasCount; phase++; Wait(8); return null;
            }
            var picker = host!.TestAppearance;
            if (phase == 1) { UiDump.ScreenshotOffscreen("appearance-world-entry"); host.TestOpenAppearance(); phase++; Wait(); return null; }
            if (phase == 2)
            {
                Check(picker.Open && picker.TestReady && picker.TestTint == plugin.MenuShirtTint, "World shirt tint or preview failed.");
                Check(Camera.allCamerasCount == cameras && PortraitTextures == 4, "World preview has recurring camera/texture cost.");
                UiDump.ScreenshotOffscreen("appearance-world-green");
                picker.TestSelect(24); phase++; Wait(); return null;
            }
            if (phase == 3)
            {
                picker.TestParts(new PlayerAppearance(23, 13, 1, 18));
                var before = picker.TestTint;
                picker.TestOpenColor();
                Check(picker.TestColorOpen, "Colour picker did not open.");
                picker.TestColorDrag(.57f, .8f, .9f);
                Check(((Color32)picker.TestTint).Equals((Color32)Color.HSVToRGB(.57f, .8f, .9f)), "HSV pointer handlers did not update RGB.");
                UiDump.ScreenshotOffscreen("appearance-colour-picker");
                picker.TestCloseColor(true);
                Check(picker.TestTint == before && !picker.TestColorOpen, "Colour cancellation did not restore the previous mode and value.");
                picker.TestTexture(); Check(picker.TestReady, "Original texture preview unavailable.");
                picker.TestTexture(); picker.TestRgb("12ABEF");
                UiDump.ScreenshotOffscreen("appearance-world-mixed-rgb"); picker.TestApply(); phase++; Wait(); return null;
            }
            if (phase == 4)
            {
                Check(plugin.MenuAppearance == new PlayerAppearance(23, 13, 1, 18, true, 0x12ABEF) && PortraitTextures == 0, "Mixed part/RGB apply or cleanup failed.");
                host.TestOpenAppearance(); picker.TestSelect(23); picker.TestTurn(); phase++; Wait(); return null;
            }
            if (phase == 5)
            {
                UiDump.ScreenshotOffscreen("appearance-world-police-back");
                escape!.ResumeGame(true);
                Check(!picker.Open && escape.isOpen && gameplay.Get_isGamePaused, "Native Back resumed the world instead of closing the modal.");
                host.TestOpenAppearance(); picker.TestBack(); escape.ResumeGame(true);
                Check(!picker.Open && escape.isOpen && gameplay.Get_isGamePaused, "Plugin-first Back resumed the world in the same frame.");
                phase++; Wait(); return null;
            }
            if (phase == 6)
            {
                Check(plugin.MenuAppearance == new PlayerAppearance(23, 13, 1, 18, true, 0x12ABEF) && PortraitTextures == 0, "Closing world selection changed its model.");
                var hands = gameplay.Player.Get_VisualHands;
                var shirt = (Renderer)HarmonyLib.AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt").GetValue(hands);
                Check(((Color32)shirt.sharedMaterial.color).Equals(new Color32(0x12, 0xAB, 0xEF, 255)), "RGB did not reach native first-person sleeves.");
                hands.ChangeShirt(4);
                Check(((Color32)shirt.sharedMaterial.color).Equals(new Color32(0x12, 0xAB, 0xEF, 255)), "A native palette restore overrode saved RGB.");
                var wardrobe = UnityEngine.Object.FindObjectsOfType<Furniture_GardeRobe>(true).First();
                wardrobe.MouseDown();
                Check(!plugin.MenuAppearance.CustomColor && shirt.sharedMaterial.color == hands.GetShirtColorFromEnum((Player_ShirtColor)hands.Get_ShirtColor),
                    "Native closet selection did not clear RGB and restore its actual palette colour.");
                gameplay.Player.Get_VisualHands.ChangeShirt(oldShirt); oldShirt = -1;
                plugin.MenuSetPlayerModel(original);
                host.Close();
                actors = new RemotePlayers(); actors.Add(new PeerInfo(60001, Guid.NewGuid(), "Appearance render probe", 0xFFFFFF));
                actorDeadline = Time.unscaledTime + 120; index = 0;
                phase++; Wait(); return null;
            }
            if (phase == 7)
            {
                Check(Time.unscaledTime < actorDeadline, "Native avatar model changes timed out.");
                actors!.Apply(60001, new PlayerPose(0, 1000, 0, 0, 0, shirt: (byte)(index % 9), model: (byte)index)); actors.Update();
                if (actors.TestBodyModel(60001) != index || !actors.TestModelFabric(60001)) return null;
                Check(!actors.TestBody(60001).Contains("fell back"), "Native avatar silently fell back: " + index);
                Check(actors.TestShirt(60001) == index % 9, "Changing a model lost its shirt selection.");
                if (index == 0) propIdentity = actors.TestModelPropIdentity(60001);
                Check(propIdentity != 0 && actors.TestModelPropIdentity(60001) == propIdentity, "Changing a model recreated its independent hand props.");
                if (++index <= PlayerModelPolicy.Maximum) return null;
                buildsBeforeBurst = actors.TestModelBuilds(60001);
                for (byte model = 0; model <= PlayerModelPolicy.Maximum; model++)
                    actors.Apply(60001, new PlayerPose(0, 1000, 0, 0, 0, model: model));
                actors.Apply(60001, new PlayerPose(0, 1000, 0, 0, 0, model: 0)); actors.Update();
                Check(actors.TestModelBuilds(60001) == buildsBeforeBurst, "Rapid choices caused immediate extra avatar rebuilding.");
                phase++; return null;
            }
            if (phase == 8)
            {
                Check(Time.unscaledTime < actorDeadline, "Final model selection failed to settle.");
                actors!.Apply(60001, new PlayerPose(0, 1000, 0, 0, 0, model: 0)); actors.Update();
                if (actors.TestBodyModel(60001) != 0 || !actors.TestModelFabric(60001)) return null;
                Check(actors.TestModelBuilds(60001) == buildsBeforeBurst + 1, "Intermediate rapid choices were rebuilt.");
                int stableBuilds = actors.TestModelBuilds(60001);
                actors.Apply(60001, new PlayerPose(0, 1000, 0, 0, 0, appearance: PlayerAppearance.Whole(0).WithTexture(true))); actors.Update();
                Check(actors.TestNativeTexture(60001) && actors.TestModelBuilds(60001) == stableBuilds, "Original texture toggle rebuilt or failed to update the body.");
                actors.Apply(60001, new PlayerPose(0, 1000, 0, 0, 0, appearance: PlayerAppearance.Whole(0))); actors.Update();
                Check(!actors.TestNativeTexture(60001) && actors.TestModelBuilds(60001) == stableBuilds, "Plaid restoration rebuilt or failed to update the body.");
                actors.Dispose(); actors = null; phase++; Wait(); return null;
            }
            Check(PortraitTextures == 0, "Preview textures survived closing the selector.");
            File.WriteAllText(Path.Combine(directory, "appearance-ui-report.txt"), "MONMP_APPEARANCE_UI_PASS\nChecks=" + checks +
                "\n50 main-menu views, apply/cancel, loaded-world shirt tint and cleanup; no preview camera." +
                "\n25 native observer avatar changes and one coalesced rapid-selection burst.\nNo paired gameplay tested.");
            Dispose(); return "MONMP_NPC_AVATAR_RENDER_PASS\nAppearanceUI=true\nChecks=" + checks;
        }
        public void Dispose()
        {
            main?.Close(); main = null; host?.Close(); host = null; plugin.MenuSetPlayerModel(original);
            actors?.Dispose(); actors = null;
            var gameplay = plugin.Gameplay;
            if (oldShirt >= 0 && gameplay != null && gameplay.Player != null) gameplay.Player.Get_VisualHands.ChangeShirt(oldShirt);
            oldShirt = -1;
            if (escape != null && escape.isOpen) escape.ResumeGame(true);
        }
    }
}
#endif
