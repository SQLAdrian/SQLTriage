/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Stores per-channel notification templates.
    /// Loaded from Config/alert-templates.json.
    /// </summary>
    public class AlertTemplateConfig
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("email")]
        public ChannelTemplate Email { get; set; } = ChannelTemplate.DefaultEmail();

        [JsonPropertyName("teams")]
        public ChannelTemplate Teams { get; set; } = ChannelTemplate.DefaultTeams();

        [JsonPropertyName("slack")]
        public ChannelTemplate Slack { get; set; } = ChannelTemplate.DefaultSlack();

        [JsonPropertyName("webhook")]
        public ChannelTemplate Webhook { get; set; } = ChannelTemplate.DefaultWebhook();

        [JsonPropertyName("pagerduty")]
        public ChannelTemplate PagerDuty { get; set; } = ChannelTemplate.DefaultPagerDuty();

        [JsonPropertyName("servicenow")]
        public ChannelTemplate ServiceNow { get; set; } = ChannelTemplate.DefaultServiceNow();
    }

    public class ChannelTemplate
    {
        /// <summary>Subject line (used by email; treated as title for others).</summary>
        [JsonPropertyName("subject")]
        public string Subject { get; set; } = "";

        /// <summary>Body content. For email: full HTML. For others: plain text / JSON snippet.</summary>
        [JsonPropertyName("body")]
        public string Body { get; set; } = "";

        /// <summary>Human-readable description shown in the UI.</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        // ── Available tokens (for UI token reference) ────────────────────────
        public static readonly string[] Tokens =
        {
            "{{alert_name}}",
            "{{severity}}",
            "{{metric}}",
            "{{current_value}}",
            "{{threshold}}",
            "{{server}}",
            "{{instance}}",
            "{{message}}",
            "{{triggered_at}}",
            "{{machine}}",
            "{{hit_count}}",
        };

        // ── Defaults ─────────────────────────────────────────────────────────

        public static ChannelTemplate DefaultEmail() => new()
        {
            Description = "HTML email sent when an alert fires. Supports full HTML and all tokens.",
            Subject = "[{{severity}}] {{alert_name}} — {{server}}",
            Body = @"<div style=""font-family:'Segoe UI',Tahoma,sans-serif;max-width:600px;margin:0 auto;"">
  <div style=""background:{{severity_color}};color:#fff;padding:12px 20px;border-radius:6px 6px 0 0;"">
    <h2 style=""margin:0;font-size:18px;"">{{alert_name}}</h2>
  </div>
  <div style=""background:#1e1e2e;color:#e0e0e0;padding:20px;border:1px solid #333;border-radius:0 0 6px 6px;"">
    <table style=""width:100%;border-collapse:collapse;"">
      <tr><td style=""padding:6px 0;color:#888;"">Severity</td>    <td style=""padding:6px 0;font-weight:bold;color:{{severity_color}};"">{{severity}}</td></tr>
      <tr><td style=""padding:6px 0;color:#888;"">Server</td>      <td style=""padding:6px 0;"">{{server}}</td></tr>
      <tr><td style=""padding:6px 0;color:#888;"">Metric</td>      <td style=""padding:6px 0;"">{{metric}}</td></tr>
      <tr><td style=""padding:6px 0;color:#888;"">Current Value</td><td style=""padding:6px 0;font-weight:bold;"">{{current_value}}</td></tr>
      <tr><td style=""padding:6px 0;color:#888;"">Threshold</td>   <td style=""padding:6px 0;"">{{threshold}}</td></tr>
      <tr><td style=""padding:6px 0;color:#888;"">Hit Count</td>   <td style=""padding:6px 0;"">{{hit_count}}</td></tr>
      <tr><td style=""padding:6px 0;color:#888;"">Time (UTC)</td>  <td style=""padding:6px 0;"">{{triggered_at}}</td></tr>
      <tr><td style=""padding:6px 0;color:#888;"">Machine</td>     <td style=""padding:6px 0;"">{{machine}}</td></tr>
    </table>
    <div style=""margin-top:16px;padding:10px;background:#0d1117;border-radius:4px;font-size:13px;"">
      {{message}}
    </div>
    <p style=""margin-top:16px;font-size:11px;color:#666;"">Sent by SQLTriage — {{machine}}</p>
  </div>
</div>"
        };

        public static ChannelTemplate DefaultTeams() => new()
        {
            Description = "Teams webhook message body (Adaptive Card JSON). Use tokens for dynamic values.",
            Subject = "[{{severity}}] {{alert_name}}",
            Body = @"{
  ""type"": ""message"",
  ""attachments"": [{
    ""contentType"": ""application/vnd.microsoft.card.adaptive"",
    ""content"": {
      ""$schema"": ""http://adaptivecards.io/schemas/adaptive-card.json"",
      ""type"": ""AdaptiveCard"",
      ""version"": ""1.4"",
      ""body"": [
        { ""type"": ""TextBlock"", ""text"": ""{{alert_name}}"", ""weight"": ""Bolder"", ""size"": ""Medium"" },
        { ""type"": ""FactSet"", ""facts"": [
          { ""title"": ""Severity"",      ""value"": ""{{severity}}"" },
          { ""title"": ""Server"",        ""value"": ""{{server}}"" },
          { ""title"": ""Current Value"", ""value"": ""{{current_value}}"" },
          { ""title"": ""Threshold"",     ""value"": ""{{threshold}}"" },
          { ""title"": ""Time (UTC)"",    ""value"": ""{{triggered_at}}"" }
        ]},
        { ""type"": ""TextBlock"", ""text"": ""{{message}}"", ""wrap"": true, ""spacing"": ""Medium"" }
      ]
    }
  }]
}"
        };

        public static ChannelTemplate DefaultSlack() => new()
        {
            Description = "Slack webhook payload (JSON). Use tokens for dynamic values.",
            Subject = "[{{severity}}] {{alert_name}}",
            Body = @"{
  ""text"": ""*[{{severity}}] {{alert_name}}* — {{server}}"",
  ""attachments"": [{
    ""color"": ""{{severity_color}}"",
    ""fields"": [
      { ""title"": ""Metric"",        ""value"": ""{{metric}}"",        ""short"": true },
      { ""title"": ""Current Value"", ""value"": ""{{current_value}}"", ""short"": true },
      { ""title"": ""Threshold"",     ""value"": ""{{threshold}}"",     ""short"": true },
      { ""title"": ""Hit Count"",     ""value"": ""{{hit_count}}"",     ""short"": true },
      { ""title"": ""Message"",       ""value"": ""{{message}}"",       ""short"": false }
    ],
    ""footer"": ""SQLTriage | {{triggered_at}}""
  }]
}"
        };

        public static ChannelTemplate DefaultWebhook() => new()
        {
            Description = "Generic webhook JSON payload. Customise the structure to match your endpoint.",
            Subject = "[{{severity}}] {{alert_name}}",
            Body = @"{
  ""alert_name"":    ""{{alert_name}}"",
  ""severity"":      ""{{severity}}"",
  ""server"":        ""{{server}}"",
  ""metric"":        ""{{metric}}"",
  ""current_value"": ""{{current_value}}"",
  ""threshold"":     ""{{threshold}}"",
  ""hit_count"":     ""{{hit_count}}"",
  ""message"":       ""{{message}}"",
  ""triggered_at"":  ""{{triggered_at}}"",
  ""machine"":       ""{{machine}}""
}"
        };

        public static ChannelTemplate DefaultPagerDuty() => new()
        {
            Description = "PagerDuty event summary and details. The routing key is set in Alerting Config.",
            Subject = "[{{severity}}] {{alert_name}} on {{server}}",
            Body = @"{{alert_name}} — {{metric}} is {{current_value}} (threshold: {{threshold}}) on {{server}}. {{message}}"
        };

        public static ChannelTemplate DefaultServiceNow() => new()
        {
            Description = "ServiceNow incident short description and work notes.",
            Subject = "[{{severity}}] {{alert_name}} — {{server}}",
            Body = @"Alert: {{alert_name}}
Server: {{server}}
Severity: {{severity}}
Metric: {{metric}}
Current Value: {{current_value}}
Threshold: {{threshold}}
Hit Count: {{hit_count}}
Time (UTC): {{triggered_at}}

{{message}}"
        };
    }
}
