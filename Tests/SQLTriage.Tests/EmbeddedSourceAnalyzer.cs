/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SQLTriage.Tests;

/// <summary>
/// Reads a COMPILED ASSEMBLY's PE debug directory and the portable-PDB metadata tables inside it, and
/// answers one question: <b>what source text does this binary carry?</b>
///
/// <para><b>WHY THIS READS STRUCTURE AND NOT CHARACTERS.</b> The property under guard is "the shipped
/// binary contains no first-party source text". A grep over <c>.cs</c> files cannot see that property
/// at all — it is a property of the OUTPUT, decided by MSBuild properties, the Razor source generator
/// and Roslyn's emit, none of which are visible in the source tree. And this repo has measured a
/// character census failing in BOTH directions in one night (a line-wrapped offender hidden from five
/// censuses; prose inside a string counted as a real instance), so under the 2026-09-11 ruling a
/// character scan is a TRIPWIRE, not a guarantee. Everything here comes from metadata tables: the
/// debug directory entry types, the <c>CustomDebugInformation</c> kind GUIDs, the <c>Document</c> and
/// <c>MethodDebugInformation</c> row counts, and the parent handle table of each row. Formatting,
/// renames and reflow cannot move any of it. No new dependency is added:
/// <c>System.Reflection.Metadata</c> and <c>System.Reflection.PortableExecutable</c> ship in the
/// shared framework, and this test project deliberately has no Roslyn reference.</para>
///
/// <para><b>WHAT IT CANNOT SEE, stated plainly.</b>
/// <list type="bullet">
/// <item>It reads <c>SQLTriage.dll</c>, the compiled assembly. It does NOT open the single-file
/// <c>SQLTriage.exe</c> or the release zip. The 2026-09-14 measurement of 629 embedded files was taken
/// on the single-file exe extracted from the zip and produced the same figures this reader produces
/// from the dll, which is why the dll is treated as the representative artifact — but that the
/// single-file host embeds exactly these bytes is BELIEVED here, not proved by this code.</item>
/// <item>It counts and names embedded documents. It does not judge what is IN them beyond the
/// document path, and it does not verify the recorded SHA-256 hashes.</item>
/// <item>Embedded source is only one way source text can reach a client. A content file copied into
/// the publish tree is the release archive's problem — see
/// <c>tools/verify-release-archive.ps1</c>.</item>
/// </list></para>
///
/// <para><b>⚠ EVERY NUMBER THIS READER RETURNS IS SPECIFIC TO THE BUILD CONFIGURATION OF THE FILE IT
/// WAS HANDED.</b> PROVED 2026-09-16 at <c>7b997b4</c> on four deliberately built cells: Release
/// EMBEDS the portable PDB and Debug ships it as a separate file, so the same commit yields 155
/// embedded source documents, an 1,851,736-byte type-17 entry and 784 <c>Document</c> rows in
/// Release-full, and ZERO of all three in Debug-full (debug directory entry types <c>[2, 19, 16]</c>).
/// This reader is correct in both cases — it answers about the file it was given — but a TEST that
/// pins one of its numbers is reporting on nothing unless it first refuses a configuration that number
/// was not frozen in. Call
/// <see cref="ProductAssemblyBuildConfiguration.RequireTheBuildConfigurationTheseNumbersWereFrozenIn"/>,
/// as <see cref="EmbeddedSourceCensusTests.ReadCensus"/> does. That obligation is ENFORCED, not
/// advised: <see cref="ProductCountGuardCensusTests"/> walks the compiled test assembly's own IL and
/// goes RED when a test type reaching this reader neither reaches that guard nor sits on its exemption
/// list with a reason. ⚠ An ABSENT or ZERO reading is NOT a clean binary — it is an unreadable one;
/// the liveness pair that says so is asserted at the census CHOKEPOINT, in
/// <see cref="EmbeddedSourceCensusTests.ReadCensus"/>, so that EVERY fact in that class refuses an
/// unreadable assembly and not just the one that was measured passing over zero sources.</para>
/// </summary>
internal static class EmbeddedSourceAnalyzer
{
    // ── The kind GUID, written TWICE on purpose ───────────────────────────────────────────────────

