using System;
using MonMP.Plugin.Replication;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Runs before the existing paired Items choreography, on BOTH roles. Uses an
    // actual native prefab; never runs in player/performance builds or ordinary saves.
    internal sealed class ItemDiscoveryNativeProbe
    {
        private readonly SharedItems items;
        private GameObject? parent;
        private InteractableItems? fixture;
        private int phase, frame, scans;
        private float deadline;
        internal ItemDiscoveryNativeProbe(SharedItems items) => this.items = items;
        internal bool Tick()
        {
            if (phase == 9) return true;
            if (phase != 0 && Time.unscaledTime > deadline)
                throw new InvalidOperationException("Item registry native probe timed out in phase " + phase);
            if (phase == 0)
            {
                for (ushort index = 0; index < 64 && FishingTrashTemplateCatalog.IsValidIndex(index); index++)
                {
                    var template = FishingTrashTemplateCatalog.TemplateAt(index);
                    var nativeItem = template == null ? null : template.GetComponent<InteractableItems>();
                    Debug.Log("MONMP_REGISTRY_NATIVE fishing template " + index + ": " + template?.name +
                        "; item=" + (nativeItem == null ? "visual only" : nativeItem.GetType().Name));
                }
                parent = new GameObject("MonMP isolated registry probe"); parent.SetActive(false);
                var prefab = Resources.Load<GameObject>("Drink/MapleSyrup");
                if (prefab == null) throw new InvalidOperationException("Registry native prefab missing");
                fixture = Object.Instantiate(prefab, parent.transform).GetComponent<InteractableItems>();
                if (fixture == null || items.TestDiscoveryContains(fixture))
                    throw new InvalidOperationException("Inactive native creation unexpectedly ran the lifecycle hook");
                phase = 1; deadline = Time.unscaledTime + 14;
            }
            else if (phase == 1 && items.TestDiscoveryContains(fixture!))
            {
                Debug.Log("MONMP_REGISTRY_NATIVE safety recovered inactive native item on this role");
                // Move to a safe isolated location before native activation.
                parent!.transform.position = Singleton<Gameplay>.i.PlayerWalking.transform.position + new Vector3(0, 2, 0);
                scans = items.TestNativeDiscoveryScans; frame = Time.frameCount;
                // Guests already hide vetted local originals during reconciliation;
                // preserve that rule instead of forcing the hidden child active.
                parent.SetActive(true); phase = 2; deadline = Time.unscaledTime + 2;
            }
            else if (phase == 2 && Time.frameCount > frame + 1)
            {
                if (!items.TestDiscoveryProcessed(fixture!) || items.TestNativeDiscoveryScans != scans)
                    throw new InvalidOperationException("Activated item did not reach shared discovery within two frames without another native scan");
                Debug.Log("MONMP_REGISTRY_NATIVE activation/guest hide policy processed without another native scan");
                Object.Destroy(parent); frame = Time.frameCount; phase = 3;
            }
            else if (phase == 3 && Time.frameCount > frame + 1)
            {
                // Unity does not send OnDestroy for a child that was never active
                // (the guest intentionally hid this one). Allow the one-second
                // managed sweep to retire its Unity-null reference instead.
                if (items.TestDiscoveryContains(fixture!)) return false;
                Debug.Log("MONMP_REGISTRY_NATIVE destroyed native item released on this role");
                var prefab = Resources.Load<GameObject>("Drink/MapleSyrup");
                parent = Object.Instantiate(prefab, Singleton<Gameplay>.i.PlayerWalking.transform.position + new Vector3(0, 2, 0), Quaternion.identity);
                fixture = parent.GetComponent<InteractableItems>();
                if (!items.TestDiscoveryContains(fixture)) throw new InvalidOperationException("Active creation missed the lifecycle registry");
                scans = items.TestNativeDiscoveryScans; frame = Time.frameCount;
                phase = 4; deadline = Time.unscaledTime + 2;
            }
            else if (phase == 4 && Time.frameCount > frame + 1)
            {
                if (!items.TestDiscoveryProcessed(fixture!) || items.TestNativeDiscoveryScans != scans)
                    throw new InvalidOperationException("New active native item was not processed within two frames without a scan");
                Debug.Log("MONMP_REGISTRY_NATIVE active creation processed within two frames without native scan");
                Object.Destroy(parent); frame = Time.frameCount; phase = 6;
            }
            else if (phase == 6 && Time.frameCount > frame + 1)
            {
                if (items.TestDiscoveryContains(fixture!)) throw new InvalidOperationException("Active native destroy retained an item");
                fixture = null; parent = null; phase = 9; return true;
            }
            return false;
        }
    }
}
