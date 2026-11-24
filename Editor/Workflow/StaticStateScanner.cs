using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>A static field that keeps its value between Play sessions when domain reload is off.</summary>
    [Serializable]
    public class StaticStateFinding
    {
        public string assembly;
        public string type;
        /// <summary>The field, or the property for an auto-property's backing field.</summary>
        public string member;
        /// <summary><c>field</c>, <c>auto-property</c> or <c>delegate</c> (a static event or callback: subscribers pile up).</summary>
        public string kind;
        public string fieldType;
        /// <summary>
        /// True when the declaring type has a <c>[RuntimeInitializeOnLoadMethod]</c>, which usually resets its statics.
        /// Check that it resets this member, and that it runs early enough (SubsystemRegistration or AfterAssembliesLoaded).
        /// </summary>
        public bool hasResetHook;
        /// <summary>The reset hook's load type(s), when it has one.</summary>
        public string resetHookLoadType;
    }

    /// <summary>
    /// Lists the static state in the project's own runtime code that survives entering Play mode with domain reload
    /// off (Enter Play Mode Options). Each such field keeps the value it had at the end of the previous Play session,
    /// and static events keep their subscribers, unless something resets them. Readonly fields and constants are left
    /// out; their contents can still change, so check readonly collections by hand.
    /// </summary>
    public static class StaticStateScanner
    {
        // The assemblies only change with a domain reload, which clears this.
        private static List<StaticStateFinding> s_Cache;

        /// <summary>
        /// Scans the project's runtime assemblies: those built from Assets/ or from embedded and local packages. The
        /// result is cached until the next domain reload.
        /// </summary>
        public static List<StaticStateFinding> Scan() => s_Cache ??= ScanUncached();

        private static List<StaticStateFinding> ScanUncached()
        {
            var projectAssemblies = ProjectRuntimeAssemblyNames();
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => projectAssemblies.Contains(a.GetName().Name));
            var types = new List<Type>();
            foreach (var assembly in loaded)
            {
                try { types.AddRange(assembly.GetTypes()); }
                catch (ReflectionTypeLoadException e) { types.AddRange(e.Types.Where(t => t != null)); }
            }
            return ScanTypes(types);
        }

        /// <summary>Scans the given types. Public for tests.</summary>
        public static List<StaticStateFinding> ScanTypes(IEnumerable<Type> types)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var result = new List<StaticStateFinding>();
            foreach (Type type in types)
            {
                if (type.IsEnum || type.IsInterface) continue;
                if (type.Name.Contains("<") || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)) continue;

                string hook = ResetHookLoadTypes(type);
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    if (field.IsLiteral || field.IsInitOnly) continue;
                    string member = field.Name;
                    string kind = "field";
                    if (member.StartsWith("<"))
                    {
                        int end = member.IndexOf(">k__BackingField", StringComparison.Ordinal);
                        if (end < 0) continue; // other compiler-generated state (lambdas' caches)
                        member = member.Substring(1, end - 1);
                        kind = "auto-property";
                    }
                    if (typeof(Delegate).IsAssignableFrom(field.FieldType)) kind = "delegate";
                    result.Add(new StaticStateFinding
                    {
                        assembly = type.Assembly.GetName().Name,
                        type = type.FullName,
                        member = member,
                        kind = kind,
                        fieldType = FriendlyName(field.FieldType),
                        hasResetHook = hook != null,
                        resetHookLoadType = hook
                    });
                }
            }
            return result.OrderBy(f => f.hasResetHook).ThenBy(f => f.type).ThenBy(f => f.member).ToList();
        }

        private static string ResetHookLoadTypes(Type type)
        {
            var loadTypes = new List<string>();
            for (Type t = type; t != null; t = t.DeclaringType)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                foreach (RuntimeInitializeOnLoadMethodAttribute a in m.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false))
                    loadTypes.Add(a.loadType.ToString());
            }
            return loadTypes.Count == 0 ? null : string.Join(",", loadTypes.Distinct());
        }

        private static HashSet<string> ProjectRuntimeAssemblyNames()
        {
            var localRoots = PackageInfo.GetAllRegisteredPackages()
                .Where(p => p.source == PackageSource.Embedded || p.source == PackageSource.Local)
                .Where(p => p.name != "com.qhenshaw.unitycliextensions")
                .Select(p => "Packages/" + p.name + "/")
                .ToList();

            var names = new HashSet<string>();
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                bool ours = assembly.sourceFiles.Any(f =>
                    f.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    localRoots.Any(r => f.StartsWith(r, StringComparison.OrdinalIgnoreCase)));
                if (ours) names.Add(Path.GetFileNameWithoutExtension(assembly.outputPath));
            }
            return names;
        }

        private static string FriendlyName(Type t)
        {
            if (!t.IsGenericType) return t.Name;
            string name = t.Name.Substring(0, t.Name.IndexOf('`'));
            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(FriendlyName)) + ">";
        }
    }
}
