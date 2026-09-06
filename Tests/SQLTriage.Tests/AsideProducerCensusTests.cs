/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Every site that sets a file ASIDE must be REGISTERED, with what it preserves, how it
    /// classifies what it preserved, and what eventually cleans it up.
    ///
    /// <para>The population grew unruled: on 2026-08-06 the SQLCipher pre-re-init copy was found
    /// being written on every migrating install by a path that had never once run, and the ruling
    /// that closed it named THREE more never-cleaned asides in passing. Force-rotate then added a
    /// fourth producer. Nobody could say how many there were, because nothing counted them. The
    /// house rule (Adrian, 2026-08-09) is discriminate-and-prove: classify the aside, delete only
    /// after the replacement is proven. This census is what stops a NEW producer shipping outside
    /// that rule.</para>
    ///
    /// <para><b>Two nets, because either alone is defeatable.</b> The VOCABULARY net catches the
    /// words the house uses for this (<c>SetAside</c>, and the preservation suffixes themselves).
    /// The SHAPE net catches a <c>File.Move</c>/<c>Copy</c>/<c>Replace</c> whose DESTINATION is a
    /// preservation-named path, which is what a producer looks like to a reader who invents new
    /// vocabulary. A producer only has to trip ONE of them to be caught, and it has to trip NEITHER
    /// to be missed.</para>
    ///
    /// <para><b>Registration is not approval.</b> Three entries here are RULED-PRESERVED: their asides
    /// are never cleaned by anything, deliberately, because they ARE the recovery story. The
    /// register makes that a stated position rather than an omission.</para>
    ///
    /// <para><b>Honest limits, both of which MISS and neither of which falsely accuses.</b> (1) The
    /// comment stripper is line-leading only, so a producer written after code on a line that starts
    /// with <c>//</c> is invisible - that is not code. (2) The shape net reads the destination
    /// argument as the text between the first and second comma on the line, so a producer whose
    /// destination is computed on a following line trips only the vocabulary net. A miss is a site
    /// someone can still register later; a false accusation trains people to delete the test.</para>
    /// </summary>
    public class AsideProducerCensusTests
    {
        /// <summary>
        /// One registered producer. All three fields are required and asserted non-empty: a register
        /// entry that names a file and says nothing about it is the omission this census replaces.
        /// </summary>
        private sealed record AsideProducer(string Preserves, string Classification, string Cleanup);

        /// <summary>
        /// THE REGISTER. Keys are repo-relative paths with forward slashes.
        ///
        /// <para>Every entry was read before it was written here. Where a producer has no cleanup at
        /// all, the entry says so in those words rather than being left blank.</para>
        /// </summary>
        private static readonly Dictionary<string, AsideProducer> Register =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Data/KeyAsideLifecycle.cs"] = new(
                    Preserves:
                        "The shared lifecycle for DPAPI-wrapped KEY FILES. It owns the rename that the "
                        + "three key-holding services used to carry three copies of, and it is the only "
                        + "place a key aside is created, classified or removed.",
                    Classification:
                        "MEASURED, by unwrapping the aside's bytes through ProtectedData.Unprotect: "
                        + "Unwrapped, Sealed (read, and DPAPI refused it), Empty (zero bytes), or "
                        + "Unmeasured (the read itself threw). The name is used to FIND candidates and "
                        + "never to judge one.",
                    Cleanup:
                        "ReconcileAsides, and only when handed a proven ReplacementProof. It removes "
                        + "the two populations a measurement can prove worthless (zero bytes, or "
                        + "material byte-identical to the key now in use) and keeps every other, named "
                        + "in the log with the reading that kept it."),

                ["Data/CredentialProtector.cs"] = new(
                    Preserves:
                        "The DPAPI-LocalMachine-wrapped AES key that decrypts every saved 'aes:' "
                        + "credential. Overwriting it in place makes those credentials undecryptable "
                        + "forever, and silently: Decrypt returns an empty string.",
                    Classification:
                        "KeyAsideLifecycle.Classify, on the regeneration path only. There is no "
                        + "length gate on the read (censused 2026-08-10), so this file has no "
                        + "wrong-length branch to make safe: it uses whatever length it unwrapped, "
                        + "and AES-GCM refuses a bad one at the point of use.",
                    Cleanup:
                        "KeyAsideLifecycle.ReconcileAsides, after ProveReplacement has read the new key "
                        + "file back off disk and unwrapped it to the material now in use. RULED "
                        + "2026-08-10: an aside that could not be taken, or a replacement that could "
                        + "not be proven, REFUSES rather than overwriting or running on."),

                ["Data/SqliteCipherHelper.cs"] = new(
                    Preserves:
                        "TWO different things under two different rulings. (a) The SQLCipher key file, "
                        + "whose loss orphans every encrypted store. (b) The pre-re-init DATABASE copy, "
                        + "<store>.db.pre-reinit-<utc>, plus the -wal/-shm siblings that can hold the "
                        + "whole of a WAL-mode store's contents.",
                    Classification:
                        "(a) KeyAsideLifecycle.Classify (an unwrap), on BOTH regeneration branches "
                        + "since 2026-08-10: the one where the key will not unwrap, and the one where "
                        + "it unwraps to the wrong length and used to be regenerated over. "
                        + "(b) ClassifyDatabaseFile, which reads the first 16 bytes and compares them "
                        + "with the plain SQLite header.",
                    Cleanup:
                        "(a) ReconcileAsides after ProveReplacement. (b) RemoveVerifiedPlaintextAside "
                        + "(RULED 2026-08-06): the PLAINTEXT population is deleted after the replacement "
                        + "store reads back AND its own first 16 bytes measure not-plain; the unreadable "
                        + "population is kept indefinitely. Note the OPPOSITE polarity of the two: an "
                        + "unreadable database is kept because it may still hold data, an unreadable key "
                        + "is kept because its refusal may be transient."),

                ["Data/Services/Licensing/SeatRegister.cs"] = new(
                    Preserves:
                        "The DPAPI-LocalMachine-wrapped HMAC key that signs the append-only seat-event "
                        + "chain. Losing it does not lose the events, it loses the ability to verify "
                        + "them, so the aside is evidence as much as it is a key.",
                    Classification:
                        "KeyAsideLifecycle.Classify, on BOTH regeneration branches since 2026-08-10: "
                        + "the one where the key will not unwrap, and the one where it unwraps to the "
                        + "wrong length and used to be regenerated over.",
                    Cleanup:
                        "KeyAsideLifecycle.ReconcileAsides, after ProveReplacement. The seat register is "
                        + "a REFUSE-class store in the export fallback registry and this posture matches "
                        + "it: only a provably worthless aside is ever removed, and since 2026-08-10 an "
                        + "aside that could not be taken refuses the overwrite outright."),

                ["Data/AuditLogService.cs"] = new(
                    Preserves:
                        "The audit-chain HMAC signing key blob, as <keyPath>.unreadable-<utc>, written "
                        + "before a fresh key is minted. Every entry signed by the old key becomes "
                        + "unverifiable, so the blob is the only route back if the original Windows "
                        + "identity is ever restored.",
                    Classification:
                        "MEASURED at the moment it is preserved and not afterwards: the unwrap has "
                        + "already been attempted and failed, and the blob's LENGTH is reported beside "
                        + "the replacement (a raw legacy key is exactly 32 bytes). Nothing re-reads it "
                        + "on a later pass.",
                    Cleanup:
                        "RULED-PRESERVED. Nothing cleans it, deliberately: it is forensic material for "
                        + "an identity restore, and the log line that mints the replacement names the "
                        + "path it was kept at."),

                ["Data/Services/Portal/Export/DpapiKeyStore.cs"] = new(
                    Preserves:
                        "TWO. (a) SetAside moves a lane's unreadable export key to "
                        + "export-<lane>.key.unreadable-<utc>Z on the force-rotation path. (b) Archive "
                        + "files a retiring key under its own fingerprint, which is the only way to read "
                        + "the vaults it encrypted.",
                    Classification:
                        "MEASURED before either runs, by ClientKeyCustody: force rotation is REFUSED "
                        + "unless the record has actually failed to unwrap here, and refused with a "
                        + "different sentence when it reads fine.",
                    Cleanup:
                        "RULED-PRESERVED, both. The aside IS the recovery story (the key is CurrentUser "
                        + "scoped, so a record unreadable under this account may be perfectly readable "
                        + "under the one that minted it) and an archive is never overwritten, let alone "
                        + "deleted. Nothing auto-cleans either, and nothing should."),

                ["Data/Services/Portal/Export/ClientKeyCustody.cs"] = new(
                    Preserves:
                        "Nothing itself: it is the CALLER that decides a force rotation is warranted and "
                        + "then asks DpapiKeyStore.SetAside to move the record. It is registered because "
                        + "it holds the refusal, which is where the classification actually happens.",
                    Classification:
                        "MEASURED: the lane's record is read and an unwrap attempted first, and the "
                        + "force path throws unless that unwrap failed.",
                    Cleanup:
                        "RULED-PRESERVED, as DpapiKeyStore above. The aside path is returned to the "
                        + "caller and named in the log so an operator can find it."),

                ["Data/ConfigFileHelper.cs"] = new(
                    Preserves:
                        "A config file that would not parse, copied to <file>.rejected-<utc> before the "
                        + "caller is handed defaults.",
                    Classification:
                        "MEASURED and it is the reason the copy exists: the file was READ and failed to "
                        + "deserialise. An empty file is measured too, and gets no copy at all, because "
                        + "an empty file has nothing to preserve.",
                    Cleanup:
                        "NONE, and this is not a ruled position. The copies accumulate. They are plain "
                        + "config, not secret material, so the exposure argument that authorises the "
                        + "SQLCipher delete does not apply and neither does the key ruling. Left as it "
                        + "stands rather than swept under a rule nobody has taken."),

                ["Data/DashboardConfigService.cs"] = new(
                    Preserves:
                        "TWO. (a) A dashboard config that would not deserialise, copied to "
                        + "<config>.corrupt.<stamp>.json. (b) The previous config, copied to "
                        + "dashboard-config.backup.json on every Save, which is a rolling backup rather "
                        + "than an accumulating aside.",
                    Classification:
                        "(a) MEASURED: the deserialise was attempted and threw. (b) NOT classified and "
                        + "does not need to be: it is a single fixed path, overwritten each Save, and it "
                        + "is read back as the fallback when the live config will not load.",
                    Cleanup:
                        "(a) NONE, and not a ruled position: the .corrupt. copies accumulate. (b) Self "
                        + "limiting at one file. Same reasoning as ConfigFileHelper: ordinary config, no "
                        + "secret material, no ruling taken."),

                ["Data/DashboardFactoryReset.cs"] = new(
                    Preserves:
                        "The operator's whole Config/dashboard-config.json, copied to "
                        + "dashboard-config.backup.json immediately before a per-dashboard factory reset "
                        + "writes over it. The reset replaces one dashboard's panels, queries, layout and "
                        + "enable flags with the shipped ones and cannot be undone from inside the app, so "
                        + "this copy is the operator's only route back to their edits.",
                    Classification:
                        "NOT classified, and does not need to be. It is the SAME single fixed path "
                        + "DashboardConfigService.Save already writes on every operator edit (registered "
                        + "above as (b), a rolling backup rather than an accumulating aside): one file, "
                        + "overwritten, read back as the fallback when the live config will not load.",
                    Cleanup:
                        "Self limiting at one file, exactly as the Save-time backup. Two orderings make "
                        + "that safe rather than lucky: the copy is taken only when there is something to "
                        + "write, so a REFUSED reset cannot destroy an older backup; and a copy that "
                        + "throws refuses the write rather than proceeding unbacked."),

                ["Data/Services/Portal/PortalPublishRunner.cs"] = new(
                    Preserves:
                        "The per-user portal settings file, renamed to <file>.migrated-<date> after its "
                        + "contents are carried forward to the machine-scoped path.",
                    Classification:
                        "MEASURED, and the measurement is a COLLISION check rather than a content one: "
                        + "an existing same-day .migrated file is never overwritten, and the completion "
                        + "line then points the operator at the legacy path instead of at a file holding "
                        + "someone else's content.",
                    Cleanup:
                        "NONE. It is a one-shot migration, so the file does not accumulate, and it is "
                        + "the only copy of the operator's old settings."),
            };

        /// <summary>
        /// How many lines each registered file matched WHEN IT WAS REGISTERED. Pinned so that a new
        /// producer added to an already-registered file fails too: a register that absorbs fresh debt
        /// under an old name is not a census.
        ///
        /// <para>These are NET MATCHES, not producer counts. Prose inside a string literal that names
        /// a suffix ("No .rejected- copy was kept") matches the vocabulary net and is counted here,
        /// because the alternative is a net that tries to judge whether a line means what it says.</para>
        /// </summary>
        private static readonly Dictionary<string, int> PinnedMatches =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Data/KeyAsideLifecycle.cs"] = 3,
                ["Data/CredentialProtector.cs"] = 1,
                // 4 → 5 and 1 → 2 on 2026-08-10: posture (a) gave the wrong-length branch in each of
                // these its own SetAside call. It was regenerating over material that UNWRAPPED, with
                // no aside at all, which is the population the whole rule exists to keep.
                ["Data/SqliteCipherHelper.cs"] = 5,
                ["Data/Services/Licensing/SeatRegister.cs"] = 2,
                ["Data/AuditLogService.cs"] = 3,
                ["Data/Services/Portal/Export/DpapiKeyStore.cs"] = 5,
                ["Data/Services/Portal/Export/ClientKeyCustody.cs"] = 2,
                ["Data/ConfigFileHelper.cs"] = 4,
                ["Data/DashboardConfigService.cs"] = 3,
                // Registered 2026-08-26 with the per-dashboard factory reset: one File.Copy of the
                // operator's config to the rolling backup path, taken only when the reset is going to
                // write.
                ["Data/DashboardFactoryReset.cs"] = 1,
                ["Data/Services/Portal/PortalPublishRunner.cs"] = 2,
            };

        // ── The two nets ────────────────────────────────────────────────────

        /// <summary>The words this house uses for preservation, and the suffixes it writes.</summary>
        internal static readonly Regex VocabularyNet = new(
            @"SetAside|PreserveAside|TryPreserveAside|\.corrupt-|\.corrupt\.|\.unreadable-|\.pre-reinit-|\.rejected-|\.migrated-",
            RegexOptions.Compiled);

        /// <summary>
        /// A file operation whose DESTINATION is a preservation-named path. This is the net that does
        /// not depend on anyone using the house vocabulary.
        /// </summary>
        internal static readonly Regex ShapeNet = new(
            @"File\.(Move|Copy|Replace)\s*\(\s*[^,]+,\s*[^,)]*(?i:aside|preserv|retired|corrupt|rejected|backup|quarantin|migrated)",
            RegexOptions.Compiled);

        /// <summary>True if this source line looks like an aside producer to either net.</summary>
        internal static bool IsProducerLine(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;
            if (trimmed.StartsWith("*", StringComparison.Ordinal)) return false;
            if (trimmed.StartsWith("/*", StringComparison.Ordinal)) return false;
            if (trimmed.StartsWith("@*", StringComparison.Ordinal)) return false;
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal)) return false;

            return VocabularyNet.IsMatch(line) || ShapeNet.IsMatch(line);
        }

        // ── The census ──────────────────────────────────────────────────────

        [Fact]
        public void Every_aside_producing_site_in_the_app_is_registered()
        {
            var found = Scan(out var scanned);

            // The scan is a WALK. If it ever stops scanning far more files than it names, it has
            // quietly become a hard-coded list, which is how the arming census was defeated on the
            // day it was written.
            scanned.Should().BeGreaterThan(Register.Count * 10,
                "the census walks the whole app tree rather than reading its own register back");

            var unregistered = found.Keys
                .Where(f => !Register.ContainsKey(f))
                .SelectMany(f => found[f])
                .ToList();

            unregistered.Should().BeEmpty(
                "a new aside producer must be registered with what it preserves, how it classifies "
                + "what it preserved, and what cleans it up (or an explicit ruled-preserved entry). "
                + "Unregistered: " + string.Join(" | ", unregistered));

            var drifted = PinnedMatches
                .Where(kv => (found.TryGetValue(kv.Key, out var lines) ? lines.Count : 0) != kv.Value)
                .Select(kv => $"{kv.Key}: pinned {kv.Value}, found "
                              + (found.TryGetValue(kv.Key, out var l) ? l.Count : 0))
                .ToList();

            drifted.Should().BeEmpty(
                "each registered file's match count is pinned, so neither a new producer inside an "
                + "already-registered file nor a removed one passes silently. Update the pin and say "
                + "why in the register: " + string.Join(" | ", drifted));
        }

        [Fact]
        public void Every_registered_producer_states_all_three_things()
        {
            Register.Should().NotBeEmpty();
            PinnedMatches.Keys.Should().BeEquivalentTo(Register.Keys,
                "a registered file without a pin, or a pin without a register entry, is a half-entry");

            foreach (var (path, entry) in Register)
            {
                entry.Preserves.Trim().Length.Should().BeGreaterThan(40,
                    $"{path} must say WHAT it preserves");
                entry.Classification.Trim().Length.Should().BeGreaterThan(40,
                    $"{path} must say HOW what it preserved gets classified");
                entry.Cleanup.Trim().Length.Should().BeGreaterThan(40,
                    $"{path} must say what CLEANS it, or state that nothing does and why");
            }
        }

        [Fact]
        public void The_ruled_preserved_asides_are_declared_as_such_and_not_merely_uncleaned()
        {
            // The force-rotate .unreadable- aside is part of the recovery story and is never
            // auto-cleaned (Adrian, 2026-08-09). "Nothing cleans it" and "nothing may clean it" are
            // different statements, and only one of them is a position.
            foreach (var path in new[]
            {
                "Data/Services/Portal/Export/DpapiKeyStore.cs",
                "Data/Services/Portal/Export/ClientKeyCustody.cs",
                "Data/AuditLogService.cs",
            })
            {
                Register[path].Cleanup.Should().Contain("RULED-PRESERVED",
                    $"{path}'s aside is kept on purpose, which has to be stated rather than implied");
            }

            // And the converse: an entry that has no cleanup and no ruling must say so in words,
            // so nobody reads a blank as a decision.
            foreach (var path in new[]
            {
                "Data/ConfigFileHelper.cs",
                "Data/DashboardConfigService.cs",
                "Data/Services/Portal/PortalPublishRunner.cs",
            })
            {
                Register[path].Cleanup.Should().Contain("NONE",
                    $"{path} has no cleanup, and an unstated absence is how this population grew");
            }
        }

        /// <summary>
        /// The three key-holding services, and the three calls each must carry.
        ///
        /// <para>⚠ THIS IS A LINT, not a behaviour test, and the distinction is the point.
        /// <see cref="Licensing.SeatRegisterKeyAsideTests"/> proves the wiring RUNS, end to end,
        /// because SeatRegister takes its key path as a constructor argument. The other two resolve
        /// theirs from a private static readonly field under AppContext.BaseDirectory, and that IS a
        /// coverage gap. The obstacle is ISOLATION, not visibility: on 2026-08-09 both were driven
        /// end to end through their public entry points alone (<c>CredentialProtector.Encrypt</c> and
        /// <c>SqliteCipherHelper.OpenEncrypted</c>), with no source change and no seam of any kind, by
        /// planting the key file and its asides in the test host's own config directory, and both
        /// passed first run. The cost is that this directory is process-global and the rest of the
        /// suite reads it: that probe rotated the host's SQLite cipher key, orphaned
        /// <c>alert-history.db</c>, and turned a green community run red on
        /// <c>AlertFiringBasisTests</c> until the orphaned stores were deleted. Taking the coverage
        /// therefore needs a dedicated xunit collection plus save-and-restore of that directory,
        /// which this lane did not build. What this test can establish is that the calls are present,
        /// which is what an unwiring looks like in a diff. It cannot establish that they execute.</para>
        /// </summary>
        [Fact]
        public void Every_key_holding_service_is_wired_to_the_shared_lifecycle()
        {
            var root = RepoRoot();

            foreach (var relative in new[]
            {
                "Data/CredentialProtector.cs",
                "Data/SqliteCipherHelper.cs",
                "Data/Services/Licensing/SeatRegister.cs",
            })
            {
                var source = File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

                source.Should().Contain("KeyAsideLifecycle.SetAside(",
                    $"{relative} must preserve through the shared rename rather than a local copy of it");
                source.Should().Contain("KeyAsideLifecycle.ProveReplacement(",
                    $"{relative} must read its new key back off disk before anything is judged against it");
                source.Should().Contain("KeyAsideLifecycle.ReconcileAsides(",
                    $"{relative} must classify what it set aside; an aside nothing ever looks at again "
                    + "is the state this lane exists to end");

                source.Should().NotContain("private static void TryPreserveAside",
                    $"{relative} carried its own nine-line rename with no classifier and no cleanup; "
                    + "three copies of it is how the populations diverged");
                source.Should().NotContain("private void TryPreserveAside", $"{relative}: as above");
            }
        }

        /// <summary>
        /// The fail-closed postures (RULED 2026-08-10), as a LINT over the same three files.
        ///
        /// <para>⚠ Same standing as the test above, and the same limit: this establishes that every
        /// SetAside result is handed to a refusal and that a refusal exists to hand it to. It cannot
        /// establish that either one runs. <see cref="Licensing.SeatRegisterKeyPostureTests"/> drives
        /// all three postures end to end at the one service whose key path is a constructor argument;
        /// the other two are unreachable in-process without rotating the test host's own key files,
        /// which is the incident recorded above.</para>
        /// </summary>
        [Fact]
        public void No_key_holding_service_can_discard_the_result_of_setting_a_key_aside()
        {
            var root = RepoRoot();

            foreach (var relative in new[]
            {
                "Data/CredentialProtector.cs",
                "Data/SqliteCipherHelper.cs",
                "Data/Services/Licensing/SeatRegister.cs",
            })
            {
                var source = File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

                var asides = Regex.Matches(source, @"KeyAsideLifecycle\.SetAside\(").Count;
                var guarded = Regex.Matches(source, @"RefuseUnlessPreserved\(KeyAsideLifecycle\.SetAside\(").Count;

                asides.Should().BeGreaterThan(0, $"{relative} must still preserve before it replaces");
                guarded.Should().Be(asides,
                    $"{relative}: RULED 2026-08-10, a caller that could not preserve the existing "
                    + "material refuses to overwrite it. A SetAside whose result goes nowhere is the "
                    + "exact shape of the defect this closed, at every branch, not just the first");

                source.Should().Contain("throw new KeyAsideRefusedException(",
                    $"{relative} must have a refusal to hand that result to");
                Regex.Matches(source, @"throw new KeyAsideRefusedException\(").Count
                    .Should().Be(2,
                        $"{relative} carries TWO refusals: the unpreserved one (posture b, which "
                        + "covers the wrong-length branch of posture a too) and the unproven one "
                        + "(posture c). One of them missing is a posture silently dropped");
            }
        }

        /// <summary>
        /// WHERE THE REFUSAL GOES. <see cref="SQLTriage.Data.KeyAsideRefusedException"/>'s doc, and
        /// SeatRegister's, state that the log is the only surface the message reaches. This is the
        /// measurement those sentences are conditioned on.
        ///
        /// <para>Both docs previously justified the message's SHAPE by naming a caller: "VerifyChain
        /// returns ex.Message to the caller, which is why the message names the file and the
        /// recovery step". MEASURED 2026-08-10: <c>ISeatRegister.VerifyChain</c> has no production
        /// caller anywhere. The three pages that inject <c>ISeatRegister</c> call <c>Seats.Filter</c>
        /// and nothing else, and the only callers in the repo are tests. So the stated reason for
        /// the copy rested on a call site that does not exist, which is the house defect class: a
        /// claim about the code's own properties, asserted beside a design decision, never
        /// measured.</para>
        ///
        /// <para>The day someone wires it to a screen this FAILS, which is the point. A new surface
        /// is a reason to read that copy again rather than a thing to find out later.</para>
        ///
        /// <para>HONEST LIMIT: this matches the NO-ARGUMENT call shape, which is the seat register's
        /// signature. <c>AuditLogService.VerifyChain</c> is a different method on a different type
        /// and every production call to it passes a trigger, so it does not collide here. A caller
        /// that reached the seat register's method through reflection would be missed. That is a
        /// miss, never a false accusation.</para>
        /// </summary>
        [Fact]
        public void The_seat_refusal_reaches_the_log_and_nothing_returns_it_to_a_screen()
        {
            var root = RepoRoot();
            var callers = new List<string>();

            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".razor", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (IsExcluded(relative)) continue;

                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)
                        || trimmed.StartsWith("*", StringComparison.Ordinal)
                        || trimmed.StartsWith("/*", StringComparison.Ordinal)) continue;

                    // The declaration and the implementation are not callers.
                    if (line.Contains("string? VerifyChain(", StringComparison.Ordinal)) continue;

                    if (Regex.IsMatch(line, @"\bVerifyChain\s*\(\s*\)"))
                        callers.Add($"{relative}:{i + 1}: {line.Trim()}");
                }
            }

            callers.Should().BeEmpty(
                "the refusal's copy is written for a log reader, because the log is the only place "
                + "it goes. A production caller here means the sentence now reaches a surface it was "
                + "not written for, and the docs on KeyAsideRefusedException and on SeatRegister."
                + "RefuseUnlessPreserved have to be re-measured: " + string.Join(" | ", callers));
        }

        [Fact]
        public void Both_nets_bite_and_neither_fires_on_an_ordinary_atomic_write()
        {
            // Positive controls, one per net.
            IsProducerLine(@"File.Move(keyPath, keyPath + "".corrupt-"" + stamp);")
                .Should().BeTrue("vocabulary net: the suffix is right there");
            IsProducerLine(@"File.Move(active, quarantineTarget);")
                .Should().BeTrue("shape net: a preservation-named destination, no house vocabulary");
            IsProducerLine(@"    string preservedPath = _store.SetAside(lane);")
                .Should().BeTrue("vocabulary net: the verb");

            // Negative controls. An atomic temp-then-promote is the shape this census must NOT
            // report, because it is most of the writes in the tree and a census that cries wolf
            // gets deleted.
            IsProducerLine(@"File.Move(tmp, _path, overwrite: true);")
                .Should().BeFalse("a temp promote preserves nothing");
            IsProducerLine(@"File.Copy(source, dest, overwrite: true);")
                .Should().BeFalse();
            IsProducerLine(@"// File.Move(path, path + "".corrupt-"" + stamp) used to live here")
                .Should().BeFalse("a commented-out line is not code");
        }

        // ── The walk ────────────────────────────────────────────────────────

        private static Dictionary<string, List<string>> Scan(out int scanned)
        {
            var root = RepoRoot();
            var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            scanned = 0;

            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".razor", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (IsExcluded(relative)) continue;
                scanned++;

                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!IsProducerLine(lines[i])) continue;
                    if (!found.TryGetValue(relative, out var sites))
                        found[relative] = sites = new List<string>();
                    sites.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }

            return found;
        }

        /// <summary>
        /// Test sources are out of scope (a test asserting on a suffix has to be able to type one),
        /// and so is anything under bin/obj, which is a copy of what was already scanned.
        /// </summary>
        private static bool IsExcluded(string relative) =>
            relative.StartsWith("Tests/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase);

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                dir = dir.Parent;
            dir.Should().NotBeNull("the census reads app SOURCE, so it needs the repo root");
            return dir!.FullName;
        }
    }
}
