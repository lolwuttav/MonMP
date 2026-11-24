using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace MonMP.Plugin.Runtime
{
    // Bounded RAM only: no file scans, periodic IO or automatic clipboard access.
    internal sealed class SupportLog : ILogListener
    {
        internal const int MaximumCharacters = 32768, MaximumEntryCharacters = 4096, ReportCharacters = 16384;
        private readonly object gate = new object();
        private readonly Queue<string> entries = new Queue<string>();
        private int characters;
        private bool disposed;
        public void LogEvent(object sender, LogEventArgs args)
        {
            try
            {
                string source = args.Source.SourceName;
                bool important = (args.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0;
                if (!important && source != "MonMP" && source != "Unity Log") return;
                string text = args.Data?.ToString() ?? "";
                if (!important && source != "MonMP" && !text.StartsWith("MonMP", StringComparison.Ordinal) &&
                    !text.StartsWith("MONMP_", StringComparison.Ordinal)) return;
                Add("[" + args.Level + " : " + source + "] " + text);
            }
            catch { } // A third-party log value must never break the logger.
        }
        internal void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > MaximumEntryCharacters)
            {
                int end = MaximumEntryCharacters - 20;
                if (char.IsHighSurrogate(text[end - 1])) end--;
                text = text.Substring(0, end) + "\n[entry truncated]";
            }
            lock (gate)
            {
                if (disposed) return;
                entries.Enqueue(text); characters += text.Length + 1;
                while (entries.Count > 64 || characters > MaximumCharacters)
                    characters -= entries.Dequeue().Length + 1;
            }
        }
        internal string Report(string heading)
        {
            if (heading.Length > MaximumEntryCharacters) heading = heading.Substring(0, MaximumEntryCharacters);
            string body;
            lock (gate) body = string.Join("\n", entries);
            if (body.Length == 0) body = "No recent warnings, errors or MonMP context were captured.";
            int available = ReportCharacters - heading.Length - 2;
            if (body.Length > available)
            {
                int start = body.Length - available;
                if (char.IsLowSurrogate(body[start])) start++;
                body = body.Substring(start);
            }
            return heading + "\n\n" + body;
        }
        public void Dispose() { lock (gate) { disposed = true; entries.Clear(); characters = 0; } }
    }
}
