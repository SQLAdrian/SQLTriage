# shipartifact.ps1
# ---------------------------------------------------------------------------
# THE INVARIANT THIS FILE EXISTS TO SERVE
#
#   A guard must scan the artefact that SHIPS, prove that artefact is the one
#   this publish produced, and FAIL LOUDLY when it cannot be scanned at all.
#   ABSENT, EMPTY and UNPARSEABLE are failures - never skips.
#
# Dot-sourced by scripts\verify-community-build.ps1. Ships to the public repo
# (.handoff\.publicallow allows scripts\*.ps1) - so it must never contain a
# gated route literal, a gated page name, or the contiguous canary token.
#
# WHAT RUNS THIS, AND WHAT GOES RED WHEN IT DOES NOT
#   - buildprofile.targets target SQLTriageVerifyCommunityPublish
#     (AfterTargets="Publish", community only, no ContinueOnError) writes the
#     ship manifest and invokes verify-community-build.ps1, which dot-sources
#     this file.
#   - Delete the manifest emission and
#       ShipArtifactTests.The_ship_manifest_is_emitted_by_the_community_publish_target
#     goes RED.
#   - Weaken any fail-closed arm below and the reddening tests in
#       Tests\SQLTriage.Tests\ShipArtifactTests.cs
#     go RED. Each has a paired control arm, so a red means the mutation and
#     not the tree.
#
# ASCII ONLY. Windows PowerShell 5.1 reads a UTF-8-no-BOM file as ANSI, so a
# non-ASCII byte here becomes mojibake in a failure message.
# ---------------------------------------------------------------------------

# =============================================================================
# PART 1 - fast multi-pattern byte scanner
# =============================================================================
# The pure-PowerShell scanner in verify-community-build.ps1 is O(needles x bytes)
# in an interpreted per-byte loop: measured 13m17s for 66 needles over one
# 8.7 MB assembly. The shipped single-file bundle inflates to several hundred MB,
# which that scanner cannot read in any useful time - so the extraction arm would
# have been unaffordable, which is one reason "we do not scan it" survived.
#
# This is ONE pass over the buffer for ALL needles, bucketed by the first two
# bytes. Semantics are identical to Count-StandaloneMatches (boundary-aware: a
# hit counts only when the byte(s) AFTER it do not continue [-A-Za-z0-9_]).
# ShipArtifactTests.The_fast_scanner_agrees_with_the_reference_scanner holds the
# two implementations byte-identical on adversarial buffers. If Add-Type is
# unavailable the caller falls back to the reference scanner below: slow, but
# correct - never to "no scan".
$script:SqltScanTypeState = 'unknown'

