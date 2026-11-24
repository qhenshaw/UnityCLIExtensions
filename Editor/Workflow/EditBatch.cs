using System;
using UnityEditor;
using UnityEngine;

namespace UnityCLIExtensions
{
    /// <summary>
    /// Holds off importing and compiling while an agent writes several files, so they compile once instead of once per
    /// file. <see cref="Begin"/> turns off auto-refresh and locks assembly reloads; <see cref="End"/> undoes both, and
    /// the caller then refreshes (cliext.edits end compiles and returns the errors).
    /// </summary>
    /// <remarks>
    /// A batch that is never ended ends itself when its timeout passes. It is kept in <see cref="SessionState"/>, so
    /// it survives a domain reload (the lock is put back) and ends with the Editor.
    /// </remarks>
    [InitializeOnLoad]
    public static class EditBatch
    {
        private const string DeadlineKey = "CLIExtensions.EditBatch.DeadlineUtcTicks";
        private const string StartedKey = "CLIExtensions.EditBatch.StartedUtcTicks";

        private static bool s_Locked;
        private static EditorDeferred.Handle s_Expiry;

        static EditBatch()
        {
            if (IsActive)
            {
                // A domain reload dropped our lock and our timer; put both back.
                Lock();
                ScheduleExpiry();
            }
        }

        /// <summary>True while a batch is open.</summary>
        public static bool IsActive => DeadlineUtc.HasValue;

        /// <summary>When an open batch ends by itself, or null.</summary>
        public static DateTime? DeadlineUtc
        {
            get
            {
                string ticks = SessionState.GetString(DeadlineKey, "");
                return long.TryParse(ticks, out long t) ? new DateTime(t, DateTimeKind.Utc) : (DateTime?)null;
            }
        }

        /// <summary>When the open batch began, or null.</summary>
        public static DateTime? StartedUtc
        {
            get
            {
                string ticks = SessionState.GetString(StartedKey, "");
                return long.TryParse(ticks, out long t) ? new DateTime(t, DateTimeKind.Utc) : (DateTime?)null;
            }
        }

        /// <summary>Opens a batch (or extends the open one) that ends by itself after <paramref name="timeoutSeconds"/>.</summary>
        public static void Begin(double timeoutSeconds)
        {
            DateTime now = DateTime.UtcNow;
            if (!IsActive) SessionState.SetString(StartedKey, now.Ticks.ToString());
            SessionState.SetString(DeadlineKey, now.AddSeconds(Math.Max(1, timeoutSeconds)).Ticks.ToString());
            Lock();
            ScheduleExpiry();
        }

        /// <summary>Closes the batch: auto-refresh and assembly reloads are allowed again. Returns false when none was open.</summary>
        public static bool End()
        {
            bool was = IsActive;
            SessionState.EraseString(DeadlineKey);
            SessionState.EraseString(StartedKey);
            s_Expiry?.Cancel();
            s_Expiry = null;
            Unlock();
            return was;
        }

        private static void Lock()
        {
            if (s_Locked) return;
            AssetDatabase.DisallowAutoRefresh();
            EditorApplication.LockReloadAssemblies();
            s_Locked = true;
        }

        private static void Unlock()
        {
            if (!s_Locked) return;
            AssetDatabase.AllowAutoRefresh();
            EditorApplication.UnlockReloadAssemblies();
            s_Locked = false;
        }

        private static void ScheduleExpiry()
        {
            s_Expiry?.Cancel();
            DateTime? deadline = DeadlineUtc;
            if (!deadline.HasValue) return;
            double wait = Math.Max(0, (deadline.Value - DateTime.UtcNow).TotalSeconds);
            // The condition never holds: the "timeout" is the point. EditorDeferred keeps ticking it from update.
            s_Expiry = EditorDeferred.Run(() => { }, () => false, wait + 0.5, Expire, "edit batch timeout", warnOnTimeout: false);
        }

        private static void Expire()
        {
            if (!IsActive) return;
            if (DeadlineUtc > DateTime.UtcNow) { ScheduleExpiry(); return; } // extended meanwhile
            End();
            Debug.LogWarning("[CLI Extensions] An edit batch was never ended (cliext.edits end), so it ended itself at its timeout. Refreshing now.");
            DialogJournal.Write(new DialogJournalEntry { kind = "escalated", title = "Edit batch timed out", message = "cliext.edits end was never called; the batch ended itself and the AssetDatabase was refreshed." });
            AssetDatabase.Refresh();
        }
    }
}
