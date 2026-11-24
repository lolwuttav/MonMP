#if MONMP_PERFORMANCE_CAPTURE
using System;
using System.Collections.Generic;
using MonMP.Core.Protocol;

namespace MonMP.Core.Session
{
    // Performance-capture build only: forwards Core dispatch timings (Stopwatch ticks)
    // to the plugin's detail scopes so time inside Session.Tick can be attributed.
    public static class CaptureTiming
    {
        public static Action<string, long>? Sink;
        private static readonly string?[] packetNames = new string?[256];
        private static readonly Dictionary<ushort, string> commandNames = new Dictionary<ushort, string>();

        internal static void Detail(string name, long ticks) => Sink?.Invoke(name, ticks);

        // The wire header is magic (4), version (2), then the message kind byte.
        internal static void Packet(byte[] bytes, long ticks)
        {
            var sink = Sink;
            if (sink == null) return;
            byte kind = bytes.Length > 6 ? bytes[6] : (byte)0;
            sink(packetNames[kind] ??= "Session.Packet." + (MessageKind)kind, ticks);
        }

        // Times each subscriber separately so a costly departure handler can be named.
        internal static void Each(Action<uint>? handlers, uint id)
        {
            if (handlers == null) return;
            if (Sink == null) { handlers(id); return; }
            foreach (Action<uint> handler in handlers.GetInvocationList())
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { handler(id); }
                finally
                {
                    var method = handler.Method;
                    Detail("Session.PeerLeft." + method.DeclaringType?.Name + "." + method.Name, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                }
            }
        }

        internal static void Command(ushort adapter, long ticks)
        {
            var sink = Sink;
            if (sink == null) return;
            if (!commandNames.TryGetValue(adapter, out var name)) commandNames.Add(adapter, name = "World.Command." + adapter);
            sink(name, ticks);
        }
    }
}
#endif
