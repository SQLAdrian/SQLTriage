/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>Outcome of a single channel's delivery attempt for one alert dispatch. #68 LEG 1 —
    /// every Send*Async now returns one of these instead of swallowing the result.</summary>
    public sealed record ChannelDispatchResult(string Channel, bool Success, string Detail);

    /// <summary>
    /// THE CHANNEL NAMES, in ONE place, because two things depend on them agreeing and they used to
    /// be seven unrelated string literals.
    ///
    /// <para>(1) Every <c>Send*Async</c> stamps its <see cref="ChannelDispatchResult.Channel"/> from
    /// here, and (2) <see cref="NotificationChannelService.ChannelAdmittedByRouting"/> matches an
    /// operator's channel pick against them, case-insensitively. The picker at
    /// <c>Pages/Alerts.razor:1199-1205</c> stores the LOWERCASED form of each of these as its option
    /// value ("pagerduty", "servicenow", ...), so the case-insensitive match is not a convenience —
    /// it is the whole reason a pick resolves at all.</para>
    ///
    /// <para><b>THE INVARIANT:</b> <i>every channel id the operator picker offers must select
    /// exactly one channel here.</i> A picker key matching nothing routes an escalation to nowhere,
    /// silently. That is pinned FROM THE SOURCE — the census reads the keys out of
    /// <c>Pages/Alerts.razor</c> and the names out of this file, and dispatches with each key to
    /// count what it selects: <c>EscalationChannelRoutingTests.EveryChannelIdThePickerOffers_selectsExactlyOneChannel</c>
    /// in <c>Tests/SQLTriage.Tests/EscalationChannelRoutingTests.cs</c>. Add an eighth channel, or
    /// rename one, without updating both ends and that test goes red naming the orphan.</para>
    /// </summary>
    internal static class ChannelNames
    {
        public const string Smtp = "SMTP";
        public const string Teams = "Teams";
        public const string Slack = "Slack";
        public const string Webhook = "Webhook";
        public const string PagerDuty = "PagerDuty";
        public const string ServiceNow = "ServiceNow";
        public const string WhatsApp = "WhatsApp";

        /// <summary>Every channel this service can dispatch to. The census enumerates THIS, so a new
        /// channel that is not added here is a channel the picker can never route to.</summary>
        public static readonly IReadOnlyList<string> All =
            new[] { Smtp, Teams, Slack, Webhook, PagerDuty, ServiceNow, WhatsApp };
    }

    /// <summary>Last-known delivery outcome for a channel — drives the delivery-health strip on the
    /// Alerting Config page so a broken channel is VISIBLE, not just logged. #68 LEG 1.</summary>
    public sealed record ChannelDeliveryHealth(string Channel, bool LastSuccess, string LastDetail, DateTime LastAttemptUtc);

    // BM:NotificationChannelService.Class — dispatches alerts via SMTP, Teams, Slack, webhook
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

        /// <summary>
        /// How notification-channels.json came off disk, and the reason this store is guarded at
        /// the same grade as rbac-users.json rather than at the grade of a preferences file.
        ///
        /// <para>Every credential this install uses to reach the outside world on an alert lives in
        /// exactly one place — here: the SMTP password, the Teams / Slack / generic webhook URLs
        /// (a webhook URL IS the credential), the generic webhook auth token, the PagerDuty routing
        /// key, the ServiceNow username and password, the WhatsApp access token. They are wrapped
        /// by CredentialProtector, so nothing else on the box holds a copy and nothing can rebuild
        /// them; recovering them means going back to seven external systems and, for several,
        /// issuing NEW ones because the old value is not retrievable from them either.</para>
        ///
        /// <para>A damaged file deserialises to <see cref="NotificationChannelConfig"/> defaults —
        /// every channel Enabled=false, every secret blank — which is byte-identical to an install
        /// where nobody has configured alerting yet. The next save writes that over the file. It
        /// does not need an operator on the credentials page to happen: <see cref="UpdateAlertWindows"/>
        /// touches ONE field and persists the whole object, so "start maintenance mode for 30
        /// minutes" is enough to delete every credential on the install.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        /// <summary>The <c>.rejected-</c> copy taken at load, or null. Feeds the recovery register.</summary>
        private string? _quarantine;

        /// <summary>
        /// True when notification-channels.json exists and did not load — so <see cref="Config"/> is
        /// defaults, every channel reads as OFF whatever the operator configured, and this install
        /// is not delivering alerts it believes it is delivering.
        /// </summary>
        public bool IsStoreDamaged
        {
            get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; }
        }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        // #68 LEG 1: last delivery outcome per channel, keyed by channel name (e.g. "SMTP", "Teams").
        // Populated on every DispatchAsync call regardless of caller — a channel that's currently
        // broken stays visibly broken here until its next successful dispatch.
        private readonly ConcurrentDictionary<string, ChannelDeliveryHealth> _deliveryHealth = new(StringComparer.OrdinalIgnoreCase);

        public NotificationChannelConfig Config
        {
            get { lock (_lock) return _config; }
        }

        /// <summary>Snapshot of the last dispatch outcome for every channel that has attempted a
        /// delivery. Empty until the first alert fires. #68 LEG 1.</summary>
        public IReadOnlyCollection<ChannelDeliveryHealth> DeliveryHealth =>
            _deliveryHealth.Values.OrderBy(h => h.Channel, StringComparer.OrdinalIgnoreCase).ToList();

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

        /// <summary>
        /// Replaces the whole channel configuration. REFUSED when the store exists and did not
        /// load — see <see cref="_load"/> for what that write would destroy.
        ///
        /// <para>Returns rather than throws, and a caller that ignores the result still gets the
        /// safe behaviour; the return exists so the page can stop saying "Configuration saved."
        /// over a write that did not happen.</para>
        /// </summary>
        public StoreWriteOutcome UpdateConfig(NotificationChannelConfig config,
                                              StoreWriteIntent intent = StoreWriteIntent.FromLoadedStore)
        {
            lock (_lock)
            {
                // Checked before _config is replaced, so a refusal leaves this service reporting
                // the same channels it reported a moment ago.
                if (ConfigFileHelper.WouldOverwriteUnreadStore(_load, intent))
                    return RefuseWrite();

                var previous = _config;
                _config = config;
                if (!SaveConfig(intent))
                {
                    _config = previous;
                    return StoreWriteOutcome.WriteFailed;
                }
            }
            OnConfigChanged?.Invoke();
            return StoreWriteOutcome.Saved;
        }

        /// <summary>
        /// Persists only the alert-window config without touching channel credentials — as far as
        /// the CALLER is concerned. The file is written whole, so on a damaged store this one-field
        /// update is the cheapest path to deleting every credential in it, and it is guarded
        /// identically. Maintenance mode is one click on a page an operator uses in an incident.
        /// </summary>
        public StoreWriteOutcome UpdateAlertWindows(AlertWindowConfig windows,
                                                    StoreWriteIntent intent = StoreWriteIntent.FromLoadedStore)
        {
            lock (_lock)
            {
                if (ConfigFileHelper.WouldOverwriteUnreadStore(_load, intent))
                    return RefuseWrite();

                var previous = _config.AlertWindows;
                _config.AlertWindows = windows;
                if (!SaveConfig(intent))
                {
                    _config.AlertWindows = previous;
                    return StoreWriteOutcome.WriteFailed;
                }
            }
            OnConfigChanged?.Invoke();
            return StoreWriteOutcome.Saved;
        }

        /// <summary>Logs the refusal, naming what it protected, and hands back the outcome. Caller holds the lock.</summary>
        private StoreWriteOutcome RefuseWrite()
        {
            _logger.LogWarning(
                "[Notifications] Refused to write {Path}: it exists and did not load ({Outcome}), so the channel "
                + "configuration in memory is built-in defaults — every channel off, every credential blank — and "
                + "writing it would delete this install's SMTP, Teams, Slack, webhook, PagerDuty, ServiceNow and "
                + "WhatsApp settings. The file is unchanged. {Recovery}",
                _configFilePath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
            return StoreWriteOutcome.RefusedStoreUnreadable;
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
                // The REPORTING overload. "Nobody has configured alerting" and "this install's
                // alerting configuration did not parse" are the same object out of the plain one,
                // and they are opposite facts about whether the silence below is expected.
                _config = ConfigFileHelper.Load<NotificationChannelConfig>(_configFilePath, null, out _load, out _quarantine);
                _logger.LogInformation("Loaded notification channel config: SMTP={SmtpEnabled}, Teams={TeamsEnabled}, Slack={SlackEnabled}, Webhook={WebhookEnabled}, PagerDuty={PagerDutyEnabled}, ServiceNow={ServiceNowEnabled}, WhatsApp={WhatsAppEnabled} (store: {Outcome})",
                    _config.Smtp.Enabled, _config.TeamsWebhook.Enabled, _config.Slack.Enabled,
                    _config.Webhook.Enabled, _config.PagerDuty.Enabled, _config.ServiceNow.Enabled, _config.WhatsApp.Enabled, _load);

                if (_load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty)
                    _logger.LogError(
                        "[Notifications] {Path} exists and did not load ({Outcome}). EVERY alert channel is off for "
                        + "this process and no notification will be delivered, whatever the file says — this install "
                        + "is silently not alerting. Writes to the file are refused until it is repaired. {Recovery}",
                        _configFilePath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load notification channel config");
                _config = new();
                _load = ConfigLoadOutcome.Unreadable;
            }
        }

        /// <summary>
        /// The write chokepoint. Intent is REQUIRED so a future mutator cannot reach the disk
        /// without saying which kind of write it is — it will not compile. Returns false when
        /// nothing reached the file, for either reason.
        /// </summary>
        private bool SaveConfig(StoreWriteIntent intent)
        {
            // Second statement of the same guard, on purpose: the mutators check it before touching
            // _config so a refusal leaves the service coherent, and this one is the guarantee that
            // holds for whatever calls SaveConfig next, written by whoever writes it.
            if (ConfigFileHelper.WouldOverwriteUnreadStore(_load, intent))
            {
                _logger.LogWarning("[Notifications] Refused to write {Path} over an unreadable store ({Outcome})",
                    _configFilePath, _load);
                return false;
            }

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

                // The file on disk is now this object, written whole — so an operator who repaired a
                // damaged store by re-entering the credentials gets writes back without a restart.
                // NOT set when the write throws: the damage is still there and must keep refusing.
                _load = ConfigLoadOutcome.Loaded;
                _quarantine = null;
                _logger.LogInformation("Saved notification channel config");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save notification channel config");
                return false;
            }
        }

        // ──────────────── Dispatch ────────────────

        /// <summary>
        /// THE ROUTING RULE, and it is one line of semantics that must not be "improved":
        /// <b>a blank pick means EVERY channel, not NO channel.</b>
        ///
        /// <para>The property this reads is documented on
        /// <c>AlertDefinition.EscalationChannel</c> (Data/Models/AlertConfiguration.cs:367) as
        /// <i>"Empty = same as primary"</i>, and all 80 shipped alerts carry <c>null</c> there
        /// (verified by parsing Config/alert-definitions.json). So the null/empty/whitespace case is
        /// the path EVERY existing definition takes. Reading it as "no channel selected" would route
        /// every escalation on every alert to nowhere, invisibly - a worse defect than the unwired
        /// picker it replaced. Pinned by
        /// <c>EscalationChannelRoutingTests.NullEmptyAndWhitespace_stillFanOutToEveryChannel_theFailClosedTrap</c>.</para>
        ///
        /// <para>Matching is case-insensitive on purpose: the picker stores lowercase keys
        /// ("pagerduty") and the dispatch results carry display names ("PagerDuty"). See
        /// <see cref="ChannelNames"/> for the census that keeps those two ends in agreement.</para>
        /// </summary>
        internal static bool ChannelAdmittedByRouting(string channelName, string? restrictToChannelId)
            => string.IsNullOrWhiteSpace(restrictToChannelId)
               || string.Equals(channelName, restrictToChannelId.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Sends an alert notification to all enabled channels that meet the severity threshold.
        /// Never throws — each channel's Send*Async catches its own exceptions and returns a
        /// <see cref="ChannelDispatchResult"/>. #68 LEG 1: unlike the old fire-and-forget version,
        /// every outcome (success AND failure) is now recorded to <see cref="DeliveryHealth"/> and a
        /// consolidated failure is logged at Error (not buried at per-channel Warning/Debug), so a
        /// channel that silently broke is visible instead of presenting green.
        ///
        /// <para><b>special-alerts-escalation-parity (2026-09-11).</b> <paramref name="restrictToChannelId"/>
        /// is how an alert's <c>EscalationChannel</c> pick finally reaches this fan-out; until this
        /// lane nothing in the product read that property at all, so the picker on the Alerts page
        /// was inert for all 80 alerts. <b>null / empty / whitespace = every admitted channel</b>
        /// (today's behaviour, and what every shipped definition asks for); a non-empty value = that
        /// channel only. See <see cref="ChannelAdmittedByRouting"/>.</para>
        /// </summary>
        public async Task<IReadOnlyList<ChannelDispatchResult>> DispatchAsync(
            AlertNotification notification, string? restrictToChannelId = null)
        {
            var tasks = new List<Task<ChannelDispatchResult>>();

            // A pick that names no channel we have sends NOTHING. That is the literal reading of
            // "that channel only" and it is deliberately not softened - but it must never be
            // SILENT, because an escalation that reaches nobody is exactly the failure this
            // project keeps finding. Logged once per dispatch, at Warning, naming what it looked
            // for and what exists.
            if (!string.IsNullOrWhiteSpace(restrictToChannelId)
                && !ChannelNames.All.Any(n =>
                        string.Equals(n, restrictToChannelId.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(
                    "Alert routing: channel '{RequestedChannel}' was selected for {AlertName} but matches no known channel, so NOTHING will be sent for it. Known channels: {KnownChannels}",
                    restrictToChannelId, notification.AlertName, string.Join(", ", ChannelNames.All));
            }

            if (_config.Smtp.Enabled && notification.SendEmail && MeetsSeverity(notification.Severity, _config.Smtp.MinimumSeverity)
                && ChannelAdmittedByRouting(ChannelNames.Smtp, restrictToChannelId))
            {
                tasks.Add(SendEmailAsync(notification));
            }

            if (_config.TeamsWebhook.Enabled && MeetsSeverity(notification.Severity, _config.TeamsWebhook.MinimumSeverity)
                && ChannelAdmittedByRouting(ChannelNames.Teams, restrictToChannelId))
            {
                tasks.Add(SendTeamsAsync(notification));
            }

            if (_config.Slack.Enabled && MeetsSeverity(notification.Severity, _config.Slack.MinimumSeverity)
                && ChannelAdmittedByRouting(ChannelNames.Slack, restrictToChannelId))
            {
                tasks.Add(SendSlackAsync(notification));
            }

            if (_config.Webhook.Enabled && MeetsSeverity(notification.Severity, _config.Webhook.MinimumSeverity)
                && ChannelAdmittedByRouting(ChannelNames.Webhook, restrictToChannelId))
            {
                tasks.Add(SendWebhookAsync(notification));
            }

            if (_config.PagerDuty.Enabled && MeetsSeverity(notification.Severity, _config.PagerDuty.MinimumSeverity)
                && ChannelAdmittedByRouting(ChannelNames.PagerDuty, restrictToChannelId))
            {
                tasks.Add(SendPagerDutyAsync(notification));
            }

            if (_config.ServiceNow.Enabled && MeetsSeverity(notification.Severity, _config.ServiceNow.MinimumSeverity)
                && ChannelAdmittedByRouting(ChannelNames.ServiceNow, restrictToChannelId))
            {
                tasks.Add(SendServiceNowAsync(notification));
            }

            if (_config.WhatsApp.Enabled && MeetsSeverity(notification.Severity, _config.WhatsApp.MinimumSeverity)
                && ChannelAdmittedByRouting(ChannelNames.WhatsApp, restrictToChannelId))
            {
                tasks.Add(SendWhatsAppAsync(notification));
            }

            if (tasks.Count == 0)
                return Array.Empty<ChannelDispatchResult>();

            var results = await Task.WhenAll(tasks);

            foreach (var r in results)
                _deliveryHealth[r.Channel] = new ChannelDeliveryHealth(r.Channel, r.Success, r.Detail, DateTime.UtcNow);

            var failures = results.Where(r => !r.Success).ToList();
            if (failures.Count > 0)
            {
                _logger.LogError(
                    "Alert dispatch FAILED on {FailCount}/{Total} channel(s) for {AlertName}: {Details}",
                    failures.Count, results.Length, notification.AlertName,
                    string.Join("; ", failures.Select(f => $"{f.Channel} — {f.Detail}")));
            }

            return results;
        }

        // ──────────────── SMTP Email ────────────────

        private Task<ChannelDispatchResult> SendEmailAsync(AlertNotification notification)
            => SendEmailCoreAsync(notification);

        private async Task<ChannelDispatchResult> SendEmailCoreAsync(AlertNotification notification)
        {
            const string channel = ChannelNames.Smtp;
            try
            {
                var smtp = _config.Smtp;

                // N-4, the sibling shape. This counted the RAW list while both transports below
                // filter blanks out of it, so ToAddresses = ["  ", ""] passed the guard and then
                // connected to the mail host to send a message with no recipients at all.
                var recipients = smtp.ToAddresses
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim())
                    .ToList();

                if (recipients.Count == 0)
                {
                    _logger.LogWarning("No usable SMTP recipient addresses configured, skipping email notification");
                    return new ChannelDispatchResult(channel, false,
                        "No usable recipient addresses configured (every entry is blank).");
                }

                if (smtp.UseOAuth2)
                    await SendEmailViaGraphAsync(smtp, notification);
                else
                    await SendEmailViaSmtpAsync(smtp, notification);

                _logger.LogInformation("Email alert sent: {AlertName} to {Recipients}",
                    notification.AlertName, string.Join(", ", recipients));
                return new ChannelDispatchResult(channel, true, $"Sent to {string.Join(", ", recipients)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email alert: {AlertName}", notification.AlertName);
                return new ChannelDispatchResult(channel, false, ex.Message);
            }
        }

        // ── Basic Auth SMTP (legacy) ──────────────────────────────────────────

        private async Task SendEmailViaSmtpAsync(SmtpChannelConfig smtp, AlertNotification notification)
        {
            if (string.IsNullOrEmpty(smtp.Host))
                throw new InvalidOperationException("SMTP host is not configured.");

            var password = DecryptIfNeeded(smtp.Password);
            var fromAddress = string.IsNullOrEmpty(smtp.FromAddress) ? smtp.Username : smtp.FromAddress;

            var emailTemplate = _templates.Config.Email;

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(smtp.FromName, fromAddress));
            foreach (var to in smtp.ToAddresses.Where(a => !string.IsNullOrWhiteSpace(a)))
                message.To.Add(MailboxAddress.Parse(to.Trim()));
            if (!string.IsNullOrWhiteSpace(smtp.ReplyToAddress))
                message.ReplyTo.Add(MailboxAddress.Parse(smtp.ReplyToAddress.Trim()));
            message.Subject = AlertTemplateService.Render(emailTemplate.Subject, notification);
            message.Body = new TextPart(MimeKit.Text.TextFormat.Html)
            {
                Text = AlertTemplateService.Render(emailTemplate.Body, notification)
            };

            using var client = new SmtpClient();
            var secureOption = smtp.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            await client.ConnectAsync(smtp.Host, smtp.Port, secureOption);
            if (!string.IsNullOrEmpty(smtp.Username) && !string.IsNullOrEmpty(password))
                await client.AuthenticateAsync(smtp.Username, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }

        // ── OAuth2 via Microsoft Graph API ────────────────────────────────────
        // Requires an Azure AD app registration with Mail.Send application permission.
        // No extra NuGet — uses HttpClient already in the service.

        private async Task SendEmailViaGraphAsync(SmtpChannelConfig smtp, AlertNotification notification)
        {
            if (string.IsNullOrEmpty(smtp.TenantId) || string.IsNullOrEmpty(smtp.ClientId))
                throw new InvalidOperationException("OAuth2 requires Tenant ID and Client ID.");

            var clientSecret = DecryptIfNeeded(smtp.ClientSecret);
            if (string.IsNullOrEmpty(clientSecret))
                throw new InvalidOperationException("OAuth2 client secret is not configured.");

            var fromAddress = string.IsNullOrEmpty(smtp.FromAddress) ? smtp.Username : smtp.FromAddress;
            if (string.IsNullOrEmpty(fromAddress))
                throw new InvalidOperationException("From address is required for OAuth2 send.");

            // ── Step 1: acquire bearer token via client credentials flow ──────
            var token = await AcquireGraphTokenAsync(smtp.TenantId, smtp.ClientId, clientSecret);

            // ── Step 2: build Graph sendMail payload ──────────────────────────
            var emailTemplate = _templates.Config.Email;
            var subject = AlertTemplateService.Render(emailTemplate.Subject, notification);
            var body = AlertTemplateService.Render(emailTemplate.Body, notification);

            var toRecipients = smtp.ToAddresses
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => new { emailAddress = new { address = a.Trim() } })
                .ToArray();

            var payload = new
            {
                message = new
                {
                    subject,
                    body = new { contentType = "HTML", content = body },
                    toRecipients,
                    from = new { emailAddress = new { address = fromAddress, name = smtp.FromName } },
                    replyTo = string.IsNullOrWhiteSpace(smtp.ReplyToAddress)
                        ? null
                        : new[] { new { emailAddress = new { address = smtp.ReplyToAddress.Trim() } } }
                },
                saveToSentItems = false
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            // ── Step 3: POST to /users/{from}/sendMail ────────────────────────
            var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(fromAddress)}/sendMail";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Graph API returned {(int)response.StatusCode}: {error}");
            }
        }

        private async Task<string> AcquireGraphTokenAsync(string tenantId, string clientId, string clientSecret)
        {
            var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id",     clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("scope",         "https://graph.microsoft.com/.default"),
                new KeyValuePair<string, string>("grant_type",    "client_credentials"),
            });

            using var response = await _httpClient.PostAsync(tokenUrl, form);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Token request failed {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
                throw new InvalidOperationException($"No access_token in response: {body}");

            return tokenEl.GetString()!;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  MSP #5 — report email with a PDF attachment (SMTP + Graph)
        // ══════════════════════════════════════════════════════════════════════
        // The alert path is body-only; a scheduled report ships the rendered PDF as an
        // attachment. Row-authorized delivery for TaskType.Report. Two transports, two
        // shapes: MimeKit BodyBuilder for SMTP, a base64 fileAttachment for Graph.

        /// <summary>Build the SMTP report message (HTML body + PDF attachment via BodyBuilder).
        /// Pure/no-send so it is unit-testable — the attachment rides <see cref="MimeMessage.Body"/>.</summary>
        internal static MimeMessage BuildReportEmail(
            SmtpChannelConfig smtp, IReadOnlyList<string> recipients,
            string subject, string bodyHtml, string attachmentPath)
        {
            var fromAddress = string.IsNullOrEmpty(smtp.FromAddress) ? smtp.Username : smtp.FromAddress;

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(smtp.FromName, fromAddress));
            foreach (var to in recipients.Where(a => !string.IsNullOrWhiteSpace(a)))
                message.To.Add(MailboxAddress.Parse(to.Trim()));
            if (!string.IsNullOrWhiteSpace(smtp.ReplyToAddress))
                message.ReplyTo.Add(MailboxAddress.Parse(smtp.ReplyToAddress.Trim()));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = bodyHtml };
            if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
                builder.Attachments.Add(attachmentPath);
            message.Body = builder.ToMessageBody();
            return message;
        }

        /// <summary>Build the Microsoft Graph sendMail JSON for a report email — the message carries an
        /// <c>attachments[]</c> array with a base64 <c>#microsoft.graph.fileAttachment</c>. Pure/no-send
        /// so it is unit-testable.</summary>
        internal static string BuildGraphReportJson(
            SmtpChannelConfig smtp, IReadOnlyList<string> recipients,
            string subject, string bodyHtml, string attachmentPath)
        {
            var fromAddress = string.IsNullOrEmpty(smtp.FromAddress) ? smtp.Username : smtp.FromAddress;

            var toRecipients = recipients
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => new { emailAddress = new { address = a.Trim() } })
                .ToArray();

            var attachments = new List<Dictionary<string, object>>();
            if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
            {
                var bytes = File.ReadAllBytes(attachmentPath);
                attachments.Add(new Dictionary<string, object>
                {
                    // C# identifiers can't start with '@', so a Dictionary carries the OData type key.
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"]        = Path.GetFileName(attachmentPath),
                    ["contentType"] = "application/pdf",
                    ["contentBytes"] = Convert.ToBase64String(bytes),
                });
            }

            var message = new Dictionary<string, object?>
            {
                ["subject"]      = subject,
                ["body"]         = new { contentType = "HTML", content = bodyHtml },
                ["toRecipients"] = toRecipients,
                ["from"]         = new { emailAddress = new { address = fromAddress, name = smtp.FromName } },
                ["attachments"]  = attachments,
            };
            if (!string.IsNullOrWhiteSpace(smtp.ReplyToAddress))
                message["replyTo"] = new[] { new { emailAddress = new { address = smtp.ReplyToAddress.Trim() } } };

            var payload = new Dictionary<string, object>
            {
                ["message"]         = message,
                ["saveToSentItems"] = false,
            };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>
        /// Sends a scheduled report as a PDF attachment via the configured email transport (SMTP or
        /// Graph OAuth2). Falls back to the SMTP channel's To-addresses when <paramref name="recipients"/>
        /// is empty. Never throws — returns a fixed, secret-free status. §4.5: only the exception TYPE is
        /// logged on failure (SMTP/Graph faults can embed credentials/hosts in the message).
        /// </summary>
        public async Task<(bool Success, string Message)> SendReportEmailAsync(
            IReadOnlyList<string> recipients, string subject, string bodyHtml, string attachmentPath)
        {
            var smtp = _config.Smtp;
            var toList = (recipients != null && recipients.Any(a => !string.IsNullOrWhiteSpace(a)))
                ? recipients
                : smtp.ToAddresses;

            if (!smtp.Enabled)
                return (false, "SMTP channel is not enabled.");
            if (toList == null || !toList.Any(a => !string.IsNullOrWhiteSpace(a)))
                return (false, "No report recipients configured.");
            if (string.IsNullOrEmpty(attachmentPath) || !File.Exists(attachmentPath))
                return (false, "Report attachment file not found.");

            var transport = smtp.UseOAuth2 ? "Graph" : "SMTP";
            var count = toList.Count(a => !string.IsNullOrWhiteSpace(a));
            try
            {
                if (smtp.UseOAuth2)
                    await SendReportViaGraphAsync(smtp, toList, subject, bodyHtml, attachmentPath);
                else
                    await SendReportViaSmtpAsync(smtp, toList, subject, bodyHtml, attachmentPath);

                _logger.LogInformation("Scheduled report email sent via {Transport} to {Count} recipient(s)",
                    transport, count);
                return (true, $"Report emailed to {count} recipient(s).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Scheduled report email failed via {Transport} ({ExType})",
                    transport, ex.GetType().Name);
                return (false, $"Report email failed via {transport}.");
            }
        }

        private async Task SendReportViaSmtpAsync(SmtpChannelConfig smtp, IReadOnlyList<string> recipients,
            string subject, string bodyHtml, string attachmentPath)
        {
            if (string.IsNullOrEmpty(smtp.Host))
                throw new InvalidOperationException("SMTP host is not configured.");

            var password = DecryptIfNeeded(smtp.Password);
            var message = BuildReportEmail(smtp, recipients, subject, bodyHtml, attachmentPath);

            using var client = new SmtpClient();
            var secureOption = smtp.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            await client.ConnectAsync(smtp.Host, smtp.Port, secureOption);
            if (!string.IsNullOrEmpty(smtp.Username) && !string.IsNullOrEmpty(password))
                await client.AuthenticateAsync(smtp.Username, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }

        private async Task SendReportViaGraphAsync(SmtpChannelConfig smtp, IReadOnlyList<string> recipients,
            string subject, string bodyHtml, string attachmentPath)
        {
            if (string.IsNullOrEmpty(smtp.TenantId) || string.IsNullOrEmpty(smtp.ClientId))
                throw new InvalidOperationException("OAuth2 requires Tenant ID and Client ID.");

            var clientSecret = DecryptIfNeeded(smtp.ClientSecret);
            if (string.IsNullOrEmpty(clientSecret))
                throw new InvalidOperationException("OAuth2 client secret is not configured.");

            var fromAddress = string.IsNullOrEmpty(smtp.FromAddress) ? smtp.Username : smtp.FromAddress;
            if (string.IsNullOrEmpty(fromAddress))
                throw new InvalidOperationException("From address is required for OAuth2 send.");

            var token = await AcquireGraphTokenAsync(smtp.TenantId, smtp.ClientId, clientSecret);
            var json = BuildGraphReportJson(smtp, recipients, subject, bodyHtml, attachmentPath);

            var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(fromAddress)}/sendMail";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Graph API returned {(int)response.StatusCode}: {error}");
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
            <tr><td style=""padding: 6px 0; color: #888;"">Current Value</td><td style=""padding: 6px 0; font-weight: bold;"">{notification.CurrentValueText}</td></tr>
            <tr><td style=""padding: 6px 0; color: #888;"">Threshold</td><td style=""padding: 6px 0;"">{WebUtility.HtmlEncode(notification.ThresholdText)}</td></tr>
            {(string.IsNullOrEmpty(notification.InstanceName) ? "" : $@"<tr><td style=""padding: 6px 0; color: #888;"">Instance</td><td style=""padding: 6px 0;"">{WebUtility.HtmlEncode(notification.InstanceName)}</td></tr>")}
            <tr><td style=""padding: 6px 0; color: #888;"">Time (UTC)</td><td style=""padding: 6px 0;"">{notification.TriggeredAt:yyyy-MM-dd HH:mm:ss}</td></tr>
            <tr><td style=""padding: 6px 0; color: #888;"">Machine</td><td style=""padding: 6px 0;"">{Environment.MachineName}</td></tr>
        </table>
        <div style=""margin-top: 16px; padding: 10px; background: #0d1117; border-radius: 4px; font-size: 13px;"">
            {WebUtility.HtmlEncode(notification.Message)}
        </div>
        <p style=""margin-top: 16px; font-size: 11px; color: #666;"">Sent by SQLTriage — {Environment.MachineName}</p>
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
            // N-4: the raw count again. An all-blank recipients box is not a configured channel.
            if (!smtp.ToAddresses.Any(a => !string.IsNullOrWhiteSpace(a)))
                return (false, "No usable recipient email addresses configured (every entry is blank).");

            var testNotification = new AlertNotification
            {
                AlertName = "SMTP Test",
                Metric = "test",
                // N-2 (2026-08-22): CurrentValue = 0 stood here, one line above the C3 comment
                // below explaining why the threshold must stay null. A connectivity test measures
                // no metric, so both are left unset and both print a phrase, not a number.
                ThresholdValue = null,   // a connectivity test crossed no threshold; saying 0.00 would invent one
                Severity = "info",
                Message = "This is a test notification from SQLTriage. If you received this email, SMTP is configured correctly.",
                InstanceName = Environment.MachineName
            };

            var result = await SendEmailCoreAsync(testNotification);
            // Gate finding 2026-08-23: this used to re-join the RAW ToAddresses list, so a blank
            // entry was printed as if it had been sent to. SendEmailCoreAsync's detail already
            // names the filtered recipients it actually addressed; surface that, not the raw box.
            return result.Success
                ? (true, $"Test email {result.Detail}")
                : (false, $"SMTP test failed: {result.Detail}");
        }

        // ──────────────── Teams Webhook ────────────────

        private async Task<ChannelDispatchResult> SendTeamsAsync(AlertNotification notification)
        {
            const string channel = ChannelNames.Teams;
            try
            {
                var webhookUrl = DecryptIfNeeded(_config.TeamsWebhook.WebhookUrl);
                if (string.IsNullOrEmpty(webhookUrl))
                {
                    _logger.LogWarning("Teams webhook URL not configured — skipping");
                    return new ChannelDispatchResult(channel, false, "Webhook URL not configured.");
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
                    return new ChannelDispatchResult(channel, true, "Sent.");
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Teams webhook returned {StatusCode}: {Body}",
                        (int)response.StatusCode, body);
                    return new ChannelDispatchResult(channel, false,
                        $"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Teams alert: {AlertName}", notification.AlertName);
                return new ChannelDispatchResult(channel, false, ex.Message);
            }
        }

        private static object[] BuildTeamsFacts(AlertNotification notification)
        {
            var facts = new List<object>
            {
                new { title = "Metric", value = notification.Metric },
                new { title = "Current Value", value = notification.CurrentValueText },
                new { title = "Threshold", value = notification.ThresholdText },
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

            var testNotification = new AlertNotification
            {
                AlertName = "Teams Webhook Test",
                Metric = "test",
                // N-2 (2026-08-22): CurrentValue = 0 stood here too. Nothing was measured.
                ThresholdValue = null,   // a connectivity test crossed no threshold; saying 0.00 would invent one
                Severity = "info",
                Message = "This is a test notification from SQLTriage. If you see this message, Teams webhook is configured correctly.",
                InstanceName = Environment.MachineName
            };

            var result = await SendTeamsAsync(testNotification);
            return result.Success
                ? (true, "Test message sent to Teams channel.")
                : (false, $"Teams webhook test failed: {result.Detail}");
        }

        // ──────────────── Slack ────────────────

        private async Task<ChannelDispatchResult> SendSlackAsync(AlertNotification notification)
        {
            const string channel = ChannelNames.Slack;
            try
            {
                var webhookUrl = DecryptIfNeeded(_config.Slack.WebhookUrl);
                if (string.IsNullOrEmpty(webhookUrl))
                {
                    _logger.LogWarning("Slack webhook URL not configured — skipping");
                    return new ChannelDispatchResult(channel, false, "Webhook URL not configured.");
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
                    ["username"] = string.IsNullOrEmpty(_config.Slack.Username) ? "SQLTriage" : _config.Slack.Username,
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
                                new { title = "Value", value = notification.CurrentValueText, @short = true },
                                new { title = "Threshold", value = notification.ThresholdText, @short = true },
                                new { title = "Machine", value = Environment.MachineName, @short = true }
                            },
                            ["footer"] = $"SQLTriage • {notification.TriggeredAt:yyyy-MM-dd HH:mm:ss} UTC",
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
                {
                    _logger.LogInformation("Slack alert sent: {AlertName}", notification.AlertName);
                    return new ChannelDispatchResult(channel, true, "Sent.");
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Slack webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                    return new ChannelDispatchResult(channel, false,
                        $"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Slack alert: {AlertName}", notification.AlertName);
                return new ChannelDispatchResult(channel, false, ex.Message);
            }
        }

        public async Task<(bool Success, string Message)> TestSlackAsync()
        {
            var webhookUrl = DecryptIfNeeded(_config.Slack.WebhookUrl);
            if (string.IsNullOrEmpty(webhookUrl))
                return (false, "Slack webhook URL is not configured.");

            var test = new AlertNotification
            {
                AlertName = "Slack Webhook Test",
                Metric = "test",
                Severity = "info",
                Message = "This is a test notification from SQLTriage. If you see this message, Slack is configured correctly.",
                InstanceName = Environment.MachineName
            };
            var result = await SendSlackAsync(test);
            return result.Success
                ? (true, "Test message sent to Slack channel.")
                : (false, $"Slack test failed: {result.Detail}");
        }

        // ──────────────── Generic Webhook ────────────────

        private async Task<ChannelDispatchResult> SendWebhookAsync(AlertNotification notification)
        {
            const string channel = ChannelNames.Webhook;
            try
            {
                var url = DecryptIfNeeded(_config.Webhook.Url);
                if (string.IsNullOrEmpty(url))
                {
                    _logger.LogWarning("Webhook URL not configured — skipping");
                    return new ChannelDispatchResult(channel, false, "Webhook URL not configured.");
                }

                var payload = new
                {
                    alertName = notification.AlertName,
                    metric = notification.Metric,
                    severity = notification.Severity,
                    currentValue = notification.CurrentValue,          // null, never 0, when nothing was measured
                    thresholdValue = notification.ThresholdValue,   // null, never 0, when no threshold fired
                    thresholdBasis = notification.BasisKind,
                    thresholdText = notification.ThresholdText,
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
                {
                    _logger.LogInformation("Webhook alert sent: {AlertName} to {Url}", notification.AlertName, url);
                    return new ChannelDispatchResult(channel, true, $"Sent to {url}");
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                    return new ChannelDispatchResult(channel, false,
                        $"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send webhook alert: {AlertName}", notification.AlertName);
                return new ChannelDispatchResult(channel, false, ex.Message);
            }
        }

        public async Task<(bool Success, string Message)> TestWebhookAsync()
        {
            var url = DecryptIfNeeded(_config.Webhook.Url);
            if (string.IsNullOrEmpty(url))
                return (false, "Webhook URL is not configured.");

            var test = new AlertNotification
            {
                AlertName = "Webhook Test",
                Metric = "test",
                Severity = "info",
                Message = "Test notification from SQLTriage.",
                InstanceName = Environment.MachineName
            };
            var result = await SendWebhookAsync(test);
            return result.Success
                ? (true, $"Test payload sent to {url}")
                : (false, $"Webhook test failed: {result.Detail}");
        }

        // ──────────────── PagerDuty ────────────────

        private async Task<ChannelDispatchResult> SendPagerDutyAsync(AlertNotification notification)
        {
            const string channel = ChannelNames.PagerDuty;
            try
            {
                var routingKey = DecryptIfNeeded(_config.PagerDuty.RoutingKey);
                if (string.IsNullOrEmpty(routingKey))
                {
                    _logger.LogWarning("PagerDuty routing key not configured — skipping");
                    return new ChannelDispatchResult(channel, false, "Routing key not configured.");
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
                        summary = $"[{notification.Severity.ToUpper()}] {notification.AlertName}: {notification.Metric} = {notification.CurrentValueText} (threshold: {notification.ThresholdText})",
                        source = Environment.MachineName,
                        severity = pdSeverity,
                        component = notification.InstanceName ?? Environment.MachineName,
                        group = "sql-health-assessment",
                        custom_details = new
                        {
                            metric = notification.Metric,
                            current_value = notification.CurrentValue,   // null, never 0, when nothing was measured
                            threshold_value = notification.ThresholdValue,   // null, never 0
                            threshold_basis = notification.BasisKind,
                            threshold_text = notification.ThresholdText,
                            message = notification.Message,
                            triggered_at_utc = notification.TriggeredAt.ToString("o")
                        }
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("https://events.pagerduty.com/v2/enqueue", content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("PagerDuty alert sent: {AlertName}", notification.AlertName);
                    return new ChannelDispatchResult(channel, true, "Sent.");
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("PagerDuty returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                    return new ChannelDispatchResult(channel, false,
                        $"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send PagerDuty alert: {AlertName}", notification.AlertName);
                return new ChannelDispatchResult(channel, false, ex.Message);
            }
        }

        public async Task<(bool Success, string Message)> TestPagerDutyAsync()
        {
            var routingKey = DecryptIfNeeded(_config.PagerDuty.RoutingKey);
            if (string.IsNullOrEmpty(routingKey))
                return (false, "PagerDuty routing key is not configured.");

            var test = new AlertNotification
            {
                AlertName = "PagerDuty Integration Test",
                Metric = "test",
                Severity = "info",
                Message = "Test notification from SQLTriage. No action required.",
                InstanceName = Environment.MachineName
            };
            var result = await SendPagerDutyAsync(test);
            return result.Success
                ? (true, "Test event sent to PagerDuty.")
                : (false, $"PagerDuty test failed: {result.Detail}");
        }

        // ──────────────── ServiceNow ────────────────

        private async Task<ChannelDispatchResult> SendServiceNowAsync(AlertNotification notification)
        {
            const string channel = ChannelNames.ServiceNow;
            try
            {
                var cfg = _config.ServiceNow;
                var instanceUrl = cfg.InstanceUrl.TrimEnd('/');
                var username = DecryptIfNeeded(cfg.Username);
                var password = DecryptIfNeeded(cfg.Password);

                if (string.IsNullOrEmpty(instanceUrl) || string.IsNullOrEmpty(username))
                {
                    _logger.LogWarning("ServiceNow not fully configured — skipping");
                    return new ChannelDispatchResult(channel, false, "Instance URL or username not configured.");
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
                    ["short_description"] = $"[{notification.Severity.ToUpper()}] {notification.AlertName}: {notification.Metric} = {notification.CurrentValueText}",
                    ["description"] = $"{notification.Message}\n\nMetric: {notification.Metric}\nCurrent Value: {notification.CurrentValueText}\nThreshold: {notification.ThresholdText}\nInstance: {notification.InstanceName}\nMachine: {Environment.MachineName}\nTime (UTC): {notification.TriggeredAt:yyyy-MM-dd HH:mm:ss}",
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
                    return new ChannelDispatchResult(channel, true, "Incident created.");
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("ServiceNow returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                    return new ChannelDispatchResult(channel, false,
                        $"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ServiceNow incident: {AlertName}", notification.AlertName);
                return new ChannelDispatchResult(channel, false, ex.Message);
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
                    // N-3 (2026-08-22). This said "Table 'x' is accessible", which a person reads
                    // as "the Test button proved my alerts will land". It proved no such thing:
                    // the probe above is an HTTP GET of one row, and SendServiceNowAsync POSTs an
                    // incident to the same table. A read-only ACL passes this test and fails every
                    // alert. The sentence now claims exactly the verb that was exercised. A true
                    // create probe would have to create and close a canary incident in the
                    // client's ServiceNow instance, which is a side effect nobody asked for and
                    // is Adrian's ruling to make, not this method's.
                    return (true,
                        $"Read table '{table}' on {instanceUrl} with an HTTP GET, using these credentials. "
                        + "That proves READ access to the table. It does not prove this account can CREATE "
                        + "incidents: alerts POST to the same table, and a read-only ACL will fail there.");
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

        private async Task<ChannelDispatchResult> SendWhatsAppAsync(AlertNotification notification)
        {
            const string channel = ChannelNames.WhatsApp;
            try
            {
                var cfg = _config.WhatsApp;
                var accessToken = DecryptIfNeeded(cfg.AccessToken);

                if (string.IsNullOrEmpty(cfg.PhoneNumberId) || string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogWarning("WhatsApp not fully configured — skipping");
                    return new ChannelDispatchResult(channel, false, "Phone Number ID or Access Token not configured.");
                }

                // N-4 (2026-08-22). This guard used to count the RAW list while the loop below
                // filtered blanks out of it, so RecipientNumbers = ["  ", ""] passed the guard,
                // sent nothing, collected no failures, and returned SUCCESS with the detail
                // "Sent to 0 recipient(s)." The one line an operator reads after a typo in the
                // recipients box said the alert had been delivered. Filter first, then guard on
                // what is actually sendable.
                var recipients = cfg.RecipientNumbers
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .ToList();

                if (recipients.Count == 0)
                {
                    _logger.LogWarning("No usable WhatsApp recipient numbers configured, skipping");
                    return new ChannelDispatchResult(channel, false,
                        "No usable recipient numbers configured (every entry is blank).");
                }

                var url = $"https://graph.facebook.com/v21.0/{cfg.PhoneNumberId}/messages";
                var failures = new List<string>();
                var sentCount = 0;

                foreach (var recipient in recipients)
                {
                    try
                    {
                        var payload = BuildWhatsAppPayload(cfg, notification, recipient);
                        var json = JsonSerializer.Serialize(payload);

                        using var request = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                        var response = await _httpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            sentCount++;
                            _logger.LogInformation("WhatsApp alert sent: {AlertName} to {Recipient}",
                                notification.AlertName, recipient);
                        }
                        else
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            _logger.LogWarning("WhatsApp API returned {StatusCode} for {Recipient}: {Body}",
                                (int)response.StatusCode, recipient, body);
                            failures.Add($"{recipient}: HTTP {(int)response.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send WhatsApp alert to {Recipient}", recipient);
                        failures.Add($"{recipient}: {ex.Message}");
                    }
                }

                // "no failures" is not "delivered". Success is a POSITIVE delivery count and
                // nothing else (verify-cardinality-not-just-presence).
                if (failures.Count == 0 && sentCount > 0)
                    return new ChannelDispatchResult(channel, true, $"Sent to {sentCount} recipient(s).");

                if (failures.Count == 0)
                    return new ChannelDispatchResult(channel, false,
                        "No message was sent and no error was reported. Nothing was delivered.");

                return new ChannelDispatchResult(channel, false,
                    $"{sentCount} sent, {failures.Count} failed — {string.Join("; ", failures)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WhatsApp alert: {AlertName}", notification.AlertName);
                return new ChannelDispatchResult(channel, false, ex.Message);
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
                                    new { type = "text", text = notification.CurrentValueText },
                                    new { type = "text", text = notification.ThresholdText },
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
                           $"Value: {notification.CurrentValueText} (threshold: {notification.ThresholdText})\n" +
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
            // N-4: counting the raw list here let an all-blank recipients box reach a send that
            // delivered nothing and reported success.
            if (!cfg.RecipientNumbers.Any(n => !string.IsNullOrWhiteSpace(n)))
                return (false, "No usable recipient phone numbers configured (every entry is blank).");

            var test = new AlertNotification
            {
                AlertName = "WhatsApp Test",
                Metric = "test",
                Severity = "info",
                Message = "This is a test notification from SQLTriage. If you received this message, WhatsApp is configured correctly.",
                InstanceName = Environment.MachineName
            };
            var result = await SendWhatsAppAsync(test);
            return result.Success
                ? (true, result.Detail)
                : (false, $"WhatsApp test failed: {result.Detail}");
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
