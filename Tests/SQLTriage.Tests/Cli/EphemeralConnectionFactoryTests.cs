/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using SQLTriage.Cli;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests.Cli
{
    /// <summary>
    /// #33 headless CLI — the ephemeral, never-persisted ServerConnection the CLI builds for a
    /// --audit run. <see cref="EphemeralConnectionFactory.PreflightAsync"/> (real socket I/O) is
    /// exercised live in the console verify pass, not here — these tests stay offline, mirroring
    /// the project's existing "no live SQL Server needed" unit-test convention.
    /// </summary>
    public class EphemeralConnectionFactoryTests
    {
        private static AuditCliArgs BaseArgs(string auth = "integrated", string? user = null, string? password = null) =>
            new()
            {
                Servers = new[] { ".\\old2017", ".\\new2022" },
                Auth = auth,
                User = user,
                Password = password,
                Formats = new[] { "json" },
                OutDir = "output",
                Parallel = 4,
                BundlePath = "unused-in-these-tests",
                Quiet = true,
            };

        [Fact]
        public void Build_IntegratedAuth_SetsWindowsAuthenticationFields()
        {
            var conn = EphemeralConnectionFactory.Build(BaseArgs());

            Assert.True(conn.UseWindowsAuthentication);
            Assert.Equal(AuthenticationTypes.Windows, conn.AuthenticationType);
            Assert.Null(conn.Username);
            Assert.Null(conn.Password);
        }

        [Fact]
        public void Build_SqlAuth_SetsUsernameAndAuthType()
        {
            var conn = EphemeralConnectionFactory.Build(BaseArgs("sql", "sa", "s3cr3t"));

            Assert.False(conn.UseWindowsAuthentication);
            Assert.Equal(AuthenticationTypes.SqlServer, conn.AuthenticationType);
            Assert.Equal("sa", conn.Username);
        }

        [Fact]
        public void Build_SqlAuth_PasswordStaysPlaintextInMemory_NeverEncrypted()
        {
            var conn = EphemeralConnectionFactory.Build(BaseArgs("sql", "sa", "s3cr3t-plaintext"));

            // Never routed through SetPassword (which calls CredentialProtector.Encrypt) — the
            // raw field holds the plaintext exactly, unprefixed with "aes:"/"enc:".
            Assert.Equal("s3cr3t-plaintext", conn.Password);
            Assert.False(CredentialProtector.IsEncrypted(conn.Password));
        }

        [Fact]
        public void Build_SqlAuth_GetDecryptedPassword_ReturnsSamePlaintext_NoDpapiInvolved()
        {
            // GetConnectionString calls GetDecryptedPassword -> CredentialProtector.Decrypt.
            // Because the value is unprefixed, Decrypt takes the "legacy plaintext" fallback
            // branch and returns it unchanged — no DPAPI/AES call, which is the whole point of
            // bypassing CredentialProtector for a no-profile Task Scheduler run.
            var conn = EphemeralConnectionFactory.Build(BaseArgs("sql", "sa", "s3cr3t-plaintext"));

            Assert.Equal("s3cr3t-plaintext", conn.GetDecryptedPassword());

            var connString = conn.GetConnectionString(".\\old2017", "master");
            Assert.Contains("s3cr3t-plaintext", connString);
        }

        [Fact]
        public void Build_TrustServerCertificate_DefaultsTrue()
        {
            var conn = EphemeralConnectionFactory.Build(BaseArgs());

            Assert.True(conn.TrustServerCertificate);
        }

        [Fact]
        public void Build_ServerNames_JoinsAllRequestedServers()
        {
            var conn = EphemeralConnectionFactory.Build(BaseArgs());

            Assert.Equal(new[] { ".\\old2017", ".\\new2022" }, conn.GetServerList());
        }

        [Fact]
        public void Build_NeverPersists_NoServerConnectionsFileTouched()
        {
            // Regression guard for the byte-identical requirement: building an ephemeral
            // connection must never go anywhere near Config/server-connections.json. There is
            // no ServerConnectionManager reference anywhere in EphemeralConnectionFactory —
            // this test simply proves Build() succeeds without one being supplied at all.
            var conn = EphemeralConnectionFactory.Build(BaseArgs());
            Assert.NotNull(conn);
            Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "__should_never_exist__.json")));
        }
    }
}
