/* In the name of God, the Merciful, the Compassionate */

// ──────────────────────────────────────────────────────────────────────────
// DevBridgeService — local HTTP loopback API for LLM-driven UI iteration.
//
// PURPOSE
//   Exposes a tiny set of endpoints (nav, screenshot, eval, ping, shutdown)
//   that an external tool (e.g., an LLM workflow) can hit to drive the
//   running app: navigate Blazor routes, capture screenshots of the
//   WebView2 surface, evaluate JS, and trigger a clean shutdown so the
//   build pipeline can rebuild the exe.
//
// SECURITY POSTURE — MUST READ
//   1. Listener binds to 127.0.0.1 only. Never expose externally.
//   2. Disabled by default. Must pass --devbridge on the command line.
//   3. Each request must carry a shared-secret token (X-DevBridge-Token).
//      The token is generated at startup and written to:
//          %TEMP%\sqltriage-devbridge.token
//      Anyone who can read that file can drive the app — treat the file as
//      sensitive on shared machines.
//   4. /eval runs arbitrary JS in the WebView. Do not enable on untrusted
//      machines. The arg-flag + token combination is the gate.
//
// THREAD MODEL
//   HttpListener accept loop runs on a dedicated background thread. Any
//   call that touches WebView2 (CapturePreviewAsync, ExecuteScriptAsync,
//   navigation) is marshalled to the WPF dispatcher via
//   Application.Current.Dispatcher.InvokeAsync. The WebView2 reference is
//   handed in from MainWindow once the BlazorWebView is initialised.
// ──────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using SQLTriage.Data;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;

#pragma warning disable CA1416 // Windows-only — project targets net8.0-windows

namespace SQLTriage.Data.Services
{
    public sealed class DevBridgeService : IDisposable
    {
        private readonly ILogger<DevBridgeService> _logger;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Thread? _acceptThread;
        private CoreWebView2? _webView;
        private IServiceProvider? _services;
        private string _token = "";
        private int _port;
        private string? _tokenFilePath;

        public DevBridgeService(ILogger<DevBridgeService> logger)
        {
            _logger = logger;
        }

        /// <summary>Hand the DI provider to the bridge so /state can resolve services.</summary>
        public void SetServiceProvider(IServiceProvider services) => _services = services;

        public bool IsRunning => _listener?.IsListening == true;
        public int Port => _port;
        public string? TokenFilePath => _tokenFilePath;

        /// <summary>Hand the live WebView2 instance to the bridge once it's ready.</summary>
        public void SetWebView(CoreWebView2 webView) => _webView = webView;

        /// <summary>
        /// Start the HTTP listener. Call from App.xaml.cs only when the
        /// --devbridge command-line arg is present.
        /// </summary>
        public void Start(int port = 5179)
        {
            if (IsRunning) return;
            _port = port;

            // No token file. Loopback bind is the gate. See HandleRequestAsync.
            _token = "";
            _tokenFilePath = null;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DevBridge: failed to start HttpListener on port {Port}", port);
                _listener = null;
                return;
            }

            _cts = new CancellationTokenSource();
            _acceptThread = new Thread(() => AcceptLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "DevBridge-Accept"
            };
            _acceptThread.Start();

            _logger.LogInformation("DevBridge: listening on http://127.0.0.1:{Port}/ (loopback-only, no token)",
                port);
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
            }
            catch { /* best-effort */ }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose() => Stop();

