using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace MonMP.Core.Testing
{
    // Test copies never read or write the normal game's PlayerPrefs through this store.
    public sealed class IsolatedPreferences
    {
        private readonly string path;
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
        private bool dirty;
        public IsolatedPreferences(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            var root = XDocument.Load(path).Root ?? throw new InvalidDataException("Missing test preferences root.");
            if (root.Name != "preferences") throw new InvalidDataException("Invalid test preferences document.");
            foreach (var item in root.Elements("entry"))
            {
                string key = (string?)item.Attribute("key") ?? throw new InvalidDataException("Missing preference key.");
                string type = (string?)item.Attribute("type") ?? "";
                object value = type == "int" ? (object)int.Parse(item.Value, CultureInfo.InvariantCulture) :
                    type == "float" ? float.Parse(item.Value, CultureInfo.InvariantCulture) :
                    type == "string" ? item.Value : throw new InvalidDataException("Invalid preference type.");
                values.Add(key, value);
            }
        }
        public bool HasKey(string key) => values.ContainsKey(key);
        public T Get<T>(string key, T fallback) => values.TryGetValue(key, out var value) && value is T typed ? typed : fallback;
        public void Set<T>(string key, T value) where T : notnull
        {
            if (!(value is string || value is int || value is float)) throw new ArgumentException("Unsupported preference type.");
            if (values.TryGetValue(key, out var previous) && Equals(previous, value)) return;
            values[key] = value; dirty = true;
        }
        public void DeleteKey(string key) { if (values.Remove(key)) dirty = true; }
        public void DeleteAll() { values.Clear(); dirty = true; }
        public void Save()
        {
            if (!dirty) return;
            var root = new XElement("preferences");
            foreach (var pair in values)
                root.Add(new XElement("entry", new XAttribute("key", pair.Key),
                    new XAttribute("type", pair.Value is int ? "int" : pair.Value is float ? "float" : "string"),
                    pair.Value is float number ? number.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            new XDocument(root).Save(temporary);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            dirty = false;
        }
    }
}