function Initialize-SqltFastScanner {
    if ($script:SqltScanTypeState -ne 'unknown') { return ($script:SqltScanTypeState -eq 'ok') }
    $src = @'
using System;
using System.Collections.Generic;

public sealed class SqltMultiScan
{
    private readonly byte[][] _needles;
    private readonly int[] _strides;
    private readonly bool[] _standalone;
    private readonly List<int>[] _buckets;

    public SqltMultiScan(byte[][] needles, int[] strides, bool[] standalone)
    {
        if (needles == null || needles.Length == 0) throw new ArgumentException("no needles");
        _needles = needles; _strides = strides; _standalone = standalone;
        _buckets = new List<int>[65536];
        for (int n = 0; n < needles.Length; n++)
        {
            if (needles[n] == null || needles[n].Length < 2) throw new ArgumentException("needle shorter than two bytes");
            int key = needles[n][0] | (needles[n][1] << 8);
            if (_buckets[key] == null) _buckets[key] = new List<int>();
            _buckets[key].Add(n);
        }
    }

    public int[] Count(byte[] data, int length)
    {
        int[] counts = new int[_needles.Length];
        if (data == null || length < 2) return counts;
        if (length > data.Length) throw new ArgumentException("length exceeds buffer");
        int last = length - 2;
        for (int i = 0; i <= last; i++)
        {
            List<int> b = _buckets[data[i] | (data[i + 1] << 8)];
            if (b == null) continue;
            for (int k = 0; k < b.Count; k++)
            {
                int n = b[k];
                byte[] nd = _needles[n];
                if (i + nd.Length > length) continue;
                int j = 2;
                while (j < nd.Length && data[i + j] == nd[j]) j++;
                if (j != nd.Length) continue;
                if (_standalone[n])
                {
                    int ni = i + nd.Length;
                    bool tok = false;
                    if (_strides[n] == 2) { if (ni + 1 < length && data[ni + 1] == 0) tok = IsTok(data[ni]); }
                    else { if (ni < length) tok = IsTok(data[ni]); }
                    if (tok) continue;
                }
                counts[n]++;
            }
        }
        return counts;
    }

    private static bool IsTok(byte b)
    {
        return (b >= 65 && b <= 90) || (b >= 97 && b <= 122) || (b >= 48 && b <= 57) || b == 45 || b == 95;
    }

    // Every offset at which needle occurs. Used to find the bundle marker: the
    // interpreted PowerShell equivalent took 23.6 s over one 174 MB artefact,
    // and a guard that costs half a minute per read is a guard people route
    // around. Same answer, measured against the reference search in
    // ShipArtifactTests.The_fast_scanner_agrees_with_the_reference_scanner.
    public static int[] FindAll(byte[] data, byte[] needle)
    {
        List<int> hits = new List<int>();
        if (data == null || needle == null || needle.Length == 0 || data.Length < needle.Length) return hits.ToArray();
        byte first = needle[0];
        int limit = data.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
        {
            if (data[i] != first) continue;
            int j = 1;
            while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) hits.Add(i);
        }
        return hits.ToArray();
    }
}
'@
    try {
        if (-not ([System.Management.Automation.PSTypeName]'SqltMultiScan').Type) {
            Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop
        }
        $script:SqltScanTypeState = 'ok'
    } catch {
        $script:SqltScanTypeState = 'unavailable'
    }
    return ($script:SqltScanTypeState -eq 'ok')
}

# Needle spec: an array of hashtables @{ Text = '...'; Standalone = $true|$false }
# Each spec is compiled to two needles (UTF-16LE and UTF-8), because .NET string
# metadata is UTF-16LE while resources and content files are UTF-8.
function New-SqltNeedleSet([object[]]$Specs) {
    $needles = New-Object 'System.Collections.Generic.List[byte[]]'
    $strides = New-Object 'System.Collections.Generic.List[int]'
    $stand   = New-Object 'System.Collections.Generic.List[bool]'
    $owner   = New-Object 'System.Collections.Generic.List[string]'
    foreach ($s in $Specs) {
        $u16 = [System.Text.Encoding]::Unicode.GetBytes($s.Text)
        $u8  = [System.Text.Encoding]::UTF8.GetBytes($s.Text)
        $needles.Add($u16); $strides.Add(2); $stand.Add([bool]$s.Standalone); $owner.Add([string]$s.Text)
        $needles.Add($u8);  $strides.Add(1); $stand.Add([bool]$s.Standalone); $owner.Add([string]$s.Text)
    }
    return @{ Needles = $needles.ToArray(); Strides = $strides.ToArray(); Standalone = $stand.ToArray(); Owner = $owner.ToArray() }
}

# Returns a hashtable: needle text -> total hit count over both encodings.
function Measure-SqltNeedles {
    param([byte[]]$Data, [int]$Length, [hashtable]$NeedleSet, [switch]$ForceReference)
    $totals = @{}
    foreach ($o in $NeedleSet.Owner) { if (-not $totals.ContainsKey($o)) { $totals[$o] = 0 } }
    if ($Length -lt 2) { return $totals }
    $counts = $null
    if ((-not $ForceReference) -and (Initialize-SqltFastScanner)) {
        # The bucketed scanner needs at least two bytes per needle. A one-character
        # needle in the list must make the scan SLOWER, never absent: falling back
        # keeps the same answer, whereas letting this throw would take the guard
        # down and leave nothing scanned at all.
        try {
            $scan = New-Object SqltMultiScan($NeedleSet.Needles, $NeedleSet.Strides, $NeedleSet.Standalone)
            $counts = $scan.Count($Data, $Length)
        } catch {
            $counts = $null
        }
    }
    if ($null -eq $counts) {
        $counts = New-Object 'int[]' $NeedleSet.Needles.Length
        for ($n = 0; $n -lt $NeedleSet.Needles.Length; $n++) {
            $counts[$n] = Measure-SqltNeedleReference $Data $Length $NeedleSet.Needles[$n] $NeedleSet.Strides[$n] $NeedleSet.Standalone[$n]
        }
    }
    for ($n = 0; $n -lt $counts.Length; $n++) { $totals[$NeedleSet.Owner[$n]] += $counts[$n] }
    return $totals
}

