using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.VFX;
using UnityEditor.VFX.Block;
using UnityEngine;

namespace UnityCLIExtensions.VFX
{
    /// <summary>
    /// Checks a VFX Graph for authoring mistakes that compile cleanly but show nothing, and lists the event attributes
    /// each event's systems read from script. Every <c>cliext.vfx</c> edit and <c>inspect</c> returns the result, and
    /// <c>cliext.vfx check</c> runs it on its own.
    /// </summary>
    internal static class VfxGraphCheck
    {
        /// <summary>One problem found in a graph.</summary>
        internal sealed class Issue
        {
            /// <summary><c>error</c>, <c>warning</c> or <c>info</c>.</summary>
            public string severity;
            /// <summary>Id of the model the issue is on, as <c>cliext.vfx inspect</c> lists it.</summary>
            public string id;
            /// <summary>The model's name, and its system's label when it has one.</summary>
            public string where;
            public string problem;
            /// <summary><c>cliext</c> for this package's checks, <c>vfx</c> for VFX Graph's own warnings.</summary>
            public string source;
        }

        /// <summary>The attributes an event's systems read from the event (Set ... From Source in Initialize).</summary>
        internal sealed class EventAttributes
        {
            public string @event;
            public string[] attributes;
        }

        internal sealed class Result
        {
            public List<Issue> warnings = new List<Issue>();
            public List<EventAttributes> eventAttributes = new List<EventAttributes>();

            public bool HasErrors => warnings.Any(w => w.severity == Error);
            public bool HasWarnings => warnings.Any(w => w.severity == Error || w.severity == Warning);
        }

        public const string Error = "error";
        public const string Warning = "warning";
        public const string Info = "info";
        private const string OwnSource = "cliext";
        private const string GraphSource = "vfx";

