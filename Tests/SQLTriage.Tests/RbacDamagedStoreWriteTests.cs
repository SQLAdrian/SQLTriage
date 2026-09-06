/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:RbacDamagedStoreWriteTests — a page that did not load real values cannot persist synthesized ones
    /// <summary>
    /// D4 of the 2026-08-03 cold gate, and the SHAPE of its fix.
    ///
    /// <para><b>The defect.</b> When <c>rbac-config.json</c> exists and does not load, RbacService
    /// correctly reports <c>IsConfigStoreDamaged</c> and the Settings banner correctly says the
    /// configuration did not load. Directly beneath that banner sat
    /// <c>&lt;input type="checkbox" @bind="_rbacEnabled" @bind:after="SaveRbacConfig" /&gt;</c>, with
    /// <c>_rbacEnabled</c> copied out of the DEFAULTS object. Measured on rendered markup: in the
    /// truncated state that checkbox was byte-identical to the healthy state where an operator had
    /// genuinely switched access control off, and its dependent block collapsed identically. Then
    /// <c>@bind:after</c> saved on toggle — so ONE CLICK persisted this build's defaults as the
    /// operator's recorded choice, overwrote the damaged file, and destroyed the only evidence it
    /// had ever been damaged. A loud state converted to a quiet one by a click.</para>
    ///
    /// <para><b>Why the fix is here and not on the checkbox.</b> This is the second instance of the
    /// pattern in this codebase — <c>/portal-status</c> never echoes the stored Azure SAS back into
    /// its input, so clicking Save wrote an empty string and cleared a live credential — and this
    /// lane has now watched four static scanners and four authorization audits each be defeated
    /// through the category the instrument was blind to. A per-control guard would be defeated by
    /// the next control added to the page. So the rule lives on the WRITE:
    /// <see cref="StoreWriteIntent"/>, checked by <c>RbacService.UpdateConfig</c> and by both
    /// private persist methods, whose intent parameter is REQUIRED so that a future mutator cannot
    /// reach the disk without answering the question. The safe value is the default: a caller that
    /// says nothing is refused, not admitted.</para>
    ///
    /// <para><b>What these tests are.</b> Behavioural, over the file on disk — the assertion is
    /// that the BYTES are unchanged, not that some flag was consulted. The one markup assertion at
    /// the bottom is labelled a lint and is not the boundary.</para>
    /// </summary>
    public class RbacDamagedStoreWriteTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacDamagedStoreWriteTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-d4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
        }

        private RbacService Build() => new(NullLogger<RbacService>.Instance, _configPath, _usersPath);

        private static RbacUser TheAdmin() => new()
        {
            Email = @"D4\admin",
            DisplayName = "Configured Admin",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Admin,
            Enabled = true,
        };

        private static string IntactStore() => JsonSerializer.Serialize(new List<RbacUser> { TheAdmin() });

        /// <summary>
        /// The seven shapes the adversary drove, carried over from RbacRound15RegressionTests so the
        /// two files cannot drift. Six are damage. The seventh PARSES — <c>bootstrapCompletedUtc</c>
        /// is an unmapped member since the latch was deleted, and System.Text.Json skips it — and it
        /// is kept deliberately as this file's negative control: the guard must let a healthy save
        /// through, or "nothing is ever overwritten" would be a fix that broke saving.
        /// </summary>
        public static TheoryData<string, string, bool> SevenShapes => new()
        {
            { "truncated",     "{\"enabled\": true, \"windows\": {\"enab",                 true  },
            { "empty",         "",                                                          true  },
            { "whitespace",    "   \n\t ",                                                  true  },
            { "wrong-type",    "{\"enabled\": \"yes\"}",                                    true  },
            { "wrong-shape",   "[]",                                                        true  },
            { "literal-null",  "null",                                                      true  },
            { "bad-latch-type","{\"enabled\": true, \"bootstrapCompletedUtc\": 12345}",     false },
        };

        /// <summary>
        /// Exactly what <c>Settings.SaveRbacConfig</c> hands to <c>UpdateConfig</c>: a FRESH
        /// RbacConfig assembled from the form fields, which <c>LoadRbacSettings</c> filled from
        /// <c>RbacService.Config</c>. In the damaged state that source is the defaults object, so
        /// every field below is this build's guess — which is the whole point.
        /// </summary>
        private static RbacConfig AsSettingsWouldSave(RbacConfig inTheForm, bool enabled) => new()
        {
            Enabled = enabled,
            RequireExplicitAccess = inTheForm.RequireExplicitAccess,
            DefaultRole = inTheForm.DefaultRole,
            Google = new OAuthProviderConfig
            {
                Enabled = inTheForm.Google.Enabled,
                ClientId = inTheForm.Google.ClientId,
                ClientSecret = inTheForm.Google.ClientSecret,
                AllowedDomain = inTheForm.Google.AllowedDomain,
            },
            Microsoft = new OAuthProviderConfig
            {
                Enabled = inTheForm.Microsoft.Enabled,
                ClientId = inTheForm.Microsoft.ClientId,
                ClientSecret = inTheForm.Microsoft.ClientSecret,
                AllowedDomain = inTheForm.Microsoft.AllowedDomain,
            },
            Windows = new WindowsAuthConfig
            {
                Enabled = inTheForm.Windows.Enabled,
                AllowedDomains = new List<string>(inTheForm.Windows.AllowedDomains),
            },
            LocalPassword = new LocalPasswordConfig { Enabled = inTheForm.LocalPassword.Enabled },
        };

        // ── 1. The one click ─────────────────────────────────────────────

        /// <summary>
        /// THE D4 ASSERTION, driven the way the defect was reached: load the page (which copies
        /// whatever the service is holding into the form), tick the checkbox, let
        /// <c>@bind:after</c> fire. On the six damaged shapes the file must come back off disk
        /// BYTE FOR BYTE, and the install must still be saying it cannot read its configuration.
        ///
        /// <para>Byte comparison rather than "is it still damaged", because the loss being guarded
        /// is the evidence itself: a rewrite that happened to leave the store classified as damaged
        /// would still have thrown away what the operator had configured.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(SevenShapes))]
        public void OneClickOnTheEnableCheckboxCannotOverwriteAConfigThatDidNotLoad(
            string shape, string content, bool isDamage)
        {
            File.WriteAllText(_configPath, content);
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = Build();
            var before = File.ReadAllBytes(_configPath);

            // The page's fields, and then the toggle: whatever the box was showing, flipped.
            var inTheForm = rbac.Config;
            var afterTheClick = AsSettingsWouldSave(inTheForm, enabled: !inTheForm.Enabled);

            var outcome = rbac.UpdateConfig(afterTheClick);

            if (isDamage)
            {
                Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable, outcome);
                Assert.Equal(before, File.ReadAllBytes(_configPath));
                Assert.True(rbac.IsConfigStoreDamaged,
                    $"shape '{shape}': the install must still be able to say its configuration did not load");
                Assert.Equal(RbacService.PostureKind.ConfigDidNotLoad,
                    rbac.DescribeEnforcementPosture().Kind);
            }
            else
            {
                // Negative control: this one parsed, so the save is an ordinary one and must land.
                Assert.Equal(StoreWriteOutcome.Saved, outcome);
                Assert.NotEqual(before, File.ReadAllBytes(_configPath));
            }
        }

        /// <summary>
        /// A refusal must not leave the service describing a configuration that exists nowhere. The
        /// guard is checked before <c>_config</c> is replaced, so what the banner and the log report
        /// a moment after a refused click is what they reported a moment before it.
        /// </summary>
        [Fact]
        public void ARefusedSaveLeavesTheServiceSayingWhatItSaidBefore()
        {
            File.WriteAllText(_configPath, "{\"enabled\": true, \"windows\": {\"enab");
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = Build();
            var headlineBefore = rbac.DescribeEnforcementPosture().Headline;

            rbac.UpdateConfig(AsSettingsWouldSave(rbac.Config, enabled: true));

            Assert.Equal(headlineBefore, rbac.DescribeEnforcementPosture().Headline);
            Assert.False(rbac.Config.Enabled);
        }

        // ── 2. The repair path stays open ────────────────────────────────

        /// <summary>
        /// The operator's explicit "discard the unreadable file and enter a configuration now" is
        /// the ONE way through, and it has to work — the enforcement banner advertises re-entering
        /// the settings as a repair, and a fix that closed it would strand a headless install on a
        /// file it cannot read.
        /// </summary>
        [Theory]
        [MemberData(nameof(SevenShapes))]
        public void TheOperatorsExplicitReplacementIsTheOneWayThrough(string shape, string content, bool isDamage)
        {
            _ = isDamage;   // every shape must accept an explicit replacement, damaged or not

            File.WriteAllText(_configPath, content);
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = Build();

            var replacement = AsSettingsWouldSave(rbac.Config, enabled: false);
            replacement.DefaultRole = AppRoles.Viewer;

            var outcome = rbac.UpdateConfig(replacement, StoreWriteIntent.ReplaceUnreadableStore);

            Assert.Equal(StoreWriteOutcome.Saved, outcome);
            Assert.False(rbac.IsConfigStoreDamaged, $"shape '{shape}': the store was replaced, so it loads now");
            Assert.NotEqual(RbacService.PostureKind.ConfigDidNotLoad, rbac.DescribeEnforcementPosture().Kind);

            // And a fresh service over the same path reads it back, so the repair survives a restart.
            Assert.False(Build().IsConfigStoreDamaged);
        }

        /// <summary>
        /// A file that was never there is a fresh install, not damage. Refusing the first save would
        /// make onboarding impossible, so <see cref="ConfigLoadOutcome.Missing"/> is deliberately
        /// outside the guard.
        /// </summary>
        [Fact]
        public void AFreshInstallStillWritesItsFirstConfig()
        {
            var rbac = Build();   // neither file exists

            Assert.Equal(StoreWriteOutcome.Saved, rbac.UpdateConfig(new RbacConfig { Enabled = false }));
            Assert.True(File.Exists(_configPath));
        }

        /// <summary>
        /// A config that loaded takes writes exactly as it always did. The regression this guards
        /// against is a fix that reads "refuse when in doubt" as "refuse".
        /// </summary>
        [Fact]
        public void AHealthyConfigStillSavesOnAnOrdinaryClick()
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(new RbacConfig { Enabled = false }));
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = Build();
            Assert.Equal(StoreWriteOutcome.Saved,
                rbac.UpdateConfig(AsSettingsWouldSave(rbac.Config, enabled: true)));
            Assert.True(Build().Config.Enabled);
        }

        // ── 3. The same defect on the other store ────────────────────────

        /// <summary>
        /// <c>rbac-users.json</c> is the same trap and a worse one: a truncated store deserialises
        /// to an EMPTY LIST, which in memory is identical to an install with no users yet. Every
        /// mutator then writes that empty list back plus whatever it just did, and every account,
        /// role and password hash on the install is gone — with the damaged file that would have
        /// proved it overwritten in the same stroke. <c>RecordLogin</c> reaches it with no operator
        /// click at all.
        /// </summary>
        [Theory]
        [InlineData("add")]
        [InlineData("update")]
        [InlineData("remove")]
        [InlineData("set-password")]
        [InlineData("record-login")]
        public void NoMutatorOverwritesAUserStoreThatDidNotLoad(string mutator)
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(new RbacConfig
            {
                Enabled = true,
                RequireExplicitAccess = false,
                DefaultRole = AppRoles.Admin,
                Windows = new WindowsAuthConfig { Enabled = true },
            }));
            File.WriteAllText(_usersPath, "[{\"email\": \"real.admin@contoso.com\", \"ro");

            var rbac = Build();
            Assert.True(rbac.IsUserStoreDamaged);
            var before = File.ReadAllBytes(_usersPath);

            switch (mutator)
            {
                case "add":
                    Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable, rbac.AddUser(TheAdmin()));
                    break;
                case "update":
                    Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable, rbac.UpdateUser(TheAdmin()));
                    break;
                case "remove":
                    Assert.Equal(StoreWriteOutcome.RefusedStoreUnreadable, rbac.RemoveUser(TheAdmin().Id));
                    break;
                case "set-password":
                    Assert.False(rbac.SetPassword("real.admin@contoso.com", "correct horse battery staple"));
                    break;
                case "record-login":
                    // No click anywhere: an auto-created principal on requireExplicitAccess=false.
                    rbac.RecordLogin(@"D4\someone", "Someone", AuthProviders.Windows);
                    break;
            }

            Assert.Equal(before, File.ReadAllBytes(_usersPath));
            Assert.True(rbac.IsUserStoreDamaged, "the install must still be able to say its user store did not load");
        }

        /// <summary>Negative control for the store above: a healthy one takes every write.</summary>
        [Fact]
        public void AHealthyUserStoreStillTakesWrites()
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(new RbacConfig()));
            File.WriteAllText(_usersPath, IntactStore());

            var rbac = Build();
            var added = new RbacUser
            {
                Email = "second.admin@contoso.com",
                DisplayName = "Second",
                Provider = AuthProviders.Local,
                Role = AppRoles.Operator,
                Enabled = true,
            };

            Assert.Equal(StoreWriteOutcome.Saved, rbac.AddUser(added));
            Assert.Equal(2, Build().GetUsers().Count);

            Assert.Equal(StoreWriteOutcome.Saved, rbac.RemoveUser(added.Id));
            Assert.Single(Build().GetUsers());
        }

        // ── 4. The shape, pinned ─────────────────────────────────────────

        /// <summary>
        /// STRUCTURAL, and it is the half that makes this a chokepoint rather than a patch: both
        /// private persist methods take a REQUIRED <see cref="StoreWriteIntent"/>, so a control
        /// added to Settings next year cannot reach either file without a call site that answers the
        /// question — it will not compile. The compiler is the enforcement; this test is what stops
        /// the parameter quietly acquiring a default value, which would restore the old behaviour
        /// for every future caller while every existing test kept passing.
        /// </summary>
        [Fact]
        public void BothPersistPathsRequireTheirWriteIntent()
        {
            var savers = typeof(RbacService)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name is "SaveConfig" or "SaveUsers")
                .ToArray();

            Assert.Equal(2, savers.Length);

            foreach (var saver in savers)
            {
                var intent = Assert.Single(saver.GetParameters()
                    .Where(p => p.ParameterType == typeof(StoreWriteIntent)));

                Assert.False(intent.IsOptional,
                    $"{saver.Name}'s write intent must stay required — an optional one lets a new mutator "
                    + "reach the disk without stating what kind of write it is doing.");
            }
        }

        /// <summary>
        /// The safe value has to be the one a forgetful caller gets. Stated as a test because the
        /// polarity is the whole design: <c>IsRbacEnforced</c> is in this same file precisely
        /// because a predicate whose default answer is the permissive one turned a truncated file
        /// into an open door.
        /// </summary>
        [Fact]
        public void TheDefaultWriteIntentIsTheRefusingOne()
        {
            Assert.Equal(default, StoreWriteIntent.FromLoadedStore);
            Assert.True(ConfigFileHelper.WouldOverwriteUnreadStore(
                ConfigLoadOutcome.Unreadable, default));

            var updateConfig = typeof(RbacService).GetMethod(nameof(RbacService.UpdateConfig));
            Assert.NotNull(updateConfig);
            var intent = Assert.Single(updateConfig!.GetParameters()
                .Where(p => p.ParameterType == typeof(StoreWriteIntent)));
            Assert.True(intent.IsOptional);
            Assert.Equal(StoreWriteIntent.FromLoadedStore, (StoreWriteIntent)intent.DefaultValue!);
        }

        /// <summary>
        /// ⚠ THIS IS A LINT, NOT A BOUNDARY, and it is named for what it can actually establish:
        /// that the page CONSULTS the damaged-store signal at all. Before this change
        /// <c>Settings.razor.cs</c> contained zero references to <c>IsConfigStoreDamaged</c> or any
        /// other damaged-config signal — nothing on the page knew — and that is the one fact a
        /// static read of the file can settle.
        ///
        /// <para>It cannot establish that the controls are actually withheld when rendered. This
        /// wave has now watched five static instruments be defeated, the last of them by an ordinary
        /// English sentence dropped into the fixture, so treat a pass here as "the page still asks
        /// the question", never as "the page shows nothing it cannot support". What holds the
        /// invariant is above: the write is refused, so a control that renders a default cannot
        /// persist one.</para>
        /// </summary>
        [Fact]
        public void LintOnly_TheSettingsPageAsksWhetherItMayShowStoredValues()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "Settings.razor");
            Assert.True(File.Exists(path),
                $"Settings.razor was not copied to the test output ({path}); this assertion would silently pass.");

            var markup = File.ReadAllText(path);

            // The gate exists…
            Assert.Contains("RbacControlsMayShowValues", markup, StringComparison.Ordinal);

            // …and the toggle that carried the defect is on its guarded side, along with the block
            // that depended on it. Both are literal checks and both are defeatable by an author who
            // moves the control; see the paragraph above.
            Assert.Contains("@if (!RbacControlsMayShowValues)", markup, StringComparison.Ordinal);

            // The dependent block, still on the guarded side. This read
            // "@if (_rbacEnabled && RbacControlsMayShowValues)" until 2026-08-25, and the
            // _rbacEnabled half was a SEPARATE defect wearing this guard's clothes: it hid the
            // only control that adds an Admin behind the very state that control exists to make
            // reachable, so a cold store could not be configured at all. The withholding this file
            // is about is the RbacControlsMayShowValues half, which is what is asserted. See
            // RbacBootstrapReachabilityTests, which walks the nesting rather than the words.
            Assert.Contains("@if (RbacControlsMayShowValues)", markup, StringComparison.Ordinal);
        }
    }
}