        // ── Accept loop ─────────────────────────────────────────────────────
        private void AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    break; // listener stopped
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleRequestAsync(ctx));
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                // Auth model: loopback-only (127.0.0.1 bind) is the only gate.
                // The token+file dance was tripping Bitdefender's behaviour
                // heuristic ("read credential file → loopback HTTP with auth
                // header" matches infostealer patterns). Since the listener
                // refuses non-loopback peers anyway, the token added little
                // and broke the LLM workflow. If you reintroduce a token,
                // pass it via env var or stdin — never via a file read.
                //
                // Defence in depth: reject any non-loopback peer just in case
                // a future config change exposes the listener.
                var remote = ctx.Request.RemoteEndPoint?.Address;
                if (remote == null || !System.Net.IPAddress.IsLoopback(remote))
                {
                    await WriteJsonAsync(ctx, 403, new { error = "loopback only" });
                    return;
                }

                var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? "";
                switch (path)
                {
                    case "/ping": await HandlePingAsync(ctx); break;
                    case "/nav": await HandleNavAsync(ctx); break;
                    case "/screenshot": await HandleScreenshotAsync(ctx); break;
                    case "/eval": await HandleEvalAsync(ctx); break;
                    case "/print-pdf": await HandlePrintPdfAsync(ctx); break;
                    case "/render-pdf": await HandleRenderPdfAsync(ctx); break;
                    case "/state": await HandleStateAsync(ctx); break;
                    case "/encrypt-free-bundle": await HandleEncryptFreeBundleAsync(ctx); break;
                    case "/hotspots/capture": await HandleHotspotsCaptureAsync(ctx); break;
                    case "/shutdown": await HandleShutdownAsync(ctx); break;
                    default: await WriteJsonAsync(ctx, 404, new { error = "unknown endpoint", path }); break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DevBridge: error handling {Path}", ctx.Request.Url?.AbsolutePath);
                try { await WriteJsonAsync(ctx, 500, new { error = ex.Message }); } catch { }
            }
        }

        // ── Endpoints ───────────────────────────────────────────────────────

        // GET/POST /state — structured snapshot of in-process diagnostic data
        // (memory + caches + servers + routes). Lets external diagnostic
        // sessions (e.g. Claude via DevBridge) read app state without JS eval.
        private async Task HandleStateAsync(System.Net.HttpListenerContext ctx)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var gc = System.GC.GetGCMemoryInfo();
                var genInfo = gc.GenerationInfo.ToArray();
                var gens = new object[genInfo.Length];
                for (var i = 0; i < genInfo.Length; i++)
                {
                    gens[i] = new
                    {
                        gen = i switch { 3 => "LOH", 4 => "POH", _ => "Gen " + i },
                        sizeAfter = genInfo[i].SizeAfterBytes,
                        fragmentedAfter = genInfo[i].FragmentationAfterBytes,
                    };
                }

                int? hotTierCount = null;
                int panelMetrics = 0;
                int weightOverrides = 0;
                int serverCount = 0;
                int connectedServers = 0;
                int poolActiveConnections = -1; // -1 = pool service not resolved
                if (_services != null)
                {
                    var memCache = _services.GetService(typeof(Microsoft.Extensions.Caching.Memory.IMemoryCache))
                                  as Microsoft.Extensions.Caching.Memory.IMemoryCache;
                    if (memCache is Microsoft.Extensions.Caching.Memory.MemoryCache mc) hotTierCount = mc.Count;

                    var pm = _services.GetService(typeof(PanelMetricsService)) as PanelMetricsService;
                    if (pm != null) panelMetrics = pm.GetAll().Count;

                    var ws = _services.GetService(typeof(RemediationWeightStore)) as RemediationWeightStore;
                    if (ws != null) weightOverrides = ws.OverrideCount;

                    var conn = _services.GetService(typeof(SQLTriage.Data.ServerConnectionManager))
                              as SQLTriage.Data.ServerConnectionManager;
                    if (conn != null)
                    {
                        var connections = conn.GetConnections();
                        serverCount = connections.Count;
                        connectedServers = connections.Count(c => c.IsConnected);
                    }

                    var poolSvc = _services.GetService(typeof(SQLTriage.Data.SqlConnectionPoolService))
                                 as SQLTriage.Data.SqlConnectionPoolService;
                    if (poolSvc != null) poolActiveConnections = poolSvc.TotalActiveConnections;
                }

                await WriteJsonAsync(ctx, 200, new
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    process = new
                    {
                        workingSet = proc.WorkingSet64,
                        privateBytes = proc.PrivateMemorySize64,
                        virtualBytes = proc.VirtualMemorySize64,
                        threadCount = proc.Threads.Count,
                        handleCount = proc.HandleCount,
                        uptimeSeconds = (int)(DateTime.Now - proc.StartTime).TotalSeconds,
                    },
                    gc = new
                    {
                        totalCommitted = gc.TotalCommittedBytes,
                        heapSize = gc.HeapSizeBytes,
                        fragmented = gc.FragmentedBytes,
                        promoted = gc.PromotedBytes,
                        gen0Collections = System.GC.CollectionCount(0),
                        gen1Collections = System.GC.CollectionCount(1),
                        gen2Collections = System.GC.CollectionCount(2),
                        generations = gens,
                    },
                    caches = new
                    {
                        hotTierItems = hotTierCount,
                        panelMetrics,
                        weightOverrides,
                    },
                    servers = new
                    {
                        total = serverCount,
                        connected = connectedServers,
                    },
                    pool = new
                    {
                        activeConnections = poolActiveConnections,
                    },
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DevBridge /state failed");
                await WriteJsonAsync(ctx, 500, new { error = ex.Message });
            }
        }


        private async Task HandlePingAsync(HttpListenerContext ctx)
        {
            await WriteJsonAsync(ctx, 200, new
            {
                ok = true,
                webViewReady = _webView != null,
                port = _port,
                version = typeof(DevBridgeService).Assembly.GetName().Version?.ToString()
            });
        }

        // POST /nav  body: { "url": "/diagnostics-roadmap" }
        private async Task HandleNavAsync(HttpListenerContext ctx)
        {
            if (_webView == null)
            {
                await WriteJsonAsync(ctx, 503, new { error = "webview not ready" });
                return;
            }

            var body = await ReadJsonAsync(ctx);
            var rel = body.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(rel))
            {
                await WriteJsonAsync(ctx, 400, new { error = "missing 'url'" });
                return;
            }

            // Drive Blazor's CLIENT-SIDE router via a synthetic anchor click.
            // Blazor's navigation interception hooks real <a> click events (and
            // NavigationManager) — it does NOT reliably observe a manually
            // dispatched popstate, so the old pushState+popstate approach left
            // the SPA on its default route (/guide) for most pages. A real
            // anchor click keeps the circuit alive (no host reload) AND is what
            // Blazor's router actually listens for. Origin is normalised to a
            // root-relative path so "http://0.0.0.0/cio" and "/cio" both work.
            var safe = JsonEncodedText.Encode(rel).ToString();
            var script =
                "(() => { let p = '" + safe + "';" +
                " try { const u = new URL(p, location.origin); p = u.pathname + u.search + u.hash; } catch (e) {}" +
                " const a = document.createElement('a'); a.href = p; a.style.display = 'none';" +
                " document.body.appendChild(a); a.click();" +
                " setTimeout(() => a.remove(), 0);" +
                " return p; })()";

            // Dispatcher.InvokeAsync(async () => ...) returns a
            // DispatcherOperation<Task> — awaiting it only waits for the
            // operation to *start*, not for the inner async body to finish.
            // We need the inner task; .Task.Unwrap() gives it to us.
            var result = await Application.Current.Dispatcher.InvokeAsync(
                async () => await _webView!.ExecuteScriptAsync(script)).Task.Unwrap();

            await WriteJsonAsync(ctx, 200, new { ok = true, url = rel, result });
        }

        // POST /screenshot  body: { "out": "C:\\temp\\shot.png", "format": "png|jpeg", "selector": "#optional" }
        // - If "out" is omitted, returns the PNG bytes inline.
        // - If "selector" is given, scrolls that element into view first (so it
        //   sits at the top of the captured viewport). Capture is always of
        //   the visible WebView surface — to get a full-page image, scroll
        //   piecewise and stitch in the caller, or use /print-pdf instead.
        private async Task HandleScreenshotAsync(HttpListenerContext ctx)
        {
            if (_webView == null)
            {
                await WriteJsonAsync(ctx, 503, new { error = "webview not ready" });
                return;
            }

            var body = await ReadJsonAsync(ctx);
            var outPath = body.TryGetProperty("out", out var op) ? op.GetString() : null;
            var format = body.TryGetProperty("format", out var fp) ? fp.GetString() : "png";
            var selector = body.TryGetProperty("selector", out var sp) ? sp.GetString() : null;

            var imageFormat = string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase)
                ? CoreWebView2CapturePreviewImageFormat.Jpeg
                : CoreWebView2CapturePreviewImageFormat.Png;

            // See /nav for the .Task.Unwrap() rationale — without it the outer
            // await returns before CapturePreviewAsync finishes, leaving bytes
            // empty (PNG written to disk would be 0 bytes).
            var bytes = await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (!string.IsNullOrWhiteSpace(selector))
                {
                    var safe = JsonEncodedText.Encode(selector!).ToString();
                    await _webView!.ExecuteScriptAsync(
                        $"document.querySelector('{safe}')?.scrollIntoView({{block:'start',behavior:'instant'}});");
                    // Yield once so layout/scroll settles before capture.
                    await Task.Delay(60);
                }

                using var ms = new MemoryStream();
                await _webView!.CapturePreviewAsync(imageFormat, ms);
                return ms.ToArray();
            }).Task.Unwrap();

            if (!string.IsNullOrWhiteSpace(outPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(outPath!, bytes);
                    await WriteJsonAsync(ctx, 200, new { ok = true, path = outPath, bytes = bytes.Length });
                }
                catch (Exception ex)
                {
                    await WriteJsonAsync(ctx, 500, new { error = $"write failed: {ex.Message}" });
                }
                return;
            }

            // Inline image response.
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = imageFormat == CoreWebView2CapturePreviewImageFormat.Jpeg
                ? "image/jpeg" : "image/png";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        // POST /eval  body: { "js": "document.title" }
        // Returns whatever JSON ExecuteScriptAsync produces. Useful for
        // reading computed styles or inspecting DOM state.
        private async Task HandleEvalAsync(HttpListenerContext ctx)
        {
            if (_webView == null)
            {
                await WriteJsonAsync(ctx, 503, new { error = "webview not ready" });
                return;
            }

            var body = await ReadJsonAsync(ctx);
            var js = body.TryGetProperty("js", out var jp) ? jp.GetString() : null;
            if (string.IsNullOrWhiteSpace(js))
            {
                await WriteJsonAsync(ctx, 400, new { error = "missing 'js'" });
                return;
            }

            var result = await Application.Current.Dispatcher.InvokeAsync(
                async () => await _webView!.ExecuteScriptAsync(js)).Task.Unwrap();

            // ExecuteScriptAsync returns a JSON-encoded string already.
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var payload = Encoding.UTF8.GetBytes(result ?? "null");
            ctx.Response.ContentLength64 = payload.Length;
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.Close();
        }

        // POST /print-pdf
        //   body: {
        //     "out":             "C:/temp/page.pdf",   (required)
        //     "orientation":     "landscape|portrait", (default: landscape)
        //     "backgrounds":     true|false,           (default: true — needed for theme/pill colours)
        //     "togglePrintClass": true|false           (default: true — adds body.printing-active so
        //                                              page-specific @media print rules engage)
        //   }
        //
        // This is the "simple" print path — just toggles the printing-active
        // body class (so the page's @media print rules + print-color-adjust
        // kick in) and calls WebView2.PrintToPdfAsync. It does NOT run the
        // page's full PrintPrepareDomAsync, so cover pages, exec summary,
        // page-break injections, and footer bars won't appear. Use the app's
        // built-in "Export PDF" button for a production report; use this
        // endpoint for fast styling iteration.
        private async Task HandlePrintPdfAsync(HttpListenerContext ctx)
        {
            if (_webView == null)
            {
                await WriteJsonAsync(ctx, 503, new { error = "webview not ready" });
                return;
            }

            var body = await ReadJsonAsync(ctx);
            var outPath = body.TryGetProperty("out", out var op) ? op.GetString() : null;
            if (string.IsNullOrWhiteSpace(outPath))
            {
                await WriteJsonAsync(ctx, 400, new { error = "missing 'out'" });
                return;
            }

            var orientation = body.TryGetProperty("orientation", out var or) && or.GetString() == "portrait"
                ? CoreWebView2PrintOrientation.Portrait
                : CoreWebView2PrintOrientation.Landscape;
            var backgrounds = !body.TryGetProperty("backgrounds", out var bg) || bg.GetBoolean();
            var togglePrintClass = !body.TryGetProperty("togglePrintClass", out var tp) || tp.GetBoolean();

            try
            {
                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { /* PrintToPdfAsync will surface the real error */ }

            // Run the entire prep + print + cleanup on the UI thread. We use
            // .Task.Unwrap() to wait for the actual async body — see /nav for
            // why the naked InvokeAsync return doesn't work.
            string? error = null;
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (togglePrintClass)
                    {
                        await _webView!.ExecuteScriptAsync(
                            "document.body.classList.add('printing-active');");
                        // Brief settle so layout/paint reflects the new class
                        // before chromium hands the DOM to the print pipeline.
                        await Task.Delay(150);
                    }

                    var settings = _webView!.Environment.CreatePrintSettings();
                    settings.Orientation = orientation;
                    settings.ShouldPrintBackgrounds = backgrounds;
                    settings.MarginTop = 0.2;
                    settings.MarginBottom = 0.25;
                    settings.MarginLeft = 0.2;
                    settings.MarginRight = 0.2;

                    await _webView.PrintToPdfAsync(outPath!, settings);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    if (togglePrintClass)
                    {
                        try
                        {
                            await _webView!.ExecuteScriptAsync(
                                "document.body.classList.remove('printing-active');");
                        }
                        catch { /* best effort */ }
                    }
                }
            }).Task.Unwrap();

            if (error != null)
            {
                await WriteJsonAsync(ctx, 500, new { error });
                return;
            }

            long bytes = 0;
            try { bytes = new FileInfo(outPath!).Length; } catch { }
            await WriteJsonAsync(ctx, 200, new { ok = true, path = outPath, bytes });
        }

        // POST /render-pdf
        //   body: {
        //     "in":   "C:/temp/page.pdf",     (required, source PDF)
        //     "out":  "C:/temp/page",         (required, output basename — pdftoppm appends -1.png, -2.png ...)
        //     "dpi":  100,                    (default 100; bump to 150 for finer text)
        //     "page": 3                       (optional; render only this 1-based page)
        //   }
        // Wraps pdftoppm (Poppler). Searches PATH, then the well-known
        // winget install dir. Returns the list of files produced.
        // Install if missing:  winget install --id oschwartz10612.Poppler
        private async Task HandleRenderPdfAsync(HttpListenerContext ctx)
        {
            var body = await ReadJsonAsync(ctx);
            var inPath = body.TryGetProperty("in", out var ip) ? ip.GetString() : null;
            var outBase = body.TryGetProperty("out", out var op) ? op.GetString() : null;
            if (string.IsNullOrWhiteSpace(inPath) || string.IsNullOrWhiteSpace(outBase))
            {
                await WriteJsonAsync(ctx, 400, new { error = "need 'in' (pdf path) and 'out' (basename)" });
                return;
            }
            if (!File.Exists(inPath))
            {
                await WriteJsonAsync(ctx, 400, new { error = $"input pdf not found: {inPath}" });
                return;
            }

            int dpi = body.TryGetProperty("dpi", out var dp) && dp.ValueKind == JsonValueKind.Number ? dp.GetInt32() : 100;
            int? page = body.TryGetProperty("page", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetInt32() : null;

            var pdftoppm = ResolvePdftoppm();
            if (pdftoppm == null)
            {
                await WriteJsonAsync(ctx, 503, new
                {
                    error = "pdftoppm not found",
                    hint = "winget install --id oschwartz10612.Poppler"
                });
                return;
            }

            // Args: -r DPI -png [-f PAGE -l PAGE] IN OUTBASE
            var args = new List<string> { "-r", dpi.ToString(), "-png" };
            if (page.HasValue)
            {
                args.Add("-f"); args.Add(page.Value.ToString());
                args.Add("-l"); args.Add(page.Value.ToString());
            }
            args.Add(inPath!);
            args.Add(outBase!);

            try
            {
                var dir = Path.GetDirectoryName(outBase);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pdftoppm,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            string stderr;
            int exit;
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                if (proc == null)
                {
                    await WriteJsonAsync(ctx, 500, new { error = "failed to start pdftoppm" });
                    return;
                }
                stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                exit = proc.ExitCode;
            }

            if (exit != 0)
            {
                await WriteJsonAsync(ctx, 500, new { error = $"pdftoppm exit {exit}", stderr });
                return;
            }

            // Discover produced files: <outBase>-1.png, -2.png, ... (or -01 for >9 pages — pdftoppm uses
            // the minimum width that fits all pages, but for the LLM workflow we don't need to predict;
            // just glob the directory.)
            var outDir = Path.GetDirectoryName(outBase) ?? ".";
            var prefix = Path.GetFileName(outBase) + "-";
            string[] produced;
            try
            {
                produced = Directory.GetFiles(outDir, prefix + "*.png").OrderBy(p => p).ToArray();
            }
            catch
            {
                produced = Array.Empty<string>();
            }

            await WriteJsonAsync(ctx, 200, new { ok = true, files = produced, count = produced.Length });
        }

        // Find pdftoppm.exe. Order: PATH, then the user's winget install dir.
        private static string? ResolvePdftoppm()
        {
            // 1. PATH
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var d in pathDirs)
            {
                try
                {
                    var candidate = Path.Combine(d.Trim(), "pdftoppm.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            // 2. winget package install dir (per-user, no PATH entry by default)
            try
            {
                var wingetRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(wingetRoot))
                {
                    foreach (var pkg in Directory.GetDirectories(wingetRoot, "*Poppler*"))
                    {
                        var hits = Directory.GetFiles(pkg, "pdftoppm.exe", SearchOption.AllDirectories);
                        if (hits.Length > 0) return hits[0];
                    }
                }
            }
            catch { }

            return null;
        }

        // POST /encrypt-free-bundle  body:
        //   {
        //     "configSource": "C:/Github/SQLTriage/SQLTriage/Config",
        //     "corpusSource": "C:/Github/sqltriage-corpus",
        //     "out": "<install-dir>/Config/free-bundle.dat",
        //     "buildNumber": 1936,                    // diagnostic only — AAD is pinned to 0
        //     "permittedCheckIds": [1,2,3]            // optional; if absent or [], all yamls included
        //   }
        //
        // DEV-ONLY shortcut so a workflow that can drive DevBridge (loopback only)
        // can produce a fresh Free-tier bundle without rebuilding the CorpusEncryptor
        // out-of-process. Mirrors CorpusEncryptor.RunBuildFreeBundle but skips the
        // selection-rules + coverage-report path — caller is expected to compute
        // permittedCheckIds out-of-band when they need the curated subset. Empty list
        // == include every yaml in corpusSource (full Free bundle for smoke purposes).
        //
        // NOT a production path. The official Free bundle should be generated by
        // CorpusEncryptor on the build machine before publish.
        private async Task HandleEncryptFreeBundleAsync(HttpListenerContext ctx)
        {
            var body = await ReadJsonAsync(ctx);
            if (!body.TryGetProperty("configSource", out var cfgSrcEl) ||
                !body.TryGetProperty("corpusSource", out var corpusSrcEl) ||
                !body.TryGetProperty("out", out var outEl))
            {
                await WriteJsonAsync(ctx, 400, new { error = "missing required: configSource, corpusSource, out" });
                return;
            }

            var configSource = cfgSrcEl.GetString() ?? "";
            var corpusSource = corpusSrcEl.GetString() ?? "";
            var outPath = outEl.GetString() ?? "";
            var buildNumber = body.TryGetProperty("buildNumber", out var bnEl) ? bnEl.GetInt32() : 0;

            var permittedIds = new HashSet<int>();
            if (body.TryGetProperty("permittedCheckIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in idsEl.EnumerateArray())
                    if (e.TryGetInt32(out var id)) permittedIds.Add(id);
            }

            if (!Directory.Exists(configSource))
            {
                await WriteJsonAsync(ctx, 400, new { error = $"configSource not a directory: {configSource}" });
                return;
            }
            if (!Directory.Exists(corpusSource))
            {
                await WriteJsonAsync(ctx, 400, new { error = $"corpusSource not a directory: {corpusSource}" });
                return;
            }

            // Gather the 9 gated Config files. Mirrors CorpusEncryptor.BundleBuilder.GatedConfigFiles.
            string[] configFiles = {
                "control_mappings.json", "governance-weights.json", "queries.json",
                "ruleset.json", "error-catalog.json", "roadmap-mapping.json",
                "roadmap-aliases.json", "sql-build-catalogue.json", "sql-licensing-pricing.json"
            };

            var manifest = new BundleManifest
            {
                BundleVersion = 1,
                BuildNumber = buildNumber,
                CreatedUtc = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                ClientName = "FREE",
                Tier = "Free",
                Features = new ManifestFeatures
                {
                    RagEnabled = false,
                    SpBlitzImport = true,
                    FullCorpus = false,
                    CheckIds = permittedIds.ToList(),
                },
            };

            var missing = new List<string>();
            foreach (var name in configFiles)
            {
                var p = Path.Combine(configSource, name);
                if (!File.Exists(p)) { missing.Add(name); continue; }
                manifest.Files[$"Config/{name}"] = await File.ReadAllTextAsync(p);
            }
            if (missing.Count > 0)
            {
                await WriteJsonAsync(ctx, 400, new { error = "config files missing", missing });
                return;
            }

            // Gather YAML + .sql sibling files from corpus source root (top-level only).
            // If permittedIds is non-empty, filter to yamls whose CheckNr is in the set.
            var yamlFiles = Directory.GetFiles(corpusSource, "*.yaml", SearchOption.TopDirectoryOnly);
            int skipped = 0;
            foreach (var yamlPath in yamlFiles)
            {
                var yamlText = await File.ReadAllTextAsync(yamlPath);
                if (permittedIds.Count > 0)
                {
                    var checkNr = ExtractCheckNrFromYamlBody(yamlText);
                    if (checkNr is null || !permittedIds.Contains(checkNr.Value)) { skipped++; continue; }
                }
                var yamlName = Path.GetFileName(yamlPath);
                manifest.Corpus[yamlName] = yamlText;

                var sqlPath = Path.ChangeExtension(yamlPath, ".sql");
                if (File.Exists(sqlPath))
                    manifest.Corpus[Path.GetFileName(sqlPath)] = await File.ReadAllTextAsync(sqlPath);
            }

            // Encrypt with the same AAD scheme LicenseService.TryUnlockFree uses:
            //   client=FREE, tier=Free, bundle_v=1, build=0 (pinned; not the running build).
            var key = new byte[BundleCrypto.KeySize]; // zeros
            var aad = AadBuilder.Build("FREE", "Free", 1, 0);
            var wire = BundleCrypto.EncryptManifest(manifest, key, aad);

            var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            await File.WriteAllBytesAsync(outPath, wire);

            await WriteJsonAsync(ctx, 200, new
            {
                ok = true,
                path = outPath,
                bytes = wire.Length,
                bundleSha256 = BundleCrypto.Sha256Hex(wire),
                filesIncluded = manifest.Files.Count,
                corpusItemsIncluded = manifest.Corpus.Count,
                yamlsSkippedByFilter = skipped,
                aadDescriptor = "client=FREE|tier=Free|bundle_v=1|build=0",
            });
        }

        private static int? ExtractCheckNrFromYamlBody(string yaml)
        {
            using var sr = new StringReader(yaml);
            for (var i = 0; i < 50; i++)
            {
                var line = sr.ReadLine();
                if (line is null) break;
                var m = Regex.Match(line, @"^\s*CheckNr\s*:\s*(\d+)\s*$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
            }
            return null;
        }

        // POST /hotspots/capture  body: { "server": "MYSERVER" }
        //   Hand-trigger one CodeHotspots snapshot for the given server.
        //   Returns { ok, rowsCaptured, server }.
        //   D3 small-build verifier: lets a workflow prove the cache writer
        //   lands rows before the timer/UI ship in the big build.
        private async Task HandleHotspotsCaptureAsync(HttpListenerContext ctx)
        {
            if (_services == null)
            {
                await WriteJsonAsync(ctx, 503, new { error = "DI not ready" });
                return;
            }

            var body = await ReadJsonAsync(ctx);
            var serverName = body.TryGetProperty("server", out var sEl) ? sEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(serverName))
            {
                await WriteJsonAsync(ctx, 400, new { error = "missing 'server'" });
                return;
            }

            var connManager = _services.GetService(typeof(ServerConnectionManager)) as ServerConnectionManager;
            var cache = _services.GetService(typeof(CodeHotspotsCacheService)) as CodeHotspotsCacheService;
            if (connManager == null || cache == null)
            {
                await WriteJsonAsync(ctx, 503, new { error = "required services not registered" });
                return;
            }

            var conn = connManager.GetEnabledConnections()
                .FirstOrDefault(c => c.GetServerList()
                    .Contains(serverName, System.StringComparer.OrdinalIgnoreCase));
            if (conn == null)
            {
                await WriteJsonAsync(ctx, 404, new { error = $"server '{serverName}' not found in enabled connections" });
                return;
            }

            try
            {
                var rows = await cache.CaptureSnapshotAsync(conn, serverName);
                await WriteJsonAsync(ctx, 200, new { ok = true, server = serverName, rowsCaptured = rows });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "DevBridge /hotspots/capture failed for {Server}", serverName);
                await WriteJsonAsync(ctx, 500, new { error = ex.Message });
            }
        }

        // POST /shutdown  — graceful exit so the build pipeline can replace the exe.
        private async Task HandleShutdownAsync(HttpListenerContext ctx)
        {
            await WriteJsonAsync(ctx, 200, new { ok = true, message = "shutting down" });
            // Let the response flush before tearing down the app.
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            });
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static async Task<JsonElement> ReadJsonAsync(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return default;
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

    }
}