# The reference implementation. Deliberately naive: it is the thing the fast
# scanner is held equal to, so it must stay obviously correct rather than fast.
function Measure-SqltNeedleReference([byte[]]$Data, [int]$Length, [byte[]]$Needle, [int]$Stride, [bool]$Standalone) {
    $limit = $Length - $Needle.Length
    $count = 0
    for ($i = 0; $i -le $limit; $i++) {
        $match = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Data[$i + $j] -ne $Needle[$j]) { $match = $false; break }
        }
        if (-not $match) { continue }
        if ($Standalone) {
            $ni = $i + $Needle.Length
            $nextIsTokenChar = $false
            if ($Stride -eq 2) {
                if (($ni + 1) -lt $Length -and $Data[$ni + 1] -eq 0) {
                    $c = [char]$Data[$ni]; if ($c -match '[-A-Za-z0-9_]') { $nextIsTokenChar = $true }
                }
            } else {
                if ($ni -lt $Length) {
                    $c = [char]$Data[$ni]; if ($c -match '[-A-Za-z0-9_]') { $nextIsTokenChar = $true }
                }
            }
            if ($nextIsTokenChar) { continue }
        }
        $count++
    }
    return $count
}

# =============================================================================
# PART 2 - single-file bundle reader
# =============================================================================
# A Release publish of this project is a COMPRESSED single-file bundle
# (SQLTriage.csproj sets PublishSingleFile + EnableCompressionInSingleFile for
# every Release configuration). A raw byte scan of that exe answers the SAME
# thing for a clean build and a gated one, because the payload is deflated - so
# the only honest way to scan the shipped bytes is to inflate them.
#
# Format (host-authored, see the .NET single-file bundler): a 32-byte signature
# equal to SHA-256(".net core bundle") is embedded in the host; the int64
# immediately BEFORE it is the offset of the bundle header. The header carries
# major/minor version, a file count, a bundle id, deps/runtimeconfig locations
# and flags, then one entry per file.
#
# FAIL-CLOSED RULES, each exercised by a reddening test:
#   * no signature            -> FAIL (this is not a bundle)
#   * unrecognised major      -> FAIL. A future major shifts every field and the
#                                parse yields GARBAGE, NOT AN ERROR. Only the
#                                majors listed below are parsed.
#   * header offset out of range, or any entry offset+size beyond the file
#                             -> FAIL, BEFORE any inflate is attempted
#   * inflated length != declared size
#                             -> FAIL
#   * more than one signature that parses to DIFFERENT headers
#                             -> FAIL (ambiguous seating). The probe that proved
#                                extraction possible seated on the FIRST match
#                                and never retried; that is fixed here - every
#                                match is tried, and disagreement is loud.
$script:SqltKnownBundleMajors = @(6)

function Get-SqltBundleSignature {
    # The 32-byte bundle marker, pinned as a literal and MEASURED, not derived.
    #
    # The .NET host's own source comments this constant as "SHA-256 for
    # '.net core bundle'" and it is NOT: SHA-256 of that string is
    # db2a6c16fec7fbebe... The derivation was tried first here and found zero
    # signatures in a real 174 MB artefact; the bytes below were then read out of
    # that artefact at the offset an independent probe had recorded, and they are
    # the constant every single-file host embeds. A derived-looking value that
    # cannot be derived is worse than a literal, because it reads as
    # self-verifying. (2026-09-11, lane community-verifier-scans-the-wrong-file.)
    #
    # If a future SDK changes it, Read-SqltBundle fails with "no single-file
    # bundle signature" - loudly, and in the safe direction.
    return ,[byte[]]@(
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    )
}

