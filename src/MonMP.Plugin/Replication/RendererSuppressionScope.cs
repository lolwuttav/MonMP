using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Local pickup and remote held copies can overlap during a handoff. Release
    // only our suppression, rather than restoring a stale forceRenderingOff bit.
    internal sealed class RendererSuppressionScope : IDisposable
    {
        private sealed class Entry { internal bool Original; internal int Users; }
        private static readonly Dictionary<Renderer, Entry> entries = new Dictionary<Renderer, Entry>();
        private Renderer? renderer;
        internal bool Original { get; }
        internal RendererSuppressionScope(Renderer renderer)
        {
            this.renderer = renderer;
            if (!entries.TryGetValue(renderer, out var entry))
            { entry = new Entry { Original = renderer.forceRenderingOff }; entries.Add(renderer, entry); }
            entry.Users++; Original = entry.Original; renderer.forceRenderingOff = true;
        }
        public void Dispose()
        {
            var source = renderer; renderer = null;
            if (ReferenceEquals(source, null) || !entries.TryGetValue(source!, out var entry)) return;
            if (--entry.Users != 0) return;
            entries.Remove(source!);
            if (source != null) source.forceRenderingOff = entry.Original;
        }
    }
}
