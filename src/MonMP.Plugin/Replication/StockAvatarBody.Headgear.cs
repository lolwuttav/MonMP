using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class StockAvatarBody
    {
        private static MeshFilter? helmetTemplate;
        private static float nextHelmetSearch;
        private MeshRenderer? racingHelmet;
        internal bool RacingHelmetVisible => racingHelmet != null && racingHelmet.gameObject.activeSelf;

        internal void SetRacingHelmet(bool visible)
        {
            if (visible && racingHelmet == null)
            {
                if (helmetTemplate == null && Time.unscaledTime >= nextHelmetSearch)
                {
                    nextHelmetSearch = Time.unscaledTime + 3f;
                    foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
                        if (filter != null && filter.gameObject.scene.IsValid() &&
                            filter.name.StartsWith("Actors_Helmet_FullFace", StringComparison.Ordinal) &&
                            filter.sharedMesh != null && filter.sharedMesh.name == "Helmet_FullFace" &&
                            filter.GetComponent<MeshRenderer>() != null && filter.transform.parent?.name == "Head")
                        { helmetTemplate = filter; break; }
                }
                if (helmetTemplate != null)
                {
                    // Borrow only the game's exact derby mesh/materials. No native actor,
                    // colliders, scripts or bones are cloned or modified.
                    var helmet = new GameObject("MonMP Racing Helmet");
                    helmet.layer = head.gameObject.layer;
                    helmet.transform.SetParent(head, false);
                    helmet.transform.localPosition = helmetTemplate.transform.localPosition;
                    helmet.transform.localRotation = helmetTemplate.transform.localRotation;
                    helmet.transform.localScale = helmetTemplate.transform.localScale;
                    helmet.AddComponent<MeshFilter>().sharedMesh = helmetTemplate.sharedMesh;
                    racingHelmet = helmet.AddComponent<MeshRenderer>();
                    racingHelmet.sharedMaterials = helmetTemplate.GetComponent<MeshRenderer>().sharedMaterials;
                    racingHelmet.shadowCastingMode = shadowCasting?.IsShadowOnly == true ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
                }
            }
            if (racingHelmet != null && racingHelmet.gameObject.activeSelf != visible)
                racingHelmet.gameObject.SetActive(visible);
            SetNpcHeadwear(!RacingHelmetVisible);
        }
    }
}