    /// <summary>
    /// The <c>EmbeddedSource</c> custom-debug-information kind, canonical string form.
    /// <para>⚠ <b>WHY THERE ARE TWO OF THESE.</b> On 2026-09-14 a gate agent mistyped ONE NIBBLE of
    /// this constant. Nothing failed: no row matched the wrong GUID, the embedded-source count came
    /// back 0, and the gate reported the binary CLEAN. A wrong constant here fails toward clean, which
    /// is the worst direction a constant can fail in. So it is written twice, in two encodings that a
    /// single typo cannot corrupt identically — the canonical string here, and the 16-byte
    /// little-endian layout below — and <c>EmbeddedSourceCensusTests</c> asserts they are equal. That
    /// assertion is double-entry bookkeeping, not decoration: delete it and a typo goes silent
    /// again.</para>
    /// </summary>
    internal static readonly Guid EmbeddedSourceKind = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    /// <summary>
    /// The same kind, written as the 16 raw bytes a portable PDB stores. A <see cref="Guid"/>'s first
    /// three fields are LITTLE-ENDIAN on the wire and the last eight bytes are in written order, so
    /// <c>0E8A571B-6926-466E-</c> becomes <c>1B 57 8A 0E · 26 69 · 6E 46</c> and <c>B4AD8AB04611F5FE</c>
    /// stays as written. Deriving this by hand is the point: it is an INDEPENDENT transcription, so it
    /// only agrees with <see cref="EmbeddedSourceKind"/> if both are right.
    /// </summary>
    internal static readonly Guid EmbeddedSourceKindFromRawBytes = new Guid(new byte[]
    {
        0x1B, 0x57, 0x8A, 0x0E,   // Data1, little-endian  (0x0E8A571B)
        0x26, 0x69,               // Data2, little-endian  (0x6926)
        0x6E, 0x46,               // Data3, little-endian  (0x466E)
        0xB4, 0xAD,               // Data4[0..1]
        0x8A, 0xB0, 0x46, 0x11, 0xF5, 0xFE, // Data4[2..7]
    });

    /// <summary>The debug directory entry type for an embedded portable PDB. Type 17 by spec.</summary>
    internal const int EmbeddedPortablePdbDebugDirectoryType = 17;

    /// <summary>
    /// The directory segment the Razor source generator writes its generated documents under. Used to
    /// tell a GENERATED document from a hand-written one by its PROVENANCE rather than by its name —
    /// a hand-written file called <c>Thing_razor.g.cs</c> would satisfy a name test and fail this one.
    /// <para>⚠ This segment is the Razor SDK's, not ours: a Razor SDK upgrade can rename it, and the
    /// census then goes RED. That is the correct direction — re-derive the segment from a fresh
    /// build and update it — but it means a red here is not always an IP leak.</para>
    /// </summary>
    internal const string RazorGeneratorPathSegment =
        "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator";

    /// <summary>The file-name suffix the Razor source generator gives a generated component.</summary>
    internal const string RazorGeneratedSuffix = "_razor.g.cs";

    // ── What a read produces ──────────────────────────────────────────────────────────────────────

    /// <summary>One embedded document: its recorded path and the size of the source text it carries.</summary>
    internal sealed record EmbeddedDocument(string DocumentPath, int UncompressedBytes)
    {
        /// <summary>Forward-slash form, so comparisons do not depend on the emitting machine.</summary>
        public string NormalizedPath => DocumentPath.Replace('\\', '/');

        public string FileName
        {
            get
            {
                var p = NormalizedPath;
                var i = p.LastIndexOf('/');
                return i < 0 ? p : p.Substring(i + 1);
            }
        }

        /// <summary>
        /// True when this document was produced by the Razor source generator: BOTH the generator's
        /// own output directory in the path AND the generated-component suffix on the file name.
        /// Either alone is forgeable by naming; together they are the generator's signature.
        /// </summary>
        public bool IsRazorGenerated =>
            NormalizedPath.Contains(RazorGeneratorPathSegment, StringComparison.Ordinal) &&
            FileName.EndsWith(RazorGeneratedSuffix, StringComparison.OrdinalIgnoreCase);

        public override string ToString() => $"{NormalizedPath} ({UncompressedBytes:N0} bytes)";
    }