function Find-SqltByteOffsets([byte[]]$Data, [byte[]]$Needle, [switch]$ForceReference) {
    if ((-not $ForceReference) -and (Initialize-SqltFastScanner)) {
        $fast = [SqltMultiScan]::FindAll($Data, $Needle)
        $list = New-Object 'System.Collections.Generic.List[int]'
        foreach ($h in $fast) { $list.Add($h) }
        return ,$list
    }
    $hits = New-Object 'System.Collections.Generic.List[int]'
    $limit = $Data.Length - $Needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        if ($Data[$i] -ne $Needle[0]) { continue }
        $match = $true
        for ($j = 1; $j -lt $Needle.Length; $j++) {
            if ($Data[$i + $j] -ne $Needle[$j]) { $match = $false; break }
        }
        if ($match) { $hits.Add($i) }
    }
    return ,$hits
}

function Read-SqltVarString([byte[]]$Data, [ref]$Pos) {
    # 7-bit-encoded length prefix, then UTF-8 bytes (BinaryWriter.Write(string)).
    $len = 0; $shift = 0
    do {
        if ($Pos.Value -ge $Data.Length) { throw "string length prefix runs past end of file" }
        $b = $Data[$Pos.Value]; $Pos.Value++
        $len = $len -bor (($b -band 0x7F) -shl $shift)
        $shift += 7
        if ($shift -gt 35) { throw "string length prefix is not 7-bit encoded (corrupt or wrong format)" }
    } while (($b -band 0x80) -ne 0)
    if ($len -lt 0 -or ($Pos.Value + $len) -gt $Data.Length) { throw "string of $len byte(s) runs past end of file" }
    $s = [System.Text.Encoding]::UTF8.GetString($Data, $Pos.Value, $len)
    $Pos.Value += $len
    return $s
}

# Parses the bundle at one candidate signature offset. Throws on anything it
# does not fully understand - a partial understanding of a binary format is how
# a guard reports a verdict about bytes it never read.
function Read-SqltBundleAt([byte[]]$Data, [int]$SigOffset) {
    $fileLen = $Data.Length
    if ($SigOffset -lt 8) { throw "signature at $SigOffset has no room for the preceding header offset" }
    $headerOffset = [System.BitConverter]::ToInt64($Data, $SigOffset - 8)
    if ($headerOffset -lt 0 -or $headerOffset -ge $fileLen) { throw "header offset $headerOffset is outside the file (length $fileLen)" }
    $p = [int]$headerOffset
    if (($p + 12) -gt $fileLen) { throw "header at $p is truncated" }
    $major = [System.BitConverter]::ToUInt32($Data, $p); $p += 4
    $minor = [System.BitConverter]::ToUInt32($Data, $p); $p += 4
    if ($script:SqltKnownBundleMajors -notcontains [int]$major) {
        throw "unrecognised bundle major version $major (known: $($script:SqltKnownBundleMajors -join ',')). REFUSING to parse - a different major shifts every field and would yield garbage, not an error."
    }
    $count = [System.BitConverter]::ToInt32($Data, $p); $p += 4
    if ($count -lt 0 -or $count -gt 200000) { throw "implausible file count $count" }
    $posRef = [ref]$p
    $bundleId = Read-SqltVarString $Data $posRef
    $p = $posRef.Value
    # major >= 2: deps.json / runtimeconfig.json locations + flags
    if (($p + 40) -gt $fileLen) { throw "header at $headerOffset is truncated before the location table" }
    $p += 40
    $entries = New-Object 'System.Collections.Generic.List[object]'
    for ($i = 0; $i -lt $count; $i++) {
        if (($p + 25) -gt $fileLen) { throw "entry $i is truncated" }
        $offset = [System.BitConverter]::ToInt64($Data, $p); $p += 8
        $size   = [System.BitConverter]::ToInt64($Data, $p); $p += 8
        $comp   = [System.BitConverter]::ToInt64($Data, $p); $p += 8   # major >= 6 only
        $type   = $Data[$p]; $p += 1
        $posRef = [ref]$p
        $rel = Read-SqltVarString $Data $posRef
        $p = $posRef.Value
        # BOUNDS CHECK BEFORE ANY INFLATE. A bad offset must be a failure here,
        # not an exception (or worse, a plausible-looking wrong answer) later.
        $stored = if ($comp -gt 0) { $comp } else { $size }
        if ($offset -lt 0 -or $size -lt 0 -or $comp -lt 0) { throw "entry $i ('$rel') has a negative offset/size" }
        if (($offset + $stored) -gt $fileLen) { throw "entry $i ('$rel') claims bytes [$offset,$($offset + $stored)) beyond the file length $fileLen" }
        $entries.Add([pscustomobject]@{
            Index = $i; Offset = $offset; Size = $size; CompressedSize = $comp; Type = $type; RelativePath = $rel
        })
    }
    return [pscustomobject]@{
        SignatureOffset = $SigOffset
        HeaderOffset    = $headerOffset
        Major           = [int]$major
        Minor           = [int]$minor
        BundleId        = $bundleId
        Entries         = $entries
    }
}

