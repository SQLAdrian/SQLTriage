/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using SQLTriage.Data.Services.Licensing.Crypto;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// A <see cref="FactAttribute"/> that reports <b>SKIPPED</b> - not passed - when the BIP39
/// wordlist resource is not embedded in the SQLTriage assembly under test, by computing
/// <see cref="FactAttribute.Skip"/> at discovery time.
///
/// <para>WHY. Resources\bip39-english.txt is gitignored and fetched by
/// tools\fetch-bip39-wordlist.ps1, so a fresh clone, a fresh worktree and every CI runner build
/// SQLTriage.dll without it (SQLTriage.csproj:472 includes the EmbeddedResource only when the
/// file Exists). Bip39MirrorTests is NOT profile-gated, so it is discovered and run in the one
/// job CI does run. Before this attribute, eight of its nine facts opened with
/// <c>if (!WordlistAvailable()) return;</c> and reported Passed for a run that asserted nothing:
/// eight assertion-free greens folded into every later "5153 passed" claim. That is the house
/// defect class - a verdict not conditioned on the measurement it names - and it is the same
/// class <see cref="SQLTriage.Tests.LiveFactAttribute"/> was written for.</para>
///
/// <para>WHY NOT <see cref="SQLTriage.Tests.LiveFactAttribute"/> DIRECTLY. That one keys off
/// environment variables; the discriminator here is a resource embedded in the assembly, which
/// no environment variable can describe. Same discovery-time mechanism, different predicate.
/// That difference is also why Portal/LiveHarnessArmingCensusTests never flagged these eight:
/// its structural walk matches the literal <c>GetEnvironmentVariable(</c> shape, and this file
/// never had one. The category is identical; the census sees only the literal form (it says so
/// itself, 2026-08-13).</para>
///
/// <para>PROFILE-NEUTRAL ON PURPOSE. Tests/SQLTriage.Tests/Licensing/ is not Compile-Removed for
/// <c>-p:SQLTriageProfile=community</c> (only Licensing\ActivateFullAuditCardRenderTests.cs is),
/// and this type binds nothing that community removes - Bip39 lives in
/// Data\Services\Licensing\Crypto\ which every profile compiles. Binding a community-removed
/// type from a non-gated test is the house lesson "profile-gated code needs profile-gated tests"
/// that cost twelve days of an uncompilable community build.</para>
///
/// <para>THE ATTRIBUTE IS NOT THE WHOLE GUARD. Each test body must ALSO assert the wordlist is
/// present, so that if this attribute is ever weakened or removed the body FAILS rather than
/// passing vacuously. See Bip39MirrorTests.RequireWordlist - it matters most for the two facts
/// (empty phrase, wrong word count) whose assertion throws before Bip39 ever loads the
/// wordlist, and which would therefore pass without it.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class Bip39WordlistFactAttribute : FactAttribute
{
    public Bip39WordlistFactAttribute()
    {
        if (!Bip39WordlistProbe.Available)
            Skip = "BIP39 wordlist not embedded in this build of SQLTriage.dll, so there is "
                   + "nothing for this test to encode against. Run tools/fetch-bip39-wordlist.ps1 "
                   + "from the repo root and rebuild to run it. CI builds the community profile "
                   + "and never fetches the wordlist, so Skipped is the expected verdict there.";
    }
}

/// <summary>
/// The wordlist predicate, taken once per test process off the assembly actually loaded.
///
/// <para>Written as a probe rather than a File.Exists on the source tree deliberately: the
/// binary under test may have been built in a tree that had the wordlist, or not, and the only
/// honest question is whether THIS SQLTriage.dll carries the embedded resource. Bip39.EnsureLoaded
/// throws InvalidOperationException for both the missing-resource and corrupt-wordlist cases;
/// both mean the same thing to a caller, so both are reported as unavailable.</para>
/// </summary>
internal static class Bip39WordlistProbe
{
    private static readonly Lazy<bool> _available = new(Probe);

    /// <summary>True when SQLTriage.dll carries a loadable 2048-word BIP39 wordlist.</summary>
    public static bool Available => _available.Value;

    private static bool Probe()
    {
        try
        {
            // Any valid 32-byte input forces the wordlist load; the output is discarded.
            Bip39.Encode(new byte[32]);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
