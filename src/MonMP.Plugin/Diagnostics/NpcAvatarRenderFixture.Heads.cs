#if MONMP_DEVELOPMENT
using System;
using System.IO;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using MonMP.Plugin.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed partial class NpcAvatarRenderFixture
    {
        private string? StepHeadPortraits()
        {
            if (Time.unscaledTime - started > 600) throw new TimeoutException("Upper portrait batch timed out.");
            var torsos = AvatarPartChoices.TorsoStyles; var styles = AvatarPartChoices.HeadStyles;
            if (modelIndex >= torsos.Count * styles.Count)
            {
                File.WriteAllText(Path.Combine(directory, "renders.json"), JsonConvert.SerializeObject(reports, Formatting.Indented));
                string result = "MONMP_NPC_AVATAR_RENDER_PASS\nUpperPortraits=true\nPairs=" + modelIndex + "\nCaptures=" + reports.Count;
                Dispose(); return result;
            }
            byte torso = torsos[modelIndex / styles.Count], style = styles[modelIndex % styles.Count];
            if (root == null)
            {
                root = new GameObject("MonMP head depth portrait"); root.transform.position = new Vector3(0, 1000, 0);
                body = StockAvatarBody.TryCreateAppearance(root.transform, Color.white, new PlayerAppearance(style, torso, 0, 0), out reason);
                Require(body != null, "Head portrait construction failed: " + reason);
                frames = caseIndex = 0; modelStarted = Time.unscaledTime;
            }
            body!.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), .05f);
            if (++frames < 3 || !body.FabricReady && Time.unscaledTime - modelStarted < 5) return null;
            Require(body.FabricReady, "Head portrait fabric not ready.");
            int layer = caseIndex % 4; bool back = caseIndex >= 4;
            string part = layer == 0 ? "skin" : layer == 3 ? "nativefabric" : "fabric";
            bool mask = layer < 2;
            string file = (mask ? "mask-" : "reference-") + AvatarPartChoices.UpperPortrait(torso, style, back, part);
            var view = back ? AvatarPreviewCapture.View.Back : AvatarPreviewCapture.View.Front;
            void Capture(string name) => AvatarPreviewCapture.SaveBitmap(root.transform, Path.Combine(directory, name), view,
                root.transform, root.transform.position + Vector3.up * .86f, 1.2f, Color.clear, png: true);
            body.SetNativeShirtTexture(layer == 3); body.ShirtMaterial.color = Color.white;
            Require(body.FabricReady, "Upper portrait texture not ready.");
            if (mask) body.CaptureUpperMask(layer == 1, () => Capture(file)); else Capture(file);
            reports.Add(new { kind = mask ? "upper-mask" : "reference", torso, style, back, part, file, sources = reason });
            // Close views exercise the donor scalp fit at front, rear and looking
            // to either side, rather than relying only on tiny portrait pixels.
            if ((torso == 0 || torso == 4 || torso == 13 || torso == 24) && (style == 13 || style == 24) && layer == 0)
            {
                foreach (float yaw in new[] { -65f, 0f, 65f })
                {
                    var look = StockAvatarBody.Overlay.Look(back ? -25 : 25); look.HeadYaw = yaw;
                    body.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, look, .05f);
                    string close = "fit-" + torso + "-" + style + "-" + (back ? "back" : "front") + "-" + yaw + ".png";
                    AvatarPreviewCapture.SaveBitmap(root.transform, Path.Combine(directory, close), view, root.transform,
                        root.transform.position + Vector3.up * 1.5f, .32f, png: true);
                    reports.Add(new { kind = "hair-fit", torso, style, back, yaw, file = close });
                }
            }
            if (++caseIndex == 8) { ReleaseModel(); modelIndex++; }
            return null;
        }
    }
}
#endif
