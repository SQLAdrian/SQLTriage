/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The recovery advice printed beside a damaged-store verdict must be conditioned on what the
    /// loader ACTUALLY did, not on what it usually does.
    ///
    /// <para><b>The defect these pin (2026-08-04 cold gate, measured live).</b> Four surfaces — the
    /// service's own lapse reason, <c>Settings.razor</c>, <c>Settings.razor.cs</c> and
    /// <c>Onboarding.razor</c> — each carried an independent copy of "a copy of the unparseable one
    /// is kept beside it with a <c>.rejected-</c> suffix", printed whenever a store was damaged. The
    /// quarantine is not unconditional: it fired on <see cref="System.Text.Json.JsonException"/>
    /// only. A <b>0-byte</b> file is damage, is reported as damage, and produces NO copy — so on
    /// that shape all four surfaces pointed the operator at a file that had never been written.</para>
    ///
    /// <para>What makes it worth a test rather than a fix: it was found on the round whose entire
    /// subject was this same defect class one control higher on the same page. Fixing a named
    /// instance and shipping a fresh one is the pattern this wave keeps producing, so the invariant
    /// is pinned behaviourally — assert what the sentence CLAIMS against what is on disk, never that
    /// it matches some wording.</para>
    /// </summary>
    public class RbacStoreRecoveryProseTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacStoreRecoveryProseTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-prose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* temp dir */ }
        }

        private RbacService Build() => new(NullLogger<RbacService>.Instance, _configPath, _usersPath);

        private bool AnyRejectedCopyExists() =>
            Directory.GetFiles(_tempDir).Any(f => f.Contains(".rejected-", StringComparison.Ordinal));

        /// <summary>
        /// THE REGRESSION. An empty file is damage and gets no copy, so the advice must not promise
        /// one. Asserted against the FILESYSTEM, not against wording: if a future change starts
        /// quarantining empty files, this still passes for the right reason.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n\t ")]
        public void AnEmptyStore_IsDamaged_AndTheAdviceDoesNotPromiseACopyThatWasNeverWritten(string content)
        {
            File.WriteAllText(_configPath, content);
            var svc = Build();

            Assert.True(svc.IsConfigStoreDamaged, "an empty store is damage, not 'not configured yet'");

            var advice = svc.DescribeStoreRecovery(forConfigStore: true);

            // The claim under test: does the sentence assert a copy exists?
            var claimsACopy = advice.Contains(".rejected-", StringComparison.Ordinal);
            Assert.Equal(AnyRejectedCopyExists(), claimsACopy);

            // And on this shape there is genuinely nothing to recover, so it must say so rather
            // than sending the operator to look for a file.
            Assert.False(claimsACopy);
            Assert.Contains("EMPTY", advice, StringComparison.Ordinal);
        }

        /// <summary>
        /// The other side of the same coin: when a copy really was taken, the advice should name it.
        /// A fix that made the sentence honest by deleting all mention of the copy would pass the
        /// test above and fail this one.
        /// </summary>
        [Theory]
        [InlineData("{ not json at all")]
        [InlineData("{\"enabled\": ")]
        [InlineData("[]")]
        public void AnUnparseableStore_IsQuarantined_AndTheAdviceNamesTheCopy(string content)
        {
            File.WriteAllText(_configPath, content);
            var svc = Build();

            Assert.True(svc.IsConfigStoreDamaged);

            var copies = Directory.GetFiles(_tempDir).Where(f => f.Contains(".rejected-", StringComparison.Ordinal)).ToList();
            Assert.True(copies.Count == 1, $"expected exactly one quarantine copy, found {copies.Count}");

            var advice = svc.DescribeStoreRecovery(forConfigStore: true);
            Assert.Contains(Path.GetFileName(copies[0]), advice, StringComparison.Ordinal);

            // The copy must hold the ORIGINAL bytes - a copy that does not preserve what was there
            // is the same false comfort in a different form.
            Assert.Equal(File.ReadAllText(_configPath), File.ReadAllText(copies[0]));
        }

        /// <summary>
        /// Literal <c>null</c> is valid JSON, so no JsonException fires and the quarantine's catch
        /// block never runs - the shape that made this a two-case bug rather than one. There IS
        /// content here, so unlike the empty case it is worth preserving.
        /// </summary>
        [Fact]
        public void ALiteralNullStore_IsQuarantinedToo_BecauseThereIsContentToPreserve()
        {
            File.WriteAllText(_configPath, "null");
            var svc = Build();

            Assert.True(svc.IsConfigStoreDamaged);
            Assert.True(AnyRejectedCopyExists(), "literal null has content; it must be preserved");
            Assert.Contains(".rejected-", svc.DescribeStoreRecovery(forConfigStore: true), StringComparison.Ordinal);
        }

        /// <summary>
        /// A healthy store must not be told how to recover from damage it does not have.
        /// </summary>
        [Fact]
        public void AHealthyStore_IsNotDamaged_AndNoCopyIsTaken()
        {
            File.WriteAllText(_configPath, "{\"enabled\": false}");
            var svc = Build();

            Assert.False(svc.IsConfigStoreDamaged);
            Assert.False(AnyRejectedCopyExists());
        }

        /// <summary>
        /// A MISSING file is a fresh install, not damage - and must not be quarantined or described
        /// as recoverable. This is the fail-safe polarity the wave was bitten by in the other
        /// direction, so it is pinned explicitly.
        /// </summary>
        [Fact]
        public void AMissingStore_IsNotDamage_AndNoCopyIsTaken()
        {
            Assert.False(File.Exists(_configPath));
            var svc = Build();

            Assert.False(svc.IsConfigStoreDamaged, "a missing file is a fresh install, not damage");
            Assert.False(AnyRejectedCopyExists());
        }

        /// <summary>
        /// The user store travels the same path and had the same two independently-worded copies of
        /// the sentence, so it gets the same assertion rather than being assumed to follow.
        /// </summary>
        [Fact]
        public void TheUserStoreObeysTheSameRule()
        {
            File.WriteAllText(_configPath, "{\"enabled\": true}");
            File.WriteAllText(_usersPath, "");          // empty => damage, no copy
            var svc = Build();

            var advice = svc.DescribeStoreRecovery(forConfigStore: false);
            Assert.Equal(AnyRejectedCopyExists(), advice.Contains(".rejected-", StringComparison.Ordinal));
            Assert.Contains("EMPTY", advice, StringComparison.Ordinal);
        }

        /// <summary>
        /// ONE REGISTER, NOT FOUR. The four surfaces drifted because each composed its own sentence.
        /// This asserts the shipped markup renders the service's string rather than restating it -
        /// the same shape as the banner fix in the previous round.
        ///
        /// <para>⚠ This one IS a markup lint over a literal, and says so: it cannot detect a fifth
        /// surface written in words nobody has thought of. The behavioural assertions above are what
        /// hold the invariant.</para>
        /// </summary>
        [Fact]
        public void TheSettingsMarkupRendersTheComputedAdvice_AndRestatesNoCopyClaimOfItsOwn()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "Settings.razor");
            Assert.True(File.Exists(path), $"Settings.razor was not copied to the test output ({path}).");
            var markup = File.ReadAllText(path);

            Assert.Contains("DescribeStoreRecovery", markup, StringComparison.Ordinal);
            Assert.DoesNotContain(".rejected-", markup, StringComparison.Ordinal);
        }
    }
}
