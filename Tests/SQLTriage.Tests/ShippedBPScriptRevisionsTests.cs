/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE ANTI-ROT PIN for <see cref="ShippedBPScriptRevisions"/>.
///
/// <para><b>WHY A TEST AND NOT A COMMENT.</b> The manifest classifies a file on an operator's install
/// as "stock, refreshable" or "theirs, untouchable" by looking its hash up in a recorded set. The
/// moment somebody ships a new revision of a stock script WITHOUT adding its hash, that revision
/// becomes indistinguishable from an operator edit for every install that already has the old one —
/// the refresh silently stops working for that file, forever, and nothing anywhere reports it. A
/// comment asking the next person to remember is not a guard. This is.</para>
///
/// <para><b>THE SET IS DERIVED FROM THE CSPROJ, NOT FROM A HAND LIST</b> (CLAUDE.md generation
/// discipline, rule 2b). <see cref="ScriptsThePayloadDelivers"/> parses the <c>TargetPath</c> elements
/// that actually put files into <c>BPScripts.default\</c>, so adding a twelfth stock script to the
/// payload turns this class red without anyone editing this file. Pinning a COUNT instead would
/// measure the regex rather than the payload, which is the defect this project has now shipped four
/// times.</para>
///
/// <para><b>⚠ SOURCE VERSUS OUTPUT, DECIDED PER CALL SITE</b> (lane brief C7.2, and the eighteen-hour
/// silent breakage that produced it). These tests hash the <b>repo source</b> under <c>BPScripts\</c>,
/// resolved by walking up to <c>SQLTriage.sln</c>, because the manifest describes the bytes the
/// payload will DELIVER and the csproj delivers each script with a plain <c>None Update</c> /
/// <c>CopyToOutputDirectory</c> / <c>TargetPath</c> copy. That equivalence is not assumed: where a
/// build output is present beside the test assembly,
/// <see cref="The_delivered_copy_is_byte_identical_to_the_source_it_is_copied_from"/> byte-compares
/// them and says plainly when it could not.</para>
/// </summary>
public class ShippedBPScriptRevisionsTests
{
    private readonly ITestOutputHelper _out;

    public ShippedBPScriptRevisionsTests(ITestOutputHelper output) => _out = output;

    private static string SourceScriptsFolder() =>
        Path.Combine(FrkContractTests.RepoRoot(), "BPScripts");

