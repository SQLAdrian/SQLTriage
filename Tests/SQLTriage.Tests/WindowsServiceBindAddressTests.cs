/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Configuration;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// <c>ServiceBindAddress</c> — which interfaces the Windows service host binds.
    ///
    /// <para>Added 2026-08-03, after the RBAC lane moved the security boundary into
    /// <c>InteractiveAppAdmission</c>. The bind is NOT the boundary and these tests do not treat it
    /// as one: an unauthenticated non-loopback caller is refused by the middleware regardless of
    /// what is bound. This setting exists so a locked-down install can narrow the listener anyway.</para>
    ///
    /// <para>The invariant actually worth pinning is the POLARITY on bad input. The default is
    /// already "every interface", so the only reason to set this key is to restrict — which means an
    /// unreadable value must take the NARROW reading. Resolving it permissively would silently
    /// discard an operator's intent to lock the service down, which is the exact shape that beat
    /// this wave once already (<c>IsRbacEnforced</c> read as an expiry: a truncated user store
    /// re-opened an anonymous admin hatch with no attacker and no operator action).</para>
    /// </summary>
    public class WindowsServiceBindAddressTests
    {
        private static IConfiguration Config(string? value)
        {
            var items = new Dictionary<string, string?>();
            if (value != null) items["ServiceBindAddress"] = value;
            return new ConfigurationBuilder().AddInMemoryCollection(items).Build();
        }

        // ── The default is unchanged: share-via-browser still works out of the box ──

        [Fact]
        public void Unset_BindsEveryInterface_AndDoesNotWarn()
        {
            var addr = WindowsServiceHost.ResolveBindAddress(Config(null), out var description, out var warning);

            Assert.Null(addr);            // null == ListenAnyIP
            Assert.Null(warning);
            Assert.Contains("every interface", description);
        }

        [Theory]
        [InlineData("any")]
        [InlineData("ANY")]
        [InlineData("AnyIp")]
        [InlineData("*")]
        [InlineData("0.0.0.0")]
        [InlineData("  any  ")]
        public void ExplicitAny_BindsEveryInterface_AndDoesNotWarn(string value)
        {
            var addr = WindowsServiceHost.ResolveBindAddress(Config(value), out var description, out var warning);

            Assert.Null(addr);
            Assert.Null(warning);
            Assert.Contains("every interface", description);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankIsTreatedAsUnset_NotAsUnreadable(string value)
        {
            // Blank is an absent value, not a mistyped one — an empty string in a config file is
            // how "I did not set this" usually looks. It must not trip the narrow reading.
            var addr = WindowsServiceHost.ResolveBindAddress(Config(value), out var description, out var warning);

            Assert.Null(addr);
            Assert.Null(warning);
            Assert.Contains("every interface", description);
        }

        // ── Narrowing works, by name and by address ──

        [Theory]
        [InlineData("loopback")]
        [InlineData("LOOPBACK")]
        [InlineData("localhost")]
        public void LoopbackByName_BindsLoopback_AndDoesNotWarn(string value)
        {
            var addr = WindowsServiceHost.ResolveBindAddress(Config(value), out var description, out var warning);

            Assert.Equal(IPAddress.Loopback, addr);
            Assert.Null(warning);          // asking for loopback is not a mistake
            Assert.Contains("loopback", description);
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("192.168.10.32")]
        [InlineData("::1")]
        public void AParseableAddress_BindsThatAddress_AndDoesNotWarn(string value)
        {
            var addr = WindowsServiceHost.ResolveBindAddress(Config(value), out var description, out var warning);

            Assert.Equal(IPAddress.Parse(value), addr);
            Assert.Null(warning);
            Assert.Contains(value, description);
        }

        // ── The polarity. This is the test that matters. ──

        [Theory]
        [InlineData("loopbak")]            // typo for the restrictive value
        [InlineData("lokalhost")]
        [InlineData("999.999.999.999")]    // looks like an address, is not one
        [InlineData("192.168.10")]         // truncated address — the damaged-input shape
        [InlineData("all")]                // plausible synonym for "any" that is NOT accepted
        [InlineData("none")]
        [InlineData(";DROP TABLE x")]
        public void AnUnreadableValue_TakesTheNARROWReading_AndSaysSoLoudly(string value)
        {
            var addr = WindowsServiceHost.ResolveBindAddress(Config(value), out var description, out var warning);

            // The whole point: it must NOT resolve to "every interface". An operator who set this
            // key was trying to restrict; honouring a typo permissively silently defeats them.
            Assert.NotNull(addr);
            Assert.Equal(IPAddress.Loopback, addr);

            // And it must not do that quietly — a service that stopped answering on the LAN needs
            // to say why, in its own log, naming the value it could not read.
            Assert.NotNull(warning);
            Assert.Contains(value, warning!);
            Assert.Contains("LOOPBACK ONLY", warning!);
            Assert.Contains("loopback", description);
            Assert.Contains("not understood", description);
        }

        [Fact]
        public void AnUnreadableValueNeverResolvesToEveryInterface_AcrossAWideSpread()
        {
            // Belt and braces on the theory above: whatever else changes, no unrecognised token may
            // ever come back as ListenAnyIP. Reintroducing a permissive fallback fails here.
            foreach (var value in new[]
                     {
                         "loopbak", "any-ip", "anyway", "0.0.0.0.0", "::g", "true", "false", "1",
                         "0", "yes", "no", "internal", "lan", "public", "\t", "\n", "a".PadRight(300, 'a'),
                     })
            {
                var addr = WindowsServiceHost.ResolveBindAddress(Config(value), out _, out var warning);

                // "\t" and "\n" are whitespace and therefore "unset" — everything else must narrow.
                if (string.IsNullOrWhiteSpace(value))
                {
                    Assert.Null(addr);
                    Assert.Null(warning);
                    continue;
                }

                Assert.True(addr != null,
                    $"'{value}' resolved to every-interface. An unrecognised value must take the narrow " +
                    "reading — see this class's summary for why the polarity runs this way.");
            }
        }
    }
}
