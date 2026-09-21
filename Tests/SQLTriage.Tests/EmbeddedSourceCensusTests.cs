/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using SQLTriage.Data;
using SQLTriage.Tests.Build;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>the shipped binary must carry no hand-written first-party source text.</b>
///
/// <para><b>WHAT HAPPENED.</b> Until 2026-09-14 the full release shipped the application's COMPLETE
/// SOURCE. Measured on the artifact that ships — the single-file exe extracted from
/// <c>release/full/*.zip</c> — 629 source files, 15,945,475 bytes, recovered from the type-17
/// EMBEDDED_PORTABLE_PDB and proved to be the compiler's own input: all 629 blobs hashed to the
/// SHA-256 the PDB's own <c>Document</c> table recorded for them. 128 of them were community-EXCLUDED
/// units (Portal 39, AccessSurface 20, RiskReport 5, DevBridge 4, Mcp 3, plus 49 community-removed
/// <c>.razor</c> pages). The whole community/full/private IP boundary was moot for anyone holding a
/// full zip. Commit <c>12f7bcd</c> set <c>EmbedAllSources=false</c> and
/// <c>EmbedUntrackedSources=false</c>.</para>
///
/// <para><b>WHY A CENSUS AND NOT A PINNED NUMBER.</b> Two MSBuild properties, an SDK default and a
/// source generator all decide this, and NOT ONE of them is visible in the source tree. Nobody can
/// read the csproj and know what the binary carries — the only way to know is to read the binary. A
/// census that reads the emitted metadata goes red when any of those four levers moves, including
/// levers nobody has thought of yet. See <see cref="EmbeddedSourceAnalyzer"/> for how it reads
/// structure (debug directory entry types, CDI kind GUIDs, parent handle tables, row counts) rather
/// than characters, and for what it cannot see.</para>
///
/// <para><b>⚠ THE RESIDUAL, AND THE MECHANISM NOBODY HAS FOUND.</b> 155 documents are STILL embedded,
/// 7,686,112 bytes, every one of them a generated <c>*_razor.g.cs</c>. Nothing known reaches them:
/// <c>EmbedAllSources</c> does not, <c>EmbedUntrackedSources</c> does not,
/// <c>EmbedRazorGenerateSources</c> is already false by SDK default, and a target clearing
/// <c>@(EmbeddedFiles)</c> <c>BeforeTargets="CoreCompile"</c> was tried on 2026-09-14 and changed the
/// measurement by zero bytes (see the comment above <c>&lt;/Project&gt;</c> in SQLTriage.csproj — do
/// not re-add it). <b>THIS CENSUS ONLY DETECTS THAT RESIDUAL; IT DOES NOT FIX IT, AND NEITHER DOES
/// ANYTHING ELSE IN THE REPO.</b> One OBSERVATION worth recording for whoever picks it up: every one
/// of the 155 document paths lies under the Razor source generator's own output directory
/// (<c>obj/.../Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/</c>) — PROVED, it is
/// what this census reads. The inference that Roslyn embeds generator-produced documents
/// unconditionally, because there is no file on disk for a debugger to open, is BELIEVED and
/// UNTESTED. The only lever known to work is <c>DebugType=portable</c> with an unshipped <c>.pdb</c>,
/// and it costs a client's stack traces their file:line — which Adrian explicitly declined.</para>
///
/// <para><b>WHAT THIS CLASS DOES NOT COVER, said plainly.</b> It reads <c>SQLTriage.dll</c> as built,
/// not the single-file exe and not the zip. It judges document PATHS and row COUNTS, never the text
/// inside a blob. It says nothing about source files copied into the publish tree as content — that
/// is <c>tools/verify-release-archive.ps1</c>'s job. And a green run here on the full profile says
/// nothing about the community profile's binary; build it.</para>
///
/// <para><b>⚠ AND IT SAYS NOTHING AT ALL ABOUT A DEBUG BUILD.</b> Every number here comes from the
/// embedded portable PDB, which only Release embeds — MEASURED 2026-09-16: 155 embedded documents in
/// Release-full, ZERO in Debug-full, where the PDB sits beside the assembly as a separate file. So
/// <see cref="ReadCensus"/> calls
/// <see cref="ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/>
/// and this class FAILS rather than reporting on a build its numbers were not frozen in. That it keeps
/// doing so is enforced by <see cref="ProductCountGuardCensusTests"/>, which enumerates every test type
/// that reads the product assembly's IL or metadata from the compiled test assembly itself.</para>
/// </summary>
public sealed class EmbeddedSourceCensusTests
{
    private readonly ITestOutputHelper _out;
    public EmbeddedSourceCensusTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The compiled product assembly, resolved from a type the test project BINDS rather than from a
    /// path literal. <c>typeof(ConfigFileHelper).Assembly</c> is the same handle
    /// <c>ConfigStoreI1CensusTests</c> uses, and its <c>Location</c> is the copy MSBuild produced and
    /// staged for THIS test run — so the census cannot drift onto a stale build sitting in some other
    /// <c>bin\</c>, which is how a verifier scanned an intermediate it had not built on 2026-09-11.
    /// </summary>
    private static Assembly Product => typeof(ConfigFileHelper).Assembly;