    /// <summary>Everything one pass over the assembly measured. Reported whole, so a failure message
    /// can show the reader the histogram instead of a bare count.</summary>
    internal sealed record Census(
        string AssemblyPath,
        long AssemblyBytes,
        DateTime AssemblyLastWriteTimeUtc,
        int DebugDirectoryEntryCount,
        IReadOnlyList<int> DebugDirectoryEntryTypes,
        bool HasEmbeddedPortablePdb,
        int EmbeddedPdbDataSize,
        int DocumentRows,
        int MethodDebugInformationRows,
        int CustomDebugInformationRows,
        IReadOnlyDictionary<Guid, int> KindHistogram,
        IReadOnlyDictionary<string, int> ParentTableHistogram,
        int DocumentParentedRows,
        IReadOnlyList<EmbeddedDocument> EmbeddedSources)
    {
        public long EmbeddedSourceBytes => EmbeddedSources.Sum(d => (long)d.UncompressedBytes);

        /// <summary>The full kind histogram, for a failure message. A count on its own cannot tell a
        /// mistyped GUID from a genuinely clean binary; the histogram can.</summary>
        public string DescribeKindHistogram() =>
            KindHistogram.Count == 0
                ? "    (no CustomDebugInformation rows at all)"
                : string.Join(Environment.NewLine,
                    KindHistogram.OrderByDescending(kv => kv.Value)
                                 .Select(kv => $"    {kv.Key:B} = {kv.Value}" +
                                               (kv.Key == EmbeddedSourceKind ? "   <- EmbeddedSource" : "")));

        public string DescribeParentHistogram() =>
            string.Join(Environment.NewLine,
                ParentTableHistogram.OrderByDescending(kv => kv.Value).Select(kv => $"    {kv.Key} = {kv.Value}"));

        public string DescribeProvenance() =>
            $"{AssemblyPath}{Environment.NewLine}" +
            $"    {AssemblyBytes:N0} bytes, written {AssemblyLastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}Z";
    }

    // ── The read ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens <paramref name="assemblyPath"/> and reads its embedded portable PDB.
    /// <para>⚠ <b>Throws</b> when the file is absent, when it has no type-17 debug directory entry, or
    /// when the embedded PDB cannot be inflated. ABSENT IS NOT CLEAN: on 2026-09-11 a profile verifier
    /// scanned an intermediate it had not built and passed a 174 MB FULL build as community with
    /// exit 0 — an IP boundary that failed toward clean. Every "cannot read" path here is an
    /// exception, never an empty result that a caller could mistake for a pass.</para>
    /// </summary>
    internal static Census Read(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new InvalidOperationException(
                "EMBEDDED-SOURCE CENSUS CANNOT RUN: the product assembly's location is an empty string. " +
                "That happens when the assembly was loaded from memory or from a single-file bundle. " +
                "A census with nothing to read must fail, not pass.");

        var file = new FileInfo(assemblyPath);
        if (!file.Exists)
            throw new FileNotFoundException(
                "EMBEDDED-SOURCE CENSUS CANNOT RUN: the product assembly is not on disk." +
                Environment.NewLine + "  expected at: " + assemblyPath +
                Environment.NewLine + "  Build the app for the profile under test and re-run; an assembly this " +
                "census cannot open is NOT clean, and reporting it as clean is exactly the 2026-09-11 failure.",
                assemblyPath);

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);

        ImmutableArray<DebugDirectoryEntry> directory;
        try { directory = pe.ReadDebugDirectory(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"EMBEDDED-SOURCE CENSUS CANNOT RUN: {assemblyPath} has no readable PE debug directory " +
                $"({ex.GetType().Name}: {ex.Message}). Not clean - unreadable.", ex);
        }

        var types = directory.Select(e => (int)e.Type).ToList();
        var embedded = directory.Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToList();