    /// <summary>
    /// The stock scripts the payload delivers into <c>BPScripts.default\</c>, read out of the csproj
    /// rather than listed here. One entry per <c>TargetPath</c> that names that folder.
    /// </summary>
    private static IReadOnlyList<string> ScriptsThePayloadDelivers()
    {
        var csproj = File.ReadAllText(Path.Combine(FrkContractTests.RepoRoot(), "SQLTriage.csproj"));

        var names = Regex.Matches(
                csproj,
                @"<TargetPath>\s*" + Regex.Escape(ConfigDefaultsSeeder.BPScriptsDefaultsFolderName)
                    + @"[\\/](?<name>[^<]+?)\s*</TargetPath>",
                RegexOptions.IgnoreCase)
            .Select(m => m.Groups["name"].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        names.Should().NotBeEmpty(
            "the csproj must deliver at least one script into " + ConfigDefaultsSeeder.BPScriptsDefaultsFolderName
            + "; finding none means this regex stopped matching the payload rather than that the payload "
            + "is empty, and a census that silently measures nothing is worse than no census");

        return names;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// THE GUARD THAT STOPS THE MANIFEST ROTTING. Every script the payload delivers must have the
    /// hash of its CURRENT bytes recorded, or that revision cannot be told apart from an operator's
    /// edit. The failure message prints the exact line to paste, because a guard that tells you only
    /// that you are wrong gets worked around rather than fixed.
    /// </summary>
    [Fact]
    public void Every_shipped_default_has_its_delivered_hash_recorded()
    {
        var source = SourceScriptsFolder();
        var missing = new List<string>();

        foreach (var name in ScriptsThePayloadDelivers())
        {
            var path = Path.Combine(source, name);
            File.Exists(path).Should().BeTrue(
                "the csproj delivers " + name + " into " + ConfigDefaultsSeeder.BPScriptsDefaultsFolderName
                + " but there is no such file at " + path);

            var hash = HashFile(path);
            _out.WriteLine($"{name} -> {hash}");

            if (!ShippedBPScriptRevisions.IsKnownShipped(name, hash))
                missing.Add($"                    \"{hash}\", // current, <commit> <date>   [\"{name}\"]");
        }

        missing.Should().BeEmpty(
            "every revision this product ships must be recorded in ShippedBPScriptRevisions.KnownShipped, "
            + "or an install holding the PREVIOUS revision will classify it as an operator edit and never "
            + "receive the improvement. Add the line(s) below to the matching entry in "
            + "Data\\Services\\ShippedBPScriptRevisions.cs:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// I-2 for the 2026-09-19 client-name scrub: an UNTOUCHED stock script on an existing install must
    /// still receive the scrub. Every install that predates this change holds the pre-scrub bytes of these two
    /// scripts in <c>BPScripts\</c>. The seeder decides on the ON-DISK file's hash
    /// (<c>ConfigDefaultsSeeder.SeedPair</c> passes <c>existingHash</c> to <c>isKnownShipped</c>), so the
    /// older hash must stay in <see cref="ShippedBPScriptRevisions.KnownShipped"/>: drop it and every
    /// such install classifies its file as an operator edit and keeps it forever. The old bytes cannot
    /// live in this repo (they carry what the scrub removed, and this folder publishes), so their
    /// delivered-form hashes are pinned here instead. The current bytes must hash DIFFERENTLY, or the
    /// seeder would report "stock, already current" and refresh nothing.
    /// </summary>
    [Fact]
    public void The_pre_scrub_revisions_stay_refreshable()
    {
        var preScrub = new (string Name, string Hash)[]
        {
            ("03. dba quick view.sql", "b30ef6b070447f372a97a6820f92a5769defefbff21ca650c21a37bd91c742c8"),
            ("WeeklyReportSchedule.sql", "14be43db9c65ee9968e0fea90483455a11ed54b0e033c54312fa97e5d8471858"),
        };

        foreach (var (name, oldHash) in preScrub)
        {
            ShippedBPScriptRevisions.IsKnownShipped(name, oldHash).Should().BeTrue(
                name + " at " + oldHash[..12] + " is what every install that predates this change holds; without it "
                + "the seeder keeps that file as an operator edit and the install never receives the scrub");

            var currentHash = HashFile(Path.Combine(SourceScriptsFolder(), name));
            _out.WriteLine($"{name}: pre-scrub {oldHash[..12]} -> current {currentHash[..12]}");

            currentHash.Should().NotBe(oldHash,
                "the delivered bytes of " + name + " must differ from the pre-scrub revision, or the seeder "
                + "reports 'stock, already current' and refreshes nothing");

            ShippedBPScriptRevisions.IsKnownShipped(name, currentHash).Should().BeTrue(
                "the revision the refresh writes must itself be recorded, so the NEXT revision can refresh it");
        }
    }

    /// <summary>
    /// I-3 for the 2026-09-19 contact-address change (lane public-release): an UNTOUCHED stock copy of
    /// either script on an existing install must still receive the change. Same mechanism as
    /// <see cref="The_pre_scrub_revisions_stay_refreshable"/>: the seeder decides on the ON-DISK file's
    /// hash, so the revision each install holds today must stay in
    /// <see cref="ShippedBPScriptRevisions.KnownShipped"/>. The old bytes carry the address this change
    /// removed, and this folder publishes, so only their delivered-form hashes are pinned here.
    /// </summary>
    [Fact]
    public void The_pre_address_change_revisions_stay_refreshable()
    {
        var preChange = new (string Name, string Hash)[]
        {
            ("03. dba quick view.sql", "89d457143471ba2889ca822eb3be4d617df658e91a29edc09ec188039aa31bdd"),
            ("09. Do Stats.sql", "bd8c8874f77b2f1aedd27bce7df8225908246077de9be731b74ba29ab3117f82"),
        };

        foreach (var (name, oldHash) in preChange)
        {
            ShippedBPScriptRevisions.IsKnownShipped(name, oldHash).Should().BeTrue(
                name + " at " + oldHash[..12] + " is what every install that predates the address change holds; "
                + "without it the seeder keeps that file as an operator edit and the install never receives the change");

            var currentHash = HashFile(Path.Combine(SourceScriptsFolder(), name));
            _out.WriteLine($"{name}: pre-change {oldHash[..12]} -> current {currentHash[..12]}");

            currentHash.Should().NotBe(oldHash,
                "the delivered bytes of " + name + " must differ from the pre-change revision, or the seeder "
                + "reports 'stock, already current' and refreshes nothing");

            ShippedBPScriptRevisions.IsKnownShipped(name, currentHash).Should().BeTrue(
                "the revision the refresh writes must itself be recorded, so the NEXT revision can refresh it");
        }
    }

    /// <summary>
    /// The manifest and the payload must name the same files. A script added to the payload with no
    /// manifest entry can never be refreshed; a manifest entry for a script the payload no longer
    /// carries is a stale claim about what we ship.
    /// </summary>
    [Fact]
    public void The_manifest_covers_exactly_the_scripts_the_payload_delivers()
    {
        var delivered = ScriptsThePayloadDelivers();
        var recorded = ShippedBPScriptRevisions.KnownShipped.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        _out.WriteLine($"payload delivers {delivered.Count}, manifest records {recorded.Count}");

        delivered.Except(recorded, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "the payload delivers these scripts but ShippedBPScriptRevisions has no entry for them, so "
            + "they can never be refreshed on an existing install");

        recorded.Except(delivered, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "ShippedBPScriptRevisions records these but the payload no longer delivers them, so the "
            + "manifest is making a stale claim about what this product ships");
    }

    /// <summary>
    /// ⚠ THE MEASUREMENT THAT MAKES THE WHOLE MANIFEST TRUE OR USELESS, pinned so it cannot rot
    /// silently. The git blob is LF-normalised while the working tree — and therefore the payload —
    /// is CRLF; <c>.gitattributes</c> now pins that with <c>BPScripts/* text eol=crlf</c> (it did not
    /// for 10 of these 11 files when this manifest was recovered — that CRLF depended only on
    /// <c>core.autocrlf=true</c> at machine level, which does not travel with a clone). The manifest
    /// was recovered from history by hashing <c>LF→CRLF(blob)</c>. If that stops being the delivered
    /// form (a <c>.gitattributes</c> change, a new file committed with CRLF already in it), every
    /// recovered historical hash silently stops matching anything and the refresh quietly dies with
    /// all tests green. This asserts the delivered bytes really are CRLF-terminated, which is the
    /// checkable half.
    /// </summary>
    [Fact]
    public void The_delivered_bytes_are_the_CRLF_form_that_the_manifest_hashes()
    {
        var source = SourceScriptsFolder();
        var offenders = new List<string>();

        foreach (var name in ScriptsThePayloadDelivers())
        {
            var bytes = File.ReadAllBytes(Path.Combine(source, name));
            var lf = 0;
            var crlf = 0;

            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++; else lf++;
            }

            _out.WriteLine($"{name}: {crlf} CRLF, {lf} bare LF");
            if (lf > 0) offenders.Add($"{name} has {lf} bare LF line ending(s) among {crlf} CRLF");
        }

        offenders.Should().BeEmpty(
            "the manifest's hashes were recovered from git by converting each historical blob LF->CRLF. "
            + "A delivered file containing bare LF endings is not that form, so its historical hashes "
            + "match nothing, every install classifies it as operator-modified, and it silently stops "
            + "being refreshed. Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// Closes the source-versus-output gap rather than assuming it. The manifest hashes the repo
    /// source; the operator receives the build output. Every file that IS delivered must be
    /// byte-identical to the source it was copied from.
    ///
    /// <para>⚠ IT ASSERTS AGREEMENT, NOT DELIVERY, and the distinction is not pedantic — it is what a
    /// community build measured on 2026-09-11. An earlier draft treated a script missing from the
    /// output as a mismatch and went red on the community profile, where
    /// <c>buildprofile.targets:170-171</c> removes all eleven scripts BY DESIGN and leaves an empty
    /// <c>BPScripts.default\</c> behind. Absence there is correct; a differing byte is not. Which files
    /// ought to ship is <c>ScriptCensusTests</c>' and
    /// <c>RuntimeWriteFolderCensusTests.The_community_profile_still_excludes_the_script_payload_under_its_new_name</c>'s
    /// question, not this one's.</para>
    ///
    /// <para><b>AND IT REFUSES TO PASS VACUOUSLY.</b> Comparing zero files proves nothing, so that case
    /// prints UNTESTED naming why rather than returning a green nobody can interpret — the shape in
    /// <c>a-control-that-cannot-reproduce-cannot-refute</c>.</para>
    /// </summary>
    [Fact]
    public void The_delivered_copy_is_byte_identical_to_the_source_it_is_copied_from()
    {
        var output = Path.Combine(AppContext.BaseDirectory, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName);
        var source = SourceScriptsFolder();
        var mismatches = new List<string>();
        var compared = 0;

        foreach (var name in ScriptsThePayloadDelivers())
        {
            var delivered = Path.Combine(output, name);
            if (!File.Exists(delivered)) continue;

            compared++;
            var a = HashFile(Path.Combine(source, name));
            var b = HashFile(delivered);
            if (a != b) mismatches.Add($"{name}: source {a[..12]} != delivered {b[..12]}");
        }

        mismatches.Should().BeEmpty(
            "the manifest hashes the repo source on the basis that delivery is a plain copy. If the "
            + "delivered bytes differ, every hash in ShippedBPScriptRevisions is measuring the wrong "
            + "thing. Mismatches: " + string.Join("; ", mismatches));

        if (compared == 0)
            _out.WriteLine(
                "UNTESTED, said out loud rather than passed silently: no delivered script was found "
                + "under " + output + ", so source-vs-output was NOT exercised on this run. That is the "
                + "expected state for a community build, which ships none of them. The equivalence is "
                + "BELIEVED here from the csproj using a plain None Update/TargetPath copy with no "
                + "transform, and is PROVED on any full-profile run of this same test.");
        else
            _out.WriteLine($"PROVED byte-identical for {compared} delivered script(s) under {output}");
    }

    /// <summary>
    /// The classifier's fail-safe direction, stated as an executable claim rather than as prose: an
    /// unknown hash, an unknown filename and a null hash all answer false, and false means KEEP.
    /// </summary>
    [Fact]
    public void Anything_the_manifest_cannot_vouch_for_is_not_stock()
    {
        var known = ShippedBPScriptRevisions.KnownShipped.First();
        var goodName = known.Key;
        var goodHash = known.Value.First();

        ShippedBPScriptRevisions.IsKnownShipped(goodName, goodHash).Should().BeTrue(
            "a hash recorded for that filename is exactly what 'stock' means");

        ShippedBPScriptRevisions.IsKnownShipped(goodName, new string('a', 64)).Should().BeFalse(
            "an unknown hash is the operator's work and must be kept");

        ShippedBPScriptRevisions.IsKnownShipped("a script we never shipped.sql", goodHash).Should().BeFalse(
            "a hash is only meaningful for the filename it was recorded against");

        ShippedBPScriptRevisions.IsKnownShipped(goodName, null).Should().BeFalse(
            "a file we could not read is a file we must never overwrite");

        ShippedBPScriptRevisions.IsKnownShipped(goodName, "").Should().BeFalse(
            "an empty hash is not a measurement");

        ShippedBPScriptRevisions.IsKnownShipped(goodName.ToUpperInvariant(), goodHash.ToUpperInvariant())
            .Should().BeTrue("Windows filenames and hex digests are both case-insensitive here");
    }

    /// <summary>
    /// <see cref="ShippedBPScriptRevisions.TryHashFile"/> returns null rather than throwing for a file
    /// it cannot read, because the caller treats null as "keep". Proved by mutation: the same method
    /// on a readable file returns a hash that matches an independent computation.
    /// </summary>
    [Fact]
    public void An_unreadable_file_hashes_to_null_rather_than_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bpshash-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "readable.sql");
            File.WriteAllText(file, "-- content");
            ShippedBPScriptRevisions.TryHashFile(file).Should().Be(HashFile(file),
                "the instrument must agree with an independent hash of the same bytes before its null "
                + "answers mean anything");

            ShippedBPScriptRevisions.TryHashFile(Path.Combine(dir, "no-such-file.sql")).Should().BeNull(
                "an absent file is unmeasurable, and unmeasurable means keep");

            ShippedBPScriptRevisions.TryHashFile(dir).Should().BeNull(
                "a directory is not a file, and it must not throw out of a startup path");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