    /// <summary>
    /// Reads the census — and REFUSES A BUILD CONFIGURATION THESE NUMBERS WERE NOT FROZEN IN before
    /// returning it.
    ///
    /// <para><b>THE GUARD IS HERE, at the one place every census-reading test in this class passes
    /// through, rather than copied into each <c>[Fact]</c>.</b> Five of the six facts below rest on
    /// numbers read out of the compiled assembly, and five hand-edited copies of a configuration check
    /// is how a SET gets shipped as an INSTANCE. A sixth test added later cannot forget it.</para>
    ///
    /// <para><b>WHY THIS CLASS NEEDED IT, MEASURED 2026-09-16 at <c>7b997b4</c> and not assumed.</b>
    /// Release EMBEDS the portable PDB — 155 source documents, 1,851,736 bytes, 784 <c>Document</c>
    /// rows — and Debug ships it as a SEPARATE <c>SQLTriage.pdb</c> of 3,124,112 bytes beside the
    /// assembly. So in a Debug build every number this census reads is ZERO: embedded sources 0,
    /// <c>HasEmbeddedPortablePdb</c> false, debug directory entry types <c>[2, 19, 16]</c> with no
    /// type 17. Three of the six here failed on their own assertions in Debug, a fourth on a confound, and — worse — the one that matters most,
    /// <see cref="No_hand_written_source_is_embedded_in_the_shipped_assembly"/>, PASSED over zero
    /// embedded sources. That is an IP boundary failing toward CLEAN, the same shape as the profile
    /// verifier that scanned an intermediate and passed a 174 MB full build as community on
    /// 2026-09-11.</para>
    ///
    /// <para><b>AFTER this change, MEASURED the same day on built cells:</b> in Debug FIVE of the
    /// six facts fail and every one of them fails AT THE GUARD, not on a wrong number - the sixth
    /// reads no assembly at all. And because a guard on the CONFIGURATION cannot see an OPTIMIZED
    /// assembly whose PDB is merely not embedded, the liveness pair in ReadCensus covers that case
    /// for every fact rather than only the one that was caught: proved in a Release build with
    /// <c>DebugType=portable</c>, where the configuration guard correctly PASSES and the liveness
    /// fires instead.</para>
    ///
    /// <para>Enforced, not documented: <see cref="ProductCountGuardCensusTests"/> walks the test
    /// assembly's IL and goes RED if this class stops reaching the guard.</para>
    /// </summary>
    private EmbeddedSourceAnalyzer.Census ReadCensus()
    {
        ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn(
            Product, _out,
            "HasEmbeddedPortablePdb, the type-17 entry size, DocumentRows, MethodDebugInformationRows, "
            + "CustomDebugInformationRows and the 155-document residual - every one of them frozen "
            + "against a Release build, in which the portable PDB is EMBEDDED. A Debug build reads "
            + "ZERO for all of them because it writes the PDB to a separate file beside the assembly.");

        var census = EmbeddedSourceAnalyzer.Read(Product.Location);
        _out.WriteLine("ASSEMBLY UNDER CENSUS: " + census.DescribeProvenance());
        _out.WriteLine($"  debug directory entries : {census.DebugDirectoryEntryCount} " +
                       $"[types {string.Join(", ", census.DebugDirectoryEntryTypes)}]");
        _out.WriteLine($"  embedded portable pdb   : {census.HasEmbeddedPortablePdb} " +
                       $"({census.EmbeddedPdbDataSize:N0} bytes)");
        _out.WriteLine($"  Document rows           : {census.DocumentRows:N0}");
        _out.WriteLine($"  MethodDebugInformation  : {census.MethodDebugInformationRows:N0}");
        _out.WriteLine($"  CustomDebugInformation  : {census.CustomDebugInformationRows:N0}");
        _out.WriteLine($"  EmbeddedSource rows     : {census.EmbeddedSources.Count:N0} " +
                       $"({census.EmbeddedSourceBytes:N0} uncompressed bytes)");
        _out.WriteLine("  CDI kind histogram:");
        _out.WriteLine(census.DescribeKindHistogram());
        _out.WriteLine("  CDI parent-table histogram:");
        _out.WriteLine(census.DescribeParentHistogram());
        // ⚠ ABSENT IS NOT CLEAN - AND IT IS ASSERTED HERE, AT THE CHOKEPOINT, BECAUSE THE SET IS
        // FIVE, NOT ONE. Every fact in this class that counts or intersects anything reads it out of
        // the EMBEDDED portable PDB. When there is no embedded PDB the census does not report
        // "nothing is embedded" - it reports NOTHING, and an emptiness assertion over a dead
        // instrument is green. MEASURED 2026-09-16 in a Release build with DebugType=portable
        // (optimized, so the configuration guard above PASSES and cannot catch this): TWO
        // IP-boundary facts in this class both passed over ZERO embedded sources -
        // No_hand_written_source_is_embedded_in_the_shipped_assembly and
        // Community_excluded_embedded_units_do_not_grow_beyond_the_known_residual. The second one
        // controls both halves of its PROFILE model and neither half of its CENSUS, which is how it
        // stayed green. Two IP boundaries failing toward CLEAN is the 2026-09-11 shape exactly: a
        // verifier scanned an obj\ intermediate it had not built and passed a 174 MB FULL
        // build as community, exit 0. Fixing it inside one [Fact] would ship a SET as an INSTANCE.
        census.HasEmbeddedPortablePdb.Should().BeTrue(
            "every number in this census is read out of the EMBEDDED portable PDB, and this assembly "
            + "carries no type-17 EMBEDDED_PORTABLE_PDB entry - so every count in this class is ZERO "
            + "and every emptiness assertion over them is VACUOUS rather than clean. That is an "
            + "UNREADABLE binary, not a clean one. WHAT TO CHECK: the configuration guard above has "
            + "already cleared that this is a Debug build, so what is left to check is whether "
            + "DebugType is still embedded for this build - "
            + nameof(Release_assembly_carries_a_type_17_embedded_portable_pdb) + " pins that "
            + "separately and its message explains the csproj side - and whether the assembly read "
            + "is the one you think it is: " + census.DescribeProvenance());
        census.DocumentRows.Should().BeGreaterThan(0,
            "zero Document rows means the portable PDB carries no document table, so every search "
            + "for an embedded document in this class can only return nothing. That is a DEAD "
            + "INSTRUMENT, not a clean binary. WHAT TO CHECK: the debug directory entry types and "
            + "the PDB size printed above, before believing any emptiness in this class.");

        return census;
    }