        if (embedded.Count == 0)
        {
            // Reported, not thrown: "there is no embedded PDB" is a legitimate and even desirable
            // state (DebugType=portable with an unshipped .pdb). The TEST decides whether it is
            // acceptable for this product; the reader only reports what is there.
            return new Census(assemblyPath, file.Length, file.LastWriteTimeUtc,
                              directory.Length, types, HasEmbeddedPortablePdb: false, EmbeddedPdbDataSize: 0,
                              DocumentRows: 0, MethodDebugInformationRows: 0, CustomDebugInformationRows: 0,
                              KindHistogram: new Dictionary<Guid, int>(),
                              ParentTableHistogram: new Dictionary<string, int>(),
                              DocumentParentedRows: 0,
                              EmbeddedSources: Array.Empty<EmbeddedDocument>());
        }

        // ReadEmbeddedPortablePdbDebugDirectoryData validates the "MPDB" signature and inflates the
        // deflate stream that follows it. Doing that by hand would be a second implementation of a
        // format the framework already parses, and a bug in it would fail toward clean.
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0]);
        var md = provider.GetMetadataReader();

        var kinds = new Dictionary<Guid, int>();
        var parents = new Dictionary<string, int>(StringComparer.Ordinal);
        var docParented = 0;
        var docs = new List<EmbeddedDocument>();

        foreach (var handle in md.CustomDebugInformation)
        {
            var cdi = md.GetCustomDebugInformation(handle);
            var kind = md.GetGuid(cdi.Kind);
            kinds[kind] = kinds.TryGetValue(kind, out var k) ? k + 1 : 1;

            var parentTable = cdi.Parent.IsNil ? "Nil" : cdi.Parent.Kind.ToString();
            parents[parentTable] = parents.TryGetValue(parentTable, out var p) ? p + 1 : 1;
            if (!cdi.Parent.IsNil && cdi.Parent.Kind == HandleKind.Document) docParented++;

            if (kind != EmbeddedSourceKind) continue;
            if (cdi.Parent.IsNil || cdi.Parent.Kind != HandleKind.Document) continue;

            var document = md.GetDocument((DocumentHandle)cdi.Parent);
            var name = md.GetString(document.Name);
            docs.Add(new EmbeddedDocument(name, UncompressedSourceSize(md.GetBlobBytes(cdi.Value))));
        }

        return new Census(assemblyPath, file.Length, file.LastWriteTimeUtc,
                          directory.Length, types, HasEmbeddedPortablePdb: true,
                          EmbeddedPdbDataSize: embedded[0].DataSize,
                          DocumentRows: md.Documents.Count,
                          MethodDebugInformationRows: md.MethodDebugInformation.Count,
                          CustomDebugInformationRows: md.CustomDebugInformation.Count,
                          KindHistogram: kinds, ParentTableHistogram: parents,
                          DocumentParentedRows: docParented,
                          EmbeddedSources: docs);
    }

    /// <summary>
    /// Size of the source text an <c>EmbeddedSource</c> blob carries. The blob's first four bytes are
    /// an int32 "format": 0 means the UTF-8 bytes follow uncompressed, and any other value is the
    /// UNCOMPRESSED size of the deflate stream that follows. Reading the declared size rather than
    /// inflating is deliberate - the census needs the magnitude, not the text, and never materialises
    /// a client's source into a test process.
    /// </summary>
    private static int UncompressedSourceSize(byte[] blob)
    {
        if (blob.Length < sizeof(int)) return 0;
        var format = BitConverter.ToInt32(blob, 0);
        return format == 0 ? blob.Length - sizeof(int) : format;
    }

    /// <summary>
    /// Maps a repo-relative <c>.razor</c> path to the document path the Razor source generator gives
    /// its generated component: <c>Pages/AccessSurface.razor</c> -&gt;
    /// <c>Pages/AccessSurface_razor.g.cs</c>. Mirrors
    /// <see cref="Build.ProfileGateModel.QualifiedTypeNamesFromRemovedPages"/>'s premise - the folder
    /// path is preserved and only the extension is rewritten - and is MEASURED against a real build
    /// by <c>EmbeddedSourceCensusTests</c>, which fails if the mapping stops matching anything.
    /// </summary>
    internal static string GeneratedDocumentPathForRazorPage(string repoRelativeRazorPath)
    {
        var rel = repoRelativeRazorPath.Replace('\\', '/').Trim();
        return rel.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
            ? rel.Substring(0, rel.Length - ".razor".Length) + RazorGeneratedSuffix
            : rel;
    }
}
