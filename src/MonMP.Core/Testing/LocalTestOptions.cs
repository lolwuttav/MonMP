using System;
using System.Globalization;
using System.Linq;

namespace MonMP.Core.Testing
{
    public sealed class LocalTestOptions
    {
        public string Instance { get; private set; } = "";
        public int Port { get; private set; } = 27020;
        public int Width { get; private set; } = 960;
        public int Height { get; private set; } = 700;
        public bool Enabled => Instance.Length > 0;
        public bool IsHost => Instance == "host";

        // The capture-only isolated variant has no fixture runner or developer
        // controls. Never silently start it without its required isolation args.
        public static LocalTestOptions ParseIsolatedCapture(string[] args)
        {
            if (args.Any(arg => arg.StartsWith("--monmp-", StringComparison.Ordinal) &&
                !arg.StartsWith("--monmp-test-", StringComparison.Ordinal)))
                throw new ArgumentException("Performance capture isolation accepts local-test options only; automated fixtures require a Development build.");
            var result = Parse(args);
            if (!result.Enabled) throw new ArgumentException("Isolated performance capture requires --monmp-test-instance=host or guest followed by a number.");
            return result;
        }

        public static LocalTestOptions Parse(string[] args)
        {
            var result = new LocalTestOptions();
            foreach (string arg in args)
            {
                if (!arg.StartsWith("--monmp-test-", StringComparison.Ordinal)) continue;
                int separator = arg.IndexOf('=');
                if (separator < 0) throw new ArgumentException("Test arguments require =value.");
                string key = arg.Substring(0, separator), value = arg.Substring(separator + 1);
                switch (key)
                {
                    case "--monmp-test-instance":
                        if (value != "host" && !(value.StartsWith("guest", StringComparison.Ordinal) && value.Length > 5 && value.Substring(5).All(char.IsDigit)))
                            throw new ArgumentException("Test instance must be host or guest followed by a number.");
                        if (value.Length > 24 || result.Enabled) throw new ArgumentException("Invalid or duplicate test instance.");
                        result.Instance = value; break;
                    case "--monmp-test-port": result.Port = Integer(value, 1, 65535); break;
                    case "--monmp-test-width": result.Width = Integer(value, 64, 3840); break;
                    case "--monmp-test-height": result.Height = Integer(value, 64, 2160); break;
                    default: throw new ArgumentException("Unknown MonMP test argument: " + key);
                }
            }
            if (!result.Enabled && args.Any(a => a.StartsWith("--monmp-test-", StringComparison.Ordinal)))
                throw new ArgumentException("Test options require an instance name.");
            return result;
        }

        private static int Integer(string text, int minimum, int maximum)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
                throw new ArgumentException("Test option is outside its allowed range.");
            return value;
        }
    }
}
