/* In the name of God, the Merciful, the Compassionate */

// ── Sessions.razor's dispose-while-loading race, 2026-08-20 ────────────────────────────────────
//
// WHY THIS FILE EXISTS. Navigating away from /dashboard/sessions while its first load is still in
// flight blanked the ENTIRE app, silently and permanently, for every route afterwards. The
// mechanism (verifier-traced, reproduced live in two processes): LoadSessions takes _loadLock via
// Wait(0), awaits the SQL call, and releases the lock in its finally. Dispose() used to call
// _loadLock.Dispose() unconditionally. A component torn down mid-load hits Dispose() while the
// lock is still HELD (Wait(0) already returned true, the finally has not run yet) -- Dispose()
// itself does not throw, but the in-flight LoadSessions's later _loadLock.Release() then throws
// ObjectDisposedException, which MainLayout's root ErrorBoundary swallows into a bare nav shell
// and an empty <main> that never recovers, because the disposed component is never re-created.
//
// THE FIX. Dispose() no longer disposes _loadLock at all. SemaphoreSlim only frees anything via
// AvailableWaitHandle, which this class never touches (grepped repo-wide, zero hits), so skipping
// the call leaks nothing and removes the whole exception class rather than papering over one
// throw site.
//
// WHAT IS REAL HERE. This exercises the actual private field and the actual public Dispose() on
// the actual compiled Sessions component via reflection -- not a hand-written stand-in for the
// class. WHAT IS NOT is a full render or a real navigation: HtmlRenderer cannot dispatch the
// second NavigationManager.LocationChanged that used to trigger this in production, and a race on
// timing (does Dispose() land before or after Wait(0) returns) is not something a unit test can
// force deterministically. So instead of racing, this test constructs the exact ordering the live
// repro proved -- lock acquired, then disposed, then released -- and asserts the release no
// longer throws. The live DevBridge repro is the re-verify phase's job, per the wave brief.

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using Xunit;

// Filed under Gated/ (2026-08-21): typeof(SQLTriage.Pages.Sessions) binds Pages/Sessions.razor,
// which buildprofile.targets Content-Removes for the community profile -- the only one CI builds.
// This test still exercises the real compiled Sessions component; only its address moved. See
// Gated/README.md.

namespace SQLTriage.Tests.Gated
{
    public sealed class SessionsDisposeLifecycleTests
    {
        private static object NewSessionsComponent()
        {
            var type = typeof(SQLTriage.Pages.Sessions);
            var instance = Activator.CreateInstance(type);
            Assert.NotNull(instance);
            return instance!;
        }

        private static void SetInjectedProperty<T>(object target, T value)
        {
            var prop = target.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(p => p.PropertyType == typeof(T));
            prop.SetValue(target, value);
        }

        private static SemaphoreSlim GetLoadLock(object target)
        {
            var field = target.GetType().GetField("_loadLock", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (SemaphoreSlim)field!.GetValue(target)!;
        }

        /// <summary>
        /// Reproduces the exact defect ordering without needing a full render or a real race: a load
        /// past its Wait(0) guard, a navigate-away Dispose() while it is still in flight, and the
        /// in-flight call's own Release() completing afterwards in its finally block.
        /// </summary>
        [Fact]
        public void Dispose_WhileALoadIsInFlight_DoesNotPoisonTheSemaphore_ForTheLoadsOwnRelease()
        {
            var sessions = NewSessionsComponent();
            SetInjectedProperty(sessions, new GlobalInstanceSelector(NullLogger<GlobalInstanceSelector>.Instance));

            var loadLock = GetLoadLock(sessions);

            // LoadSessions has passed its guard and is now awaiting the SQL call.
            loadLock.Wait(0).Should().BeTrue("this simulates the in-flight load having already taken the lock");

            // The user navigates away; Blazor tears the component down mid-load.
            var dispose = () => ((IDisposable)sessions).Dispose();
            dispose.Should().NotThrow("navigating away mid-load must not itself throw");

            // The in-flight LoadSessions's finally block runs after Dispose() returned. Before the
            // fix this threw ObjectDisposedException, which is what MainLayout's ErrorBoundary
            // swallowed into a permanently blank app.
            var release = () => loadLock.Release();
            release.Should().NotThrow<ObjectDisposedException>(
                "the load's own finally-block Release() must survive a concurrent Dispose(), or every "
                + "navigation away from an in-flight Sessions load blanks the app");
        }
    }
}
