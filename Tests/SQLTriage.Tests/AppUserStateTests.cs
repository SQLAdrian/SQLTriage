/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class AppUserStateTests
    {
        private AppUserState NewState(HostEnvironmentInfo? host = null, RbacService? rbac = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "aus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            rbac ??= new RbacService(NullLogger<RbacService>.Instance,
                Path.Combine(dir, "rbac-config.json"), Path.Combine(dir, "rbac-users.json"));

            // Empty provider — AppUserState resolves AuthenticationStateProvider and
            // IHttpContextAccessor optionally, so an empty container exercises the
            // no-signal path (which must fail CLOSED, not open).
            var services = new ServiceCollection().BuildServiceProvider();

            return new AppUserState(
                host ?? HostEnvironmentInfo.BrowserHosted,
                rbac,
                services,
                NullLogger<AppUserState>.Instance);
        }

        // ── T9: default role is Viewer (most-restrictive default) ────────

        [Fact]
        public void DefaultRole_IsViewer_BeforeInitAsync()
        {
            // Arrange + Act: construct fresh AppUserState (no InitAsync called)
            var state = NewState();

            // Assert: Role defaults to Viewer (prevents privilege-escalation race in server mode)
            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.False(state.IsAdmin);
            Assert.False(state.IsOperator);
        }

        [Fact]
        public void SetRole_ToAdmin_UpdatesRole()
        {
            var state = NewState();

            state.SetRole(AppRoles.Admin);

            Assert.Equal(AppRoles.Admin, state.Role);
            Assert.True(state.IsAdmin);
        }

        [Fact]
        public void SetRole_ToOperator_IsNotAdmin()
        {
            var state = NewState();

            state.SetRole(AppRoles.Operator);

            Assert.Equal(AppRoles.Operator, state.Role);
            Assert.False(state.IsAdmin);
            Assert.True(state.IsOperator);
        }
    }
}
