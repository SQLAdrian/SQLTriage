/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Defect 2, the visible half: <c>/auth/login</c> rendered a heading over nothing when both
    /// OAuth providers were off — no explanation, no recovery route, on the exact screen an
    /// operator reaches after locking themselves out.
    /// </summary>
    public class SqlTriageAuthLoginPageTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public SqlTriageAuthLoginPageTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "auth-login-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private RbacService NewRbac(RbacConfig config, params RbacUser[] users)
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (users.Length > 0) File.WriteAllText(_usersPath, JsonSerializer.Serialize(users.ToList()));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        private static HttpContext Ctx(string? error = null, int port = 5155)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Host = new HostString("sqltriage.example", port);
            if (error != null) ctx.Request.QueryString = new QueryString("?error=" + error);
            return ctx;
        }

        [Fact]
        public void NoProviderConfigured_SaysSoAndNamesTheWayBackIn()
        {
            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(new RbacConfig { Enabled = true }), null);

            Assert.Contains("no sign-in method is configured", html, StringComparison.OrdinalIgnoreCase);
            // The recovery route: the console, on loopback, at Settings.
            Assert.Contains("http://localhost:5155/settings", html);
            Assert.Contains("rbac-users.json", html);
        }

        [Fact]
        public void NoProviderConfigured_IsNotAnEmptyPage()
        {
            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(new RbacConfig { Enabled = true }), null);

            // The shipped page interpolated an empty provider list into a fixed shell; that
            // shell is ~250 chars. Anything near it means the operator got a blank page again.
            var body = html[html.IndexOf("<h2>Sign In</h2>", StringComparison.Ordinal)..];
            Assert.True(body.Length > 600, "The sign-in page must never render an empty body. Got:\n" + body);
        }

        [Fact]
        public void WindowsEnabled_OffersTheWindowsLink()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;

            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(config), null);

            Assert.Contains("/auth/challenge/windows", html);
            Assert.DoesNotContain("no sign-in method is configured", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GoogleEnabledWithoutAClientId_IsNotOffered()
        {
            // ConfigureAuthentication never registers the handler without a ClientId, so a link
            // would go to a challenge that cannot be issued. The page must agree with the pipeline.
            var config = new RbacConfig { Enabled = true };
            config.Google.Enabled = true;
            config.Google.ClientId = "";

            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(config), null);

            Assert.DoesNotContain("/auth/challenge/google", html);
            Assert.Contains("no sign-in method is configured", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void GoogleFullyConfigured_IsOffered()
        {
            // AMENDED 2026-08-01 (round 2): a ClientSecret is now part of "configured". Without one
            // the provider is not registered at all (registering it makes UseAuthentication throw on
            // every request), so offering a link to it would be a dead end on the recovery screen.
            var config = new RbacConfig { Enabled = true };
            config.Google.Enabled = true;
            config.Google.ClientId = "1234.apps.googleusercontent.com";
            config.Google.ClientSecret = "a-real-secret";

            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(config), null);

            Assert.Contains("/auth/challenge/google", html);
        }

        [Fact]
        public void GoogleEnabledButIncomplete_IsNotOffered_AndSaysWhy()
        {
            var config = new RbacConfig { Enabled = true };
            config.Google.Enabled = true;
            config.Google.ClientId = "1234.apps.googleusercontent.com";
            config.Google.ClientSecret = "";

            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(config), null);

            Assert.DoesNotContain("/auth/challenge/google", html);
            Assert.Contains("not fully configured", html);
        }

        [Fact]
        public void TheLoginPageDeclaresUtf8()
        {
            // It is the recovery screen and it is full of em dashes; served as bare text/html the
            // browser fell back to its locale codepage and rendered mojibake.
            var config = new RbacConfig { Enabled = true };

            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(config), null);

            Assert.Contains("<meta charset='utf-8' />", html);
            Assert.Equal("text/html; charset=utf-8", SqlTriageAuth.LoginPageContentType);
        }

        [Fact]
        public void LocalPasswordEnabled_RendersASignInForm()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;

            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(config), null);

            Assert.Contains("action='/auth/local'", html);
            Assert.Contains("name='username'", html);
            Assert.Contains("name='password'", html);
        }

        [Fact]
        public void AllMethodsOn_OffersAllOfThem()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            config.LocalPassword.Enabled = true;
            config.Google.Enabled = true;
            config.Google.ClientId = "gid";
            config.Google.ClientSecret = "gsecret";
            config.Microsoft.Enabled = true;
            config.Microsoft.ClientId = "mid";
            config.Microsoft.ClientSecret = "msecret";

            var html = SqlTriageAuth.RenderLoginPage(Ctx(), NewRbac(config), null);

            Assert.Contains("/auth/challenge/windows", html);
            Assert.Contains("/auth/challenge/google", html);
            Assert.Contains("/auth/challenge/microsoft", html);
            Assert.Contains("action='/auth/local'", html);
        }

        [Theory]
        [InlineData("access_denied", "not authorised")]
        [InlineData("bad_credentials", "not recognised")]
        [InlineData("domain_restricted", "allowed domain")]
        [InlineData("no_windows_identity", "account name")]
        public void ErrorCodes_RenderAsSentencesNotCodes(string code, string fragment)
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;

            var html = SqlTriageAuth.RenderLoginPage(Ctx(code), NewRbac(config), null);

            Assert.Contains(fragment, html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(">" + code + "<", html);
        }

        /// <summary>
        /// The two throttle refusals. Both used to fall through to "Sign-in failed.", so the page
        /// gave one sentence for a lockout, a load refusal and an unrecognised code alike.
        ///
        /// <para>The assertion is that the three bodies DIFFER, not merely that each contains a
        /// phrase: a copy change that made two of them identical again would satisfy any
        /// contains-check written for one of them.</para>
        /// </summary>
        [Fact]
        public void TheTwoThrottleRefusalsAreDistinctSentences_NotTheGenericOne()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;

            string Body(string? code)
            {
                var html = SqlTriageAuth.RenderLoginPage(
                    Ctx(code), NewRbac(config), null);
                var at = html.IndexOf("<p style='color:#f88", StringComparison.Ordinal);
                return at < 0 ? "" : html[at..html.IndexOf("</p>", at, StringComparison.Ordinal)];
            }

            var lockedOut = Body("too_many_attempts");
            var busy = Body("busy");
            var generic = Body("an_unrecognised_code");

            Assert.NotEqual("", lockedOut);
            Assert.NotEqual("", busy);
            Assert.NotEqual(lockedOut, busy);
            Assert.NotEqual(lockedOut, generic);
            Assert.NotEqual(busy, generic);
        }

        [Fact]
        public void TheLockoutMessageNamesTheLimitAndHowLongItLasts()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;

            var html = SqlTriageAuth.RenderLoginPage(Ctx("too_many_attempts"), NewRbac(config), null);

            // Read off the constants, so this fails if the copy is hard-coded and the limiter moves.
            Assert.Contains(LocalLoginThrottle.MaxAttempts.ToString(), html, StringComparison.Ordinal);
            Assert.Contains(
                LocalLoginThrottle.LockoutDuration.TotalMinutes.ToString("0") + " minute",
                html, StringComparison.Ordinal);
        }

        [Fact]
        public void TheBusyMessageDoesNotImplyTheCredentialsWereWrong()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;

            var html = SqlTriageAuth.RenderLoginPage(Ctx("busy"), NewRbac(config), null);
            var at = html.IndexOf("<p style='color:#f88", StringComparison.Ordinal);
            var body = html[at..html.IndexOf("</p>", at, StringComparison.Ordinal)];

            // It says what was measured: the server's own concurrency limit, by its number.
            Assert.Contains(
                LocalLoginThrottle.MaxConcurrentVerifications.ToString(), body, StringComparison.Ordinal);

            // And it must not say, or imply, that the credentials failed. Busy is not an attempt:
            // counting it as one would let a flood lock out every legitimate caller.
            foreach (var wrong in new[]
                     {
                         "not recognised", "incorrect", "invalid", "wrong password",
                         "failed sign-in", "check your password",
                     })
                Assert.DoesNotContain(wrong, body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheErrorQueryStringIsHtmlEncoded()
        {
            var html = SqlTriageAuth.RenderLoginPage(
                Ctx("<script>alert(1)</script>"), NewRbac(new RbacConfig { Enabled = true }), null);

            Assert.DoesNotContain("<script>alert(1)</script>", html);
        }

        [Fact]
        public void TheLoopbackClaimNameIsStable()
        {
            // AppUserState reads this claim to decide bootstrap eligibility. If the two ever
            // disagree, the console user silently loses the hatch.
            Assert.Equal("sqltriage:loopback", SqlTriageAuthClaims.Loopback);
        }
    }
}
