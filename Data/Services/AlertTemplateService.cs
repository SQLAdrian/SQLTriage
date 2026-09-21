/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:AlertTemplateService.Class — loads and renders email/notification alert templates
    public class AlertTemplateService
    {
        private readonly ILogger<AlertTemplateService> _logger;
        private AlertTemplateConfig _config = new();

        /// <summary>
        /// How alert-templates.json came off disk (alerts-r1-09). Before the guard, a file that
        /// deserialised to literal <c>null</c> silently became built-in defaults AND logged "Alert
        /// templates loaded" at Information — a false success line — and the next Save wrote those
        /// defaults over the operator's customised templates with no announcement.
        ///
        /// <para><b>ANNOUNCE tier, not refuse</b> — the same grade as alert-definitions.json. This
        /// file holds re-authorable email/notification template text and carries no secret and no
        /// security control, so a damaged load is announced loudly (and quarantined by the loader)
        /// rather than refused. The write still goes through, matching the sibling
        /// AlertDefinitionService.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        /// <summary>The <c>.rejected-</c> copy taken at load, or null. Feeds the recovery register.</summary>
        private string? _quarantine;

        private readonly object _lock = new();

        /// <summary>True when alert-templates.json exists and did not load, so the templates in force are built-in defaults.</summary>
        public bool IsStoreDamaged { get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; } }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        /// <summary>
        /// Where this instance's templates live. Was a static readonly path, which meant every
        /// test that constructed this service read and WROTE the one file beside the test
        /// assembly (Load writes defaults on first run). Instance field plus a path override,
        /// the same seam AlertDefinitionService and AdminAuthService already use.
        /// </summary>
        private readonly string _configPath;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public AlertTemplateConfig Config => _config;
        public event Action? OnChanged;

        public AlertTemplateService(ILogger<AlertTemplateService> logger)
            : this(logger, null) { }

        /// <param name="templatesFilePath">
        /// Test seam (house pattern): null means the real Config/alert-templates.json beside the
        /// executable, exactly as before.
        /// </param>
        internal AlertTemplateService(ILogger<AlertTemplateService> logger, string? templatesFilePath)
        {
            _logger = logger;
            _configPath = templatesFilePath ?? Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Config", "alert-templates.json");
            Load();
        }

        private void Load()
        {
            try
            {
                // alerts-r1-09: through the shared loader, which distinguishes "missing" (fresh
                // install) from "exists and did not parse" (damage) and quarantines the damaged
                // file to a .rejected- copy. The old hand-rolled Load coerced a literal-null file
                // to defaults with `?? new()` and then logged a SUCCESS line, so a damaged store
                // read as a clean load and the next Save overwrote the operator's templates.
                _config = ConfigFileHelper.Load<AlertTemplateConfig>(
                    _configPath, null, out _load, out _quarantine);

                if (_load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty)
                {
                    _logger.LogError(
                        "[AlertTemplates] {Path} exists and did not load ({Outcome}). The templates in force "
                        + "are built-in defaults, not the operator's. Every notification renders the shipped "
                        + "wording. Saving any template from here will replace the file with those defaults, so "
                        + "repair it first if you want the customised templates back. {Recovery}",
                        _configPath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                }
                else if (_load == ConfigLoadOutcome.Missing)
                {
                    _config = new AlertTemplateConfig(); // defaults
                    Save(); // write defaults on first run — Missing is a fresh install, not damage
                    _logger.LogInformation("Alert templates initialised with defaults at {Path}", _configPath);
                }
                else
                {
                    _logger.LogInformation("Alert templates loaded from {Path}", _configPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load alert templates, using defaults");
                _config = new AlertTemplateConfig();
                _load = ConfigLoadOutcome.Unreadable;
            }
        }

        public void Save()
        {
            // Announced, not refused — see _load. A damaged load whose file is now replaced by the
            // in-memory defaults says so once, loudly, because after a successful write the store is
            // Loaded again and this is the only record of the moment the customised templates went.
            var replacingDamage = _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                ConfigFileHelper.Save(_configPath, _config, _jsonOpts);

                if (replacingDamage)
                    _logger.LogWarning(
                        "[AlertTemplates] {Path} did not load ({Outcome}) and has now been REPLACED by the "
                        + "built-in default templates held in memory. Any customised template the damaged file "
                        + "held is gone from the live file. {Recovery}",
                        _configPath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));

                lock (_lock) { _load = ConfigLoadOutcome.Loaded; _quarantine = null; }
                OnChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save alert templates");
            }
        }

        public void Update(AlertTemplateConfig config)
        {
            _config = config;
            Save();
        }

        /// <summary>
        /// Applies token substitution to a template string using an AlertNotification.
        /// </summary>
        public static string Render(string template, AlertNotification n)
        {
            var severityColor = n.Severity?.ToLower() switch
            {
                "critical" => "#dc3545",
                "warning" => "#ffc107",
                "high" => "#e06c00",
                _ => "#17a2b8"
            };

            return template
                .Replace("{{alert_name}}", n.AlertName ?? "")
                .Replace("{{severity}}", n.Severity?.ToUpper() ?? "")
                .Replace("{{severity_color}}", severityColor)
                .Replace("{{metric}}", n.Metric ?? "")
                // N-2 (2026-08-22), the same class as C3 below: CurrentValue was a plain double,
                // so a connectivity test-send -- which measures nothing -- rendered "0.00" as the
                // metric reading on every template. CurrentValueText is the one place that
                // decision is made, and it never returns a number nobody measured.
                .Replace("{{current_value}}", n.CurrentValueText)
                // Gate fix C3 (2026-08-05). This rendered n.ThresholdValue directly, and the
                // evaluation service coerced a missing threshold to 0, so a trend fire reached
                // every one of the six default templates as "Threshold: 0.00" - a number that
                // exists in no alert definition, in the row a DBA reads at 3am. ThresholdText is
                // the single place that decision is made, and it never returns a number the
                // firing basis did not measure.
                .Replace("{{threshold}}", n.ThresholdText)
                .Replace("{{server}}", n.InstanceName ?? "")
                .Replace("{{instance}}", n.InstanceName ?? "")
                .Replace("{{message}}", n.Message ?? "")
                .Replace("{{triggered_at}}", n.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{{machine}}", Environment.MachineName)
                // N-1 (2026-08-22). This was the literal string "1", on every notification this
                // product has ever sent. AlertState.HitCount is incremented on every re-fire and
                // was never carried to the notification, so an alert that had been firing for
                // hours reached the client as "Hit Count: 1". HitCountText is the single place
                // that decision is made, and it prints "not recorded" rather than a number when
                // the sender did not count (a connectivity test-send, or the legacy path).
                .Replace("{{hit_count}}", n.HitCountText);
        }

        /// <summary>Returns the subject rendered with tokens.</summary>
        public string RenderSubject(string templateSubject, AlertNotification n)
            => Render(templateSubject, n);

        /// <summary>Returns the body rendered with tokens.</summary>
        public string RenderBody(string templateBody, AlertNotification n)
            => Render(templateBody, n);
    }
}
