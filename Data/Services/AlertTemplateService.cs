/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
    public class AlertTemplateService
    {
        private readonly ILogger<AlertTemplateService> _logger;
        private AlertTemplateConfig _config = new();
        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Config", "alert-templates.json");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public AlertTemplateConfig Config => _config;
        public event Action? OnChanged;

        public AlertTemplateService(ILogger<AlertTemplateService> logger)
        {
            _logger = logger;
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    _config = JsonSerializer.Deserialize<AlertTemplateConfig>(json) ?? new();
                    _logger.LogInformation("Alert templates loaded from {Path}", ConfigPath);
                }
                else
                {
                    _config = new AlertTemplateConfig(); // defaults
                    Save(); // write defaults on first run
                    _logger.LogInformation("Alert templates initialised with defaults at {Path}", ConfigPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load alert templates, using defaults");
                _config = new AlertTemplateConfig();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, _jsonOpts));
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
                "warning"  => "#ffc107",
                "high"     => "#e06c00",
                _          => "#17a2b8"
            };

            return template
                .Replace("{{alert_name}}",    n.AlertName ?? "")
                .Replace("{{severity}}",      n.Severity?.ToUpper() ?? "")
                .Replace("{{severity_color}}", severityColor)
                .Replace("{{metric}}",        n.Metric ?? "")
                .Replace("{{current_value}}", n.CurrentValue.ToString("N2"))
                .Replace("{{threshold}}",     n.ThresholdValue.ToString("N2"))
                .Replace("{{server}}",        n.InstanceName ?? "")
                .Replace("{{instance}}",      n.InstanceName ?? "")
                .Replace("{{message}}",       n.Message ?? "")
                .Replace("{{triggered_at}}",  n.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss"))
                .Replace("{{machine}}",       Environment.MachineName)
                .Replace("{{hit_count}}",     "1"); // NotificationChannelService can pass richer data if needed
        }

        /// <summary>Returns the subject rendered with tokens.</summary>
        public string RenderSubject(string templateSubject, AlertNotification n)
            => Render(templateSubject, n);

        /// <summary>Returns the body rendered with tokens.</summary>
        public string RenderBody(string templateBody, AlertNotification n)
            => Render(templateBody, n);
    }
}
