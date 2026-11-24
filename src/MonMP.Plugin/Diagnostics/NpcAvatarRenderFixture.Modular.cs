#if MONMP_DEVELOPMENT
using System;
using System.IO;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed partial class NpcAvatarRenderFixture
    {
        private static readonly PlayerAppearance[] MixedParts = {
            new PlayerAppearance(13, 4, 1, 23), new PlayerAppearance(23, 13, 3, 19),
            new PlayerAppearance(18, 24, 1, 23), new PlayerAppearance(22, 18, 24, 13),
            new PlayerAppearance(24, 22, 18, 3), new PlayerAppearance(1, 20, 23, 24),
            new PlayerAppearance(20, 23, 13, 18), new PlayerAppearance(3, 21, 8, 0)
        };
        private string? StepModular()
        {
            if (Time.unscaledTime - started > 600) throw new TimeoutException("Modular avatar render batch timed out.");
            bool mixed = modelIndex >= NpcAvatarModels.Catalog.Length;
            int mix = modelIndex - NpcAvatarModels.Catalog.Length;
            if (mixed && mix >= MixedParts.Length)
            {
                File.WriteAllText(Path.Combine(directory, "renders.json"), JsonConvert.SerializeObject(reports, Formatting.Indented));
                string result = "MONMP_NPC_AVATAR_RENDER_PASS\nModular=true\nModels=" + modelIndex + "\nRenders=" + reports.Count;
                Dispose(); return result;
            }
            var appearance = mixed ? MixedParts[mix] : PlayerAppearance.Whole((byte)modelIndex);
            string key = mixed ? "mix-" + mix : NpcAvatarModels.Catalog[modelIndex].Key;
            if (root == null)
            {
                root = new GameObject("MonMP modular render " + key); root.transform.position = new Vector3(0, 1000, 0);
                body = StockAvatarBody.TryCreateAppearance(root.transform, Color.white, appearance, out reason);
                Require(body != null, "Modular construction failed: " + key + ": " + reason);
                frames = caseIndex = 0; modelStarted = Time.unscaledTime;
            }
            var avatar = body!;
            avatar.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), .05f);
            if (++frames < 3 || !avatar.FabricReady && Time.unscaledTime - modelStarted < 5) return null;
            Require(avatar.FabricReady && avatar.TestSleeveMaskApplied, "Modular garment or skin coverage failed: " + key);
            if (!mixed && caseIndex < 14)
            {
                string[] layers = { "shoes", "legs", "torso", "fabric", "nativefabric", "headskin", "headwear" };
                int[] slots = { 3, 2, 1, 1, 1, 0, 0 };
                int layer = caseIndex % 7; bool back = caseIndex >= 7;
                string file = key + (back ? "-back-" : "-front-") + layers[layer] + ".png";
                avatar.SetNativeShirtTexture(layer == 4);
                avatar.ShirtMaterial.color = Color.white;
                avatar.CaptureModularPart(slots[layer], layer == 3 || layer == 4, () => AvatarPreviewCapture.SaveBitmap(root.transform,
                    Path.Combine(directory, file), back ? AvatarPreviewCapture.View.Back : AvatarPreviewCapture.View.Front, root.transform,
                    root.transform.position + Vector3.up * .86f, 1.2f, Color.clear, png: true), headwearOnly: layer == 6, skinOnly: layer == 5);
                reports.Add(new { model = key, file, layer = layers[layer], back, appearance, sources = reason });
                caseIndex++; return null;
            }
            avatar.SetNativeShirtTexture(false);
            int pose = mixed ? caseIndex : caseIndex - 14;
            var overlay = StockAvatarBody.Overlay.Look(0);
            if (pose == 1) { overlay.Gesture = PlayerEmoteKind.Wave; overlay.GestureTime = .65f; }
            var mode = pose == 3 ? StockAvatarBody.Mode.Drive : StockAvatarBody.Mode.Stand;
            avatar.ShirtMaterial.color = RemotePlayers.AppearanceColor(new PlayerAppearance(0, 0, 0, 0, true, 0x20B879), 0);
            for (int i = 0; i < 10; i++) { avatar.Present(mode, 0, 0, pose == 2 ? 1 : 0, overlay, .05f); avatar.Solve(new StockAvatarBody.Goals(), 1); }
            string shot = key + "-pose-" + pose + ".bmp";
            AvatarPreviewCapture.SaveBitmap(root.transform, Path.Combine(directory, shot), pose == 4 ? AvatarPreviewCapture.View.Back : AvatarPreviewCapture.View.Front, frame: root.transform);
            reports.Add(new { model = key, file = shot, pose, appearance, sources = reason });
            if (pose >= 4) { ReleaseModel(); modelIndex++; } else caseIndex++;
            return null;
        }
    }
}
#endif
