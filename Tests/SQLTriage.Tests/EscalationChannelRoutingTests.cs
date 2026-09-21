/* In the name of God, the Merciful, the Compassionate */

// -- special-alerts-escalation-parity (2026-09-11): EscalationChannel, wired ---------------------
//
// WHAT THIS FILE PINS. Until this lane AlertDefinition.EscalationChannel had exactly TWO references
// in the whole tree: its own declaration (Data/Models/AlertConfiguration.cs:333) and the <select>
// that binds it (Pages/Alerts.razor:877). Nothing read it. The Alerts page therefore offered every
// one of the 80 shipped alerts an escalation-channel picker the dispatcher could not honour -
// invariant 2 of this lane, "the engine must never offer an operator a control it cannot honour".
// Adrian ruled WIRING, not removal (2026-09-10 21:15, widget).
//
// THE FAIL-CLOSED TRAP, WHICH IS THE ENTIRE RISK OF THE CHANGE. The property's own documented
// semantics are "Empty = same as primary". escalationChannel ships NULL on all 80 seeded alerts.
// A naive wiring that reads null as "no channel selected" would route EVERY escalation on EVERY
// alert to nowhere, and would do it silently - a strictly worse defect than the inert picker, and
// invisible because nothing escalates today so nobody would notice for months. So the null case is
// pinned first, hardest, and at both layers.
//
// TWO LAYERS ON PURPOSE, because they can fail independently:
//   LAYER 1, the predicate: NotificationChannelService.DispatchAsync's fan-out honours the
//     restriction. Driven through the real DispatchAsync with all seven channels enabled against a
//     capturing HttpClient - the same harness shape as ChannelPayloadReachCensusTests, so nothing
//     leaves the box.
//   LAYER 2, the wiring: the ENGINE actually hands alert.EscalationChannel to that fan-out. A
//     perfect predicate nobody calls with the right argument is exactly this lane's original
//     defect wearing a different hat, so layer 2 drives a real special-path escalation through
//     AlertEvaluationService and reads what came out of the socket.
//
// NON-VACUITY. Every count assertion here has a control that produces a DIFFERENT count on the same
// harness (memory: a-control-that-cannot-reproduce-cannot-refute). Six for a blank pick, one for a
// named pick, zero for an unknown pick - all three from one instrument, in one class.
//
// SAFETY. Every channel points at an .invalid hostname AND the service's HttpClient is repointed at
// a capturing handler that answers before DNS is consulted, so no request can reach a real
// endpoint. SMTP would be the one channel that could bypass an HttpClient, and it is configured
// UseOAuth2 (Graph sendMail over the same HttpClient) - and an escalation never sets SendEmail
// anyway, which the first test measures rather than assumes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;

