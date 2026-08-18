/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class RbacServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private RbacService NewService(RbacConfig? config = null)
        {
            if (config != null)
                File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        // ── HasPermission matrix ────────────────────────────────────────
        //
        // Admin-only:    settings, manage_servers, manage_users, manage_alerts
        // Admin+Operator:execute_checks, run_scripts, export_data, acknowledge_alerts
        // Everyone:      view_dashboard, view_results, view_audit_log
        // Unknown perm:  admin-only (deny by default)

        [Theory]
        [InlineData("settings")]
        [InlineData("manage_servers")]
        [InlineData("manage_users")]
        [InlineData("manage_alerts")]
        public void HasPermission_AdminOnlyPermissions_GrantsAdmin(string permission)
        {
            Assert.True(RbacService.HasPermission(AppRoles.Admin, permission));
        }

        [Theory]
        [InlineData("settings")]
        [InlineData("manage_servers")]
        [InlineData("manage_users")]
        [InlineData("manage_alerts")]
        public void HasPermission_AdminOnlyPermissions_DeniesOperator(string permission)
        {
            Assert.False(RbacService.HasPermission(AppRoles.Operator, permission));
        }

        [Theory]
        [InlineData("settings")]
        [InlineData("manage_servers")]
        [InlineData("manage_users")]
        [InlineData("manage_alerts")]
        public void HasPermission_AdminOnlyPermissions_DeniesViewer(string permission)
        {
            Assert.False(RbacService.HasPermission(AppRoles.Viewer, permission));
        }

        [Theory]
        [InlineData("execute_checks")]
        [InlineData("run_scripts")]
        [InlineData("export_data")]
        [InlineData("acknowledge_alerts")]
        public void HasPermission_OperatorPermissions_GrantsAdminAndOperator(string permission)
        {
            Assert.True(RbacService.HasPermission(AppRoles.Admin, permission));
            Assert.True(RbacService.HasPermission(AppRoles.Operator, permission));
        }

        [Theory]
        [InlineData("execute_checks")]
        [InlineData("run_scripts")]
        [InlineData("export_data")]
        [InlineData("acknowledge_alerts")]
        public void HasPermission_OperatorPermissions_DeniesViewer(string permission)
        {
            Assert.False(RbacService.HasPermission(AppRoles.Viewer, permission));
        }

        [Theory]
        [InlineData("view_dashboard")]
        [InlineData("view_results")]
        [InlineData("view_audit_log")]
        public void HasPermission_ViewerPermissions_GrantsEveryone(string permission)
        {
            Assert.True(RbacService.HasPermission(AppRoles.Admin, permission));
            Assert.True(RbacService.HasPermission(AppRoles.Operator, permission));
            Assert.True(RbacService.HasPermission(AppRoles.Viewer, permission));
        }

        [Theory]
        [InlineData("unknown_permission")]
        [InlineData("delete_universe")]
        [InlineData("")]
        public void HasPermission_UnknownPermission_DefaultsToAdminOnly(string permission)
        {
            Assert.True(RbacService.HasPermission(AppRoles.Admin, permission));
            Assert.False(RbacService.HasPermission(AppRoles.Operator, permission));
            Assert.False(RbacService.HasPermission(AppRoles.Viewer, permission));
        }

        [Fact]
        public void HasPermission_GarbageRole_DeniesEvenViewerPermissions()
        {
            // A user with an unrecognised role string still gets viewer perms because
            // the matrix returns true for everyone on view-only permissions.
            Assert.True(RbacService.HasPermission("garbage-role", "view_dashboard"));
            // But not admin or operator perms.
            Assert.False(RbacService.HasPermission("garbage-role", "manage_users"));
            Assert.False(RbacService.HasPermission("garbage-role", "execute_checks"));
        }

        [Fact]
        public void HasPermission_RoleComparison_IsCaseInsensitive()
        {
            // Role match is normalised to lowercase — matches HasRole() and tolerates
            // whatever casing the OAuth provider/cookie hands in.
            Assert.True(RbacService.HasPermission(AppRoles.Admin, "settings"));
            Assert.True(RbacService.HasPermission("ADMIN", "settings"));
            Assert.True(RbacService.HasPermission("Admin", "settings"));
            Assert.False(RbacService.HasPermission("admin-typo", "settings"));
        }

        [Fact]
        public void HasPermission_NullRole_DeniesEverythingExceptViewPermissions()
        {
            // null role is treated as empty string — matches no role constant,
            // so only the universal view perms (which return true unconditionally)
            // are allowed.
            Assert.True(RbacService.HasPermission(null!, "view_dashboard"));
            Assert.False(RbacService.HasPermission(null!, "settings"));
            Assert.False(RbacService.HasPermission(null!, "execute_checks"));
        }

        // ── Argon2id password hashing ───────────────────────────────────

        [Fact]
        public void HashPassword_AcceptsAndRoundTrips()
        {
            var hash = RbacService.HashPassword("hunter2");
            Assert.StartsWith("argon2id$v=19$", hash);
            Assert.True(RbacService.VerifyPassword("hunter2", hash));
        }

        [Fact]
        public void HashPassword_TwoCallsSameInput_ProduceDifferentHashes()
        {
            // Random salt — identical passwords must never hash to the same string.
            var a = RbacService.HashPassword("samepassword");
            var b = RbacService.HashPassword("samepassword");
            Assert.NotEqual(a, b);
            // But both verify against the original.
            Assert.True(RbacService.VerifyPassword("samepassword", a));
            Assert.True(RbacService.VerifyPassword("samepassword", b));
        }

        [Fact]
        public void HashPassword_EmptyOrNull_Throws()
        {
            Assert.Throws<ArgumentException>(() => RbacService.HashPassword(""));
            Assert.Throws<ArgumentException>(() => RbacService.HashPassword(null!));
        }

        [Fact]
        public void VerifyPassword_WrongPassword_ReturnsFalse()
        {
            var hash = RbacService.HashPassword("correct");
            Assert.False(RbacService.VerifyPassword("wrong", hash));
        }

        [Fact]
        public void VerifyPassword_UnicodePassword_RoundTrips()
        {
            var pw = "пароль🔑漢字";
            var hash = RbacService.HashPassword(pw);
            Assert.True(RbacService.VerifyPassword(pw, hash));
            Assert.False(RbacService.VerifyPassword("парол🔑漢字", hash));  // one char different
        }

        [Theory]
        [InlineData("")]
        [InlineData("plain text")]
        [InlineData("argon2id$wrong")]
        [InlineData("argon2id$v=19$m=19456,t=2,p=1$badbase64$alsobadbase64")]
        [InlineData("$$$$$")]
        public void VerifyPassword_MalformedHash_ReturnsFalseInsteadOfThrowing(string storedHash)
        {
            Assert.False(RbacService.VerifyPassword("anypassword", storedHash));
        }

        [Theory]
        [InlineData(null, "abc")]
        [InlineData("", "argon2id$...")]
        [InlineData("abc", null)]
        public void VerifyPassword_NullOrEmptyInputs_ReturnFalse(string? password, string? storedHash)
        {
            Assert.False(RbacService.VerifyPassword(password!, storedHash!));
        }

        [Fact]
        public void VerifyPassword_TamperedHashByte_ReturnsFalse()
        {
            // Flip one byte in the stored hash's hash component → should NOT verify.
            var hash = RbacService.HashPassword("password");
            var parts = hash.Split('$');
            var hashBytes = Convert.FromBase64String(parts[4]);
            hashBytes[0] ^= 0xFF;
            parts[4] = Convert.ToBase64String(hashBytes);
            var tampered = string.Join('$', parts);
            Assert.False(RbacService.VerifyPassword("password", tampered));
        }

        [Fact]
        public void VerifyPassword_TamperedSaltByte_ReturnsFalse()
        {
            var hash = RbacService.HashPassword("password");
            var parts = hash.Split('$');
            var salt = Convert.FromBase64String(parts[3]);
            salt[0] ^= 0xFF;
            parts[3] = Convert.ToBase64String(salt);
            var tampered = string.Join('$', parts);
            Assert.False(RbacService.VerifyPassword("password", tampered));
        }

        [Fact]
        public void Argon2_Parameters_MeetOwasp2024Minimums()
        {
            // Constants are private — pin them via reflection so a future contributor
            // dropping the memory/iteration cost fails this gate.
            var t = typeof(RbacService);
            int mem = (int)t.GetField("Argon2MemoryKib", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
            int iters = (int)t.GetField("Argon2Iterations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
            int saltBytes = (int)t.GetField("Argon2SaltBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
            int hashBytes = (int)t.GetField("Argon2HashBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;

            Assert.True(mem >= 19_456, $"Argon2MemoryKib={mem} below OWASP 2024 minimum 19,456 KiB");
            Assert.True(iters >= 2, $"Argon2Iterations={iters} below OWASP 2024 minimum 2");
            Assert.True(saltBytes >= 16, $"Argon2SaltBytes={saltBytes} below 128-bit minimum");
            Assert.True(hashBytes >= 32, $"Argon2HashBytes={hashBytes} below 256-bit minimum");
        }

        // ── Instance: AddUser / UpdateUser / RemoveUser / GetUsers ──────

        [Fact]
        public void AddUser_PersistsAndIsRetrievable()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "a@example.com", Role = AppRoles.Operator });

            // Retrievable in the same instance
            Assert.NotNull(svc.GetUserByEmail("a@example.com"));

            // Persisted: a fresh service instance reads the same data from disk
            var fresh = NewService();
            var loaded = fresh.GetUserByEmail("a@example.com");
            Assert.NotNull(loaded);
            Assert.Equal(AppRoles.Operator, loaded!.Role);
        }

        [Fact]
        public void AddUser_DuplicateEmail_IsRejectedCaseInsensitively()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "dupe@example.com", Role = AppRoles.Admin });
            svc.AddUser(new RbacUser { Email = "DUPE@example.com", Role = AppRoles.Viewer });

            var users = svc.GetUsers();
            Assert.Single(users);
            Assert.Equal(AppRoles.Admin, users[0].Role); // First add wins
        }

        [Fact]
        public void GetUserByEmail_IsCaseInsensitive()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "Mixed.Case@Example.com" });

            Assert.NotNull(svc.GetUserByEmail("mixed.case@example.com"));
            Assert.NotNull(svc.GetUserByEmail("MIXED.CASE@EXAMPLE.COM"));
        }

        [Fact]
        public void UpdateUser_PersistsChanges()
        {
            var svc = NewService();
            var u = new RbacUser { Email = "u@example.com", Role = AppRoles.Viewer };
            svc.AddUser(u);

            u.Role = AppRoles.Admin;
            u.Enabled = false;
            svc.UpdateUser(u);

            var reloaded = NewService().GetUserByEmail("u@example.com");
            Assert.NotNull(reloaded);
            Assert.Equal(AppRoles.Admin, reloaded!.Role);
            Assert.False(reloaded.Enabled);
        }

        [Fact]
        public void RemoveUser_DeletesUser()
        {
            var svc = NewService();
            var u = new RbacUser { Email = "u@example.com" };
            svc.AddUser(u);
            Assert.NotNull(svc.GetUserByEmail("u@example.com"));

            svc.RemoveUser(u.Id);
            Assert.Null(svc.GetUserByEmail("u@example.com"));
            // Persisted removal
            Assert.Null(NewService().GetUserByEmail("u@example.com"));
        }

        [Fact]
        public void GetUsers_ReturnsCopy_NotInternalReference()
        {
            // Mutating the returned list must not mutate the service's internal state.
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "a@example.com" });

            var users = svc.GetUsers();
            users.Clear();

            Assert.Single(svc.GetUsers());
        }

        // ── RecordLogin: provisioning + denial gates ────────────────────

        [Fact]
        public void RecordLogin_KnownUser_UpdatesLastLogin()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com", Role = AppRoles.Operator });

            var before = DateTime.UtcNow.AddSeconds(-1);
            var result = svc.RecordLogin("u@example.com", "Updated Name", "google");
            var after = DateTime.UtcNow.AddSeconds(1);

            Assert.NotNull(result);
            Assert.Equal(AppRoles.Operator, result!.Role);
            Assert.InRange(result.LastLogin!.Value, before, after);
            Assert.Equal("Updated Name", result.DisplayName);
        }

        [Fact]
        public void RecordLogin_UnknownUser_RequireExplicitAccess_DeniesAndReturnsNull()
        {
            var svc = NewService(new RbacConfig { RequireExplicitAccess = true });
            var result = svc.RecordLogin("stranger@example.com", "Stranger", "google");
            Assert.Null(result);
            // And the stranger was NOT auto-added.
            Assert.Empty(svc.GetUsers());
        }

        [Fact]
        public void RecordLogin_UnknownUser_OpenSignup_AutoCreatesWithDefaultRole()
        {
            var svc = NewService(new RbacConfig
            {
                RequireExplicitAccess = false,
                DefaultRole = AppRoles.Operator
            });
            var result = svc.RecordLogin("new@example.com", "New User", "microsoft");

            Assert.NotNull(result);
            Assert.Equal(AppRoles.Operator, result!.Role);
            Assert.Equal("microsoft", result.Provider);
            Assert.Single(svc.GetUsers());
        }

        [Fact]
        public void RecordLogin_DisabledUser_DeniesAndReturnsNull()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com", Enabled = false });

            Assert.Null(svc.RecordLogin("u@example.com", "U", "google"));
        }

        [Fact]
        public void RecordLogin_EmailMatchingIsCaseInsensitive()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com", Role = AppRoles.Admin });

            var result = svc.RecordLogin("U@EXAMPLE.COM", "U", "google");
            Assert.NotNull(result);
            Assert.Equal(AppRoles.Admin, result!.Role);
        }

        // ── HasRole ─────────────────────────────────────────────────────

        [Fact]
        public void HasRole_ReturnsTrueOnlyForExactRole()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com", Role = AppRoles.Operator });

            Assert.True(svc.HasRole("u@example.com", AppRoles.Operator));
            Assert.True(svc.HasRole("U@EXAMPLE.COM", "OPERATOR")); // role check itself is case-insensitive
            Assert.False(svc.HasRole("u@example.com", AppRoles.Admin));
            Assert.False(svc.HasRole("nobody@example.com", AppRoles.Operator));
        }

        // ── Local password auth ─────────────────────────────────────────

        [Fact]
        public void SetPassword_ThenValidateLocalLogin_Succeeds()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com", Role = AppRoles.Admin });
            svc.SetPassword("u@example.com", "correct-pw");

            var user = svc.ValidateLocalLogin("u@example.com", "correct-pw");
            Assert.NotNull(user);
            Assert.Equal(AppRoles.Admin, user!.Role);
        }

        [Fact]
        public void ValidateLocalLogin_WrongPassword_ReturnsNull()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com" });
            svc.SetPassword("u@example.com", "real-pw");

            Assert.Null(svc.ValidateLocalLogin("u@example.com", "fake-pw"));
        }

        [Fact]
        public void ValidateLocalLogin_DisabledUser_ReturnsNull()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com", Enabled = false });
            svc.SetPassword("u@example.com", "real-pw");

            Assert.Null(svc.ValidateLocalLogin("u@example.com", "real-pw"));
        }

        [Fact]
        public void ValidateLocalLogin_UnknownEmail_ReturnsNull()
        {
            var svc = NewService();
            Assert.Null(svc.ValidateLocalLogin("nobody@example.com", "any-pw"));
        }

        [Fact]
        public void ValidateLocalLogin_UnknownEmail_StillRunsArgon2_ToPreventTimingEnumeration()
        {
            // Constant-time security property: a miss should not be obviously faster than a hit.
            // We can't easily assert exact equality (CI jitter), but a miss should run an
            // Argon2 verification anyway, so its duration should be in the same order of
            // magnitude as a real verification.
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "real@example.com" });
            svc.SetPassword("real@example.com", "real-pw");

            // Warm up — first Argon2 call is slower due to JIT / cold caches.
            _ = svc.ValidateLocalLogin("real@example.com", "wrong-pw");
            _ = svc.ValidateLocalLogin("ghost@example.com", "any-pw");

            var realTimes = new long[5];
            var ghostTimes = new long[5];
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                svc.ValidateLocalLogin("real@example.com", "wrong-pw");
                sw.Stop();
                realTimes[i] = sw.ElapsedMilliseconds;

                sw = Stopwatch.StartNew();
                svc.ValidateLocalLogin("ghost@example.com", "any-pw");
                sw.Stop();
                ghostTimes[i] = sw.ElapsedMilliseconds;
            }

            // Median to dodge GC pauses.
            Array.Sort(realTimes); Array.Sort(ghostTimes);
            long realMed = realTimes[2];
            long ghostMed = ghostTimes[2];

            // Ghost must take at least 30% of real (i.e. NOT a fast-path early-return).
            // Generous threshold — we're guarding against "fast 0ms miss", not asserting
            // perfect equality which would be CI-flaky.
            Assert.True(ghostMed * 100 >= realMed * 30,
                $"Ghost ({ghostMed}ms) was <30% of real ({realMed}ms) — looks like a timing-leak fast path.");
        }

        [Fact]
        public void SetPassword_EmptyPassword_Throws()
        {
            var svc = NewService();
            svc.AddUser(new RbacUser { Email = "u@example.com" });
            Assert.Throws<ArgumentException>(() => svc.SetPassword("u@example.com", ""));
        }

        [Fact]
        public void SetPassword_UnknownUser_DoesNotThrow()
        {
            // Per implementation: logs a warning and returns. Important contract:
            // callers don't have to pre-check existence.
            var svc = NewService();
            svc.SetPassword("nobody@example.com", "anything");
            // No exception; no user created either.
            Assert.Empty(svc.GetUsers());
        }

        // ── UpdateConfig ────────────────────────────────────────────────

        [Fact]
        public void UpdateConfig_FiresOnConfigChangedEvent()
        {
            var svc = NewService();
            var fired = false;
            svc.OnConfigChanged += () => fired = true;

            svc.UpdateConfig(new RbacConfig { Enabled = true });
            Assert.True(fired);
        }

        [Fact]
        public void UpdateConfig_PersistsToDisk()
        {
            var svc = NewService();
            svc.UpdateConfig(new RbacConfig
            {
                Enabled = true,
                RequireExplicitAccess = false,
                DefaultRole = AppRoles.Operator
            });

            var fresh = NewService();
            Assert.True(fresh.Config.Enabled);
            Assert.False(fresh.Config.RequireExplicitAccess);
            Assert.Equal(AppRoles.Operator, fresh.Config.DefaultRole);
        }

        [Fact]
        public void UpdateConfig_EncryptsPlainClientSecretsOnSave()
        {
            var svc = NewService();
            svc.UpdateConfig(new RbacConfig
            {
                Google = new OAuthProviderConfig { ClientSecret = "plain-google-secret" },
                Microsoft = new OAuthProviderConfig { ClientSecret = "plain-ms-secret" }
            });

            // After update, the in-memory secret should be encrypted, not plaintext.
            Assert.NotEqual("plain-google-secret", svc.Config.Google.ClientSecret);
            Assert.NotEqual("plain-ms-secret", svc.Config.Microsoft.ClientSecret);
            Assert.True(SQLTriage.Data.CredentialProtector.IsEncrypted(svc.Config.Google.ClientSecret));
            Assert.True(SQLTriage.Data.CredentialProtector.IsEncrypted(svc.Config.Microsoft.ClientSecret));
        }

        [Fact]
        public void UpdateConfig_DoesNotDoubleEncryptAlreadyEncryptedSecrets()
        {
            var svc = NewService();
            svc.UpdateConfig(new RbacConfig
            {
                Google = new OAuthProviderConfig { ClientSecret = "plain" }
            });
            var firstCipher = svc.Config.Google.ClientSecret;

            // Round-trip the encrypted blob through UpdateConfig again — should not change.
            svc.UpdateConfig(new RbacConfig
            {
                Google = new OAuthProviderConfig { ClientSecret = firstCipher }
            });
            Assert.Equal(firstCipher, svc.Config.Google.ClientSecret);
        }

        // ── GetDesktopUserRole ──────────────────────────────────────────

        [Fact]
        public void GetDesktopUserRole_AlwaysReturnsAdmin()
        {
            // WPF mode contract: the local desktop user is always Admin.
            Assert.Equal(AppRoles.Admin, NewService().GetDesktopUserRole());
        }

        // ── Loading missing / corrupt config files ──────────────────────

        [Fact]
        public void Constructor_MissingFiles_LoadsDefaults()
        {
            // Neither file exists → both should fall back to default-constructed objects.
            var svc = NewService();
            Assert.False(svc.Config.Enabled);
            Assert.True(svc.Config.RequireExplicitAccess);
            Assert.Equal(AppRoles.Viewer, svc.Config.DefaultRole);
            Assert.Empty(svc.GetUsers());
        }

        [Fact]
        public void Constructor_CorruptUsersFile_LoadsEmptyListInsteadOfThrowing()
        {
            File.WriteAllText(_usersPath, "{ this is not json ]");
            var svc = NewService();
            Assert.Empty(svc.GetUsers());
        }

        [Fact]
        public void Constructor_CorruptConfigFile_LoadsDefaultsInsteadOfThrowing()
        {
            File.WriteAllText(_configPath, "garbage");
            var svc = NewService();
            Assert.False(svc.Config.Enabled);
        }
    }
}
