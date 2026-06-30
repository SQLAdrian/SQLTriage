/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Services
{
    // BM:KeyboardShortcutService.Class — singleton broadcasting keyboard shortcut events to pages
    /// <summary>
    /// Singleton service that broadcasts keyboard shortcut events to any subscribed page.
    /// MainLayout fires the trigger; pages subscribe to the actions they support.
    /// </summary>
    public class KeyboardShortcutService
    {
        /// <summary>Ctrl+R — Run / Scan / Refresh on the active page.</summary>
        public event Func<System.Threading.Tasks.Task>? OnRunRequested;

        /// <summary>Ctrl+P — PDF export on the active page.</summary>
        public event Func<System.Threading.Tasks.Task>? OnExportPdfRequested;

        /// <summary>Ctrl+E — CSV export on the active page.</summary>
        public event Func<System.Threading.Tasks.Task>? OnExportCsvRequested;

        /// <summary>Ctrl+K — Command palette.</summary>
        public event Func<System.Threading.Tasks.Task>? OnCommandPaletteRequested;

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
    }
}