# Reads the bundle in $Path. Tries EVERY signature match, not just the first.
# Returns the parsed bundle, or throws with a message naming what it refused.
function Read-SqltBundle([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "artefact ABSENT at $Path" }
    $data = [System.IO.File]::ReadAllBytes($Path)
    if ($data.Length -eq 0) { throw "artefact at $Path is EMPTY (0 bytes)" }
    $sig = Get-SqltBundleSignature
    $hits = Find-SqltByteOffsets $data $sig
    if ($hits.Count -eq 0) { throw "no single-file bundle signature in $Path - this artefact is not a bundle, so its payload was never scanned" }
    $ok = New-Object 'System.Collections.Generic.List[object]'
    $errors = New-Object 'System.Collections.Generic.List[string]'
    foreach ($h in $hits) {
        try { $ok.Add((Read-SqltBundleAt $data $h)) }
        catch { $errors.Add("signature at ${h}: $($_.Exception.Message)") }
    }
    if ($ok.Count -eq 0) { throw "found $($hits.Count) bundle signature(s) in $Path but none parsed: $($errors -join ' | ')" }
    $distinct = @($ok | ForEach-Object { $_.HeaderOffset } | Sort-Object -Unique)
    if ($distinct.Count -gt 1) {
        throw "AMBIGUOUS bundle seating in ${Path}: $($hits.Count) signature(s), $($ok.Count) parsed to $($distinct.Count) DIFFERENT headers ($($distinct -join ', ')). Refusing to guess which one ships."
    }
    $bundle = $ok[0]
    Add-Member -InputObject $bundle -NotePropertyName Path -NotePropertyValue ((Resolve-Path -LiteralPath $Path).ProviderPath)
    Add-Member -InputObject $bundle -NotePropertyName Bytes -NotePropertyValue $data
    Add-Member -InputObject $bundle -NotePropertyName SignatureMatches -NotePropertyValue $hits.Count
    Add-Member -InputObject $bundle -NotePropertyName ParsedCandidates -NotePropertyValue $ok.Count
    return $bundle
}

# Frees the whole-file buffer once the header has been parsed and the raw image
# scanned. The largest entry in the shipped artefact inflates to 240 MB - bigger
# than the exe itself - and holding both at once OOMs a 64-bit PowerShell that
# allocates either one happily on its own. Measured, not feared: this was the
# first exhaustive run's actual failure.
function Clear-SqltBundleBytes([object]$Bundle) {
    $Bundle.Bytes = $null
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
}

# Inflates one entry from DISK rather than from the whole-file buffer, so peak
# memory is one entry rather than one entry plus the artefact.
# The declared size is asserted against what came out: a short read must be a
# FAILURE, never a partially-scanned buffer reported clean.
function Expand-SqltBundleEntry([object]$Bundle, [object]$Entry) {
    $fs = [System.IO.File]::OpenRead($Bundle.Path)
    try {
        $fs.Position = [int64]$Entry.Offset
        $out = New-Object byte[] ([int]$Entry.Size)
        if ($Entry.CompressedSize -gt 0) {
            # Reading past the entry's compressed extent is harmless: the deflate
            # stream ends at its own end-of-stream marker, and we stop at the
            # declared size. The bounds check in Read-SqltBundleAt already proved
            # [Offset, Offset+CompressedSize) lies inside the file.
            $ds = New-Object System.IO.Compression.DeflateStream($fs, [System.IO.Compression.CompressionMode]::Decompress)
            try {
                $read = 0
                while ($read -lt $out.Length) {
                    $n = $ds.Read($out, $read, $out.Length - $read)
                    if ($n -le 0) { break }
                    $read += $n
                }
                if ($read -ne $out.Length) {
                    throw "entry '$($Entry.RelativePath)' inflated to $read byte(s), declared $($Entry.Size) - REFUSING to report a verdict over a partially-read buffer"
                }
            } finally { $ds.Dispose() }
            # ,$out - the unary comma is load-bearing. `return $out` makes
            # PowerShell ENUMERATE the array into the pipeline; for the 240 MB
            # entry in the shipped bundle that is an OutOfMemoryException
            # ("Array dimensions exceeded supported range") at the RETURN, not at
            # the allocation. Measured 2026-09-11 on the real artefact.
            return ,$out
        }
        $read = 0
        while ($read -lt $out.Length) {
            $n = $fs.Read($out, $read, $out.Length - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        if ($read -ne $out.Length) { throw "entry '$($Entry.RelativePath)' read $read byte(s), declared $($Entry.Size)" }
        return ,$out
    } finally { $fs.Dispose() }
}

function Get-SqltSha256([byte[]]$Data, [int]$Length) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($Data, 0, $Length))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-SqltFileSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($Path)
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-', '') }
    finally { $fs.Dispose(); $sha.Dispose() }
}

