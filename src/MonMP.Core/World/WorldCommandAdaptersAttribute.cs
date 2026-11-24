using System;

namespace MonMP.Core.World
{
    // Explicitly limits a CommandRequested callback to records with these adapters.
    // Unannotated callbacks, including observers with cross-adapter side effects,
    // continue to receive every accepted command in normal subscription order.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class WorldCommandAdaptersAttribute : Attribute
    {
        internal ushort[] Adapters { get; }
        public WorldCommandAdaptersAttribute(params ushort[] adapters)
        {
            Adapters = adapters == null ? Array.Empty<ushort>() : (ushort[])adapters.Clone();
        }
    }
}
