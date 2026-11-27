using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityCLIExtensions.Animation;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>Reads and writes SerializedProperties as text, for settings assets that have no public API.</summary>
    public static class SerializedText
    {
        /// <summary>The ProjectSettings asset at <paramref name="path"/> (ProjectSettings/QualitySettings.asset, ...).</summary>
        public static SerializedObject Settings(string path)
        {
            var obj = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault();
            if (obj == null) throw new ArgumentException($"No settings at '{path}'.");
            return new SerializedObject(obj);
        }

        public static string Read(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return ValueText.Format(p.floatValue);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum: return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.intValue.ToString();
                case SerializedPropertyType.ObjectReference: return p.objectReferenceValue != null ? (AssetDatabase.GetAssetPath(p.objectReferenceValue) is string s && s.Length > 0 ? s : p.objectReferenceValue.name) : null;
                case SerializedPropertyType.Vector2: return ValueText.Format(p.vector2Value);
                case SerializedPropertyType.Vector3: return ValueText.Format(p.vector3Value);
                case SerializedPropertyType.Vector4: return ValueText.Format(p.vector4Value);
                case SerializedPropertyType.Color: return ValueText.Format(p.colorValue);
                case SerializedPropertyType.LayerMask: return p.intValue.ToString();
                default: return p.isArray ? $"[{p.arraySize} items]" : null;
            }
        }

        /// <summary>
        /// Sets <paramref name="p"/> from text. A value of the wrong form is invalid usage (exit 2) with a one-line
        /// message naming the form; an asset path with nothing there is not found (exit 13).
        /// </summary>
        public static void Write(SerializedProperty p, string text)
        {
            string t = (text ?? "").Trim();
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.LayerMask: p.longValue = (long)ValueText.Parse(t, typeof(long)); break;
                    case SerializedPropertyType.Boolean: p.boolValue = (bool)ValueText.Parse(t, typeof(bool)); break;
                    case SerializedPropertyType.Float: p.floatValue = (float)ValueText.Parse(t, typeof(float)); break;
                    case SerializedPropertyType.String: p.stringValue = text ?? ""; break;
                    case SerializedPropertyType.Enum:
                    {
                        // A name (as listed, or as shown in the Inspector), or the stored number (antiAliasing=2 is 2x,
                        // not the third name).
                        int i = Array.FindIndex(p.enumNames, n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
                        if (i < 0) i = Array.FindIndex(p.enumDisplayNames, n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
                        if (i >= 0) { p.enumValueIndex = i; break; }
                        int old = p.intValue;
                        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n2))
                        {
                            p.intValue = n2;
                            if (p.enumValueIndex >= 0) break;
                            p.intValue = old;
                        }
                        throw new AnimUsageException($"'{t}' isn't one of: {string.Join(", ", p.enumNames)}.");
                    }
                    case SerializedPropertyType.ObjectReference:
                    {
                        if (t == "" || t == "null" || t == "none") { p.objectReferenceValue = null; break; }
                        string path = AnimationErrors.AssetPath(t);
                        var asset = AssetDatabase.LoadMainAssetAtPath(path) ?? throw new AnimNotFoundException($"No asset at '{t}'. Check the path (asset find lists assets).");
                        p.objectReferenceValue = asset;
                        if (p.objectReferenceValue != asset)
                            throw new AnimUsageException($"'{t}' is {AnimationErrors.A(asset.GetType().Name)}, which {p.name} can't hold.");
                        break;
                    }
                    case SerializedPropertyType.Vector2: p.vector2Value = (Vector2)ValueText.Parse(t, typeof(Vector2)); break;
                    case SerializedPropertyType.Vector3: p.vector3Value = (Vector3)ValueText.Parse(t, typeof(Vector3)); break;
                    case SerializedPropertyType.Color: p.colorValue = (Color)ValueText.Parse(t, typeof(Color)); break;
                    default: throw new AnimUsageException($"Can't set {p.name} ({(p.isArray ? "a list" : p.propertyType.ToString())}) from text.");
                }
            }
            catch (ArgumentException e) when (!(e is AnimUsageException) && !(e is AnimNotFoundException)) { throw new AnimUsageException(e.Message); }
        }

        /// <summary>The simple properties directly under <paramref name="parent"/>, as name -> text.</summary>
        public static Dictionary<string, string> Fields(SerializedProperty parent)
        {
            var result = new Dictionary<string, string>();
            var it = parent.Copy();
            var end = parent.GetEndProperty();
            if (!it.NextVisible(true)) return result;
            while (!SerializedProperty.EqualContents(it, end))
            {
                string value = Read(it);
                if (value != null) result[it.name] = value;
                if (!it.NextVisible(false)) break;
            }
            return result;
        }

        /// <summary>
        /// Sets "name=value;..." on fields under <paramref name="parent"/> (not applied: the caller applies the
        /// SerializedObject, so an error leaves the asset as it was). Returns "name: old -> new" for each field whose
        /// value changed. An unknown or <paramref name="readOnly"/> name, a pair without '=', or a bad value is invalid
        /// usage (exit 2), naming the field and, for an unknown one, the close names and every settable one.
        /// </summary>
        public static List<string> SetFields(SerializedProperty parent, string pairs, params string[] readOnly)
        {
            var changes = new List<string>();
            var settable = Fields(parent).Keys.Where(k => !readOnly.Contains(k)).ToList();
            foreach (string pair in (pairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.Trim().Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new AnimUsageException($"'{pair.Trim()}' isn't name=value.");
                string name = pair.Substring(0, eq).Trim();
                var p = readOnly.Contains(name, StringComparer.OrdinalIgnoreCase) ? null
                    : parent.FindPropertyRelative(name) ?? parent.FindPropertyRelative("m_" + char.ToUpperInvariant(name[0]) + name.Substring(1));
                if (p == null || readOnly.Contains(p.name))
                {
                    var close = AnimationErrors.Close(name, settable);
                    throw new AnimUsageException($"No setting '{name}'.{(close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : "")} Settings: {string.Join(", ", settable)}.");
                }
                string before = Read(p);
                try { Write(p, pair.Substring(eq + 1)); }
                catch (AnimUsageException e) { throw new AnimUsageException($"{p.name}: {e.Message}"); }
                catch (AnimNotFoundException e) { throw new AnimNotFoundException($"{p.name}: {e.Message}"); }
                string after = Read(p);
                if (before != after) changes.Add($"{p.name}: {before} -> {after}");
            }
            return changes;
        }
    }
}
