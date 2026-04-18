/* In the name of God, the Merciful, the Compassionate */

using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Web.WebView2.Core;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Abstraction over PrintService — enables testing and server-mode fallback without WebView2 dependency.
    /// </summary>
    public interface IPrintService
    {
        bool IsAvailable { get; }

        void SetWebView(CoreWebView2 webView);

        Task<(bool Success, string? Path, string? Error)> PrintToPdfAsync(
            string fileName = "Report.pdf",
            bool printBackgrounds = false,
            CoreWebView2PrintOrientation orientation = CoreWebView2PrintOrientation.Landscape,
            string? headerTitle = null,
            string? footerUri = null,
            int timeoutSeconds = 30);

        Task<(bool Success, string? Path, string? Error)> PrintViaBrowserAsync(IJSRuntime jsRuntime);
    }
}