    // ── 1. The constant, checked by double entry ──────────────────────────────────────────────────

    /// <summary>
    /// The anti-typo control, and it exists because the failure it guards ALREADY HAPPENED: on
    /// 2026-09-14 a gate agent mistyped one nibble of the EmbeddedSource kind GUID, no row matched,
    /// the count came back 0 and the binary was reported CLEAN. A wrong constant fails toward clean.
    ///
    /// <para>Two independent transcriptions — the canonical string and the 16-byte little-endian
    /// layout — must agree. A single mistyped nibble cannot corrupt both identically, so this
    /// assertion catches exactly the defect that shipped. It is checked BEFORE any count is believed.
    /// A count derived from an unverified constant is not evidence.</para>
    ///
    /// <para><b>The one fact in this class that does NOT go through <c>ReadCensus</c>, and so does not
    /// reach the configuration guard — deliberately.</b> It compares two hand-written constants with
    /// each other and reads no assembly at all, so there is no measured number here for a build
    /// configuration to invalidate. The reason is written here because this is where the next reader
    /// tempted to complete the set will be standing.</para>
    /// </summary>
    [Fact]
    public void EmbeddedSource_kind_guid_is_confirmed_by_two_independent_transcriptions()
    {
        _out.WriteLine($"canonical string form : {EmbeddedSourceAnalyzer.EmbeddedSourceKind:B}");
        _out.WriteLine($"raw little-endian form: {EmbeddedSourceAnalyzer.EmbeddedSourceKindFromRawBytes:B}");

        EmbeddedSourceAnalyzer.EmbeddedSourceKindFromRawBytes.Should().Be(
            EmbeddedSourceAnalyzer.EmbeddedSourceKind,
            "the EmbeddedSource kind GUID is written twice, in two encodings, precisely so a " +
            "one-nibble typo cannot pass unnoticed. On 2026-09-14 such a typo made this census " +
            "match ZERO rows and report a binary full of source as CLEAN. If these two disagree, " +
            "ONE of them is mistyped - re-derive both from the portable-PDB spec " +
            "(0E8A571B-6926-466E-B4AD-8AB04611F5FE) before trusting any count in this class.");

        EmbeddedSourceAnalyzer.EmbeddedSourceKind.ToString("D").ToUpperInvariant()
            .Should().Be("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    }

    // ── 2. The artifact exists and carries the PDB this census reads ──────────────────────────────

    /// <summary>
    /// (a) and (f). The assembly must be on disk and must carry a type-17 EMBEDDED_PORTABLE_PDB, and
    /// an assembly this census cannot read must FAIL rather than report nothing found.
    ///
    /// <para><b>Why an ABSENT embedded PDB fails here rather than passing.</b> "No embedded PDB" would
    /// satisfy the IP invariant trivially — but it would also mean the product had silently lost
    /// <c>DebugType=embedded</c>, i.e. lost the file:line in every client stack trace, which is the
    /// thing Adrian chose to keep when he took the source text out. Both directions of that trade
    /// are pinned: this fact holds the PDB present, and
    /// <see cref="Document_and_method_debug_information_rows_are_preserved"/> holds its contents
    /// non-empty. If the product ever DELIBERATELY moves to an unshipped <c>.pdb</c>, this is one of
    /// the tests that must be changed on purpose.</para>
    /// </summary>
    [Fact]
    public void Release_assembly_carries_a_type_17_embedded_portable_pdb()
    {
        var census = ReadCensus();

        census.HasEmbeddedPortablePdb.Should().BeTrue(
            "the product is built with DebugType=embedded (SQLTriage.csproj:198, the <DebugType>embedded</DebugType> property - grep the property, the line moves) so the compiled " +
            "assembly must carry a type-17 EMBEDDED_PORTABLE_PDB debug directory entry. Found entry " +
            $"types [{string.Join(", ", census.DebugDirectoryEntryTypes)}] instead. " +
            "A DEBUG BUILD PRODUCES EXACTLY THIS - entry types [2, 19, 16] and no type 17, because " +
            "Debug writes the PDB to a separate file beside the assembly (MEASURED 2026-09-16 on four " +
            "built cells). If you are reading this message then the configuration guard in ReadCensus " +
            "has already cleared that possibility and the assembly IS optimized. WHAT TO CHECK in " +
            "that case: either DebugType changed in SQLTriage.csproj, in which case this census and " +
            "the client file:line guarantee both need a deliberate decision, or this is not the " +
            "artifact you think it is: " +
            census.DescribeProvenance());

        census.DebugDirectoryEntryTypes.Should().Contain(
            EmbeddedSourceAnalyzer.EmbeddedPortablePdbDebugDirectoryType,
            "type 17 is EMBEDDED_PORTABLE_PDB by the PE debug directory spec, and reading it by " +
            "NUMBER as well as by the framework's enum keeps this test honest if the enum is ever " +
            "remapped.");

        census.EmbeddedPdbDataSize.Should().BeGreaterThan(0,
            "a type-17 entry with a zero-byte payload is an unreadable PDB, not a clean one.");
    }

    // ── 3. The count, cross-derived so a dead instrument cannot look clean ────────────────────────

    /// <summary>
    /// (b). Counts EmbeddedSource rows by kind GUID, and then checks that count against a SECOND,
    /// INDEPENDENT derivation: the number of CustomDebugInformation rows whose PARENT is a
    /// <c>Document</c> handle. The two routes share nothing — one reads the kind GUID heap, the other
    /// reads the parent coded index — so they can only agree if the GUID constant is right AND the
    /// walk is alive.
    ///
    /// <para>This is the assertion that turns a dead instrument RED. With only a forward count, every
    /// way of breaking this census (wrong GUID, broken enumeration, empty PDB) produces ZERO, and
    /// zero is the greenest possible result for an IP boundary. Five instruments in this repo reported
    /// success on 2026-09-10 while answering their own question; this is the control that stops this
    /// one joining them.</para>
    ///
    /// <para>⚠ <b>The honest limit.</b> Today EmbeddedSource is the ONLY CDI kind parented to a
    /// Document (measured: 155 = 155). If a future Roslyn adds another Document-parented kind, the two
    /// derivations will disagree and this goes red for a reason that is not an IP leak. The failure
    /// message prints the whole kind histogram so that diagnosis takes seconds, and a genuine fix
    /// that removes all embedded source leaves BOTH numbers at zero and still passes.</para>
    /// </summary>
    [Fact]
    public void Embedded_source_row_count_agrees_with_an_independent_derivation()
    {
        var census = ReadCensus();

        census.EmbeddedSources.Count.Should().Be(census.DocumentParentedRows,
            "two independent derivations of the same number must agree, or the instrument is not " +
            "measuring what it claims. Route 1 counted CustomDebugInformation rows whose KIND GUID " +
            $"is EmbeddedSource: {census.EmbeddedSources.Count}. Route 2 counted rows whose PARENT " +
            $"is a Document handle: {census.DocumentParentedRows}. A disagreement means either the " +
            "kind GUID constant is wrong (the 2026-09-14 defect - and note that a WRONG constant " +
            "makes route 1 report ZERO, which looks clean) or a new Document-parented CDI kind " +
            "exists. The full histogram says which:" + Environment.NewLine +
            census.DescribeKindHistogram() + Environment.NewLine +
            "parent tables:" + Environment.NewLine + census.DescribeParentHistogram());

        // The instrument must be ALIVE, independently of what it found. A PDB with no CDI rows at all
        // is an unreadable PDB, not a clean binary: every .NET assembly built by this SDK carries
        // CompilationOptions and CompilationMetadataReferences rows at minimum.
        census.CustomDebugInformationRows.Should().BeGreaterThan(0,
            "a portable PDB with ZERO CustomDebugInformation rows means the metadata walk is dead, " +
            "not that the binary is clean. ABSENT IS NOT CLEAN.");

        _out.WriteLine($"RESULT: {census.EmbeddedSources.Count} EmbeddedSource rows, " +
                       $"{census.EmbeddedSourceBytes:N0} uncompressed bytes.");
    }

    // ── 4. THE invariant ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// (c). <b>THE REAL GUARD IN THIS FILE.</b> No hand-written first-party source may be embedded in
    /// the shipped binary. It is currently ZERO, and zero is the number to defend: every one of the
    /// 629 files that shipped before 2026-09-14 would fail here.
    ///
    /// <para>"Generated" is decided by the Razor source generator's SIGNATURE, not by a name: the
    /// document path must lie under the generator's own output directory AND the file name must carry
    /// the <c>_razor.g.cs</c> suffix. A name test alone would let a hand-written
    /// <c>Anything_razor.g.cs</c> through. See
    /// <see cref="EmbeddedSourceAnalyzer.RazorGeneratorPathSegment"/> for why a Razor SDK upgrade can
    /// make this red without an IP leak.</para>
    ///
    /// <para>This fact carries NO allowlist and NO count. It does not care how many generated
    /// documents are embedded — that is
    /// <see cref="Community_excluded_embedded_units_do_not_grow_beyond_the_known_residual"/>'s
    /// business. It cares only that nothing HAND-WRITTEN is in there.</para>
    /// </summary>
    [Fact]
    public void No_hand_written_source_is_embedded_in_the_shipped_assembly()
    {
        var census = ReadCensus();

        // The liveness pair that makes this fact's emptiness MEAN something - there is an
        // embedded PDB and it has documents - is asserted in ReadCensus, because this fact is not
        // the only one that would otherwise pass over nothing. See the comment there.

        var handWritten = census.EmbeddedSources.Where(d => !d.IsRazorGenerated)
                                                .OrderBy(d => d.NormalizedPath, StringComparer.Ordinal)
                                                .ToList();

        if (handWritten.Count > 0)
        {
            _out.WriteLine($"HAND-WRITTEN SOURCE EMBEDDED: {handWritten.Count} document(s)");
            foreach (var d in handWritten.Take(50)) _out.WriteLine("    " + d);
        }

        handWritten.Should().BeEmpty(
            $"the shipped assembly must embed NO hand-written first-party source. Found " +
            $"{handWritten.Count} of them, totalling " +
            $"{handWritten.Sum(d => (long)d.UncompressedBytes):N0} bytes:" + Environment.NewLine +
            string.Join(Environment.NewLine, handWritten.Take(25).Select(d => "    " + d)) +
            (handWritten.Count > 25 ? Environment.NewLine + $"    ... and {handWritten.Count - 25} more" : "") +
            Environment.NewLine +
            "This is the 2026-09-14 defect returning: 629 files and 15,945,475 bytes of source text " +
            "inside the release artifact, including 128 community-excluded units. The levers are " +
            "EmbedAllSources and EmbedUntrackedSources in SQLTriage.csproj (both false since commit " +
            "12f7bcd) - check whether one was flipped back, or whether a new mechanism now embeds " +
            "source. Do NOT suppress this test to get a release out.");

        _out.WriteLine($"RESULT: 0 hand-written embedded documents out of " +
                       $"{census.EmbeddedSources.Count} embedded ({census.EmbeddedSourceBytes:N0} bytes, " +
                       "all Razor-generated).");
    }

    // ── 5. The community-excluded residual ────────────────────────────────────────────────────────

    /// <summary>
    /// The generated documents of community-REMOVED pages that are still embedded in the full
    /// binary. These carry markup AND <c>@code</c> for pages a community recipient is never meant to
    /// have, so this set is the sharp end of the IP boundary — and it must not grow.
    ///
    /// <para><b>⚠ THE BRIEF FOR THIS LANE SAID THIS SET WAS SIX, AND IT IS 49. MEASURED, not
    /// estimated.</b> The brief named six (AccessSurface, ExportPack, PortalStatus, PublishToPortal,
    /// ReplicationMap, RiskReport) and also asked — correctly — that the set be derived FROM
    /// <c>buildprofile.targets</c> rather than hand-listed. Those two instructions do not agree: every
    /// definition derivable from that file yields 49, not 6. Independently measured twice, by this
    /// test's own derivation and by a separate script outside the build. 49 is also exactly the
    /// number the original 2026-09-14 measurement recorded ("plus 49 community-removed .razor
    /// pages"), so the six appear to be a narrower hand-picked subset whose selection rule is not
    /// expressed anywhere in the repo. The six are named in <see cref="BriefNamedSubset"/> so the
    /// record connects, but the GUARD is the derived 49: a set that is derived cannot quietly
    /// disagree with the build, and a set of six would have passed while 43 gated pages leaked.</para>
    ///
    /// <para><b>2026-09-17: IN A FULL BUILD THE DERIVED SET IS 50, NOT 49.</b> Lane
    /// <c>page-route-profile-census</c> found <c>/live</c> (<c>Components/Dashboards/LiveDashboard.razor</c>)
    /// routable in the COMMUNITY build and ruled it a LiveMonitoring page, so buildprofile.targets now
    /// Content-Removes it for community. That one line puts the page into
    /// <see cref="ProfileGateModel.CommunitySurface"/>, which this fact reads, so its generated document,
    /// already embedded in the full binary, is now counted here. Measured by that lane's gate on a
    /// byte-identical full SQLTriage.dll: community-removed pages 50 to 51, still embedded 49 to 50. The
    /// 49 above is the 2026-09-14 derivation, kept as that lane's record. A full+private build embeds one
    /// more (51): SQLT_PERFREPORT compiles Pages/PerformanceReport.razor in, and that page's generated
    /// document is NOT in the known residual. That red was measured at 18f5171 too, so it predates the
    /// /live entry; it is a separate open question, not something this entry covers.</para>
    ///
    /// <para><b>How the set is derived.</b>
    /// <see cref="ProfileGateModel.CommunitySurface"/> evaluates buildprofile.targets' MSBuild
    /// conditions under the community profile and returns BOTH kinds of unit a community build
    /// excludes — the <c>.razor</c> pages it <c>Content Remove</c>s and the <c>.cs</c> files it
    /// <c>Compile Remove</c>s — globs expanded against disk. That is the existing, shared model;
    /// reusing it rather than writing a second parser is deliberate, because two parsers of the same
    /// file drift and the drift is invisible. Each removed page is mapped to the document path Razor
    /// generates for it; each removed <c>.cs</c> is matched by its own repo-relative path. Both are
    /// intersected with the embedded set.</para>
    ///
    /// <para><b>⚠ WHY BOTH KINDS, and how that was found.</b> The first version of this test
    /// intersected only the pages. Under the mutation that reinstates <c>EmbedAllSources=true</c> it
    /// PASSED — while 474 hand-written documents, 8,259,363 bytes, sat in the assembly, among them
    /// the <c>Portal</c>, <c>AccessSurface</c>, <c>RiskReport</c>, <c>DevBridge</c> and <c>Mcp</c>
    /// units that made up most of the 128 excluded units in the original incident. A guard that
    /// passes the very mutation reproducing the incident it was written for is not a guard. MEASURED
    /// before commit, which is the only reason it is not still true.</para>
    ///
    /// <para><b>What this fact does NOT do.</b> It does not fail when the set SHRINKS — a shrink is
    /// the fix landing. So the residual list below can rot downward silently, and that is an accepted
    /// trade: growth is the direction that leaks. It also cannot see a gated page whose gate is
    /// expressed outside buildprofile.targets (an <c>#if</c> fence, a runtime check).</para>
    /// </summary>
    [Fact]
    public void Community_excluded_embedded_units_do_not_grow_beyond_the_known_residual()
    {
        var census = ReadCensus();
        var root = RawPassedScan.RepoRoot();

        var surface = ProfileGateModel.CommunitySurface(root);
        var removedPages = surface.RemovedPages;  // .razor a community build Content-Removes
        var removedFiles = surface.Files;         // .cs   a community build Compile-Removes

        // INSTRUMENT CONTROL, first and separately, on BOTH halves. If the model stops reading
        // buildprofile.targets the intersection becomes empty and this fact would pass over nothing -
        // the single most common way a census in this repo has reported a false clean.
        removedPages.Count.Should().BeGreaterThan(0,
            "ProfileGateModel.CommunitySurface returned ZERO community-removed .razor pages, which " +
            "means it is not reading buildprofile.targets' <Content Remove=\"...razor\"> entries. A " +
            "guard that intersects against an empty set finds nothing and looks clean. Fail, do not " +
            "pass over nothing.");
        removedFiles.Count.Should().BeGreaterThan(0,
            "ProfileGateModel.CommunitySurface returned ZERO community Compile-Removed .cs files, so " +
            "the never-ship module globs (Mcp, Portal, AccessSurface, RiskReport, DevBridge) are not " +
            "being read. Those were most of the 128 excluded units that shipped on 2026-09-14; " +
            "intersecting against an empty set would look clean.");

        // MAPPING SELF-TEST, on a literal. If Razor's generated-file naming changes, every lookup
        // below silently misses and the census reports clean. This pins the mapping independently of
        // what today's build happens to contain.
        EmbeddedSourceAnalyzer.GeneratedDocumentPathForRazorPage("Pages/AccessSurface.razor")
            .Should().Be("Pages/AccessSurface_razor.g.cs",
                "the page -> generated-document mapping is what makes the page half of this " +
                "intersection work. Measured against a real build on 2026-09-14.");

        // Every community-excluded unit keyed by the document path it would carry IF embedded.
        var excludedUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in removedPages)
            excludedUnits[EmbeddedSourceAnalyzer.GeneratedDocumentPathForRazorPage(page)] = page;
        foreach (var file in removedFiles)
            excludedUnits[file.Replace('\\', '/')] = file;

        // docKey -> the buildprofile.targets unit it came from, so a failure names the SOURCE.
        var foundUnits = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var doc in census.EmbeddedSources)
        {
            var p = doc.NormalizedPath;
            foreach (var kv in excludedUnits)
            {
                if (p.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("/" + kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    foundUnits[kv.Key] = kv.Value;
                    break;
                }
            }
        }

        var found = foundUnits.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // POSITIVE-CAPABILITY CONTROL, and it is deliberately conditional. "Absence of evidence is
        // evidence of absence ONLY once the instrument is proved able to show a positive." While ANY
        // Razor-generated document is embedded, at least one of them must still resolve to a known
        // residual entry - otherwise the mapping has drifted and a broken lookup is reading as clean.
        // ⚠ Narrow false-red: if the residual is genuinely fixed to zero WHILE non-gated generated
        // pages remain embedded, this fires on an improvement. That red means "re-derive, then relax
        // me deliberately" - it does not mean a leak. A build with no embedded source at all skips
        // this control, because then there is nothing for the mapping to find.
        // ⚠ THE PROFILE MUST BE ESTABLISHED FROM THE ASSEMBLY, NOT ASSUMED, AND NOT READ FROM
        // buildprofile.targets. PROVED 2026-09-14 by running this class under CI's own command
        // (`-p:SQLTriageProfile=community`): a community build REMOVES the gated pages, so none of
        // the residual entries can resolve, while non-gated generated pages stay embedded - so
        // the control below fired and returned EXIT 1. It fired on the state it exists to call an
        // improvement, and it would have turned CI red on every push while CI minutes are blocked.
        // ProfileGateModel reads the TARGETS FILE, which describes what a community build WOULD
        // remove; it cannot tell you which profile produced the binary in front of you. Only the
        // binary can. AccessSurfaceCollector is Compile-Removed at buildprofile.targets:230, so its
        // presence IS the discriminator.
        var gatedType = Product.GetType("SQLTriage.Data.Services.AccessSurfaceCollector", throwOnError: false);
        var isFullBuild = gatedType is not null;

        _out.WriteLine($"profile detected from the assembly : {(isFullBuild ? "FULL" : "COMMUNITY")}");

        if (!isFullBuild)
        {
            // ABSENT IS NOT CLEAN - so prove the discriminator actually discriminates rather than
            // silently reading every build as community. A community assembly must still contain a
            // type the community profile KEEPS; if neither the gated type nor a kept type resolves,
            // the lookup is broken and we must fail, not skip.
            Product.GetType("SQLTriage.Data.ConfigFileHelper", throwOnError: false)
                .Should().NotBeNull(
                    "neither the community-EXCLUDED type (AccessSurfaceCollector) nor a type the " +
                    "community profile KEEPS (ConfigFileHelper) resolved from the product assembly. " +
                    "That is a broken reflection lookup, not a community build, and it would make " +
                    "this fact skip its residual control silently on every profile.");

            _out.WriteLine($"community build: the {KnownResidual.Count} residual entries are Content/Compile-Removed here,");
            _out.WriteLine("so the residual control does not apply. The 'unexpected' assertion below");
            _out.WriteLine("STILL RUNS - a gated unit embedded in a COMMUNITY binary is a real leak.");
        }
        else if (census.EmbeddedSources.Any(d => d.IsRazorGenerated))
        {
            found.Any(f => KnownResidual.Contains(f)).Should().BeTrue(
                $"the census found {census.EmbeddedSources.Count} embedded documents, " +
                $"{removedPages.Count} community-removed pages and {removedFiles.Count} " +
                "community-removed .cs files, but NOT ONE of the known residual entries resolved " +
                "IN A FULL BUILD (AccessSurfaceCollector is present, so the gated pages were " +
                "compiled). That is a broken lookup, not a clean binary: Razor has probably changed " +
                $"the name it gives a generated component (expected '{EmbeddedSourceAnalyzer.RazorGeneratedSuffix}'). " +
                "Re-derive it from a fresh build. If the residual has genuinely been fixed, relax " +
                "this control deliberately and say so in the commit message.");
        }

        _out.WriteLine($"community-removed .razor pages   : {removedPages.Count}");
        _out.WriteLine($"community-removed .cs files      : {removedFiles.Count}");
        _out.WriteLine($"of those, STILL EMBEDDED         : {found.Count}");
        foreach (var kv in foundUnits) _out.WriteLine($"    {kv.Key}   <- {kv.Value}");

        var unexpected = foundUnits.Where(kv => !KnownResidual.Contains(kv.Key))
                                   .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        unexpected.Should().BeEmpty(
            $"{unexpected.Count} community-excluded unit(s) are embedded in the shipped binary and " +
            "are NOT in the known residual (frozen 2026-09-14, one entry added 2026-09-17) " +
            "(document <- excluded unit):" +
            Environment.NewLine +
            string.Join(Environment.NewLine,
                        unexpected.Take(30).Select(u => $"    + {u.Key}   <- {u.Value}")) +
            (unexpected.Count > 30 ? Environment.NewLine + $"    ... and {unexpected.Count - 30} more" : "") +
            Environment.NewLine +
            "Each one is source text for a unit a community recipient is never meant to have, sitting " +
            "inside the full release artifact - that is the 2026-09-14 incident, in which 128 such " +
            "units shipped. This is NOT a list to top up without thinking: a NEW entry means either a " +
            "new gated unit was added (decide whether its source text may ship, then record the " +
            "decision here with the date) or the embedding mechanism widened. A hand-written .cs " +
            "appearing here is the SERIOUS case - it means EmbedAllSources or EmbedUntrackedSources " +
            "is back on, and " + nameof(No_hand_written_source_is_embedded_in_the_shipped_assembly) +
            " will say so too. The mechanism that embeds the 155 generated documents is UNKNOWN - see " +
            "the class comment - so a new entry cannot be assumed harmless.");

        // The brief's six must remain a SUBSET of what we derived, or the record and the code have
        // parted company and one of them is wrong.
        foreach (var six in BriefNamedSubset)
            KnownResidual.Should().Contain(six,
                $"'{six}' is named in the 2026-09-14 record as a community-excluded page whose " +
                "source still ships, so it must appear in the derived residual. If it does not, " +
                "either the page was renamed or the derivation changed - reconcile the record.");
    }

    /// <summary>
    /// THE KNOWN RESIDUAL, frozen 2026-09-14 by measurement, not by estimate: the community-excluded
    /// units whose source text is still embedded in the full binary after
    /// <c>EmbedAllSources=false</c> and <c>EmbedUntrackedSources=false</c>. Today every entry is a
    /// community-removed <c>.razor</c> page's generated document, and NO hand-written <c>.cs</c> is
    /// in this list - a <c>.cs</c> arriving here is the serious case, not a routine addition.
    ///
    /// <para><b>This is an EXCEPTION LIST, not an approval.</b> Every entry is source text for a
    /// gated page sitting inside the shipped artifact. They are listed rather than counted because a
    /// count cannot tell you WHICH page appeared, and rule 4 of this repo's generation discipline
    /// forbids pinning a number and calling it the guarantee. The GUARD is the derivation in
    /// <see cref="Community_excluded_embedded_units_do_not_grow_beyond_the_known_residual"/>, which
    /// reads buildprofile.targets and the binary; this list only records what was already true when
    /// the guard was written.</para>
    ///
    /// <para>Reason for each entry is the same and is the honest one: <b>mechanism unknown, detection
    /// only.</b> Nothing in the SDK's documented surface reaches a Razor-generated document, four
    /// levers were tried and measured on 2026-09-14, and the one lever that works costs client-side
    /// file:line. So these stay, and this list makes them visible and bounded.</para>
    ///
    /// <para><b>One entry added since, dated at the entry:</b> 2026-09-17,
    /// <c>Components/Dashboards/LiveDashboard_razor.g.cs</c>. It reclassifies source text that already
    /// shipped in the full binary; it is not new source text shipping.</para>
    /// </summary>
    private static readonly HashSet<string> KnownResidual = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Never-ship modules. The sharpest end: code no community recipient is meant to have. ──
        "Pages/AccessSurface_razor.g.cs",
        "Pages/Portal/ExportPack_razor.g.cs",
        "Pages/Portal/PortalStatus_razor.g.cs",
        "Pages/Portal/PublishToPortal_razor.g.cs",
        "Pages/RiskReport_razor.g.cs",
        // ── SQLTExcludeOperations ──
        "Pages/AgentJobTimeline_razor.g.cs",
        "Pages/AlertingConfig_razor.g.cs",
        "Pages/Alerts_razor.g.cs",
        "Pages/AlertsNoc_razor.g.cs",
        "Pages/EditAuditScripts_razor.g.cs",
        "Pages/EnvironmentView_razor.g.cs",
        "Pages/QueryExecutor_razor.g.cs",
        "Pages/ReplicationMap_razor.g.cs",
        "Pages/ScheduledTasks_razor.g.cs",
        "Pages/ServiceManagement_razor.g.cs",
        "Pages/Services_razor.g.cs",
        // ── SQLTExcludeLiveMonitoring ──
        "Pages/Dashboard_razor.g.cs",
        "Pages/InstanceOverview_razor.g.cs",
        "Pages/LongQueries_razor.g.cs",
        "Pages/PerformanceTrends_razor.g.cs",
        "Pages/Pevents_razor.g.cs",
        "Pages/Pmemory_razor.g.cs",
        "Pages/PmemoryAnalysis_razor.g.cs",
        "Pages/Pquery_razor.g.cs",
        "Pages/QueryStore_razor.g.cs",
        "Pages/SchedulerHealth_razor.g.cs",
        "Pages/Sessions_razor.g.cs",
        "Pages/WaitEvents_razor.g.cs",
        "Pages/XEvents_razor.g.cs",
        // Added 2026-09-17, lane page-route-profile-census. RULING (that lane's orchestrator, recorded as
        // the approved route table in RouteProfileCensusTests): /live is a LiveMonitoring page, so
        // buildprofile.targets now Content-Removes Components/Dashboards/LiveDashboard.razor for community.
        // Its source text stays embedded in the FULL artifact on the same terms as the LiveMonitoring pages
        // above: mechanism unknown, detection only. This is a reclassification, not new source text: the
        // full SQLTriage.dll was byte-identical with and without that Content Remove (measured by the lane
        // gate), and the same line took the page out of the community compile (community routes 43 to 42).
        "Components/Dashboards/LiveDashboard_razor.g.cs",
        // ── SQLTExcludePremium ──
        "Components/Shared/ActivateFullAuditCard_razor.g.cs",
        "Components/Shared/FullAuditUpsellPill_razor.g.cs",
        "Components/Shared/PremiumLockCard_razor.g.cs",
        "Pages/AdvancedReporting_razor.g.cs",
        "Pages/AgentJobGuard_razor.g.cs",
        "Pages/AgentJobSync_razor.g.cs",
        "Pages/BestPractice_razor.g.cs",
        "Pages/CapacityConsolidation_razor.g.cs",
        "Pages/Premium_razor.g.cs",
        "Pages/Remediation_razor.g.cs",
        "Pages/ServerConfiguration_razor.g.cs",
        // ── SQLTExcludeDevTools ──
        "Components/DevTools/PerfLoadWaterfall_razor.g.cs",
        "Pages/BuildProfile_razor.g.cs",
        "Pages/CheckValidator_razor.g.cs",
        "Pages/ImportResults_razor.g.cs",
        "Pages/OffboardingTrace_razor.g.cs",
        "Pages/RemediationLab_razor.g.cs",
        "Pages/RemediationTuner_razor.g.cs",
        "Pages/SodMatrix_razor.g.cs",
        "Pages/TestPlan_razor.g.cs",
    };

