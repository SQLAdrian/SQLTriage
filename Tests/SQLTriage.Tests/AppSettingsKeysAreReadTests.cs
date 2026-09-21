/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// dead-config-cleanup (Adrian, 2026-09-08 ~06:38 ruling on the eight keys + 08:47 NZST
    /// service-box approval). Structural closure so a NEW dead <c>Config/appsettings.json</c> leaf
    /// cannot be added silently the way <c>ConnectionStrings:SqlwatchDB</c>,
    /// <c>AppSettings:AutoRefreshIntervalSeconds</c>, <c>AppSettings:EnableAutoRefresh</c>,
    /// <c>AppSettings:DefaultTimeRangeHours</c>, <c>ConnectionEncryptionEnabled</c>,
    /// <c>ConnectionTrustServerCertificate</c>, <c>EnableAuditLogging</c> and
    /// <c>SessionTimeoutMinutes</c> were — all eight had zero readers in product source and this
    /// lane deleted them.
    ///
    /// <para><b>A leaf counts as READ</b> if any product source file (<c>*.cs</c>, <c>*.razor</c>,
    /// <c>*.cshtml</c> under the repo root, excluding <c>Tests/</c>, <c>bin/</c>, <c>obj/</c>,
    /// <c>.git/</c>, <c>node_modules/</c>, <c>wwwroot/_framework/</c> and <c>Config/</c> itself)
    /// contains (a) the full colon-joined path as a string literal, or (b) the leaf name alone as a
    /// string literal, or (c) a <c>public</c> property declaration of the leaf name (options
    /// binding by property name).</para>
    ///
    /// <para><b>Framework allow-list — the ONLY one, two entries, each independently verified at
    /// the tip this test was written against (c82a45f):</b></para>
    /// <list type="bullet">
    ///   <item><description><c>Logging:</c> — <c>Microsoft.Extensions.Logging.Configuration</c>
    ///   binds the <c>Logging</c> section into <c>ILoggingBuilder</c> by convention when the
    ///   Generic Host builds the app. <c>WebApplication.CreateBuilder()</c> is used at
    ///   <c>Data/Services/ServerModeService.cs:131</c> and
    ///   <c>Data/Services/WindowsServiceHost.cs:172</c> (the <c>--server</c> and <c>--service</c>
    ///   hosts). Gate-corrected 2026-09-08: <c>Logging:LogLevel:Default</c> IS also read by a
    ///   product literal (<c>Data/ConfigurationValidator.cs:66</c>), so this prefix is load-bearing
    ///   only for <c>Logging:LogLevel:Microsoft.AspNetCore</c>, which nothing but the framework's
    ///   convention binding consumes - proved by mutation: dropping the prefix flags exactly that
    ///   one leaf.</description></item>
    ///   <item><description><c>AllowedHosts</c> — ASP.NET Core's default
    ///   <c>HostFilteringStartupFilter</c> reads this key by convention from the same default host
    ///   (same two call sites). <c>Data/Services/HostOriginValidation.cs:86-88</c> documents that
    ///   the value is deliberately left at <c>"*"</c> for exactly this reason (Kestrel's own filter
    ///   is a bare 400; <see cref="HostOriginValidation"/> is the loud control that replaces it), and
    ///   <c>Tests/SQLTriage.Tests/HostOriginValidationTests.cs</c>'s
    ///   <c>TheShippedAllowedHostsSettingIsUnchanged</c> already pins the shipped value.</description></item>
    /// </list>
    ///
    /// <para><b>Serilog: is deliberately NOT allow-listed</b> (a correction to this lane's brief,
    /// which proposed it). There is no <c>ReadFrom.Configuration</c> call anywhere in product
    /// source — grepped, zero hits. The two <c>Serilog:FileSink:*</c> leaves this file currently
    /// carries are read by explicit product code with literals —
    /// <c>App.xaml.cs:140-142</c> (<c>configuration.GetSection("Serilog:FileSink")</c> then
    /// <c>.GetValue&lt;int&gt;("FileSizeLimitMb", 50)</c> / <c>"RetainedFileCount"</c>) and
    /// <c>Data/Services/WindowsServiceHost.cs:594</c> — so the ordinary leaf-name-literal channel
    /// already covers them; a blanket prefix allow-list here would document a convention that does
    /// not exist and would swallow any future <c>Serilog:*</c> key added with no literal reference,
    /// which is the exact false-clean shape this guard exists to prevent.</para>
    ///
    /// <para><b>KNOWN RESIDUAL AT COMMIT TIME — reported, not silenced.</b>
    /// <c>AppSettings:DashboardTitle</c> also has zero readers: no class or record named
    /// <c>AppSettings</c>, no <c>GetSection("AppSettings")</c>, no literal
    /// <c>"AppSettings"</c>/<c>"DashboardTitle"</c> anywhere in product source outside
    /// <c>Config/appsettings.json</c> itself (the only near-miss, <c>Pages/Settings.razor.cs</c>'s
    /// <c>RestoreDashboardTitle</c>, is a different identifier — no word boundary, and not a
    /// property named exactly <c>DashboardTitle</c>). This lane's brief named eight keys and was
    /// approved for exactly those eight; it did not name this one, so it is reported here and left
    /// in place rather than deleted without that approval — see the builder's report. Per this
    /// guard's own rule (no allow-list for a dead key), <see cref="Every_AppSettingsJson_Leaf_Is_Read_Or_Allowlisted"/>
    /// is RED on this one path at commit time. That is the guard doing its job, not a defect in
    /// it — widening the allow-list or silently dropping the assertion to force green would be
    /// exactly the false-clean shape this class exists to prevent.</para>
    /// </summary>
    public class AppSettingsKeysAreReadTests
    {
        /// <summary>Prefix, justification (kept short; full citation is in the class doc above).</summary>
        internal static readonly string[] FrameworkAllowListPrefixes = { "Logging:", "AllowedHosts" };

        private static Regex PropertyDecl(string leafName) =>
            new(@"public\s+[^;{=\n]*\b" + Regex.Escape(leafName) + @"\s*[{=;]", RegexOptions.Compiled);

        internal static Dictionary<string, JsonElement> Flatten(JsonElement element, string prefix = "")
        {
            var result = new Dictionary<string, JsonElement>();
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? prop.Name : prefix + ":" + prop.Name;
                    foreach (var kv in Flatten(prop.Value, path))
                        result[kv.Key] = kv.Value;
                }
            }
            else
            {
                result[prefix] = element;
            }
            return result;
        }

        internal static List<(string RelativePath, string Text)> LoadProductSources(DirectoryInfo repoRoot)
        {
            var excludeDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Tests", "bin", "obj", ".git", "node_modules" };
            var excludeSubstrings = new[]
            {
                Path.Combine("wwwroot", "_framework"),
                Path.DirectorySeparatorChar + "Config" + Path.DirectorySeparatorChar,
            };
            var includeExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".razor", ".cshtml" };

            var sources = new List<(string, string)>();
            foreach (var file in repoRoot.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(repoRoot.FullName, file.FullName);
                var segments = rel.Split(Path.DirectorySeparatorChar);
                if (segments.Length > 0 && excludeDirNames.Contains(segments[0])) continue;
                if (!includeExt.Contains(file.Extension)) continue;
                if (excludeSubstrings.Any(sub => (Path.DirectorySeparatorChar + rel).Contains(sub))) continue;

                sources.Add((rel, File.ReadAllText(file.FullName)));
            }
            return sources;
        }

        internal static bool IsRead(string leafPath, string leafName, List<(string RelativePath, string Text)> sources)
        {
            var fullLit = "\"" + leafPath + "\"";
            var nameLit = "\"" + leafName + "\"";
            var propRe = PropertyDecl(leafName);
            foreach (var (_, text) in sources)
            {
                if (text.Contains(fullLit, StringComparison.Ordinal)) return true;
                if (text.Contains(nameLit, StringComparison.Ordinal)) return true;
                if (propRe.IsMatch(text)) return true;
            }
            return false;
        }

        /// <summary>Every leaf in <paramref name="configPath"/> not covered by
        /// <see cref="FrameworkAllowListPrefixes"/>, that has zero readers under <paramref name="repoRoot"/>.</summary>
        internal static List<string> FindUnreadLeaves(string configPath, DirectoryInfo repoRoot)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var leaves = Flatten(doc.RootElement);
            var sources = LoadProductSources(repoRoot);

            var unread = new List<string>();
            foreach (var path in leaves.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (FrameworkAllowListPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
                    continue;
                var leafName = path.Split(':').Last();
                if (!IsRead(path, leafName, sources))
                    unread.Add(path);
            }
            return unread;
        }

        [Fact]
        public void Every_AppSettingsJson_Leaf_Is_Read_Or_Allowlisted()
        {
            var repoRoot = RawPassedScan.RepoRoot();
            var configPath = Path.Combine(repoRoot.FullName, "Config", "appsettings.json");
            var unread = FindUnreadLeaves(configPath, repoRoot);

            Assert.True(unread.Count == 0,
                "Config/appsettings.json leaf path(s) with no full-path literal, leaf-name literal, " +
                "or property declaration anywhere in product source, and not covered by the two-entry " +
                "framework allow-list (Logging:, AllowedHosts): " + string.Join(", ", unread) +
                ". A dead key belongs in this lane's next round, not in the allow-list.");
        }
    }
}