using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace SQLTriage.Tests
{
    public sealed class EscalationChannelRoutingTests : IDisposable
    {
        /// <summary>The six an escalation may reach. SMTP is absent BY MEASUREMENT, not by list:
        /// BuildEscalationNotification never sets SendEmail and DispatchAsync gates SMTP on it.</summary>
        private static readonly string[] SixEscalationChannels =
            { "Teams", "Slack", "Webhook", "PagerDuty", "ServiceNow", "WhatsApp" };

        private readonly string _dir;
        private readonly CapturingHandler _handler = new();
        private readonly CapturingLogger<NotificationChannelService> _channelLog = new();
        private readonly NotificationChannelService _channels;

        public EscalationChannelRoutingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "esc-routing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance,
                Path.Combine(_dir, "alert-templates.json"));
            _channels = new NotificationChannelService(_channelLog, templates);
            Repoint(_channels, "_configFilePath", Path.Combine(_dir, "notification-channels.json"));
            Invoke(_channels, "LoadConfig");
            Repoint(_channels, "_httpClient", new HttpClient(_handler));

            Assert.Equal(StoreWriteOutcome.Saved, _channels.UpdateConfig(AllSevenChannels()));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // -- LAYER 0: the instrument, proved before anything is claimed from it ------

        /// <summary>
        /// THE INSTRUMENT CHECK, and the shape of every escalation this file sends. With every
        /// channel enabled and no restriction at all, an escalation notification selects exactly
        /// SIX channels and SMTP is not among them - measured here, not taken from the brief.
        ///
        /// <para>This runs first for the same reason builder 1's preflight does: an absence is
        /// evidence only once the instrument is proved able to display a positive. If this harness
        /// could not dispatch, every "routed to nowhere" claim below would be vacuous.</para>
        /// </summary>
        [Fact]
        public async Task Instrument_anUnrestrictedEscalationSelectsExactlySixChannelsAndNeverSmtp()
        {
            var results = await _channels.DispatchAsync(Escalation());

            Assert.Equal(6, results.Count);
            Assert.Equal(SixEscalationChannels.OrderBy(x => x, StringComparer.Ordinal),
                         results.Select(r => r.Channel).OrderBy(x => x, StringComparer.Ordinal));
            Assert.DoesNotContain(results, r => r.Channel == "SMTP");

            // And SMTP's absence really is the SendEmail gate, not a disabled channel: the same
            // config sends SEVEN when the notification asks for email.
            var withEmail = Escalation();
            withEmail.SendEmail = true;
            Assert.Equal(7, (await _channels.DispatchAsync(withEmail)).Count);
        }

        // -- LAYER 1, PIN 4: THE FAIL-CLOSED TRAP -----------------------------------

        /// <summary>
        /// PIN 4. <b>Blank means EVERY channel, never none.</b> null, "", "   " and a tab must all
        /// leave the fan-out exactly as it was before this lane, because that is the path all 80
        /// shipped alerts take - every one of them carries <c>escalationChannel: null</c>.
        ///
        /// <para>RED without the change in the sense that matters: the naive wiring this test
        /// exists to forbid returns 0 here, and this test then reads 0 != 6 and names the trap.
        /// Verified by mutation - see the lane report.</para>
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public async Task NullEmptyAndWhitespace_stillFanOutToEveryChannel_theFailClosedTrap(string? pick)
        {
            var results = await _channels.DispatchAsync(Escalation(), pick);

            Assert.Equal(6, results.Count);
            Assert.Equal(SixEscalationChannels.OrderBy(x => x, StringComparer.Ordinal),
                         results.Select(r => r.Channel).OrderBy(x => x, StringComparer.Ordinal));

            // Blank is not "unknown": it must not warn either, or every one of the 80 shipped
            // alerts would log a spurious routing warning on every escalation.
            Assert.DoesNotContain(_channelLog.At(LogLevel.Warning),
                t => t.Contains("Alert routing:", StringComparison.Ordinal));
        }

        // -- LAYER 1, PIN 5: a named pick routes to that channel ONLY ---------------

        /// <summary>
        /// PIN 5. A non-empty pick selects that channel and nothing else. Every one of the six is
        /// exercised, so this cannot pass on one lucky branch of a seven-way predicate.
        /// </summary>
        [Theory]
        [InlineData("teams", "Teams")]
        [InlineData("slack", "Slack")]
        [InlineData("webhook", "Webhook")]
        [InlineData("pagerduty", "PagerDuty")]
        [InlineData("servicenow", "ServiceNow")]
        [InlineData("whatsapp", "WhatsApp")]
        public async Task ANonEmptyChannel_routesToThatChannelOnly(string pick, string expected)
        {
            var results = await _channels.DispatchAsync(Escalation(), pick);

            var only = Assert.Single(results);
            Assert.Equal(expected, only.Channel);
        }

        /// <summary>
        /// The picker stores LOWERCASE keys ("pagerduty"); the dispatcher stamps DISPLAY names
        /// ("PagerDuty"). Those two ends agreeing is the only reason a pick resolves at all, so the
        /// case-insensitivity is pinned rather than left to a reader to notice. A surrounding-space
        /// value is accepted too - a hand-edited alert-definitions.json is a supported way to set
        /// this, and " pagerduty " routing to nowhere would be silent.
        /// </summary>
        [Theory]
        [InlineData("PagerDuty")]
        [InlineData("pagerduty")]
        [InlineData("PAGERDUTY")]
        [InlineData("  pagerduty  ")]
        public async Task AChannelPickIsMatchedCaseInsensitivelyAndTrimmed_becauseThePickerStoresLowercaseKeys(string pick)
        {
            var only = Assert.Single(await _channels.DispatchAsync(Escalation(), pick));
            Assert.Equal("PagerDuty", only.Channel);
        }

        /// <summary>
        /// A pick naming no channel we have sends NOTHING - that is the literal reading of "that
        /// channel only" and it is deliberately not softened. But it must be LOUD: the whole class
        /// of defect this lane exists to close is an escalation that reaches nobody in silence. The
        /// zero here is only acceptable because the Warning beside it is asserted in the same test.
        /// </summary>
        [Fact]
        public async Task AnUnknownChannel_sendsNothingAndSaysSoAtWarning_neverSilently()
        {
            var results = await _channels.DispatchAsync(Escalation(), "pagerduty-eu");

            Assert.Empty(results);

            var warning = Assert.Single(
                _channelLog.At(LogLevel.Warning).Where(t => t.Contains("Alert routing:", StringComparison.Ordinal)));
            Assert.Contains("pagerduty-eu", warning, StringComparison.Ordinal);
            Assert.Contains("PagerDuty", warning, StringComparison.Ordinal);   // it names what DOES exist
        }

        // -- THE CENSUS: every picker key must select exactly one channel -----------

        /// <summary>
        /// THE CENSUS, derived FROM THE SOURCE and not from a count. The option values the operator
        /// picker offers are read out of <c>Pages/Alerts.razor</c>; each one is then DISPATCHED WITH
        /// and must select exactly one channel. A key that matches nothing routes to nowhere,
        /// silently, on whatever alert an operator picks it for - which is the defect this whole
        /// lane is about, one layer down.
        ///
        /// <para>Note what is enumerated: the picker's real option values, and the live behaviour of
        /// the real fan-out. Not a regex over the dispatcher, which would measure the pattern rather
        /// than the code (Generation-discipline rule 2).</para>
        ///
        /// <para>NON-VACUITY is a NAMED guard, not a number: the parse must find "smtp" and
        /// "pagerduty" among the keys, and must find at least seven of them, and prints what it did
        /// find if not. A regex that stops matching fails loudly here instead of passing empty.</para>
        /// </summary>
        [Fact]
        public async Task EveryChannelIdThePickerOffers_selectsExactlyOneChannel()
        {
            var keys = PickerChannelKeys();

            Assert.True(keys.Count >= 7,
                "the picker parse found only [" + string.Join(", ", keys) +
                "] - Pages/Alerts.razor's _enabledChannels block has moved and this census is measuring nothing");
            Assert.Contains("smtp", keys);
            Assert.Contains("pagerduty", keys);

            var orphans = new List<string>();
            foreach (var key in keys)
            {
                // SMTP is only ever selected for a notification that asks for email, so ask for it:
                // the question here is "does this key resolve to a channel", not "does an escalation
                // send email" (that is the instrument test above).
                var n = Escalation();
                n.SendEmail = true;

                var results = await _channels.DispatchAsync(n, key);
                if (results.Count != 1)
                    orphans.Add(key + " -> " + results.Count + " channel(s) ["
                                + string.Join(", ", results.Select(r => r.Channel)) + "]");
            }

            Assert.True(orphans.Count == 0,
                "these operator-selectable channel ids do not select exactly one dispatch channel, so "
                + "picking one routes the alert to nowhere: " + string.Join("; ", orphans));
        }

        /// <summary>
        /// THE TWIN CENSUS, and it is here so a decision is measured instead of remembered. Every
        /// channel-routing property on <see cref="AlertDefinition"/> is either honoured by the
        /// product or on an explicit out-of-scope list with a reason.
        ///
        /// <para>Today that is exactly two properties. <c>EscalationChannel</c> is WIRED by this
        /// lane. <c>PrimaryChannel</c> is the untouched twin: declared, offered by the same page
        /// (Pages/Alerts.razor:815), read by nothing. It was left alone deliberately - escalation
        /// routing could be changed freely because NOTHING escalates today, whereas firing
        /// notifications go out continuously and rerouting them changes live customer behaviour on
        /// the next deploy. That is a ruling for Adrian, not a builder's call.</para>
        ///
        /// <para>What makes this a guard rather than a comment: add a THIRD channel picker, or wire
        /// PrimaryChannel, and this goes red asking for the decision to be recorded.</para>
        /// </summary>
        [Fact]
        public void EveryChannelRoutingPropertyIsEitherWiredOrKnowinglyNot()
        {
            var routingProperties = typeof(AlertDefinition).GetProperties()
                .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("Channel", StringComparison.Ordinal))
                .Select(p => p.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "EscalationChannel", "PrimaryChannel" }, routingProperties);

            var productSource = ProductSourceText();

            // WIRED: read somewhere other than its own declaration and the razor binding.
            Assert.Contains("alert.EscalationChannel", productSource, StringComparison.Ordinal);

            // KNOWINGLY NOT: no product code reads PrimaryChannel. If that changes, the reason
            // recorded on AlertConfiguration.cs is stale and must be re-ruled.
            Assert.DoesNotContain("alert.PrimaryChannel", productSource, StringComparison.Ordinal);
            Assert.DoesNotContain(".PrimaryChannel,", productSource, StringComparison.Ordinal);
        }

        // -- LAYER 2: the WIRING. The engine hands the pick to the dispatcher -------

        /// <summary>
        /// THE WIRING PIN, and the one that would catch a perfect predicate nobody calls. A real
        /// special-path escalation is driven through <see cref="AlertEvaluationService"/> with all
        /// seven channels live against the capturing handler, and the assertion is read off the
        /// REQUESTS THAT WENT ON THE WIRE - not off a return value, not off a log line.
        ///
        /// <para>Two arms on one instrument. Blank pick (what all 80 shipped alerts carry) must
        /// reach SIX channels; a "pagerduty" pick on the SAME alert shape must reach exactly one,
        /// and it must be PagerDuty. If DispatchEscalation stops passing
        /// <c>alert.EscalationChannel</c>, arm 2 reads six and names the regression.</para>
        ///
        /// <para><b>Why the assertion is not a count of sockets, which is what it was first written
        /// as.</b> The FIRING notification on the same cycle is fire-and-forget too, so its requests
        /// interleave with the escalation's and are still in flight when the escalation finishes:
        /// the first draft of this test asserted six URLs on the handler and measured twelve. A
        /// socket count cannot separate the two dispatches and would be racy even if it could. So
        /// the escalation's OWN dispatch is awaited and read through
        /// <c>AlertEvaluationService.LastEscalationDispatch</c> - exact, and with no sleep in it -
        /// while the handler is used for what it can honestly say: real HTTP was attempted, and
        /// none of it left the box.</para>
        /// </summary>
        [Fact]
        public async Task TheEngineHandsTheAlertsEscalationChannelToTheDispatcher()
        {
            var log = new CapturingLogger<AlertEvaluationService>();
            using var svc = BuildEngine(log);
            svc.DryRun = false;                     // the escalation block is gated on !_dryRun

            var (connection, endpoint) = DeadEndpoint();

            // -- ARM 1: blank pick, the shipped case. Must still fan out. ------------
            var blank = SpecialEscalating(Guid.NewGuid().ToString("N"));
            Assert.Null(blank.EscalationChannel);    // the shipped value on all 80, asserted not assumed

            await svc.EvaluateSpecialAlertAsync(blank, connection, endpoint, new AlertGlobalDefaults());
            await svc.EvaluateSpecialAlertAsync(blank, connection, endpoint, new AlertGlobalDefaults());
            var fannedOut = await svc.LastEscalationDispatch;

            Assert.True(svc.ActiveAlerts.Single(s => s.AlertId == blank.Id).IsEscalated,
                "the escalation never happened, so nothing below measures routing");
            Assert.Equal(SixEscalationChannels.OrderBy(x => x, StringComparer.Ordinal),
                         fannedOut.Select(r => r.Channel).OrderBy(x => x, StringComparer.Ordinal));

            // NON-VACUITY: real HTTP was attempted, so "six channels" is not six no-ops.
            //
            // A note on how the safety claim is actually made, because the first draft of this line
            // got it wrong: it asserted no URL contained ".com", and four of twelve did. PagerDuty
            // (events.pagerduty.com) and WhatsApp (graph.facebook.com) have HARD-CODED endpoints
            // that no config can point at .invalid. Nothing leaves the box all the same, and the
            // reason is structural rather than textual: the service's HttpClient IS this capturing
            // handler, so every request lands here and none reaches a socket. Seeing the requests
            // recorded below is the proof of that, not a lucky hostname.
            Assert.NotEmpty(_handler.Urls);

            // -- ARM 2: the same alert shape with a pick. Must reach ONE. ------------
            var picked = SpecialEscalating(Guid.NewGuid().ToString("N"));
            picked.EscalationChannel = "pagerduty";

            _handler.Reset();

            await svc.EvaluateSpecialAlertAsync(picked, connection, endpoint, new AlertGlobalDefaults());
            await svc.EvaluateSpecialAlertAsync(picked, connection, endpoint, new AlertGlobalDefaults());
            var routed = await svc.LastEscalationDispatch;

            Assert.True(svc.ActiveAlerts.Single(s => s.AlertId == picked.Id).IsEscalated);

            var only = Assert.Single(routed);
            Assert.Equal("PagerDuty", only.Channel);
            Assert.True(only.Success, "PagerDuty: " + only.Detail);

            // And PagerDuty really was reached over HTTP on this arm, not merely selected.
            Assert.Contains(_handler.Urls, u => u.Contains("pagerduty", StringComparison.OrdinalIgnoreCase));
        }

        // -- helpers ----------------------------------------------------------------

        /// <summary>An escalation exactly as <c>BuildEscalationNotification</c> shapes one: critical,
        /// and SendEmail left at its default false.</summary>
        private static AlertNotification Escalation() => new()
        {
            AlertName = "[ESCALATED] Routing Subject",
            Metric = "routing_subject",
            Severity = "critical",
            InstanceName = "routing-test",
            Message = "routing test",
            TriggeredAt = DateTime.UtcNow,
        };

        private static AlertDefinition SpecialEscalating(string suffix) => new()
        {
            Id = "esc_channel_" + suffix,
            Name = "Escalation Channel Subject " + suffix,
            QueryMode = "connectivity_check",
            Operator = "greater_than",
            Thresholds = new AlertThresholds { Warning = 0 },
            Severity = "Critical",
            Escalate = true,
            EscalationAfterMinutes = 0,
            EscalationThresholdEvents = 0,
            EscalationWindowMinutes = 0,
        };

        private static (ServerConnection, string) DeadEndpoint()
        {
            const string deadEndpoint = "127.0.0.1,1"; // nothing listens here - refused immediately
            return (new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = deadEndpoint,
                UseWindowsAuthentication = true,
                ConnectionTimeout = 2,
                IsEnabled = true,
            }, deadEndpoint);
        }

        private AlertEvaluationService BuildEngine(ILogger<AlertEvaluationService> logger)
            => new(
                logger,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                _channels,
                new liveQueriesCacheStore(),
                new InlineOrchestrator(),
                evalFailureStorePath: Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));

        /// <summary>The option values the escalation-channel picker offers, read out of the razor
        /// source. Both pickers on that page are built from the same _enabledChannels list, so this
        /// is the operator's whole vocabulary.</summary>
        private static List<string> PickerChannelKeys()
        {
            var razor = File.ReadAllText(Path.Combine(RepoRoot().FullName, "Pages", "Alerts.razor"));
            return Regex.Matches(razor, "_enabledChannels\\.Add\\(new\\(\\s*\"([^\"]+)\"")
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
        }

        /// <summary>Product source with comments stripped, so no doc comment naming a property can
        /// pass for a read of it.</summary>
        private static string ProductSourceText()
        {
            var root = RepoRoot();
            var files = new[] { "Data", "Pages", "Components", "Services" }
                .Select(d => new DirectoryInfo(Path.Combine(root.FullName, d)))
                .Where(d => d.Exists)
                .SelectMany(d => d.EnumerateFiles("*.cs", SearchOption.AllDirectories)
                                  .Concat(d.EnumerateFiles("*.razor", SearchOption.AllDirectories)))
                .Where(f => !f.FullName.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.FullName.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

            var text = string.Join("\n", files.Select(f => File.ReadAllText(f.FullName)));
            text = Regex.Replace(text, "/\\*.*?\\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, "//[^\\n]*", " ");
            Assert.True(text.Length > 100_000,
                "the product-source scan read almost nothing and is measuring nothing");
            return text;
        }

        private static DirectoryInfo RepoRoot() => RawPassedScan.RepoRoot();

        private static void Repoint(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f!.SetValue(target, value);
        }

        private static void Invoke(object target, string method)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(m);
            m!.Invoke(target, null);
        }

        /// <summary>Every channel on, at "info" so nothing is filtered by severity, every hostname
        /// .invalid, and the whole lot answered by the capturing handler before DNS is consulted.</summary>
        private static NotificationChannelConfig AllSevenChannels() => new()
        {
            Smtp = new SmtpChannelConfig
            {
                Enabled = true,
                UseOAuth2 = true,
                TenantId = "contoso.example",
                ClientId = "00000000-0000-0000-0000-000000000001",
                ClientSecret = "not-a-real-secret",
                FromAddress = "alerts@example.invalid",
                ToAddresses = new List<string> { "dba@example.invalid" },
                MinimumSeverity = "info"
            },
            TeamsWebhook = new TeamsWebhookChannelConfig
            {
                Enabled = true,
                WebhookUrl = "https://teams.example.invalid/hook",
                MinimumSeverity = "info"
            },
            Slack = new SlackChannelConfig
            {
                Enabled = true,
                WebhookUrl = "https://slack.example.invalid/hook",
                MinimumSeverity = "info"
            },
            Webhook = new WebhookChannelConfig
            {
                Enabled = true,
                Url = "https://webhook.example.invalid/hook",
                MinimumSeverity = "info"
            },
            PagerDuty = new PagerDutyChannelConfig
            {
                Enabled = true,
                RoutingKey = "not-a-real-routing-key",
                MinimumSeverity = "info"
            },
            ServiceNow = new ServiceNowChannelConfig
            {
                Enabled = true,
                InstanceUrl = "https://snow.example.invalid",
                Username = "svc_sqltriage",
                Password = "not-a-real-password",
                Table = "incident",
                MinimumSeverity = "info"
            },
            WhatsApp = new WhatsAppChannelConfig
            {
                Enabled = true,
                PhoneNumberId = "123456789012345",
                AccessToken = "not-a-real-token",
                RecipientNumbers = new List<string> { "+6421000000" },
                MinimumSeverity = "info"
            }
        };

        /// <summary>Answers every request 200 and records the URL. Nothing reaches the network.</summary>
        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly List<string> _urls = new();

            public List<string> Urls { get { lock (_urls) return _urls.ToList(); } }
            public void Reset() { lock (_urls) _urls.Clear(); }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Content is not null) await request.Content.ReadAsStringAsync(cancellationToken);
                lock (_urls) _urls.Add(request.RequestUri?.ToString() ?? string.Empty);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"access_token\":\"t\",\"expires_in\":3600,\"result\":{\"sys_id\":\"1\",\"number\":\"INC1\"},\"messages\":[{\"id\":\"m\"}]}",
                        System.Text.Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            private readonly List<(LogLevel Level, string Text)> _lines = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_lines) _lines.Add((logLevel, formatter(state, exception)));
            }

            public List<string> At(LogLevel level)
            {
                lock (_lines) return _lines.Where(l => l.Level == level).Select(l => l.Text).ToList();
            }

            private sealed class Scope : IDisposable
            {
                public static readonly Scope Instance = new();
                public void Dispose() { }
            }
        }

        /// <summary>Runs the work inline, exactly as the real orchestrator does on the happy path.</summary>
        private sealed class InlineOrchestrator : IQueryOrchestrator
        {
            public async Task<QueryResult> EnqueueAsync(
                QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
            {
                try
                {
                    await request.Work(cancellationToken);
                    return new QueryResult { QueryId = request.QueryId, Success = true };
                }
                catch (Exception ex)
                {
                    return new QueryResult { QueryId = request.QueryId, Success = false, Exception = ex };
                }
            }

            public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorHealth());
            public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new OrchestratorMetrics());
            public void UpdateLimits(int globalConcurrency, int perServerConcurrency) { }
            public void Start() { }
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