    /// <summary>
    /// The six this lane's brief named. Kept so the written record and the code can be reconciled,
    /// and asserted to be a SUBSET of the derived residual. It is NOT the guard: see the class
    /// comment for why six was wrong and 49 was measured (50 in a full build since 2026-09-17).
    /// </summary>
    private static readonly string[] BriefNamedSubset =
    {
        "Pages/AccessSurface_razor.g.cs",
        "Pages/Portal/ExportPack_razor.g.cs",
        "Pages/Portal/PortalStatus_razor.g.cs",
        "Pages/Portal/PublishToPortal_razor.g.cs",
        "Pages/ReplicationMap_razor.g.cs",
        "Pages/RiskReport_razor.g.cs",
    };

    // ── 6. The half Adrian chose to KEEP ──────────────────────────────────────────────────────────

    /// <summary>
    /// (e). <c>Document</c> and <c>MethodDebugInformation</c> rows are what put file:line into a
    /// client's stack trace, and Adrian explicitly chose to keep them when the source TEXT came out.
    /// They are a separate thing from EmbeddedSource: <c>DebugType=embedded</c> supplies the sequence
    /// points and document paths, <c>EmbedAllSources</c> added the text on top, and only the text was
    /// dropped.
    ///
    /// <para>This fact is the other half of the trade, pinned so that a future "fix" for the 155
    /// residual documents cannot quietly buy a clean census by deleting the debug information. The
    /// obvious such fix — <c>DebugType=portable</c> with an unshipped <c>.pdb</c> — takes these rows
    /// to zero and would go RED here. That is the intent: it is a decision for Adrian, not a
    /// side effect of a green gate.</para>
    /// </summary>
    [Fact]
    public void Document_and_method_debug_information_rows_are_preserved()
    {
        var census = ReadCensus();

        census.DocumentRows.Should().BeGreaterThan(0,
            "the portable PDB's Document table is what maps IL offsets to file paths. Zero rows " +
            "means a client's stack trace has no file names. Adrian kept this deliberately when the " +
            "embedded source TEXT was removed on 2026-09-14 - it is not collateral, it is the half " +
            "of the trade that was chosen. If it is now zero, DebugType changed; that is a decision, " +
            "not a cleanup.");

        census.MethodDebugInformationRows.Should().BeGreaterThan(0,
            "the MethodDebugInformation table holds the sequence points that turn an IL offset into " +
            "a LINE NUMBER. Zero rows means every client stack trace loses file:line, which is the " +
            "cost of the one lever known to remove the 155 residual documents - and Adrian declined " +
            "it. A green census bought by deleting this is a regression, not a fix.");

        _out.WriteLine($"RESULT: Document rows {census.DocumentRows:N0}, " +
                       $"MethodDebugInformation rows {census.MethodDebugInformationRows:N0} - " +
                       "client file:line preserved.");
    }
}
