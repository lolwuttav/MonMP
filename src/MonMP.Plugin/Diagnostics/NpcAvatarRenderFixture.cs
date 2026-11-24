#if MONMP_DEVELOPMENT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Single-process, disposable-profile visual experiment. No session/peer or native save.
    internal sealed partial class NpcAvatarRenderFixture : IDisposable
    {
        private static readonly string[] Cases = { "stand-blue", "back", "stand-red", "stand-green", "walk", "crouch", "crouch-side",
            "wave", "shoulder-close", "drive", "chair", "carry", "binoculars", "map", "helmet", "sleep" };
        private readonly Gameplay gameplay;
        private readonly string directory;
        private readonly List<object> reports = new List<object>();
        private readonly float previousScale, started;
        private readonly int originalShirt;
        private readonly Renderer firstPerson;
        private readonly Material nativeMaterial;
        private readonly Texture nativeTexture, nativeNormal;
        private readonly Mesh nativeMesh;
        private GameObject? root;
        private StockAvatarBody? body;
        private RemoteHandProps? props;
        private int modelIndex, caseIndex, frames;
        private float modelStarted;
        private string reason = "";
        private bool disposed;

        internal NpcAvatarRenderFixture(Gameplay gameplay, string directory)
        {
            if (!Testing.LocalTestIsolation.Installed) throw new InvalidOperationException("NPC renders require an isolated test profile.");
            this.gameplay = gameplay; this.directory = Path.Combine(directory, "npc-avatar-experiment");
            Directory.CreateDirectory(this.directory);
            var hands = gameplay.Player.Get_VisualHands;
            originalShirt = hands.Get_ShirtColor; hands.ChangeShirt(originalShirt);
            firstPerson = (Renderer)AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt").GetValue(hands);
            nativeMaterial = firstPerson.sharedMaterial; nativeTexture = nativeMaterial.mainTexture;
            nativeNormal = nativeMaterial.GetTexture("_BumpMap"); nativeMesh = ((SkinnedMeshRenderer)firstPerson).sharedMesh;
            previousScale = Time.timeScale; Time.timeScale = 0; started = Time.unscaledTime;
        }
        internal string? Step()
        {
            if (Environment.GetCommandLineArgs().Contains("--monmp-avatar-head-portraits")) return StepHeadPortraits();
            if (Environment.GetCommandLineArgs().Contains("--monmp-modular-avatar-test")) return StepModular();
            if (Environment.GetCommandLineArgs().Contains("--monmp-npc-avatar-portrait-test")) return StepPortraits();
            if (Environment.GetCommandLineArgs().Contains("--monmp-npc-avatar-context-render-test")) return StepContext();
            if (Time.unscaledTime - started > 600) throw new TimeoutException("NPC render batch exceeded ten minutes.");
            if (modelIndex >= NpcAvatarModels.Catalog.Length)
            {
                Require(firstPerson.sharedMaterial == nativeMaterial && firstPerson.sharedMaterial.mainTexture == nativeTexture &&
                    firstPerson.sharedMaterial.GetTexture("_BumpMap") == nativeNormal && ((SkinnedMeshRenderer)firstPerson).sharedMesh == nativeMesh,
                    "Native first-person mesh/material/texture was replaced.");
                File.WriteAllText(Path.Combine(directory, "renders.json"), JsonConvert.SerializeObject(reports, Formatting.Indented));
                string result = "MONMP_NPC_AVATAR_RENDER_PASS\nModels=" + modelIndex + "\nRenders=" + reports.Count +
                    "\nDirectory=" + directory + "\nNo session, paired gameplay or player-choice synchronization tested.";
                Dispose(); return result;
            }
            string model = NpcAvatarModels.Catalog[modelIndex].Key;
            if (root == null)
            {
                root = new GameObject("MonMP NPC Render " + model);
                root.transform.position = new Vector3(0, 1000, 0);
                body = StockAvatarBody.TryCreateModel(root.transform, Color.white, model, out reason);
                Require(body != null && !reason.Contains("fell back"), "Model construction failed: " + model + ": " + reason);
                props = RemoteHandProps.Create(root.transform);
                caseIndex = frames = 0; modelStarted = Time.unscaledTime;
            }
            var avatar = body!;
            avatar.Present(StockAvatarBody.Mode.Stand, 0, 1.4f, 0, StockAvatarBody.Overlay.Look(0), .05f);
            if (++frames < 3 || !avatar.FabricReady && Time.unscaledTime - modelStarted < 5) return null;
            Require(avatar.FabricReady, "Colored shirt fabric did not initialize for " + model);
            Require(avatar.TestSleeveMaskApplied, "Covered skin mask did not initialize for " + model);
            if (model == "police" || model == "chef")
                Require(avatar.ExperimentHeadwearCount > 0, "Expected NPC headwear was lost for " + model);
            string shot = Cases[caseIndex];
            int color = shot.Contains("red") || shot.Contains("crouch") || shot == "carry" ? 4 :
                shot.Contains("green") || shot == "wave" || shot == "shoulder-close" ? 6 : 0;
            gameplay.Player.Get_VisualHands.ChangeShirt(color);
            avatar.ShirtMaterial.color = firstPerson.sharedMaterial.color;
            var mode = shot == "drive" || shot == "helmet" ? StockAvatarBody.Mode.Drive :
                shot == "chair" ? StockAvatarBody.Mode.Chair : shot == "sleep" ? StockAvatarBody.Mode.Lie : StockAvatarBody.Mode.Stand;
            var overlay = StockAvatarBody.Overlay.Look(shot == "binoculars" ? -20 : 0);
            if (shot == "wave" || shot == "shoulder-close") { overlay.Gesture = PlayerEmoteKind.Wave; overlay.GestureTime = .65f; }
            if (shot == "binoculars") overlay.Zoom = 1;
            if (shot == "map") overlay.Map = 1;
            avatar.SetLieBlend(shot == "sleep" ? 1 : 0);
            var goals = new StockAvatarBody.Goals();
            if (shot == "carry") { goals.Held = root.transform.position + new Vector3(0, 1.05f, .55f); goals.HeldWidth = .6f; }
            for (int i = 0; i < 10; i++)
            {
                avatar.Present(mode, shot == "walk" ? 1 : 0, 1.4f, shot.StartsWith("crouch", StringComparison.Ordinal) ? 1 : 0, overlay, .05f);
                avatar.Solve(goals, 1f);
            }
            avatar.SetRacingHelmet(shot == "helmet");
            Require(avatar.ExperimentVisibleHeadwear == (avatar.RacingHelmetVisible ? 0 : avatar.ExperimentHeadwearCount),
                "NPC headwear was not hidden/restored with the racing helmet: " + model);
            props!.Update(avatar, overlay.Map, 0, 0, 0, 0, true, zoom: overlay.Zoom > .5f, suppressFeedback: true);
            Require(avatar.ShirtMaterial.mainTexture == nativeTexture && avatar.ShirtMaterial.GetTexture("_BumpMap") == nativeNormal,
                "Model lost native plaid or normal texture: " + model);
            Require(root.GetComponentsInChildren<Collider>(true).Length == 0 && root.GetComponentsInChildren<MonoBehaviour>(true).Length == 0 &&
                root.GetComponentsInChildren<Rigidbody>(true).Length == 0, "Appearance contains native gameplay components.");
            foreach (var skin in avatar.ExperimentObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Require(skin.bones.All(b => b != null && b.IsChildOf(avatar.ExperimentObject.transform)), "A skin still uses borrowed NPC bones: " + skin.name);
            var view = shot == "back" ? AvatarPreviewCapture.View.Back : shot == "walk" || shot == "crouch-side" || shot == "chair" || shot == "sleep"
                ? AvatarPreviewCapture.View.Side : AvatarPreviewCapture.View.Front;
            string file = model + "-" + shot + ".bmp";
            AvatarPreviewCapture.SaveBitmap(root.transform, Path.Combine(directory, file), view, root.transform,
                shot == "shoulder-close" ? avatar.Head.position - Vector3.up * .25f : (Vector3?)null,
                shot == "shoulder-close" ? .42f : 1f);
            reports.Add(new { model, shot, file, color, sources = reason, fabric = avatar.FabricReady,
                sleeveMask = avatar.TestSleeveMaskApplied, helmet = avatar.RacingHelmetVisible,
                headwear = avatar.ExperimentVisibleHeadwear, headwearCount = avatar.ExperimentHeadwearCount, scale = avatar.VisualScale });
            if (++caseIndex >= Cases.Length)
            {
                ReleaseModel(); modelIndex++;
            }
            return null;
        }
        private void ReleaseModel()
        {
            props?.Destroy(); props = null; body?.Destroy(); body = null;
            if (root != null) { root.SetActive(false); UnityEngine.Object.Destroy(root); root = null; }
            ReleaseContextStage();
        }
        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            ReleaseModel(); StockAvatarBody.ClearModelTemplates();
            gameplay.Player.Get_VisualHands.ChangeShirt(originalShirt); Time.timeScale = previousScale;
        }
    }
}
#endif
