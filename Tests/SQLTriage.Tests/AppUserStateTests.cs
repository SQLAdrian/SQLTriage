/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class AppUserStateTests
    {
        private AppUserState NewState()
        {
            var serverMode = new ServerModeService(NullLogger<ServerModeService>.Instance);
            return new AppUserState(serverMode, NullLogger<AppUserState>.Instance);
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
