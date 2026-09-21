/* In the name of God, the Merciful, the Compassionate */

using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Web.WebView2.Core;

namespace SQLTriage.Data.Services
{
    // BM:IPrintService.Class — abstraction over PrintService for testing and server mode
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

        /// <summary>
        /// Writes <paramref name="html"/> to a temporary file, navigates WebView2 to it,
        /// captures it as a PDF at <paramref name="outputPath"/>, then navigates back.
        /// Returns (false, null, error) when WebView2 is unavailable.
        /// </summary>
        Task<(bool Success, string? Path, string? Error)> PrintHtmlToPdfAsync(
            string html,
            string outputPath,
            bool printBackgrounds = false,
            CoreWebView2PrintOrientation orientation = CoreWebView2PrintOrientation.Portrait,
            string? headerTitle = null,
            int timeoutSeconds = 60);
    }
}