# =============================================================================
# PART 3 - the ship manifest
# =============================================================================
# THE BINDING IS THE WHOLE CONTROL. A manifest that merely says "a DLL lives
# near here" recreates the defect this file exists to fix with more ceremony.
#
# buildprofile.targets emits this file from the items MSBUILD ITSELF resolved -
# @(FilesToBundle), the exact list handed to the bundler task, and
# @(ResolvedFileToPublish), the exact list copied into the publish directory -
# and hashes each with the GetFileHash task IN THE SAME BUILD. Nothing here is
# chosen by a path that merely tends to hold the right file.
#
# Three different SQLTriage.dll of three different sizes can exist at any
# time (the bundled one, the one in bin, and one in a lane's publish folder).
# Binding to "the DLL nearby" is how a guard ends up certifying a file nobody
# shipped.
function Read-SqltShipManifest([string]$Path, [string]$ExpectToken) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "no ship manifest path supplied" }
    if (-not (Test-Path -LiteralPath $Path)) { throw "ship manifest ABSENT at $Path - the publish did not declare what it produced, so nothing can be bound to it" }
    $lines = @(Get-Content -LiteralPath $Path)
    # EMPTY means "carries no content", not "is zero bytes": a file of blank lines
    # or comments binds exactly as much as no file at all, and a guard that
    # distinguishes them is measuring the wrong thing.
    $usable = @($lines | Where-Object { $_.Trim() -ne '' -and -not $_.Trim().StartsWith('#') })
    if ($usable.Count -eq 0) { throw "ship manifest at $Path is EMPTY ($($lines.Count) line(s) read, none usable) - it declares nothing, so nothing can be bound to it" }
    $props = @{}
    $bundled = New-Object 'System.Collections.Generic.List[object]'
    $published = New-Object 'System.Collections.Generic.List[object]'
    foreach ($line in $lines) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $bar = $t.IndexOf('|')
        if ($bar -lt 1) { continue }
        $kind = $t.Substring(0, $bar)
        $rest = $t.Substring($bar + 1)
        if ($kind -eq 'P') {
            $eq = $rest.IndexOf('=')
            if ($eq -gt 0) { $props[$rest.Substring(0, $eq)] = $rest.Substring($eq + 1) }
            continue
        }
        if ($kind -ne 'B' -and $kind -ne 'R') { continue }
        # <RelativePath>|<NuGetPackageId>|<Sha256>|<FullPath>
        $parts = $rest.Split('|')
        if ($parts.Count -lt 4) { throw "malformed $kind row in ship manifest: $t" }
        $rec = [pscustomobject]@{
            RelativePath = $parts[0]
            NuGetPackageId = $parts[1]
            Sha256 = $parts[2].ToUpperInvariant()
            FullPath = ($parts[3..($parts.Count - 1)] -join '|')
        }
        if ($kind -eq 'B') { $bundled.Add($rec) } else { $published.Add($rec) }
    }
    if (-not $props.ContainsKey('ShipToken')) { throw "ship manifest at $Path carries no ShipToken - it cannot be shown to belong to this publish" }
    if ($ExpectToken -and $props['ShipToken'] -ne $ExpectToken) {
        throw "ship manifest token mismatch at ${Path}: manifest says '$($props['ShipToken'])', this run expected '$ExpectToken'. The manifest is stale or belongs to another build - REFUSING to render a verdict about a publish it does not describe."
    }
    if ($bundled.Count -eq 0 -and $published.Count -eq 0) {
        throw "ship manifest at $Path lists ZERO files. An empty manifest cannot bind anything - fail-closed."
    }
    return [pscustomobject]@{
        Path = $Path
        Props = $props
        Bundled = $bundled
        Published = $published
    }
}

