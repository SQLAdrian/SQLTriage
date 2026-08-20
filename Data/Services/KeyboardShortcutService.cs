/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Services
{
    // BM:KeyboardShortcutService.Class — PER-CIRCUIT keyboard-shortcut bus
    /// <summary>
    /// Relays a keyboard shortcut from the layout to whichever page in the SAME circuit
    /// subscribed to it.
    ///
    /// <para>SCOPED, NOT SINGLETON — and that is a security property, not a preference.
    /// While this was <c>AddSingleton</c> the events below were a process-wide multicast
    /// holding delegates from every live circuit, so <see cref="TriggerRun"/> raised from
    /// ONE circuit invoked the Run handler belonging to EVERY other circuit. Each of those
    /// handlers then evaluated its own <c>AppUserState.IsAuthorized("execute_checks")</c> —
    /// the VICTIM's identity — and passed. Proven live on 2026-08-02: an unauthenticated LAN
    /// caller sitting on the Access Denied page pressed Ctrl+R and a privileged circuit ran
    /// its checks. Classic confused deputy: every gate was correct and every gate was asked
    /// the wrong question.
    ///
    /// <para>The fix is the lifetime, not another permission check. A per-shortcut permission
    /// on the layout's key handler would leave the bus cross-circuit for the next caller;
    /// scoping it means a circuit's keystroke can only ever reach its own subscribers, and the
    /// per-handler gates the pages already carry then evaluate the identity that pressed the
    /// key.</para>
    ///
    /// <para>Do not register this as a singleton, and do not forward a resolved instance of it
    /// from one container into another (<c>ServerModeService.RegisterSharedSingletons</c> used
    /// to do exactly that). <c>DiLifetimeCensusTests</c> fails on both.</para>
    /// </summary>
    public class KeyboardShortcutService
    {
        /// <summary>Ctrl+R — Run / Scan / Refresh on the active page.</summary>
        public event Func<System.Threading.Tasks.Task>? OnRunRequested;

        /// <summary>Ctrl+P — PDF export on the active page.</summary>
        public event Func<System.Threading.Tasks.Task>? OnExportPdfRequested;

        /// <summary>Ctrl+E — CSV export on the active page.</summary>
        public event Func<System.Threading.Tasks.Task>? OnExportCsvRequested;

        /// <summary>
        /// Ctrl+K — open the command palette rendered by THIS circuit's layout.
        /// Replaces <c>CommandPalette.RequestOpen</c>, which was a <c>public static Action</c>
        /// and therefore process-wide by construction: the last circuit to initialise owned the
        /// slot, so one browser's Ctrl+K opened (and re-rendered) another browser's palette.
        /// </summary>
        public event Action? OnCommandPaletteRequested;

        public async System.Threading.Tasks.Task TriggerRun()
        {
            if (OnRunRequested != null)
                await OnRunRequested.Invoke();
        }

        public async System.Threading.Tasks.Task TriggerExportPdf()
        {
            if (OnExportPdfRequested != null)
                await OnExportPdfRequested.Invoke();
        }

        public async System.Threading.Tasks.Task TriggerExportCsv()
        {
            if (OnExportCsvRequested != null)
                await OnExportCsvRequested.Invoke();
        }

        public void TriggerCommandPalette() => OnCommandPaletteRequested?.Invoke();
    }
}