        public static Result Run(VfxGraphDocument doc)
        {
            var result = new Result();
            VFXGraph graph = doc.graph;
            var contexts = graph.children.OfType<VFXContext>().ToList();

            foreach (VFXContext context in contexts)
            {
                switch (context.contextType)
                {
                    case VFXContextType.Spawner:
                        CheckSpawner(context, result);
                        break;
                    case VFXContextType.Init:
                        CheckInitialize(context, result);
                        break;
                    case VFXContextType.Output:
                        CheckOutput(context, result);
                        break;
                }
            }

            foreach (VFXBasicEvent ev in contexts.OfType<VFXBasicEvent>())
                result.eventAttributes.Add(new EventAttributes { @event = ev.eventName, attributes = AttributesReadFrom(ev) });

            AddGraphWarnings(doc, result);
            return result;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Spawners

        private static void CheckSpawner(VFXContext spawner, Result result)
        {
            bool spawns = false;
            foreach (VFXBlock block in spawner.children)
            {
                if (!block.enabled || !(block is VFXAbstractSpawner) || block is VFXSpawnerSetAttribute)
                    continue;
                spawns = true;
                if (block is VFXSpawnerBurst && BurstCountIsZero(block))
                    Add(result, Warning, block, spawner, $"{block.name} has a Count of 0, so it spawns nothing (0 is the default). Set its Count slot.");
            }

            if (!spawns && spawner.outputContexts.Any(c => c.contextType == VFXContextType.Init))
                Add(result, Warning, spawner, spawner, "This spawner has no burst or rate block, so its systems never spawn anything.");
        }

        private static bool BurstCountIsZero(VFXBlock burst)
        {
            VFXSlot count = burst.inputSlots.FirstOrDefault(s => s.name == "Count");
            if (count == null || count.HasLink(true))
                return false;
            switch (count.value)
            {
                case float constant: return constant <= 0f;
                case Vector2 range: return range.y <= 0f;
                default: return false;
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Initialize and outputs

        private static void CheckInitialize(VFXContext init, Result result)
        {
            if (init.GetSetting("capacity").valid && Convert.ToUInt64(init.GetSetting("capacity").value) == 0)
                Add(result, Warning, init, init, "Capacity is 0, so no particle can be alive.");
        }

        private static void CheckOutput(VFXContext output, Result result)
        {
            if (!(output is VFXAbstractParticleOutput particleOutput) || !particleOutput.usesFlipbook)
                return;

            VFXSlot texture = output.inputSlots.FirstOrDefault(s => s.name == "mainTexture");
            if (texture != null && !texture.HasLink(true) && texture.value == null)
                Add(result, Warning, output, output, "The output uses a flipbook but has no mainTexture, so it draws nothing.");

            VFXSlot size = output.inputSlots.FirstOrDefault(s => s.name == "flipBookSize");
            if (size != null && !size.HasLink(true) && size.value is FlipBook book && book.x <= 1 && book.y <= 1)
                Add(result, Info, output, output, "uvMode is Flipbook with a 1x1 flipBookSize: the whole texture is one frame. Set flipBookSize to the texture's grid or set uvMode to Default.");
        }

        // -------------------------------------------------------------------------------------------------------------
        // Event attributes

        /// <summary>
        /// The attributes the systems an event starts read from it: Set ... From Source blocks in their Initialize
        /// contexts, per channel for variadic attributes (scale becomes scaleX, scaleY, scaleZ), minus attributes a
        /// spawner on the way sets itself (Set SpawnEvent ...).
        /// </summary>
        private static string[] AttributesReadFrom(VFXBasicEvent ev)
        {
            var read = new SortedSet<string>(StringComparer.Ordinal);
            foreach (VFXContext next in ev.outputContexts)
            {
                if (next.contextType == VFXContextType.Init)
                {
                    AddSourceReads(next, read, null);
                    continue;
                }

                if (next.contextType != VFXContextType.Spawner)
                    continue;

                var setBySpawner = new HashSet<string>(StringComparer.Ordinal);
                foreach (VFXSpawnerSetAttribute set in next.children.OfType<VFXSpawnerSetAttribute>().Where(b => b.enabled))
                    foreach (string name in AttributeNames(set.usedAttributes.Select(a => a.name)))
                        setBySpawner.Add(name);

                foreach (VFXContext init in next.outputContexts.Where(c => c.contextType == VFXContextType.Init))
                    AddSourceReads(init, read, setBySpawner);
            }

            return read.ToArray();
        }

        private static void AddSourceReads(VFXContext init, SortedSet<string> read, HashSet<string> setBySpawner)
        {
            foreach (SetAttribute block in init.children.OfType<SetAttribute>().Where(b => b.enabled && b.Source == SetAttribute.ValueSource.Source))
            {
                foreach (VFXAttributeInfo info in block.attributes)
                {
                    string name = info.attrib.name;
                    if (string.IsNullOrEmpty(name) || (setBySpawner != null && setBySpawner.Contains(name)))
                        continue;
                    read.Add(name);
                }
            }
        }

        private static IEnumerable<string> AttributeNames(IEnumerable<string> names) => names.Where(n => !string.IsNullOrEmpty(n));

        // -------------------------------------------------------------------------------------------------------------
        // VFX Graph's own warnings (the badges the graph window shows)

        private static void AddGraphWarnings(VfxGraphDocument doc, Result result)
        {
            var models = doc.AllModels().ToList();
            var reporter = new VFXErrorReporter(VFXErrorOrigin.Invalidate);
            foreach (VFXModel model in models)
                reporter.InvalidateModelErrors(model);
            try { reporter.GenerateErrors(); }
            catch (Exception e)
            {
                result.warnings.Add(new Issue { severity = Info, problem = $"VFX Graph's own checks could not run: {e.Message}", source = GraphSource });
                return;
            }

            IVFXErrorReporter compile = doc.graph.errorManager.compileReporter;
            foreach (VFXModel model in models)
            {
                IEnumerable<ReportError> errors = reporter.GetDirtyModelErrors(model);
                if (compile != null)
                    errors = errors.Concat(compile.GetDirtyModelErrors(model));
                foreach (ReportError error in errors)
                {
                    string text = string.IsNullOrEmpty(error.description) ? error.error : error.description;
                    result.warnings.Add(new Issue
                    {
                        severity = error.type == VFXErrorType.Error ? Error : Warning,
                        id = VfxGraphDocument.IdOf(model),
                        where = Where(model, model as VFXContext ?? (model as VFXBlock)?.GetParent() as VFXContext),
                        problem = (error.type == VFXErrorType.PerfWarning ? "Performance: " : "") + text,
                        source = GraphSource
                    });
                }
            }
        }

        // -------------------------------------------------------------------------------------------------------------

        private static void Add(Result result, string severity, VFXModel model, VFXContext context, string problem)
        {
            result.warnings.Add(new Issue { severity = severity, id = VfxGraphDocument.IdOf(model), where = Where(model, context), problem = problem, source = OwnSource });
        }

        private static string Where(VFXModel model, VFXContext context)
        {
            string name = model is VFXContext ? model.GetType().Name.Replace("VFXBasic", "").Replace("VFX", "") : model.name;
            string label = context != null ? context.label : null;
            return string.IsNullOrEmpty(label) ? name : $"{name} in '{label}'";
        }
    }
}
