/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports <b>SKIPPED</b> — not passed — when the named
/// environment variables are not set, by computing <see cref="FactAttribute.Skip"/> at discovery
/// time.
///
/// <para>WHY. A live harness that early-returns to green when its target is unset reports
/// "Passed: 3, Skipped: 0" on a box with no instance: three assertion-free passes folded into every
/// later "suite green" claim. That is the house defect class — a verdict not conditioned on the
/// measurement it names. Portal/LiveHarnessArmingCensusTests is the ruling, and its structural
/// census walks the WHOLE test tree looking for exactly that shape.</para>
///
/// <para>⚠ WHY NOT <c>ArmedLiveFactAttribute</c> DIRECTLY. That type lives under Tests/**/Portal/,
/// which SQLTriage.Tests.csproj Compile-Removes for <c>-p:SQLTriageProfile=community</c>. A harness
/// that is NOT community-gated cannot bind it without making the community test assembly
/// uncompilable — the house lesson "profile-gated code needs profile-gated tests", which cost
/// twelve days of a broken community build. This is the same discovery-time mechanism, stated
/// profile-neutrally. It also takes the arming value from the target variable ITSELF rather than a
/// separate <c>=1</c> flag, because the target is the instance name (or the directory, or the
/// output path) and a second flag would be a thing to forget.</para>
///
/// <para>⚠ THIS FILE IS A PROMOTION, 2026-08-14, and the promotion was pre-authorised. The type was
/// nested inside IndexAnalysisLiveSmokeTests with a note reading "Local to this file deliberately —
/// it is the first of its kind outside Portal/. Move it to a shared, profile-neutral file the
/// moment a second non-Portal harness wants it." AuditRestartBannerRenderTests is that second
/// harness: it plants a restarted audit chain into a directory the caller names, which must not run
/// unarmed. Nothing about the mechanism changed in the move.</para>
///
/// <para>Written without an early <c>return</c> on purpose: an arming read and a bare
/// <c>return;</c> within a few lines is the exact source shape the structural census forbids, and
/// this constructor — which SETS Skip rather than skipping work — must not read as that shape to
/// anyone, scanner or human.</para>
///
/// <para>⚠ THE ATTRIBUTE IS NOT THE WHOLE GUARD. Each armed body must ALSO assert its arming
/// variable, so that if this attribute is ever weakened or removed the body FAILS rather than
/// passing vacuously. See IndexAnalysisLiveSmokeTests.RequireTarget.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(params string[] requiredEnvironmentVariables)
    {
        var unset = requiredEnvironmentVariables.FirstOrDefault(
            name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

        if (unset is not null)
            Skip = $"Live harness not armed. Set {unset} to run it. An armed run needs the fixture "
                   + "its harness documents; see the INVOCATION block on the test's own class.";
    }
}