# "Ours" is decided by MSBuild's own provenance metadata, not by a filename:
# every file a NuGet package or the runtime pack contributes carries a
# NuGetPackageId. Our compiled output and our project references do not.
function Get-SqltOwnedRecords([object[]]$Records) {
    return @($Records | Where-Object { [string]::IsNullOrWhiteSpace($_.NuGetPackageId) })
}

function Test-SqltManagedAssembly([byte[]]$Data, [int]$Length) {
    # PE with a CLI header. Cheap, and it is the only thing that makes
    # "scan every managed image" a bounded job instead of "scan everything".
    if ($Length -lt 0x100) { return $false }
    if ($Data[0] -ne 0x4D -or $Data[1] -ne 0x5A) { return $false }
    $peOff = [System.BitConverter]::ToInt32($Data, 0x3C)
    if ($peOff -le 0 -or ($peOff + 0x108) -ge $Length) { return $false }
    if ($Data[$peOff] -ne 0x50 -or $Data[$peOff + 1] -ne 0x45) { return $false }
    $magic = [System.BitConverter]::ToUInt16($Data, $peOff + 24)
    $dirBase = if ($magic -eq 0x20B) { $peOff + 24 + 112 } elseif ($magic -eq 0x10B) { $peOff + 24 + 96 } else { return $false }
    $cliRva = [System.BitConverter]::ToUInt32($Data, $dirBase + 14 * 8)
    return ($cliRva -ne 0)
}

# =============================================================================
# PART 4 - the instrument self-control
# =============================================================================
# A control that cannot reproduce cannot refute. Every "not found" this guard
# prints is only meaningful once the scanner has been shown, IN THAT RUN, able
# to display a positive with THOSE needles - the route needles are read from a
# file that may have been edited since anyone last exercised them, and a scanner
# that has silently stopped matching reports a comfortable zero.
#
# Returns $null when the instrument works, or a message naming what it could not
# see. Held by ShipArtifactTests.The_instrument_self_control_must_be_able_to_display_a_positive,
# whose third arm hands it a scanner that genuinely cannot find its own needle.
function Test-SqltScannerInstrument([hashtable]$NeedleSet, [string]$Label) {
    $texts = @($NeedleSet.Owner | Sort-Object -Unique)
    $sb = New-Object System.IO.MemoryStream
    foreach ($t in $texts) {
        foreach ($b in [System.Text.Encoding]::Unicode.GetBytes(" $t ")) { $sb.WriteByte($b) }
        foreach ($b in [System.Text.Encoding]::UTF8.GetBytes(" $t ")) { $sb.WriteByte($b) }
    }
    $positive = $sb.ToArray()
    $sb.Dispose()
    $hits = Measure-SqltNeedles -Data $positive -Length $positive.Length -NeedleSet $NeedleSet
    $missed = @($texts | Where-Object { $hits[$_] -lt 1 })
    if ($missed.Count -gt 0) {
        return "INSTRUMENT BROKEN ($Label): the scanner could not find $($missed.Count) of $($texts.Count) needle(s) in a buffer built to contain every one of them. Every 'not found' this run would have produced is meaningless. First missed: '$($missed[0])'."
    }
    $negative = [System.Text.Encoding]::UTF8.GetBytes(("the quick brown fox jumps over the lazy dog " * 32))
    $nhits = Measure-SqltNeedles -Data $negative -Length $negative.Length -NeedleSet $NeedleSet
    $spurious = @($texts | Where-Object { $nhits[$_] -gt 0 })
    if ($spurious.Count -gt 0) {
        return "INSTRUMENT BROKEN ($Label): the scanner reported $($spurious.Count) hit(s) in a buffer containing none of the needles. First: '$($spurious[0])'."
    }
    return $null
}
