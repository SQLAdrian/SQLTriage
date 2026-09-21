/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// THE INVARIANT: <b>a stock Best Practice script the operator has not touched must keep receiving
    /// improvements; one they HAVE touched must never be overwritten.</b> This type is the oracle that
    /// tells those two apart, and it is the whole reason seed-once alone was not the finished fix.
    ///
    /// <para><b>WHY THE OBVIOUS TEST DOES NOT WORK — read this before "simplifying" it away.</b> The
    /// tempting rule is "if the file on disk equals the CURRENT shipped default, refresh it; otherwise
    /// the operator edited it". That rule is wrong on the only population that matters. An existing
    /// install holds a file that differs from the current default for <b>two indistinguishable
    /// reasons</b>: the operator edited it, <i>or</i> we shipped a newer version since they installed.
    /// A single current-default comparison cannot separate those, and guessing wrong either freezes
    /// improvements out forever or destroys the operator's work. Recording a baseline at first seed
    /// fails for the same population from the other end: an existing install has no prior record, and
    /// adopting whatever is on disk as "the baseline" adopts an operator's edit as stock, so the next
    /// update overwrites exactly the file that most needed protecting.</para>
    ///
    /// <para><b>WHAT THIS IS INSTEAD:</b> the sha256 of <i>every version of every stock script this
    /// product has ever shipped</i>, recovered from git history rather than typed. A file whose hash is
    /// in this set is an unmodified stock script of <i>some</i> revision, whoever's install it is and
    /// whenever they installed, and it can be refreshed safely. A file whose hash is NOT in this set is
    /// the operator's and is never touched. That classifies correctly with <b>no prior state on the
    /// install</b>, which is precisely what the existing-install population requires.</para>
    ///
    /// <para><b>⚠ THE HASHES ARE OF THE DELIVERED BYTES, NOT OF THE GIT BLOB, AND THE DIFFERENCE IS NOT
    /// COSMETIC.</b> The git blob is LF-normalised while the working tree — and therefore the payload,
    /// because <c>SQLTriage.csproj</c> delivers each one with a plain <c>None Update</c> /
    /// <c>TargetPath</c> copy — is CRLF. <c>.gitattributes</c> NOW pins that with <c>BPScripts/* text
    /// eol=crlf</c>; when this manifest was recovered it did not, for 10 of these 11 files, and the
    /// CRLF depended entirely on <c>core.autocrlf=true</c> being set at machine/system level on the
    /// author's box — a setting that does not travel with a clone, so a fresh checkout without it
    /// would have landed LF. On <c>01. MaintenanceSolution.sql</c> the LF/CRLF difference is 9,515
    /// bytes. A manifest built from raw blob hashes would match nothing on any install: every file
    /// would classify as operator-modified, no refresh would ever fire, and <b>nothing would report
    /// it</b>. The recovery therefore hashes <c>LF→CRLF(blob)</c>, and that transform was proved to
    /// reproduce the working tree byte-for-byte on all 11 files before a single hash was written
    /// down.</para>
    ///
    /// <para><b>THE FAIL-SAFE DIRECTION, stated rather than discovered:</b> a file whose hash is unknown
    /// — including one that predates this manifest, or one delivered by some route that altered its
    /// bytes — is treated as operator-modified and <b>kept</b>. The failure mode of this design is
    /// therefore "an improvement did not arrive", never "the operator's work was destroyed". That is
    /// the correct direction for this lane and it is deliberate.</para>
    ///
    /// <para><b>THE UNCHANGED TRADE:</b> an operator who edited a stock script still never receives an
    /// improved version of that file. Adrian ruled for that on 2026-09-10 and this change does not
    /// revisit it. The current revision stays readable beside theirs in <c>BPScripts.default\</c> and
    /// they merge it themselves. What this change fixes is the far larger population the ruling never
    /// intended to catch: every install whose stock scripts are <i>untouched</i>, which seed-once alone
    /// would have frozen at their installed revision forever.</para>
    ///
    /// <para><b>⚠ THIS MANIFEST ROTS IF NOTHING REGENERATES IT.</b> Shipping a new revision of a stock
    /// script means adding its delivered-form hash here, or that revision is indistinguishable from an
    /// operator edit for every install that already has the old one. The guard is
    /// <c>Tests\SQLTriage.Tests\ShippedBPScriptRevisionsTests.cs</c>,
    /// <c>Every_shipped_default_has_its_delivered_hash_recorded</c> — it hashes the files the csproj
    /// actually delivers and goes red, printing the exact line to paste, the moment one is missing. Its
    /// sibling <c>The_manifest_covers_exactly_the_scripts_the_payload_delivers</c> derives the filename
    /// set from the csproj rather than from a hand list. Both names are real and were checked to exist
    /// when this comment was written; <c>AutoUpdateService.cs:631</c> is the cautionary tale, having
    /// named a test that exists nowhere in the repo.</para>
    /// </summary>
    public static class ShippedBPScriptRevisions
    {
        /// <summary>
        /// Filename → every delivered-form sha256 that filename has ever shipped with, newest first.
        /// Recovered 2026-09-11 by walking <c>git log --all --follow</c> for each
        /// <c>BPScripts/&lt;name&gt;</c> and hashing <c>LF→CRLF(blob)</c> of every distinct blob. Five
        /// files carry more than one revision: <c>AddTraceflags.ps1</c> (two; changed 2026-03-16),
        /// <c>Install-All-Scripts.sql</c> (two; the First Responder Kit 8.34 refresh, 2026-08-23),
        /// <c>WeeklyReportSchedule.sql</c> (two; the client-name scrub, 2026-09-19, which replaced a real
        /// client's name with the neutral <c>'&lt;Client&gt;'</c>), <c>09. Do Stats.sql</c> (two; the
        /// contact-address change, lane public-release, 2026-09-19) and <c>03. dba quick view.sql</c>
        /// (three; the client-name scrub, then the contact-address change, both 2026-09-19). Every OLDER
        /// hash is the bytes some install that predates that change still holds: it must stay, or those
        /// installs classify the old file as an operator edit and never receive the change. Pinned by
        /// <c>ShippedBPScriptRevisionsTests.The_pre_scrub_revisions_stay_refreshable</c> and
        /// <c>ShippedBPScriptRevisionsTests.The_pre_address_change_revisions_stay_refreshable</c>. The
        /// other six have shipped exactly one revision since the 2026-03-12 initial commit, so for those
        /// the set holds one hash today. That is the real history, not a truncated one, and the shape is
        /// what makes the next revision safe to add.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> KnownShipped =
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["01. MaintenanceSolution.sql"] = new[]
                {
                    "4045a69b50371371c0a6b06b4d97ce36cd82cf763dc49d2557e818d1ab7fac5d", // current, c3e91cd2 2026-03-12
                },
                ["03. dba quick view.sql"] = new[]
                {
                    "176634ae25a4af72242edb052842b95635a5ecf1339b0ce1780c8edabe8f8013", // current, lane public-release 2026-09-19
                    "89d457143471ba2889ca822eb3be4d617df658e91a29edc09ec188039aa31bdd", //          cb967b04 2026-09-19
                    "b30ef6b070447f372a97a6820f92a5769defefbff21ca650c21a37bd91c742c8", //          c3e91cd2 2026-03-12
                },
                ["09. Do Stats.sql"] = new[]
                {
                    "b53ac8890e03ebcdfca72149180c20c685954e98c75b88c23c7899388dc4cba9", // current, lane public-release 2026-09-19
                    "bd8c8874f77b2f1aedd27bce7df8225908246077de9be731b74ba29ab3117f82", //          c3e91cd2 2026-03-12
                },
                ["AddTraceflags.ps1"] = new[]
                {
                    "878d47681116563526db0819738ea5bd5d9b06c6516e3721a5938c9366e87f13", // current, b1c82cea 2026-03-16
                    "46aeefcd2c07fd52a36f1c169ade8287cd58d05f6ac7950b17eb62e18a652686", //          c3e91cd2 2026-03-12
                },
                ["availibility group job step script replica check.sql"] = new[]
                {
                    "f69c202f446df12bea88036c83fdd808f1c9408ed1c6a6e0496b47381f1f0829", // current, c3e91cd2 2026-03-12
                },
                ["doSPNs.sql"] = new[]
                {
                    "8da53f8010c9678cb5d7122861ab8848f2bf70901a470a61f0e989b7793846de", // current, c3e91cd2 2026-03-12
                },
                ["find indexes to drop.sql"] = new[]
                {
                    "de1a9ddc94ea9c1fa163f30a5c94297512f05069464440d5f9aebb5ef824843f", // current, c3e91cd2 2026-03-12
                },
                ["Firewall rules with PowerShell.ps1"] = new[]
                {
                    "f767b4c1141155adcd60e6ab91e20a4fd811aaea35664afa81fb21257bc1000a", // current, c3e91cd2 2026-03-12
                },
                ["Install-All-Scripts.sql"] = new[]
                {
                    "c129021a32b0211b841c6a51e44d5862ca0d55cef982081385c4a157e2749de6", // current, 77d85fd2 2026-08-23
                    "eed782c3d165baa8f8d88f295bd2f994bb80e5ef8a0044b00808cf35ad027eeb", //          c3e91cd2 2026-03-12
                },
                ["shrinkfile -gradual.sql"] = new[]
                {
                    "1bf12c06a3f362351dd1c92d37151352db13fe739990790bd3d890dc2d81e53c", // current, c3e91cd2 2026-03-12
                },
                ["WeeklyReportSchedule.sql"] = new[]
                {
                    "ce6689a676e4d8d877081bcd56d80e7e79512563a0ac4aaa4b93146b888ef107", // current, lane bpscripts-client-name-scrub 2026-09-19
                    "14be43db9c65ee9968e0fea90483455a11ed54b0e033c54312fa97e5d8471858", //          c3e91cd2 2026-03-12
                },
            };

        /// <summary>
        /// The lowercase hex sha256 of a file's bytes, or null when it could not be read. Null is a
        /// deliberate value and not an exception: an unreadable file is classified as unknown, which
        /// under <see cref="IsKnownShipped"/> means KEEP. A file we cannot measure is never one we
        /// overwrite.
        /// </summary>
        public static string? TryHashFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="hash"/> is a revision of <paramref name="fileName"/> that this
        /// product shipped — i.e. the file on disk is stock and may be refreshed. False for an unknown
        /// hash, for an unknown filename, and for a null hash: all three mean KEEP.
        /// </summary>
        public static bool IsKnownShipped(string fileName, string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            if (!KnownShipped.TryGetValue(fileName, out var known)) return false;

            foreach (var candidate in known)
                if (string.Equals(candidate, hash, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
    }
}
