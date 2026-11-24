using System;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// A replacement for <see cref="EditorApplication.delayCall"/> for work that has to happen soon.
    /// </summary>
    /// <remarks>
    /// In an unfocused Editor driven from the command line, delayCall callbacks have been seen to sit in the queue for
    /// minutes after a domain reload while <see cref="EditorApplication.update"/> kept ticking (6000.5.8f1, Interaction
    /// Mode No Throttling). This runs the work from <see cref="EditorApplication.update"/> instead, asks for another
    /// update with <see cref="EditorApplication.QueuePlayerLoopUpdate"/> while it waits, and gives up with a warning after
    /// a timeout, so deferred work either runs or says it didn't. Like delayCall, pending work does not survive a domain
    /// reload: queue it again from an <c>[InitializeOnLoad]</c> or <c>[InitializeOnLoadMethod]</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// EditorDeferred.Run(() => Debug.Log("next update"));
    /// EditorDeferred.Run(OpenWindow, when: () => !EditorApplication.isCompiling &amp;&amp; !EditorApplication.isUpdating, timeoutSeconds: 120);
    /// </code>
    /// </example>
    public static class EditorDeferred
    {
        /// <summary>A queued call. <see cref="Cancel"/> drops it if it hasn't run.</summary>
        public sealed class Handle
        {
            internal Action Action;
            internal Func<bool> When;
            internal Action OnTimeout;
            internal double Deadline;
            internal string Label;

            /// <summary>True once the work ran (or threw).</summary>
            public bool Ran { get; internal set; }
            /// <summary>True when the timeout passed before the condition held.</summary>
            public bool TimedOut { get; internal set; }
            /// <summary>True when the call was cancelled.</summary>
            public bool Cancelled { get; private set; }
            /// <summary>True while the call is still waiting.</summary>
            public bool Pending => !Ran && !TimedOut && !Cancelled;

            /// <summary>Drops the call if it hasn't run yet.</summary>
            public void Cancel()
            {
                if (!Pending) return;
                Cancelled = true;
                EditorApplication.update -= Tick;
            }

            internal void Tick()
            {
                if (!Pending) { EditorApplication.update -= Tick; return; }

                bool ready;
                try { ready = When == null || When(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    ready = false;
                }

                if (ready)
                {
                    EditorApplication.update -= Tick;
                    Ran = true;
                    try { Action(); }
                    catch (Exception e) { Debug.LogException(e); }
                    return;
                }

                if (EditorApplication.timeSinceStartup >= Deadline)
                {
                    EditorApplication.update -= Tick;
                    TimedOut = true;
                    Debug.LogWarning($"[CLI Extensions] Deferred call '{Label}' gave up: its condition did not hold within the timeout.");
                    try { OnTimeout?.Invoke(); }
                    catch (Exception e) { Debug.LogException(e); }
                    return;
                }

                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the main thread from the next <see cref="EditorApplication.update"/> where
        /// <paramref name="when"/> returns true (or the next update, without a condition). After
        /// <paramref name="timeoutSeconds"/> of waiting it logs a warning, calls <paramref name="onTimeout"/> and drops the
        /// call. Exceptions from the action or the condition are logged, never thrown into the update loop.
        /// </summary>
        /// <param name="label">Names the call in the timeout warning; defaults to the action's method.</param>
        public static Handle Run(Action action, Func<bool> when = null, double timeoutSeconds = 30, Action onTimeout = null, string label = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var handle = new Handle
            {
                Action = action,
                When = when,
                OnTimeout = onTimeout,
                Deadline = EditorApplication.timeSinceStartup + Math.Max(0, timeoutSeconds),
                Label = label ?? $"{action.Method.DeclaringType?.Name}.{action.Method.Name}"
            };
            EditorApplication.update += handle.Tick;
            // An unfocused Editor may not schedule an update soon on its own; ask for one.
            EditorApplication.QueuePlayerLoopUpdate();
            return handle;
        }

        /// <summary>Runs <paramref name="action"/> once the Editor is idle: not compiling, not importing, not changing Play mode.</summary>
        public static Handle RunWhenIdle(Action action, double timeoutSeconds = 300, Action onTimeout = null, string label = null)
        {
            return Run(action, IsIdle, timeoutSeconds, onTimeout, label);
        }

        /// <summary>True when the Editor isn't compiling, importing or about to change Play mode.</summary>
        public static bool IsIdle() =>
            !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
            EditorApplication.isPlaying == EditorApplication.isPlayingOrWillChangePlaymode;
    }
}
