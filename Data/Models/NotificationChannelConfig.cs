/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SqlHealthAssessment.Data.Models
{
    /// <summary>
    /// Root configuration for all outbound notification channels.
    /// Persisted to Config/notification-channels.json.
    /// </summary>
    public class NotificationChannelConfig
    {
        [JsonPropertyName("smtp")]
        public SmtpChannelConfig Smtp { get; set; } = new();

        [JsonPropertyName("teamsWebhook")]
        public TeamsWebhookChannelConfig TeamsWebhook { get; set; } = new();

        [JsonPropertyName("slack")]
        public SlackChannelConfig Slack { get; set; } = new();

        [JsonPropertyName("webhook")]
        public WebhookChannelConfig Webhook { get; set; } = new();

        [JsonPropertyName("pagerDuty")]
        public PagerDutyChannelConfig PagerDuty { get; set; } = new();

        [JsonPropertyName("serviceNow")]
        public ServiceNowChannelConfig ServiceNow { get; set; } = new();

        [JsonPropertyName("whatsApp")]
        public WhatsAppChannelConfig WhatsApp { get; set; } = new();
    }

    public class SmtpChannelConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; } = 587;

        [JsonPropertyName("useTls")]
        public bool UseTls { get; set; } = true;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Encrypted via CredentialProtector. Never stored in plaintext.
        /// </summary>
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("fromAddress")]
        public string FromAddress { get; set; } = string.Empty;

        [JsonPropertyName("fromName")]
        public string FromName { get; set; } = "SQL Health Assessment";

        [JsonPropertyName("replyToAddress")]
        public string ReplyToAddress { get; set; } = string.Empty;

        [JsonPropertyName("toAddresses")]
        public List<string> ToAddresses { get; set; } = new();

        /// <summary>
        /// Minimum severity to send email: info, warning, critical
        /// </summary>
        [JsonPropertyName("minimumSeverity")]
        public string MinimumSeverity { get; set; } = "warning";
    }

    public class TeamsWebhookChannelConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Microsoft Teams Incoming Webhook URL. Encrypted via CredentialProtector.
        /// </summary>
        [JsonPropertyName("webhookUrl")]
        public string WebhookUrl { get; set; } = string.Empty;

        /// <summary>
        /// Minimum severity to post to Teams: info, warning, critical
        /// </summary>
        [JsonPropertyName("minimumSeverity")]
        public string MinimumSeverity { get; set; } = "warning";
    }

    public class SlackChannelConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Slack Incoming Webhook URL. Encrypted via CredentialProtector.
        /// </summary>
        [JsonPropertyName("webhookUrl")]
        public string WebhookUrl { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = "SQL Health Assessment";

        [JsonPropertyName("minimumSeverity")]
        public string MinimumSeverity { get; set; } = "warning";
    }

    public class WebhookChannelConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Generic HTTP POST endpoint URL. Encrypted via CredentialProtector.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Optional Bearer token or API key for Authorization header. Encrypted.
        /// </summary>
        [JsonPropertyName("authToken")]
        public string AuthToken { get; set; } = string.Empty;

        /// <summary>
        /// Custom headers as key=value pairs, one per line.
        /// </summary>
        [JsonPropertyName("customHeaders")]
        public string CustomHeaders { get; set; } = string.Empty;

        [JsonPropertyName("minimumSeverity")]
        public string MinimumSeverity { get; set; } = "warning";
    }

    public class PagerDutyChannelConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// PagerDuty Events API v2 integration/routing key. Encrypted.
        /// </summary>
        [JsonPropertyName("routingKey")]
        public string RoutingKey { get; set; } = string.Empty;

        /// <summary>
        /// Minimum severity to trigger PagerDuty: warning, critical
        /// </summary>
        [JsonPropertyName("minimumSeverity")]
        public string MinimumSeverity { get; set; } = "critical";
    }

    public class ServiceNowChannelConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// ServiceNow instance URL (e.g. https://mycompany.service-now.com).
        /// </summary>
        [JsonPropertyName("instanceUrl")]
        public string InstanceUrl { get; set; } = string.Empty;

        /// <summary>
        /// ServiceNow API username. Encrypted.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// ServiceNow API password. Encrypted.
        /// </summary>
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Table to create incidents in (default: incident).
        /// </summary>
        [JsonPropertyName("table")]
        public string Table { get; set; } = "incident";

        /// <summary>
        /// Assignment group for created incidents.
        /// </summary>
        [JsonPropertyName("assignmentGroup")]
        public string AssignmentGroup { get; set; } = string.Empty;

        /// <summary>
        /// Caller ID (sys_id or user name) for created incidents.
        /// </summary>
        [JsonPropertyName("callerId")]
        public string CallerId { get; set; } = string.Empty;

        [JsonPropertyName("minimumSeverity")]
        public string MinimumSeverity { get; set; } = "critical";
    }

    public class WhatsAppChannelConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// WhatsApp Business Phone Number ID (from Meta Business dashboard).
        /// </summary>
        [JsonPropertyName("phoneNumberId")]
        public string PhoneNumberId { get; set; } = string.Empty;

        /// <summary>
        /// Permanent access token for the WhatsApp Business API. Encrypted via CredentialProtector.
        /// </summary>
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Recipient phone numbers in E.164 format (e.g. +1234567890). Comma-separated in UI.
        /// </summary>
        [JsonPropertyName("recipientNumbers")]
        public List<string> RecipientNumbers { get; set; } = new();

        /// <summary>
        /// Pre-approved message template name (e.g. "sql_alert_notification").
        /// </summary>
        [JsonPropertyName("templateName")]
        public string TemplateName { get; set; } = string.Empty;

        /// <summary>
        /// Template language code (e.g. "en_US", "en").
        /// </summary>
        [JsonPropertyName("templateLanguage")]
        public string TemplateLanguage { get; set; } = "en_US";

        [JsonPropertyName("minimumSeverity")]
        public string MinimumSeverity { get; set; } = "critical";
    }
}
