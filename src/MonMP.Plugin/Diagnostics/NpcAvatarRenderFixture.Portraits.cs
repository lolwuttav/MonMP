#if MONMP_DEVELOPMENT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Plugin.Replication;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed partial class NpcAvatarRenderFixture
    {
        private string? StepPortraits()
        {
            if (Time.unscaledTime - started > 600) throw new TimeoutException("Portrait render batch timed out.");
            if (modelIndex >= NpcAvatarModels.Catalog.Length)
            {
                File.WriteAllText(Path.Combine(directory, "renders.json"), JsonConvert.SerializeObject(reports, Formatting.Indented));
                string result = "MONMP_NPC_AVATAR_RENDER_PASS\nPortraits=true\nModels=" + modelIndex + "\nRenders=" + reports.Count;
                Dispose(); return result;
            }
            string model = NpcAvatarModels.Catalog[modelIndex].Key;
            if (root == null)
            {
                root = new GameObject("MonMP portrait " + model); root.transform.position = new Vector3(0, 1000, 0);
                body = StockAvatarBody.TryCreateModel(root.transform, Color.white, model, out reason);
                Require(body != null && !reason.Contains("fell back"), "Portrait model failed: " + model + ": " + reason);
                frames = caseIndex = 0; modelStarted = Time.unscaledTime;
            }
            var avatar = body!;
            avatar.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), .05f);
            if (++frames < 3 || (!avatar.FabricReady || !avatar.TestSleeveMaskApplied) && Time.unscaledTime - modelStarted < 5) return null;
            Require(avatar.FabricReady && avatar.TestSleeveMaskApplied, "Portrait fabric not ready: " + model);
            avatar.ShirtMaterial.color = Color.white;
            bool mask = (caseIndex & 1) != 0, back = caseIndex >= 2;
            string shot = (back ? "back" : "front") + (mask ? "-mask" : "-full");
            string file = model + "-" + shot + ".bmp";
            var changed = new List<(Renderer Renderer, Material[] Materials)>();
            Material? white = null, black = null;
            try
            {
                if (mask)
                {
                    var shader = Shader.Find("Standard") ?? throw new InvalidOperationException("Portrait mask shader missing.");
                    white = new Material(shader) { color = Color.white }; black = new Material(shader) { color = Color.black };
                    foreach (var material in new[] { white, black })
                    {
                        material.SetFloat("_Metallic", 0); material.SetFloat("_Glossiness", 0);
                        material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                        material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", material == white ? Color.white : Color.black);
                    }
                    foreach (var surface in root.GetComponentsInChildren<Renderer>(true))
                    {
                        changed.Add((surface, surface.sharedMaterials));
                        surface.sharedMaterials = surface.sharedMaterials.Select(_ => surface.name == "Shirt_LongSleeve" ? white : black).ToArray();
                    }
                }
                AvatarPreviewCapture.SaveBitmap(root.transform, Path.Combine(directory, file),
                    back ? AvatarPreviewCapture.View.Back : AvatarPreviewCapture.View.Front, root.transform,
                    distanceScale: 1.2f, clearColor: mask ? Color.black : (Color?)null);
            }
            finally
            {
                foreach (var value in changed) if (value.Renderer != null) value.Renderer.sharedMaterials = value.Materials;
                if (white != null) UnityEngine.Object.Destroy(white); if (black != null) UnityEngine.Object.Destroy(black);
            }
            reports.Add(new { model, shot, file, portrait = true, sources = reason });
            if (++caseIndex >= 4) { ReleaseModel(); modelIndex++; }
            return null;
        }
    }
}
#endif
