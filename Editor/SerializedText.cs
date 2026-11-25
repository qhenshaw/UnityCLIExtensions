using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        public static void Write(SerializedProperty p, string text)
        {
            string t = (text ?? "").Trim();
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask: p.longValue = long.Parse(t, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Boolean: p.boolValue = t == "1" || bool.Parse(t); break;
                case SerializedPropertyType.Float: p.floatValue = float.Parse(t, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.String: p.stringValue = text ?? ""; break;
                case SerializedPropertyType.Enum:
                {
                    int i = Array.FindIndex(p.enumNames, n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
                    if (i < 0 && int.TryParse(t, out int n2)) i = n2;
                    if (i < 0) throw new ArgumentException($"'{t}' isn't one of {string.Join(", ", p.enumNames)}.");
                    p.enumValueIndex = i;
                    break;
                }
                case SerializedPropertyType.ObjectReference:
                    p.objectReferenceValue = t == "" || t == "null" || t == "none" ? null : AssetDatabase.LoadMainAssetAtPath(t) ?? throw new ArgumentException($"No asset at '{t}'.");
                    break;
                case SerializedPropertyType.Vector2: p.vector2Value = (Vector2)ValueText.Parse(t, typeof(Vector2)); break;
                case SerializedPropertyType.Vector3: p.vector3Value = (Vector3)ValueText.Parse(t, typeof(Vector3)); break;
                case SerializedPropertyType.Color: p.colorValue = (Color)ValueText.Parse(t, typeof(Color)); break;
                default: throw new ArgumentException($"Can't set {p.propertyPath} ({p.propertyType}) from text.");
            }
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

        /// <summary>Sets "name=value;..." on fields under <paramref name="parent"/>; returns "name: old -> new" for each change.</summary>
        public static List<string> SetFields(SerializedProperty parent, string pairs)
        {
            var changes = new List<string>();
            foreach (string pair in (pairs ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"'{pair}' isn't name=value.");
                string name = pair.Substring(0, eq).Trim();
                var p = parent.FindPropertyRelative(name) ?? parent.FindPropertyRelative("m_" + char.ToUpperInvariant(name[0]) + name.Substring(1))
                        ?? throw new ArgumentException($"No setting '{name}'. Settings: {string.Join(", ", Fields(parent).Keys)}");
                string before = Read(p);
                Write(p, pair.Substring(eq + 1));
                changes.Add($"{p.name}: {before} -> {Read(p)}");
            }
            return changes;
        }
    }
}
