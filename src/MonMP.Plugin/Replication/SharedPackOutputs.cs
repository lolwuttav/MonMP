using System;
using System.Collections.Generic;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        // Native big/huge pack Use spawns every output synchronously, but its Start/body
        // setup waits until the next frame. Capture only that exact native call's Awake
        // outputs, initialize their bodies, and register them before retiring the pack.
        internal sealed class NativePackOutputScope
        {
            internal InteractableItems Pack = null!;
            internal Type OutputType = null!;
            internal NativePackOutputScope? Previous;
            internal readonly List<InteractableItems> Outputs = new List<InteractableItems>();
            internal bool Ended;
        }
        private NativePackOutputScope? nativePackOutputScope;

        internal NativePackOutputScope? BeginNativePackOutputCapture(InteractableItems pack)
        {
            if (!host || !Ready || !Handles(pack) || !ItemPrefabCatalog.HostUse(pack.GetType())) return null;
            var scope = new NativePackOutputScope
            {
                Pack = pack, Previous = nativePackOutputScope,
                OutputType = pack is Item_WeedPreRollHugePack ? typeof(Item_WeedPreRollBigPack) : typeof(Item_WeedPreRollPack)
            };
            nativePackOutputScope = scope;
            return scope;
        }
        internal void ObserveNativePackOutput(InteractableItems item)
        {
            ItemChanged(item);
            var scope = nativePackOutputScope;
            if (scope != null && !scope.Ended && item.GetType() == scope.OutputType && scope.Outputs.Count < 64)
                scope.Outputs.Add(item);
        }
        internal void EndNativePackOutputCapture(NativePackOutputScope? scope, bool used)
        {
            if (scope == null || scope.Ended) return;
            scope.Ended = true;
            if (ReferenceEquals(nativePackOutputScope, scope)) nativePackOutputScope = scope.Previous;
            if (!used) return;
            foreach (var item in scope.Outputs)
            {
                if (item == null || !item.gameObject.scene.IsValid()) continue;
                try
                {
                    // CreateBasics is idempotent; native Start still performs names/age and
                    // the rest of its initialization normally on the following frame.
                    item.CreateBasics();
                    HostRegisterNativeOutput(item, ItemLease.Unowned, held: false);
                }
                catch (Exception ex)
                {
                    // A presentation/registration failure cannot replay the native spawn or
                    // prevent its caller from retiring the consumed pack. Discovery retries it.
                    Debug.LogException(ex);
                    nextDiscovery = 0;
                }
            }
            if (byItem.TryGetValue(scope.Pack, out var binding)) binding.RetireAfterPackOutputs = true;
        }
    }
}
