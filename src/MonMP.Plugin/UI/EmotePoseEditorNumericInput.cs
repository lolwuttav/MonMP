using System;
using System.Collections.Generic;
using System.Globalization;

namespace MonMP.Plugin.UI
{
    // Text remains editable (including temporary "-" / "1e-") until focus leaves
    // the field. Save and selection changes must reconcile every retained field.
    internal sealed class EmotePoseEditorNumericInput
    {
        private sealed class Field
        {
            internal string Text = "", Label = "";
            internal float Minimum, Maximum, Value;
            internal bool Valid, Clamped;
        }

        private readonly Dictionary<string, Field> fields = new Dictionary<string, Field>(StringComparer.Ordinal);

        internal static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        internal string GetText(string key, float value) => fields.TryGetValue(key, out var field) ? field.Text : Format(value);

        internal float Edit(string key, string label, string text, float value, float minimum, float maximum)
        {
            var field = new Field { Text = text, Label = label, Minimum = minimum, Maximum = maximum, Value = value };
            field.Valid = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) &&
                !float.IsNaN(number) && !float.IsInfinity(number);
            if (field.Valid)
            {
                field.Value = Math.Max(minimum, Math.Min(maximum, number));
                field.Clamped = number != field.Value;
            }
            fields[key] = field;
            return field.Value;
        }

        internal bool HasError(string key) => fields.TryGetValue(key, out var field) && !field.Valid;
        internal bool HasErrors
        {
            get
            {
                foreach (var entry in fields.Values) if (!entry.Valid) return true;
                return false;
            }
        }
        internal string Hint(string key)
        {
            if (!fields.TryGetValue(key, out var field)) return "";
            if (!field.Valid) return Error(field);
            return field.Clamped ? "Limited to " + Format(field.Value) + "; press Enter or leave the field to accept." : "";
        }

        internal bool TryCommit(string key)
        {
            if (!fields.TryGetValue(key, out var field)) return true;
            if (!field.Valid) return false;
            field.Text = Format(field.Value); field.Clamped = false;
            return true;
        }

        internal bool TryCommitAll(out string error)
        {
            // Validate first: an unsuccessful Save must preserve all visible input.
            foreach (var field in fields.Values)
                if (!field.Valid) { error = Error(field); return false; }
            foreach (var field in fields.Values)
            { field.Text = Format(field.Value); field.Clamped = false; }
            error = ""; return true;
        }

        internal void Remove(string key) => fields.Remove(key);
        internal void Clear() => fields.Clear();
        private static string Error(Field field) => field.Label + ": enter a finite number from " +
            Format(field.Minimum) + " to " + Format(field.Maximum) + ".";
    }
}
