using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        // Capture only items created inside a vetted native producer's current call/step.
        // Publishing after that step keeps its final native pose and retains all yielded waits.
        internal sealed class NativeProductionOutputScope
        {
            internal Type[] OutputTypes = Array.Empty<Type>();
            internal NativeProductionOutputScope? Previous;
            internal readonly List<InteractableItems> Outputs = new List<InteractableItems>();
            internal readonly List<InteractableItems> ReplacementInputs = new List<InteractableItems>();
            internal bool Ended;
        }
        private NativeProductionOutputScope? nativeProductionOutputScope;

        internal NativeProductionOutputScope? BeginNativeProductionOutputCapture(Type[] outputTypes)
        {
            if (!host || !Ready || outputTypes.Length == 0) return null;
            var scope = new NativeProductionOutputScope { OutputTypes = outputTypes, Previous = nativeProductionOutputScope };
            nativeProductionOutputScope = scope;
            return scope;
        }

        internal NativeProductionOutputScope? BeginNativeSmallPackOutputCapture(InteractablePacks pack)
        {
            if (!host || !Ready || pack == null || !byItem.ContainsKey(pack)) return null;
            var output = pack.ObjectToSpawn == null ? null : pack.ObjectToSpawn.GetComponent<InteractableItems>();
            if (output == null || Supported(output) == null) return null;
            var scope = BeginNativeProductionOutputCapture(new[] { output.GetType() });
            scope?.ReplacementInputs.Add(pack);
            return scope;
        }

        internal void ObserveNativeProductionOutput(InteractableItems item)
        {
            ItemChanged(item);
            var scope = nativeProductionOutputScope;
            if (scope == null || scope.Ended || scope.Outputs.Count >= 512) return;
            foreach (Type type in scope.OutputTypes)
                if (item.GetType() == type) { scope.Outputs.Add(item); return; }
        }

        internal void CaptureNativeDealerReplacementInputs(NativeProductionOutputScope? scope, Collider[] nearby)
        {
            if (scope == null || scope.Ended) return;
            foreach (var collider in nearby)
            {
                var item = collider.GetComponent<Item_WeedPreRollBigPack>();
                if (item != null && byItem.ContainsKey(item) && scope.ReplacementInputs.Count < 512)
                    scope.ReplacementInputs.Add(item);
            }
        }

        internal void EndNativeProductionOutputCapture(NativeProductionOutputScope? scope)
        {
            if (scope == null || scope.Ended) return;
            scope.Ended = true;
            if (ReferenceEquals(nativeProductionOutputScope, scope)) nativeProductionOutputScope = scope.Previous;
            if (scope.Outputs.Count > 0)
                foreach (var input in scope.ReplacementInputs)
                    // A candidate the native transaction leaves untouched simply keeps this
                    // harmless ordering flag until it later retires. Do not rerun its selection
                    // or transaction to discover which of the nearby packs it consumed.
                    if (byItem.TryGetValue(input, out var binding)) binding.RetireAfterPackOutputs = true;
            foreach (var item in scope.Outputs)
            {
                if (item == null || !item.gameObject.scene.IsValid()) continue;
                try
                {
                    // Native Start normally creates the body next frame. The same idempotent
                    // basics can run now; native Start still performs its normal initialization.
                    // EnsureHostBinding preserves the crimper's moving plateau parent and a
                    // freshly served coffee's native hand placement/host lease.
                    if (EnsureHostBinding(item) == 0) nextDiscovery = 0;
                }
                catch (Exception ex)
                {
                    // Never replay production or interfere with its input debit. Normal
                    // discovery can recover the exact native item after a registration failure.
                    Debug.LogException(ex);
                    nextDiscovery = 0;
                }
            }
        }
    }
}
