using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Values as text for commands that set and show fields of many types: numbers, true/false, enum names,
    /// <c>[x,y,z]</c> for vectors and colours (also <c>#RRGGBB</c>), asset paths for textures and other assets,
    /// <c>[[time,value],...]</c> for curves, <c>[[time,r,g,b(,a)],...]</c> for gradients, and JSON for other structs.
    /// </summary>
    public static class ValueText
    {
        public static string Format(object value)
        {
            switch (value)
            {
                case null: return null;
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case Enum e: return e.ToString();
                case IFormattable n when value.GetType().IsPrimitive: return n.ToString(null, CultureInfo.InvariantCulture);
                case Vector2 v: return Arr(v.x, v.y);
                case Vector3 v: return Arr(v.x, v.y, v.z);
                case Vector4 v: return Arr(v.x, v.y, v.z, v.w);
                case Color c: return Arr(c.r, c.g, c.b, c.a);
                case Matrix4x4 m: return "[" + string.Join(",", Enumerable.Range(0, 16).Select(i => m[i].ToString("R", CultureInfo.InvariantCulture))) + "]";
                case AnimationCurve curve: return "[" + string.Join(",", curve.keys.Select(k => Arr(k.time, k.value))) + "]";
                case Gradient g: return "[" + string.Join(",", g.colorKeys.Select(k => Arr(k.time, k.color.r, k.color.g, k.color.b))) + "]";
                case Object o: return o == null ? null : (AssetDatabase.GetAssetPath(o) is string p && p.Length > 0 ? p : o.name);
                default:
                    try { string json = JsonUtility.ToJson(value); if (json != "{}") return json; } catch { }
                    return value.ToString();
            }
        }

        private static string Arr(params float[] values) => "[" + string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

        /// <summary>Parses <paramref name="text"/> into <paramref name="type"/>; see the class summary for the forms.</summary>
        public static object Parse(string text, Type type)
        {
            if (type == null) throw new ArgumentException("Unknown value type.");
            string s = (text ?? "").Trim();
            if (type == typeof(string)) return text ?? "";
            if (type == typeof(bool)) return s == "1" || bool.Parse(s);
            if (type.IsEnum)
            {
                if (int.TryParse(s, out int asInt)) return Enum.ToObject(type, asInt);
                return Enum.Parse(type, string.Join(", ", s.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim())), true);
            }
            if (type == typeof(float)) return float.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(uint)) return uint.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(s, CultureInfo.InvariantCulture);
            if (type == typeof(Vector2)) { var f = Floats(s, 2); return new Vector2(f[0], f[1]); }
            if (type == typeof(Vector3)) { var f = Floats(s, 3); return new Vector3(f[0], f[1], f[2]); }
            if (type == typeof(Vector4))
            {
                if (s.StartsWith("#") && ColorUtility.TryParseHtmlString(s, out Color html)) return (Vector4)html;
                var f = Floats(s, 4); return new Vector4(f[0], f[1], f[2], f[3]);
            }
            if (type == typeof(Color))
            {
                if (s.StartsWith("#") && ColorUtility.TryParseHtmlString(s, out Color html)) return html;
                var f = Floats(s, 3, 4); return new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
            }
            if (type == typeof(Matrix4x4))
            {
                var f = Floats(s, 16); var m = new Matrix4x4();
                for (int i = 0; i < 16; i++) m[i] = f[i];
                return m;
            }
            if (type == typeof(AnimationCurve))
            {
                var keys = Rows(s).Select(r => new Keyframe(r[0], r.Length > 1 ? r[1] : 0f)).ToArray();
                var curve = new AnimationCurve(keys);
                for (int i = 0; i < keys.Length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                }
                return curve;
            }
            if (type == typeof(Gradient))
            {
                var rows = Rows(s);
                var g = new Gradient();
                g.SetKeys(rows.Select(r => new GradientColorKey(new Color(r[1], r[2], r[3]), r[0])).ToArray(),
                          rows.Select(r => new GradientAlphaKey(r.Length > 4 ? r[4] : 1f, r[0])).ToArray());
                return g;
            }
            if (typeof(Object).IsAssignableFrom(type))
            {
                if (s == "" || s == "null") return null;
                Object asset = AssetDatabase.LoadAssetAtPath(s, type) ?? AssetDatabase.LoadAllAssetsAtPath(s).FirstOrDefault(type.IsInstanceOfType);
                if (asset == null) throw new ArgumentException($"No {type.Name} at '{s}'.");
                return asset;
            }
            if (type.IsValueType && !type.IsPrimitive && s.StartsWith("{"))
                return JsonUtility.FromJson(s, type);
            throw new ArgumentException($"Can't set a value of type {type.Name} from text.");
        }

        private static float[] Floats(string s, int min, int max = -1)
        {
            var parts = s.Trim('[', ']', '(', ')', ' ').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length == 1 && min > 1) parts = Enumerable.Repeat(parts[0], min).ToArray();
            if (parts.Length < min || (max > 0 && parts.Length > max) || (max < 0 && parts.Length > min))
                throw new ArgumentException($"Expected {min}{(max > 0 ? "-" + max : "")} numbers, got '{s}'.");
            return parts;
        }

        private static List<float[]> Rows(string s)
        {
            string inner = s.Trim();
            if (inner.StartsWith("[")) inner = inner.Substring(1);
            if (inner.EndsWith("]")) inner = inner.Substring(0, inner.Length - 1);
            var rows = new List<float[]>();
            foreach (string row in inner.Split(new[] { ']' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string r = row.Trim().TrimStart(',').Trim().TrimStart('[');
                if (r.Length == 0) continue;
                rows.Add(r.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray());
            }
            if (rows.Count == 0) throw new ArgumentException($"Expected [[t,v],...], got '{s}'.");
            return rows;
        }
    }
}

