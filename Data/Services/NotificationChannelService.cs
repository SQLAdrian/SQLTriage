/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Dispatches alert notifications via configured outbound channels (SMTP email, Teams webhook).
    /// Configuration is persisted to Config/notification-channels.json with credentials encrypted
    /// via CredentialProtector.
    /// </summary>
    public class NotificationChannelService
    {
        private readonly ILogger<NotificationChannelService> _logger;
        private readonly AlertTemplateService _templates;
        private readonly string _configFilePath;
        private readonly object _lock = new();
        private NotificationChannelConfig _config = new();
        private readonly HttpClient _httpClient;
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public NotificationChannelConfig Config
        {
            get { lock (_lock) return _config; }
        }

        public event Action? OnConfigChanged;

        public NotificationChannelService(ILogger<NotificationChannelService> logger, AlertTemplateService templates)
        {
            _logger = logger;
            _templates = templates;
            _configFilePath = Path.Combine(AppContext.BaseDirectory, "Config", "notification-channels.json");
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            LoadConfig();
        }

        // ──────────────── Configuration ────────────────

        public void UpdateConfig(NotificationChannelConfig config)
        {
            lock (_lock)
            {
                _config = config;
                SaveConfig();
            }
            OnConfigChanged?.Invoke();
        }

        /// <summary>Persists only the alert-window config without touching channel credentials.</summary>
        public void UpdateAlertWindows(AlertWindowConfig windows)
        {
            lock (_lock)
            {
                _config.AlertWindows = windows;
                SaveConfig();
            }
            OnConfigChanged?.Invoke();
        }

        /// <summary>Returns the current alert window config (thread-safe snapshot).</summary>
        public AlertWindowConfig GetAlertWindows()
        {
            lock (_lock) return _config.AlertWindows;
        }

        private void LoadConfig()
        {
            try
            {
                _config = ConfigFileHelper.Load<NotificationChannelConfig>(_configFilePath);
                _logger.LogInformation("Loaded notification channel config: SMTP={SmtpEnabled}, Teams={TeamsEnabled}, Slack={SlackEnabled}, Webhook={WebhookEnabled}, PagerDuty={PagerDutyEnabled}, ServiceNow={ServiceNowEnabled}, WhatsApp={WhatsAppEnabled}",
                    _config.Smtp.Enabled, _config.TeamsWebhook.Enabled, _config.Slack.Enabled,
                    _config.Webhook.Enabled, _config.PagerDuty.Enabled, _config.ServiceNow.Enabled, _config.WhatsApp.Enabled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load notification channel config");
                _config = new();
            }
        }

        private void SaveConfig()
        {
            try
            {
                // Defense-in-depth: ensure credentials are encrypted before persisting
                if (!string.IsNullOrEmpty(_config.Smtp.Password) && !CredentialProtector.IsEncrypted(_config.Smtp.Password))
                    _config.Smtp.Password = CredentialProtector.Encrypt(_config.Smtp.Password);

                if (!string.IsNullOrEmpty(_config.TeamsWebhook.WebhookUrl) && !CredentialProtector.IsEncrypted(_config.TeamsWebhook.WebhookUrl))
                    _config.TeamsWebhook.WebhookUrl = CredentialProtector.Encrypt(_config.TeamsWebhook.WebhookUrl);

                if (!string.IsNullOrEmpty(_config.Slack.WebhookUrl) && !CredentialProtector.IsEncrypted(_config.Slack.WebhookUrl))
                    _config.Slack.WebhookUrl = CredentialProtector.Encrypt(_config.Slack.WebhookUrl);

                if (!string.IsNullOrEmpty(_config.Webhook.Url) && !CredentialProtector.IsEncrypted(_config.Webhook.Url))
                    _config.Webhook.Url = CredentialProtector.Encrypt(_config.Webhook.Url);
                if (!string.IsNullOrEmpty(_config.Webhook.AuthToken) && !CredentialProtector.IsEncrypted(_config.Webhook.AuthToken))
                    _config.Webhook.AuthToken = CredentialProtector.Encrypt(_config.Webhook.AuthToken);

                if (!string.IsNullOrEmpty(_config.PagerDuty.RoutingKey) && !CredentialProtector.IsEncrypted(_config.PagerDuty.RoutingKey))
                    _config.PagerDuty.RoutingKey = CredentialProtector.Encrypt(_config.PagerDuty.RoutingKey);

                if (!string.IsNullOrEmpty(_config.ServiceNow.Username) && !CredentialProtector.IsEncrypted(_config.ServiceNow.Username))
                    _config.ServiceNow.Username = CredentialProtector.Encrypt(_config.ServiceNow.Username);
                if (!string.IsNullOrEmpty(_config.ServiceNow.Password) && !CredentialProtector.IsEncrypted(_config.ServiceNow.Password))
                    _config.ServiceNow.Password = CredentialProtector.Encrypt(_config.ServiceNow.Password);

                if (!string.IsNullOrEmpty(_config.WhatsApp.AccessToken) && !CredentialProtector.IsEncrypted(_config.WhatsApp.AccessToken))
                    _config.WhatsApp.AccessToken = CredentialProtector.Encrypt(_config.WhatsApp.AccessToken);

                _config.LastModified = DateTime.Now;
                ConfigFileHelper.Save(_configFilePath, _config, _jsonOptions);
                _logger.LogInformation("Saved notification channel config");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save notification channel config");
            }
        }

        // ──────────────── Dispatch ────────────────

        /// <summary>
        /// Sends an alert notification to all enabled channels that meet the severity threshold.
        /// Fire-and-forget safe — errors are logged but never thrown.
        /// </summary>
        public async Task DispatchAsync(AlertNotification notification)
        {
            var tasks = new List<Task>();

            if (_config.Smtp.Enabled && MeetsSeverity(notification.Severity, _config.Smtp.MinimumSeverity))
            {
                tasks.Add(SendEmailAsync(notification));
            }

            if (_config.TeamsWebhook.Enabled && MeetsSeverity(notification.Severity, _config.TeamsWebhook.MinimumSeverity))
            {
                tasks.Add(SendTeamsAsync(notification));
            }

            if (_config.Slack.Enabled && MeetsSeverity(notification.Severity, _config.Slack.MinimumSeverity))
            {
                tasks.Add(SendSlackAsync(notification));
            }

            if (_config.Webhook.Enabled && MeetsSeverity(notification.Severity, _config.Webhook.MinimumSeverity))
            {
                tasks.Add(SendWebhookAsync(notification));
            }

            if (_config.PagerDuty.Enabled && MeetsSeverity(notification.Severity, _config.PagerDuty.MinimumSeverity))
            {
                tasks.Add(SendPagerDutyAsync(notification));
            }

            if (_config.ServiceNow.Enabled && MeetsSeverity(notification.Severity, _config.ServiceNow.MinimumSeverity))
            {
                tasks.Add(SendServiceNowAsync(notification));
            }

            if (_config.WhatsApp.Enabled && MeetsSeverity(notification.Severity, _config.WhatsApp.MinimumSeverity))
            {
                tasks.Add(SendWhatsAppAsync(notification));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        // ──────────────── SMTP Email ────────────────

        private async Task SendEmailAsync(AlertNotification notification)
        {
            try
            {
                var smtp = _config.Smtp;
                if (string.IsNullOrEmpty(smtp.Host) || smtp.ToAddresses.Count == 0)
                {
                    _logger.LogWarning("SMTP not fully configured — skipping email notification");
                    return;
                }

                var password = DecryptIfNeeded(smtp.Password);

                using var client = new SmtpClient(smtp.Host, smtp.Port)
                {
                    EnableSsl = smtp.UseTls,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 15000
                };

                if (!string.IsNullOrEmpty(smtp.Username) && !string.IsNullOrEmpty(password))
                {
                    client.Credentials = new NetworkCredential(smtp.Username, password);
                }

                var from = new MailAddress(
                    string.IsNullOrEmpty(smtp.FromAddress) ? smtp.Username : smtp.FromAddress,
                    smtp.FromName);

                var emailTemplate = _templates.Config.Email;
                using var message = new MailMessage
                {
                    From = from,
                    Subject = AlertTemplateService.Render(emailTemplate.Subject, notification),
                    IsBodyHtml = true,
                    Body = AlertTemplateService.Render(emailTemplate.Body, notification)
                };

                foreach (var to in smtp.ToAddresses.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    message.To.Add(to.Trim());
                }

                if (!string.IsNullOrWhiteSpace(smtp.ReplyToAddress))
                {
                    message.ReplyToList.Add(new MailAddress(smtp.ReplyToAddress.Trim()));
                }

                await client.SendMailAsync(message);
                _logger.LogInformation("Email alert sent: {AlertName} to {Recipients}",
                    notification.AlertName, string.Join(", ", smtp.ToAddresses));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email alert: {AlertName}", notification.AlertName);
            }
        }

        private static string BuildEmailBody(AlertNotification notification)
        {
            var severityColor = notification.Severity switch
            {
                "critical" => "#dc3545",
                "warning" => "#ffc107",
                _ => "#17a2b8"
            };

            return $@"
<div style=""font-family: 'Segoe UI', Tahoma, sans-serif; max-width: 600px; margin: 0 auto;"">
    <div style=""background: {severityColor}; color: white; padding: 12px 20px; border-radius: 6px 6px 0 0;"">
        <h2 style=""margin: 0; font-size: 18px;"">{WebUtility.HtmlEncode(notification.AlertName)}</h2>
    </div>
    <div style=""background: #1e1e2e; color: #e0e0e0; padding: 20px; border: 1px solid #333; border-radius: 0 0 6px 6px;"">
        <table style=""width: 100%; border-collapse: collapse;"">
            <tr><td style=""padding: 6px 0; color: #888;"">Severity</td><td style=""padding: 6px 0; font-weight: bold; color: {severityColor};"">{notification.Severity.ToUpper()}</td></tr>
            <tr><td style=""padding: 6px 0; color: #888;"">Metric</td><td style=""padding: 6px 0;"">{WebUtility.HtmlEncode(notification.Metric)}</td></tr>
            <tr><td style=""padding: 6px 0; color: #888;"">Current Value</td><td style=""padding: 6px 0; font-weight: bold;"">{notification.CurrentValue:N2}</td></tr>
            <tr><td style=""padding: 6px 0; color: #888;"">Threshold</td><td style=""padding: 6px 0;"">{notification.ThresholdValue:N2}</td></tr>
            {(string.IsNullOrEmpty(notification.InstanceName) ? "" : $@"<tr><td style=""padding: 6px 0; color: #888;"">Instance</td><td style=""padding: 6px 0;"">{WebUtility.HtmlEncode(notification.InstanceName)}</td></tr>")}
            <tr><td style=""padding: 6px 0; color: #888;"">Time (UTC)</td><td style=""padding: 6px 0;"">{notification.TriggeredAt:yyyy-MM-dd HH:mm:ss}</td></tr>
            <tr><td style=""padding: 6px 0; color: #888;"">Machine</td><td style=""padding: 6px 0;"">{Environment.MachineName}</td></tr>
        </table>
        <div style=""margin-top: 16px; padding: 10px; background: #0d1117; border-radius: 4px; font-size: 13px;"">
            {WebUtility.HtmlEncode(notification.Message)}
        </div>
        <p style=""margin-top: 16px; font-size: 11px; color: #666;"">Sent by SQL Health Assessment — {Environment.MachineName}</p>
    </div>
</div>";
        }

        /// <summary>
        /// Sends a test email to verify SMTP configuration.
        /// </summary>
        public async Task<(bool Success, string Message)> TestSmtpAsync()
        {
            var smtp = _config.Smtp;
            if (string.IsNullOrEmpty(smtp.Host))
                return (false, "SMTP host is not configured.");
            if (smtp.ToAddresses.Count == 0)
                return (false, "No recipient email addresses configured.");

            try
            {
                var testNotification = new AlertNotification
                {
                    AlertName = "SMTP Test",
                    Metric = "test",
                    CurrentValue = 0,
                    ThresholdValue = 0,
                    Severity = "info",
                    Message = "This is a test notification from SQL Health Assessment. If you received this email, SMTP is configured correctly.",
                    InstanceName = Environment.MachineName
                };

                await SendEmailAsync(testNotification);
                return (true, $"Test email sent to {string.Join(", ", smtp.ToAddresses)}");
            }
            catch (Exception ex)
            {
                return (false, $"SMTP test failed: {ex.Message}");
            }
        }

        // ──────────────── Teams Webhook ────────────────

        private async Task SendTeamsAsync(AlertNotification notification)
        {
            try
            {
                var webhookUrl = DecryptIfNeeded(_config.TeamsWebhook.WebhookUrl);
                if (string.IsNullOrEmpty(webhookUrl))
                {
                    _logger.LogWarning("Teams webhook URL not configured — skipping");
                    return;
                }

                var themeColor = notification.Severity switch
                {
                    "critical" => "dc3545",
                    "warning" => "ffc107",
                    _ => "17a2b8"
                };

                // Adaptive Card payload for Teams Incoming Webhook
                // Using Dictionary for content to support the "$schema" key (C# identifiers can't start with $)
                var cardContent = new Dictionary<string, object>
                {
                    ["type"] = "AdaptiveCard",
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                    ["version"] = "1.4",
                    ["body"] = new object[]
                    {
                        new
                        {
                            type = "TextBlock",
                            size = "Medium",
                            weight = "Bolder",
                            text = $"\u26a0\ufe0f [{notification.Severity.ToUpper()}] {notification.AlertName}",
                            color = notification.Severity == "critical" ? "Attention" : (notification.Severity == "warning" ? "Warning" : "Default")
                        },
                        new
                        {
                            type = "FactSet",
                            facts = BuildTeamsFacts(notification)
                        },
                        new
                        {
                            type = "TextBlock",
                            text = notification.Message,
                            wrap = true,
                            spacing = "Medium"
                        }
                    }
                };

                var payload = new
                {
                    type = "message",
                    attachments = new[]
                    {
                        new
                        {
                            contentType = "application/vnd.microsoft.card.adaptive",
                            contentUrl = (string?)null,
                            content = cardContent
                        }
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(webhookUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Teams alert sent: {AlertName}", notification.AlertName);
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Teams webhook returned {StatusCode}: {Body}",
                        (int)response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Teams alert: {AlertName}", notification.AlertName);
            }
        }

        private static object[] BuildTeamsFacts(AlertNotification notification)
        {
            var facts = new List<object>
            {
                new { title = "Metric", value = notification.Metric },
                new { title = "Current Value", value = notification.CurrentValue.ToString("N2") },
                new { title = "Threshold", value = notification.ThresholdValue.ToString("N2") },
                new { title = "Severity", value = notification.Severity.ToUpper() },
                new { title = "Time (UTC)", value = notification.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss") },
                new { title = "Machine", value = Environment.MachineName }
            };

            if (!string.IsNullOrEmpty(notification.InstanceName))
                facts.Insert(0, new { title = "Instance", value = notification.InstanceName });

            return facts.ToArray();
        }

        /// <summary>
        /// Sends a test message to the configured Teams webhook.
        /// </summary>
        public async Task<(bool Success, string Message)> TestTeamsAsync()
        {
            var webhookUrl = DecryptIfNeeded(_config.TeamsWebhook.WebhookUrl);
            if (string.IsNullOrEmpty(webhookUrl))
                return (false, "Teams webhook URL is not configured.");

            try
            {
                var testNotification = new AlertNotification
                {
                    AlertName = "Teams Webhook Test",
                    Metric = "test",
                    CurrentValue = 0,
                    ThresholdValue = 0,
                    Severity = "info",
                    Message = "This is a test notification from SQL Health Assessment. If you see this message, Teams webhook is configured correctly.",
                    InstanceName = Environment.MachineName
                };

                await SendTeamsAsync(testNotification);
                return (true, "Test message sent to Teams channel.");
            }
            catch (Exception ex)
            {
                return (false, $"Teams webhook test failed: {ex.Message}");
            }
        }

        // ──────────────── Slack ────────────────

        private async Task SendSlackAsync(AlertNotification notification)
        {
            try
            {
                var webhookUrl = DecryptIfNeeded(_config.Slack.WebhookUrl);
                if (string.IsNullOrEmpty(webhookUrl))
                {
                    _logger.LogWarning("Slack webhook URL not configured — skipping");
                    return;
                }

                var severityEmoji = notification.Severity switch
                {
                    "critical" => ":rotating_light:",
                    "warning" => ":warning:",
                    _ => ":information_source:"
                };

                var color = notification.Severity switch
                {
                    "critical" => "#dc3545",
                    "warning" => "#ffc107",
                    _ => "#17a2b8"
                };

                var instanceField = string.IsNullOrEmpty(notification.InstanceName)
                    ? ""
                    : $"*Instance:* {notification.InstanceName}\n";

                var payload = new Dictionary<string, object>
                {
                    ["username"] = string.IsNullOrEmpty(_config.Slack.Username) ? "SQL Health Assessment" : _config.Slack.Username,
                    ["icon_emoji"] = ":database:",
                    ["attachments"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["color"] = color,
                            ["title"] = $"{severityEmoji} [{notification.Severity.ToUpper()}] {notification.AlertName}",
                            ["text"] = notification.Message,
                            ["fields"] = new[]
                            {
                                new { title = "Metric", value = notification.Metric, @short = true },
                                new { title = "Value", value = notification.CurrentValue.ToString("N2"), @short = true },
                                new { title = "Threshold", value = notification.ThresholdValue.ToString("N2"), @short = true },
                                new { title = "Machine", value = Environment.MachineName, @short = true }
                            },
                            ["footer"] = $"SQL Health Assessment • {notification.TriggeredAt:yyyy-MM-dd HH:mm:ss} UTC",
                            ["ts"] = new DateTimeOffset(notification.TriggeredAt, TimeSpan.Zero).ToUnixTimeSeconds()
                        }
                    }
                };

                if (!string.IsNullOrEmpty(_config.Slack.Channel))
                    payload["channel"] = _config.Slack.Channel;

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(webhookUrl, content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Slack alert sent: {AlertName}", notification.AlertName);
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Slack webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Slack alert: {AlertName}", notification.AlertName);
            }
        }

        public async Task<(bool Success, string Message)> TestSlackAsync()
        {
            var webhookUrl = DecryptIfNeeded(_config.Slack.WebhookUrl);
            if (string.IsNullOrEmpty(webhookUrl))
                return (false, "Slack webhook URL is not configured.");

            try
            {
                var test = new AlertNotification
                {
                    AlertName = "Slack Webhook Test",
                    Metric = "test",
                    Severity = "info",
                    Message = "This is a test notification from SQL Health Assessment. If you see this message, Slack is configured correctly.",
                    InstanceName = Environment.MachineName
                };
                await SendSlackAsync(test);
                return (true, "Test message sent to Slack channel.");
            }
            catch (Exception ex)
            {
                return (false, $"Slack test failed: {ex.Message}");
            }
        }

        // ──────────────── Generic Webhook ────────────────

        private async Task SendWebhookAsync(AlertNotification notification)
        {
            try
            {
                var url = DecryptIfNeeded(_config.Webhook.Url);
                if (string.IsNullOrEmpty(url))
                {
                    _logger.LogWarning("Webhook URL not configured — skipping");
                    return;
                }

                var payload = new
                {
                    alertName = notification.AlertName,
                    metric = notification.Metric,
                    severity = notification.Severity,
                    currentValue = notification.CurrentValue,
                    thresholdValue = notification.ThresholdValue,
                    message = notification.Message,
                    instanceName = notification.InstanceName,
                    machineName = Environment.MachineName,
                    triggeredAtUtc = notification.TriggeredAt.ToString("o")
                };

                var json = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                var authToken = DecryptIfNeeded(_config.Webhook.AuthToken);
                if (!string.IsNullOrEmpty(authToken))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authToken}");

                // Parse custom headers (key=value per line)
                if (!string.IsNullOrEmpty(_config.Webhook.CustomHeaders))
                {
                    foreach (var line in _config.Webhook.CustomHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eqIdx = line.IndexOf('=');
                        if (eqIdx > 0)
                            request.Headers.TryAddWithoutValidation(line[..eqIdx].Trim(), line[(eqIdx + 1)..].Trim());
                    }
                }

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Webhook alert sent: {AlertName} to {Url}", notification.AlertName, url);
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send webhook alert: {AlertName}", notification.AlertName);
            }
        }

        public async Task<(bool Success, string Message)> TestWebhookAsync()
        {
            var url = DecryptIfNeeded(_config.Webhook.Url);
            if (string.IsNullOrEmpty(url))
                return (false, "Webhook URL is not configured.");

            try
            {
                var test = new AlertNotification
                {
                    AlertName = "Webhook Test",
                    Metric = "test",
                    Severity = "info",
                    Message = "Test notification from SQL Health Assessment.",
                    InstanceName = Environment.MachineName
                };
                await SendWebhookAsync(test);
                return (true, $"Test payload sent to {url}");
            }
            catch (Exception ex)
            {
                return (false, $"Webhook test failed: {ex.Message}");
            }
        }

        // ──────────────── PagerDuty ────────────────

        private async Task SendPagerDutyAsync(AlertNotification notification)
        {
            try
            {
                var routingKey = DecryptIfNeeded(_config.PagerDuty.RoutingKey);
                if (string.IsNullOrEmpty(routingKey))
                {
                    _logger.LogWarning("PagerDuty routing key not configured — skipping");
                    return;
                }

                var pdSeverity = notification.Severity switch
                {
                    "critical" => "critical",
                    "warning" => "warning",
                    "info" => "info",
                    _ => "error"
                };

                // PagerDuty Events API v2
                var payload = new
                {
                    routing_key = routingKey,
                    event_action = "trigger",
                    dedup_key = $"sqlhealth-{notification.AlertName}-{notification.InstanceName}",
                    payload = new
                    {
                        summary = $"[{notification.Severity.ToUpper()}] {notification.AlertName}: {notification.Metric} = {notification.CurrentValue:N2} (threshold: {notification.ThresholdValue:N2})",
                        source = Environment.MachineName,
                        severity = pdSeverity,
                        component = notification.InstanceName ?? Environment.MachineName,
                        group = "sql-health-assessment",
                        custom_details = new
                        {
                            metric = notification.Metric,
                            current_value = notification.CurrentValue,
                            threshold_value = notification.ThresholdValue,
                            message = notification.Message,
                            triggered_at_utc = notification.TriggeredAt.ToString("o")
                        }
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("https://events.pagerduty.com/v2/enqueue", content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("PagerDuty alert sent: {AlertName}", notification.AlertName);
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("PagerDuty returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send PagerDuty alert: {AlertName}", notification.AlertName);
            }
        }

        public async Task<(bool Success, string Message)> TestPagerDutyAsync()
        {
            var routingKey = DecryptIfNeeded(_config.PagerDuty.RoutingKey);
            if (string.IsNullOrEmpty(routingKey))
                return (false, "PagerDuty routing key is not configured.");

            try
            {
                var test = new AlertNotification
                {
                    AlertName = "PagerDuty Integration Test",
                    Metric = "test",
                    Severity = "info",
                    Message = "Test notification from SQL Health Assessment. No action required.",
                    InstanceName = Environment.MachineName
                };
                await SendPagerDutyAsync(test);
                return (true, "Test event sent to PagerDuty.");
            }
            catch (Exception ex)
            {
                return (false, $"PagerDuty test failed: {ex.Message}");
            }
        }

        // ──────────────── ServiceNow ────────────────

        private async Task SendServiceNowAsync(AlertNotification notification)
        {
            try
            {
                var cfg = _config.ServiceNow;
                var instanceUrl = cfg.InstanceUrl.TrimEnd('/');
                var username = DecryptIfNeeded(cfg.Username);
                var password = DecryptIfNeeded(cfg.Password);

                if (string.IsNullOrEmpty(instanceUrl) || string.IsNullOrEmpty(username))
                {
                    _logger.LogWarning("ServiceNow not fully configured — skipping");
                    return;
                }

                var snowSeverity = notification.Severity switch
                {
                    "critical" => "1",  // Critical
                    "warning" => "2",   // High
                    _ => "3"            // Medium
                };

                var table = string.IsNullOrEmpty(cfg.Table) ? "incident" : cfg.Table;
                var url = $"{instanceUrl}/api/now/table/{table}";

                var incidentData = new Dictionary<string, string>
                {
                    ["short_description"] = $"[{notification.Severity.ToUpper()}] {notification.AlertName}: {notification.Metric} = {notification.CurrentValue:N2}",
                    ["description"] = $"{notification.Message}\n\nMetric: {notification.Metric}\nCurrent Value: {notification.CurrentValue:N2}\nThreshold: {notification.ThresholdValue:N2}\nInstance: {notification.InstanceName}\nMachine: {Environment.MachineName}\nTime (UTC): {notification.TriggeredAt:yyyy-MM-dd HH:mm:ss}",
                    ["urgency"] = snowSeverity,
                    ["impact"] = snowSeverity,
                    ["category"] = "Database",
                    ["subcategory"] = "SQL Server"
                };

                if (!string.IsNullOrEmpty(cfg.AssignmentGroup))
                    incidentData["assignment_group"] = cfg.AssignmentGroup;
                if (!string.IsNullOrEmpty(cfg.CallerId))
                    incidentData["caller_id"] = cfg.CallerId;

                var json = JsonSerializer.Serialize(incidentData);
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                var authBytes = Encoding.ASCII.GetBytes($"{username}:{password}");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation("ServiceNow incident created: {AlertName}. Response: {Response}",
                        notification.AlertName, responseBody.Length > 200 ? responseBody[..200] : responseBody);
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("ServiceNow returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ServiceNow incident: {AlertName}", notification.AlertName);
            }
        }

        public async Task<(bool Success, string Message)> TestServiceNowAsync()
        {
            var cfg = _config.ServiceNow;
            if (string.IsNullOrEmpty(cfg.InstanceUrl))
                return (false, "ServiceNow instance URL is not configured.");
            if (string.IsNullOrEmpty(DecryptIfNeeded(cfg.Username)))
                return (false, "ServiceNow username is not configured.");

            try
            {
                // Test connectivity by querying the table API (GET, no incident created)
                var instanceUrl = cfg.InstanceUrl.TrimEnd('/');
                var username = DecryptIfNeeded(cfg.Username);
                var password = DecryptIfNeeded(cfg.Password);
                var table = string.IsNullOrEmpty(cfg.Table) ? "incident" : cfg.Table;
                var url = $"{instanceUrl}/api/now/table/{table}?sysparm_limit=1";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var authBytes = Encoding.ASCII.GetBytes($"{username}:{password}");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return (true, $"Connected to ServiceNow ({instanceUrl}). Table '{table}' is accessible.");
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    return (false, $"ServiceNow returned {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"ServiceNow test failed: {ex.Message}");
            }
        }

        // ──────────────── WhatsApp ────────────────

        private async Task SendWhatsAppAsync(AlertNotification notification)
        {
            try
            {
                var cfg = _config.WhatsApp;
                var accessToken = DecryptIfNeeded(cfg.AccessToken);

                if (string.IsNullOrEmpty(cfg.PhoneNumberId) || string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogWarning("WhatsApp not fully configured — skipping");
                    return;
                }

                if (cfg.RecipientNumbers.Count == 0)
                {
                    _logger.LogWarning("No WhatsApp recipient numbers configured — skipping");
                    return;
                }

                var url = $"https://graph.facebook.com/v21.0/{cfg.PhoneNumberId}/messages";

                foreach (var recipient in cfg.RecipientNumbers.Where(n => !string.IsNullOrWhiteSpace(n)))
                {
                    try
                    {
                        var payload = BuildWhatsAppPayload(cfg, notification, recipient.Trim());
                        var json = JsonSerializer.Serialize(payload);

                        using var request = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                        var response = await _httpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("WhatsApp alert sent: {AlertName} to {Recipient}",
                                notification.AlertName, recipient);
                        }
                        else
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            _logger.LogWarning("WhatsApp API returned {StatusCode} for {Recipient}: {Body}",
                                (int)response.StatusCode, recipient, body);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send WhatsApp alert to {Recipient}", recipient);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WhatsApp alert: {AlertName}", notification.AlertName);
            }
        }

        private static object BuildWhatsAppPayload(WhatsAppChannelConfig cfg, AlertNotification notification, string recipient)
        {
            if (!string.IsNullOrEmpty(cfg.TemplateName))
            {
                // Template message — required for business-initiated conversations
                return new
                {
                    messaging_product = "whatsapp",
                    to = recipient,
                    type = "template",
                    template = new
                    {
                        name = cfg.TemplateName,
                        language = new { code = string.IsNullOrEmpty(cfg.TemplateLanguage) ? "en_US" : cfg.TemplateLanguage },
                        components = new[]
                        {
                            new
                            {
                                type = "body",
                                parameters = new object[]
                                {
                                    new { type = "text", text = notification.Severity.ToUpper() },
                                    new { type = "text", text = notification.AlertName },
                                    new { type = "text", text = notification.Metric },
                                    new { type = "text", text = notification.CurrentValue.ToString("N2") },
                                    new { type = "text", text = notification.ThresholdValue.ToString("N2") },
                                    new { type = "text", text = notification.InstanceName ?? Environment.MachineName },
                                    new { type = "text", text = notification.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss") }
                                }
                            }
                        }
                    }
                };
            }
            else
            {
                // Plain text message — only works within 24-hour customer-service window
                var text = $"*[{notification.Severity.ToUpper()}] {notification.AlertName}*\n\n" +
                           $"Metric: {notification.Metric}\n" +
                           $"Value: {notification.CurrentValue:N2} (threshold: {notification.ThresholdValue:N2})\n" +
                           $"Instance: {notification.InstanceName ?? "N/A"}\n" +
                           $"Time: {notification.TriggeredAt:yyyy-MM-dd HH:mm:ss} UTC\n\n" +
                           notification.Message;

                return new
                {
                    messaging_product = "whatsapp",
                    to = recipient,
                    type = "text",
                    text = new { body = text }
                };
            }
        }

        public async Task<(bool Success, string Message)> TestWhatsAppAsync()
        {
            var cfg = _config.WhatsApp;
            var accessToken = DecryptIfNeeded(cfg.AccessToken);

            if (string.IsNullOrEmpty(cfg.PhoneNumberId))
                return (false, "WhatsApp Phone Number ID is not configured.");
            if (string.IsNullOrEmpty(accessToken))
                return (false, "WhatsApp Access Token is not configured.");
            if (cfg.RecipientNumbers.Count == 0)
                return (false, "No recipient phone numbers configured.");

            try
            {
                var test = new AlertNotification
                {
                    AlertName = "WhatsApp Test",
                    Metric = "test",
                    Severity = "info",
                    Message = "This is a test notification from SQL Health Assessment. If you received this message, WhatsApp is configured correctly.",
                    InstanceName = Environment.MachineName
                };
                await SendWhatsAppAsync(test);
                return (true, $"Test message sent to {cfg.RecipientNumbers.Count} recipient(s).");
            }
            catch (Exception ex)
            {
                return (false, $"WhatsApp test failed: {ex.Message}");
            }
        }

        // ──────────────── Helpers ────────────────

        private static bool MeetsSeverity(string actual, string minimum)
        {
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["info"] = 0,
                ["warning"] = 1,
                ["critical"] = 2
            };

            if (!order.TryGetValue(actual, out var actualLevel)) actualLevel = 0;
            if (!order.TryGetValue(minimum, out var minLevel)) minLevel = 0;

            return actualLevel >= minLevel;
        }

        private static string DecryptIfNeeded(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (CredentialProtector.IsEncrypted(value))
                return CredentialProtector.Decrypt(value);
            return value;
        }
    }
}
