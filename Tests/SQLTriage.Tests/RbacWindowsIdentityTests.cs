/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Defect 3 — local and domain accounts as first-class RBAC identities.
    ///
    /// <para>Adrian typed <c>local\admin.adrian</c> into the user list and was told to enter a
    /// valid email address. Accepting that string as-is would have been worse than useless: it
    /// would have added a principal that could never authenticate, because no Windows identity
    /// could authenticate in server mode at all.</para>
    /// </summary>
    public class RbacWindowsIdentityTests : IDisposable
    {
        private const string Machine = "MSI";

        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacWindowsIdentityTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-win-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private RbacService NewService(RbacConfig? config = null, params RbacUser[] users)
        {
            if (config != null) File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (users.Length > 0) File.WriteAllText(_usersPath, JsonSerializer.Serialize(users.ToList()));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        // ── Normalisation: the table from the design ─────────────────────

        [Theory]
        [InlineData(@"MSI\admin.adrian", @"MSI\admin.adrian")]
        [InlineData(@".\admin.adrian", @"MSI\admin.adrian")]
        [InlineData("admin.adrian", @"MSI\admin.adrian")]
        [InlineData(@"CONTOSO\adrian", @"CONTOSO\adrian")]
        [InlineData(@"  MSI\admin.adrian  ", @"MSI\admin.adrian")]
        public void Parse_AcceptsAndNormalises(string input, string expected)
        {
            var parsed = WindowsIdentityKey.Parse(input, Machine);
            Assert.True(parsed.Ok, parsed.Error);
            Assert.Equal(expected, parsed.Key);
        }

        [Fact]
        public void Parse_UpnForm_IsKeptAsTyped()
        {
            var parsed = WindowsIdentityKey.Parse("adrian@contoso.com", Machine);
            Assert.True(parsed.Ok);
            Assert.Equal("adrian@contoso.com", parsed.Key);
            Assert.Equal("contoso.com", parsed.Domain);
        }

        [Fact]
        public void Parse_LocalBackslash_IsRejectedAndNamesTheFix()
        {
            // The exact string Adrian typed. `local` is not a machine name and not a NetBIOS
            // domain — it would be looked up as a domain and fail at logon every time.
            var parsed = WindowsIdentityKey.Parse(@"local\admin.adrian", Machine);

            Assert.False(parsed.Ok);
            // The message must name the fix, not restate the rule.
            Assert.Contains(@"MSI\admin.adrian", parsed.Error);
            Assert.Contains(@".\admin.adrian", parsed.Error);
            Assert.Contains(@"DOMAIN\admin.adrian", parsed.Error);
        }

        [Theory]
        [InlineData(@"localhost\adrian")]
        [InlineData(@"workgroup\adrian")]
        [InlineData(@"\adrian")]
        [InlineData(@"CONTOSO\")]
        [InlineData("")]
        public void Parse_RejectsUnusableInput(string input)
        {
            var parsed = WindowsIdentityKey.Parse(input, Machine);
            Assert.False(parsed.Ok);
            Assert.NotEmpty(parsed.Error);
        }

        // ── Comparison ───────────────────────────────────────────────────

        [Theory]
        [InlineData(@"MSI\admin.adrian", @"msi\ADMIN.ADRIAN")]   // Windows names are case-insensitive
        [InlineData(@"MSI\admin.adrian", @".\admin.adrian")]
        [InlineData(@"MSI\admin.adrian", "admin.adrian")]
        [InlineData(@"CONTOSO\adrian", @"contoso\Adrian")]
        public void Equal_SamePrincipal(string a, string b)
            => Assert.True(WindowsIdentityKey.Equal(a, b, Machine));

        [Theory]
        [InlineData(@"MSI\adrian", @"CONTOSO\adrian")]           // same account, different domain
        [InlineData(@"MSI\adrian", @"MSI\adrian.sullivan")]
        public void Equal_DifferentPrincipal(string a, string b)
            => Assert.False(WindowsIdentityKey.Equal(a, b, Machine));

        // ── Round-trip through the user store ────────────────────────────
        //
        // The requirement: a DOMAIN\user and a MACHINE\user must round-trip through the store
        // and match an incoming Windows identity.

        [Fact]
        public void MachineAccount_RoundTripsAndMatchesAnIncomingWindowsIdentity()
        {
            var key = Environment.MachineName + @"\admin.adrian";
            var rbac = NewService(new RbacConfig { RequireExplicitAccess = true }, new RbacUser
            {
                Email = key,
                Provider = AuthProviders.Windows,
                Role = AppRoles.Admin,
                Enabled = true
            });

            // Reload from disk to prove it survived serialisation, then sign in the way
            // SqlTriageAuth does: Identity.Name in down-level form, plus a SID.
            var reloaded = new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
            var user = reloaded.RecordLogin(key, "Adrian Sullivan", AuthProviders.Windows, "S-1-5-21-1-2-3-1001");

            Assert.NotNull(user);
            Assert.Equal(AppRoles.Admin, user!.Role);
            Assert.Equal(AuthProviders.Windows, user.Provider);
        }

        [Fact]
        public void DomainAccount_RoundTripsAndMatchesAnIncomingWindowsIdentity()
        {
            var rbac = NewService(new RbacConfig { RequireExplicitAccess = true }, new RbacUser
            {
                Email = @"CONTOSO\adrian",
                Provider = AuthProviders.Windows,
                Role = AppRoles.Operator,
                Enabled = true
            });

            var reloaded = new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
            var user = reloaded.RecordLogin(@"contoso\Adrian", "Adrian", AuthProviders.Windows, null);

            Assert.NotNull(user);
            Assert.Equal(AppRoles.Operator, user!.Role);
        }

        [Fact]
        public void ShorthandInTheStore_StillMatchesTheDownLevelNameWindowsSupplies()
        {
            // An operator who typed `.\admin.adrian` has it normalised on the way in, but a
            // hand-edited rbac-users.json may hold the shorthand. It must still match.
            var rbac = NewService(new RbacConfig { RequireExplicitAccess = true }, new RbacUser
            {
                Email = @".\admin.adrian",
                Provider = AuthProviders.Windows,
                Role = AppRoles.Admin,
                Enabled = true
            });

            var user = rbac.RecordLogin(Environment.MachineName + @"\admin.adrian", "Adrian", AuthProviders.Windows, null);
            Assert.NotNull(user);
            Assert.Equal(AppRoles.Admin, user!.Role);
        }

        // ── Provider-class isolation: the crux of the identity model ─────

        [Fact]
        public void AWindowsUpnAndAGoogleAddress_AreDifferentPrincipals()
        {
            // Byte-identical keys, different provider classes. Sniffing the string shape would
            // hand the Google user the Windows user's admin role.
            var rbac = NewService(new RbacConfig { RequireExplicitAccess = true },
                new RbacUser { Email = "adrian@contoso.com", Provider = AuthProviders.Windows, Role = AppRoles.Admin, Enabled = true });

            var asWindows = rbac.RecordLogin("adrian@contoso.com", "Adrian", AuthProviders.Windows, null);
            Assert.NotNull(asWindows);
            Assert.Equal(AppRoles.Admin, asWindows!.Role);

            // The same string arriving from Google matches nothing, and RequireExplicitAccess denies.
            var asGoogle = rbac.RecordLogin("adrian@contoso.com", "Adrian", AuthProviders.Google);
            Assert.Null(asGoogle);
        }

        [Fact]
        public void BothPrincipalsCanCoexistInTheStore()
        {
            var rbac = NewService(new RbacConfig());
            rbac.AddUser(new RbacUser { Email = "adrian@contoso.com", Provider = AuthProviders.Windows, Role = AppRoles.Admin });
            rbac.AddUser(new RbacUser { Email = "adrian@contoso.com", Provider = AuthProviders.Google, Role = AppRoles.Viewer });

            Assert.Equal(2, rbac.GetUsers().Count);
            Assert.Equal(AppRoles.Admin, rbac.FindUser(AuthProviders.Windows, "adrian@contoso.com")!.Role);
            Assert.Equal(AppRoles.Viewer, rbac.FindUser(AuthProviders.Google, "adrian@contoso.com")!.Role);
        }

        [Fact]
        public void AddUser_StillRejectsADuplicateWithinTheSameProviderClass()
        {
            var rbac = NewService(new RbacConfig());
            rbac.AddUser(new RbacUser { Email = @"MSI\adrian", Provider = AuthProviders.Windows, Role = AppRoles.Admin });
            rbac.AddUser(new RbacUser { Email = @"msi\ADRIAN", Provider = AuthProviders.Windows, Role = AppRoles.Viewer });

            Assert.Single(rbac.GetUsers());
            Assert.Equal(AppRoles.Admin, rbac.GetUsers()[0].Role);
        }

        [Fact]
        public void NegotiateSchemeNames_AreTreatedAsTheWindowsProvider()
        {
            // ASP.NET Core hands back the raw scheme on Identity.AuthenticationType.
            Assert.True(AuthProviders.IsWindows("Negotiate"));
            Assert.True(AuthProviders.IsWindows("NTLM"));
            Assert.True(AuthProviders.IsWindows("Kerberos"));
            Assert.False(AuthProviders.IsWindows("Google"));
            Assert.False(AuthProviders.IsWindows(null));
        }

        // ── SID durability ───────────────────────────────────────────────

        [Fact]
        public void SidIsBoundOnFirstSignIn_AndSurvivesARename()
        {
            const string sid = "S-1-5-21-9-8-7-1001";
            var rbac = NewService(new RbacConfig { RequireExplicitAccess = true }, new RbacUser
            {
                Email = @"MSI\admin.adrian",
                Provider = AuthProviders.Windows,
                Role = AppRoles.Admin,
                Enabled = true
            });

            var first = rbac.RecordLogin(@"MSI\admin.adrian", "Adrian", AuthProviders.Windows, sid);
            Assert.NotNull(first);
            Assert.Equal(sid, first!.Sid);

            // The Windows account is renamed. Without the SID this would match nothing and —
            // under RequireExplicitAccess — silently lose the admin role.
            var afterRename = rbac.RecordLogin(@"MSI\adrian.sullivan", "Adrian", AuthProviders.Windows, sid);

            Assert.NotNull(afterRename);
            Assert.Equal(AppRoles.Admin, afterRename!.Role);
            Assert.Equal(@"MSI\adrian.sullivan", afterRename.Email);   // stored key follows the rename
            Assert.Single(rbac.GetUsers());                            // and no duplicate was created
        }

        [Fact]
        public void ADifferentSidDoesNotInheritTheRoleOfAReusedName()
        {
            var rbac = NewService(new RbacConfig { RequireExplicitAccess = true }, new RbacUser
            {
                Email = @"MSI\admin.adrian",
                Provider = AuthProviders.Windows,
                Sid = "S-1-5-21-9-8-7-1001",
                Role = AppRoles.Admin,
                Enabled = true
            });

            // A NEW local account reusing the old name. It is a different principal and must not
            // pick up the admin role — this is exactly what binding the SID is for.
            var impostor = rbac.RecordLogin(@"MSI\admin.adrian", "Someone Else", AuthProviders.Windows, "S-1-5-21-9-8-7-2002");

            Assert.Null(impostor);
        }

        // ── Local password path ──────────────────────────────────────────

        [Fact]
        public void SetPassword_AcceptsTheRecordId_SoTheOnboardingCallSiteCannotSilentlyNoOp()
        {
            var rbac = NewService(new RbacConfig());
            var admin = new RbacUser { Email = "adrian@example.com", Provider = AuthProviders.Local, Role = AppRoles.Admin };
            rbac.AddUser(admin);

            Assert.True(rbac.SetPassword(admin.Id, "correct horse battery staple"));
            Assert.NotNull(rbac.GetUsers().Single().PasswordHash);
            Assert.NotNull(rbac.ValidateLocalLogin("adrian@example.com", "correct horse battery staple"));
        }

        [Fact]
        public void SetPassword_ReportsFailureForAnUnknownUser()
        {
            var rbac = NewService(new RbacConfig());
            Assert.False(rbac.SetPassword("nobody@example.com", "correct horse battery staple"));
        }

        [Fact]
        public void AWindowsUserCannotBeSignedInWithAPassword()
        {
            // A password on a Windows-provider record would be a second, weaker key to a
            // principal whose authority is the Windows credential.
            var rbac = NewService(new RbacConfig(), new RbacUser
            {
                Email = @"MSI\admin.adrian",
                Provider = AuthProviders.Windows,
                Role = AppRoles.Admin,
                Enabled = true,
                PasswordHash = RbacService.HashPassword("correct horse battery staple")
            });

            Assert.Null(rbac.ValidateLocalLogin(@"MSI\admin.adrian", "correct horse battery staple"));
        }

        // ── Back-compat with every rbac-users.json already on disk ───────

        [Fact]
        public void ALegacyUserRecordWithNoProviderField_StillSignsInViaOAuth()
        {
            File.WriteAllText(_usersPath,
                """[{"id":"1","email":"adrian@example.com","displayName":"Adrian","role":"admin","enabled":true}]""");

            var rbac = new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
            var user = rbac.RecordLogin("adrian@example.com", "Adrian", "Google");

            Assert.NotNull(user);
            Assert.Equal(AppRoles.Admin, user!.Role);
        }

        [Fact]
        public void TheEmailJsonPropertyNameIsUnchanged()
        {
            // Renaming it would strand every rbac-users.json already written by a shipped build.
            var json = JsonSerializer.Serialize(new List<RbacUser>
            {
                new() { Email = @"MSI\adrian", Provider = AuthProviders.Windows }
            });

            Assert.Contains("\"email\"", json);
            Assert.Contains("\"provider\"", json);
            Assert.DoesNotContain("\"principal\"", json);   // the alias is storage, not schema
        }

        [Fact]
        public void PrincipalIsAnAliasOverTheSameStorage()
        {
            var u = new RbacUser { Principal = @"MSI\adrian" };
            Assert.Equal(@"MSI\adrian", u.Email);
            u.Email = @"CONTOSO\adrian";
            Assert.Equal(@"CONTOSO\adrian", u.Principal);
        }
    }
}
