/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Shared JSON config file load/save to eliminate boilerplate across services.
    /// All callers follow the same pattern: read file → deserialize → modify → serialize → write file.
    /// </summary>
    /// <summary>
    /// What actually happened to a config file on the way in. <see cref="ConfigFileHelper.Load{T}(string, JsonSerializerOptions?)"/>
    /// returns defaults for all four, which is right for a config file and WRONG for anything that
    /// then makes a security decision from the result: "this install has never been configured"
    /// and "this install's configuration did not parse" are the same object and opposite facts.
    ///
    /// <para>Callers that care ask for the outcome and decide for themselves. See
    /// <c>RbacService.DescribeEnforcementLapse</c>, which fails CLOSED on
    /// <see cref="Unreadable"/> and <see cref="Empty"/> rather than treating a truncated user
    /// store as an install with no users.</para>
    /// </summary>
    public enum ConfigLoadOutcome
    {
        /// <summary>The file parsed and its contents are in hand.</summary>
        Loaded,

        /// <summary>No such file. A genuinely fresh install — defaults are the honest answer.</summary>
        Missing,

        /// <summary>The file exists but holds nothing but whitespace — a half-written save, not a state any writer here produces.</summary>
        Empty,

        /// <summary>The file exists and has content, and that content did not deserialize: truncated, wrong shape, or unreadable.</summary>
        Unreadable
    }

    /// <summary>
    /// The WRITE side of <see cref="ConfigLoadOutcome"/>, and it exists because the read side alone
    /// was not enough. <see cref="ConfigFileHelper.Load{T}(string, JsonSerializerOptions?, out ConfigLoadOutcome)"/>
    /// tells a caller that the object in its hand is a default rather than the operator's. What it
    /// cannot do is stop that default being written straight back over the file it failed to read —
    /// which is worse than the original damage, because the damage was loud and the rewrite is quiet.
    ///
    /// <para><b>Measured, twice, in this codebase.</b> (1) Settings ▸ Access Control bound a
    /// checkbox to <c>RbacConfig.Enabled</c> with <c>@bind:after="SaveRbacConfig"</c>. With
    /// <c>rbac-config.json</c> truncated, that checkbox rendered byte-identical to the state where
    /// an operator had genuinely switched access control off, and ONE CLICK persisted the built-in
    /// defaults as their recorded choice — overwriting the damaged file and destroying the evidence
    /// it was ever damaged. (2) <c>/portal-status</c> never echoes the stored Azure SAS back into
    /// its input, so clicking Save wrote an empty string and cleared a live credential.</para>
    ///
    /// <para><b>So the rule is stated on the write, not on each control.</b> Four static scanners
    /// and four authorization audits in this lane were each defeated by the category they were
    /// blind to; a per-control guard would be defeated by the next control added to the page. A
    /// writer that has adopted this type must say which of the two things it is doing, and within
    /// such a writer the SAFE value is the default: a new MUTATOR on an adopting service that
    /// passes no intent is refused, not admitted.</para>
    ///
    /// <para><b>Read that last sentence narrowly, because the wider version of it was false and was
    /// printed here for a round.</b> This type is a PER-SERVICE CONVENTION. It is NOT the runtime
    /// chokepoint for writing a config file, and nothing in this class makes it one:
    /// <see cref="ConfigFileHelper.Save{T}"/> is unconditional, takes no outcome and no intent, and
    /// any caller can reach it and overwrite anything. A new SERVICE that never touches
    /// <see cref="StoreWriteIntent"/> is not refused — it is simply unguarded, and it will look
    /// exactly like the guarded ones from the outside.</para>
    ///
    /// <para><b>Measured, not theorised.</b> When the first seven stores were guarded on
    /// 2026-08-04, the claim made here was that the boundary was on the write. The cold gate then
    /// found three more stores that were not on the list at all —
    /// <c>ServerConnectionManager</c> (raw <c>File.WriteAllText</c>, no reference to this class,
    /// holding the only copies of the SQL passwords on the box),
    /// <c>ScheduledTaskDefinitionService</c> and <c>AlertDefinitionService</c> (both calling plain
    /// <c>Save</c>) — and destroyed an operator's data through each of them. A boundary that three
    /// live services walked through was never a boundary.</para>
    ///
    /// <para><b>What would make the claim true</b> is a save that cannot be called without the
    /// outcome of the load it descends from: one stateful store handle owning the path, the read,
    /// the cached outcome and the write, with <c>Save</c> unreachable except through it. That is a
    /// larger change than this lane has taken, so until it is taken the honest statement is the one
    /// above — convention, plus a per-service adoption list — and any surface that tells an operator
    /// their config is protected from a rewrite must name the store, never the product.</para>
    ///
    /// <para><b>Adopted as of 2026-08-05, and report-pages.json since 2026-09-12.</b> Refuse:
    /// portal-settings.json, notification-channels.json, remediation-grants.json, user-settings.json,
    /// server-connections.json, report-pages.json, remediation-credit-ledger.json (which fails closed
    /// on the READ, so its write guard is a backstop). Announce: alert-thresholds.json,
    /// alert-definitions.json, finding-owners.json, bp-scripts.json, scheduled-tasks.json. Everything
    /// else is unguarded.</para>
    ///
    /// <para><b>⚠ THIS LIST IS STILL HAND-KEPT AND IS STILL NOT THE GUARANTEE.</b> It is a reading
    /// aid. What a list cannot do is notice a store nobody added to it, which is precisely how the
    /// 2026-08-04 miss happened and why it is recorded above. Since 2026-09-12 the ENUMERATION lives in
    /// <c>Tests\SQLTriage.Tests\ConfigStoreI1CensusTests.cs</c>, which reads the compiled assembly —
    /// metadata and IL, never source text — to decide what a config store is and to refuse any store
    /// that answers a FAILED READ with a replacing write. That census covers one shape of the
    /// invariant, not the whole of it, and the store handle described below is still not built.</para>
    /// </summary>
    public enum StoreWriteIntent
    {
        /// <summary>
        /// These values descend from a store this process actually READ. Refused when the store did
        /// not load, because in that state the values are this code's defaults wearing the
        /// operator's name.
        /// </summary>
        FromLoadedStore,

        /// <summary>
        /// The operator has been told the file is unreadable and has explicitly asked for it to be
        /// replaced by what they are entering now. Only ever set by a deliberate operator action
        /// that named the consequence — never inferred, never a parameter default.
        /// </summary>
        ReplaceUnreadableStore
    }

    /// <summary>What a guarded persist actually did. Callers that ignore it still get the safe behaviour.</summary>
    public enum StoreWriteOutcome
    {
        /// <summary>Written to disk.</summary>
        Saved,

        /// <summary>
        /// Refused: the store on disk exists and did not load, and the caller did not declare
        /// <see cref="StoreWriteIntent.ReplaceUnreadableStore"/>. The file is untouched.
        /// </summary>
        RefusedStoreUnreadable,

        /// <summary>Nothing needed writing — a duplicate add, an unknown id. Not a refusal.</summary>
        NothingToWrite,

        /// <summary>
        /// The write was ALLOWED and then failed: the disk is full, the ACL denies it, the file is
        /// locked. Nothing reached the store.
        ///
        /// <para>Distinct from <see cref="RefusedStoreUnreadable"/> on purpose — the refusal is this
        /// code declining, and the operator's answer is "restore the file"; this is the machine
        /// declining, and the answer is "fix the disk, then do it again". Collapsing them would
        /// print recovery advice about a <c>.rejected-</c> copy that has nothing to do with what
        /// went wrong.</para>
        ///
        /// <para>It exists because the guarded mutators returned <see cref="Saved"/> on this path:
        /// the damaged-store refusal was pre-checked, so a caller was told the write was permitted,
        /// and then a THROWN write was swallowed by the save's own try/catch and the caller went on
        /// to log "Added". Same defect class as the store guard — a claim outliving the measurement
        /// it rests on — differing only in that the cause is IO. Found by the 2026-08-04 gate.</para>
        /// </summary>
        WriteFailed,

        /// <summary>
        /// Refused: the file on disk is not the one this process loaded. Something else wrote it —
        /// another process, an operator with an editor, a restore — after our load and before this
        /// save. Nothing was written.
        ///
        /// <para><b>Why this is separate from <see cref="RefusedStoreUnreadable"/>.</b> That one
        /// means "the file is damaged, restore it"; this one means "the file is FINE and newer than
        /// yours, reload before you overwrite someone's work". Collapsing them would print recovery
        /// advice about a <c>.rejected-</c> copy for a store that was never damaged — which is
        /// exactly the defect the 2026-08-04 gate found in the RBAC prose, one register over.</para>
        ///
        /// <para><b>Measured, 2026-08-05.</b> A healthy 1-connection store was loaded; a second
        /// connection carrying a credential was added to the file by another writer; the unattended
        /// <c>UpdateSuccessfulServers</c> then rewrote the whole estate from its process-lifetime
        /// cache. Hash <c>508E…</c>→<c>93CD…</c>; the second connection and its credential were
        /// silently deleted. The startup-outcome guard could not see it: the file had loaded
        /// perfectly, and nothing re-read it before the write.</para>
        /// </summary>
        RefusedStoreChangedOnDisk,

        /// <summary>
        /// Refused BEFORE the store was touched: the value the caller asked to store must be held in
        /// a protected form, and producing that form yielded nothing usable on this machine. Nothing
        /// was written and the stored value is exactly what it was.
        ///
        /// <para><b>Why this is not <see cref="WriteFailed"/>.</b> That one means the store declined
        /// the bytes, and the operator's answer is "fix the disk, then do it again"; this one means
        /// the bytes were never made, and doing it again on the same host produces the same nothing.
        /// Collapsing them would print "disk full, file locked, or permissions denied" for a disk
        /// that is fine, and invite a retry that cannot succeed.</para>
        ///
        /// <para><b>Where it comes from.</b> <see cref="CredentialProtector.Encrypt"/> does NOT throw
        /// when both AES-GCM and DPAPI fail — it returns "". A writer that assigned that result would
        /// replace a working credential with nothing, which is the /portal-status clearing incident
        /// with a different cause. Found by the 2026-08-05 gate on
        /// <c>PortalPublishRunner.SaveIntakeSas</c>, which assigned it unconditionally.</para>
        /// </summary>
        RefusedValueNotProtectable
    }

    public static class ConfigFileHelper
    {
        /// <summary>
        /// The one predicate behind <see cref="StoreWriteIntent"/>: would this write overwrite a
        /// store this process could not read, without the operator having asked for exactly that?
        ///
        /// <para><see cref="ConfigLoadOutcome.Missing"/> is deliberately NOT damage. A file that was
        /// never there is a fresh install, defaults are the honest answer, and refusing the first
        /// save would make onboarding impossible.</para>
        /// </summary>
        public static bool WouldOverwriteUnreadStore(ConfigLoadOutcome outcome, StoreWriteIntent intent)
            => intent != StoreWriteIntent.ReplaceUnreadableStore
               && outcome is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;

        /// <summary>
        /// THE register for "what an operator does about a damaged config store". Every surface that
        /// tells anyone what to do about damage — a page banner, a service's refusal log line, an
        /// enforcement lapse reason — renders THIS string. None composes its own.
        ///
        /// <para><b>Why it lives here rather than on a service.</b> It was born on
        /// <c>RbacService</c>, covering that service's two stores, because four surfaces had each
        /// written their own copy of "a copy of the unparseable one is kept beside it with a
        /// .rejected- suffix" — unconditionally, while the quarantine is conditional, so on a
        /// 0-byte file all four pointed at a file that was never written. Measured live by the
        /// 2026-08-04 cold gate. One register fixed those four. Extending the write guard to seven
        /// more stores would have created seven more copies of exactly that sentence, which is the
        /// same defect with a bigger denominator — so the register moved to the type that owns the
        /// measurement it is conditioned on. <c>RbacService.DescribeStoreRecovery</c> now delegates
        /// here and is a naming convenience, not a second copy.</para>
        ///
        /// <para>Conditioned on <paramref name="quarantinedPath"/>, which is what
        /// <see cref="Load{T}(string, JsonSerializerOptions?, out ConfigLoadOutcome, out string?)"/>
        /// actually DID, never on what it usually does.</para>
        /// </summary>
        /// <param name="outcome">The load outcome the caller measured.</param>
        /// <param name="quarantinedPath">The <c>.rejected-</c> copy that was actually taken, or null.</param>
        public static string DescribeStoreRecovery(ConfigLoadOutcome outcome, string? quarantinedPath)
        {
            // R5 (gate residual, 2026-08-25): this register rendered on /remediation with two
            // em-dashes in one sentence, which is the device dev/VOICE_GUIDE.md rules out. Same
            // three facts, one idea per sentence, no em-dash. Nothing conditional changed.
            if (quarantinedPath != null)
                return $"The unreadable file was copied aside to {Path.GetFileName(quarantinedPath)}. "
                     + "Its contents are not lost. Restore the file, then restart SQLTriage.";

            if (outcome == ConfigLoadOutcome.Empty)
                return "The file on disk is EMPTY. There was nothing to copy aside and nothing in it to "
                     + "recover. Restore it from your own backup, or enter a configuration now. Then restart "
                     + "SQLTriage.";

            // Damaged, and the copy itself failed (ACL, full disk). Saying a copy exists here
            // would be the same false claim in a rarer state.
            return "Restore the file from your own backup, then restart SQLTriage. "
                 + "No .rejected- copy was kept. The attempt to take one failed, so the original is all "
                 + "there is. Move it aside before re-entering settings if you want to keep it.";
        }

        private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

        /// <summary>
        /// Read options used when the caller supplies none. Trailing commas and // comments are
        /// tolerated because these files are hand-edited on client servers; without this a stray
        /// comma discards the whole file.
        /// </summary>
        private static readonly JsonSerializerOptions DefaultReadOptions = new()
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>
        /// Loads and deserializes a JSON config file. Returns a new instance of T if the file
        /// doesn't exist, is empty, or can't be parsed.
        /// <para>
        /// A file that fails to parse is copied aside to <c>&lt;file&gt;.rejected-&lt;utc timestamp&gt;</c>
        /// and a warning is logged before defaults are returned, so the next save cannot make the
        /// loss silent or permanent.
        /// </para>
        /// </summary>
        public static T Load<T>(string filePath, JsonSerializerOptions? options = null) where T : new()
            => Load<T>(filePath, options, out _);

        /// <summary>
        /// As <see cref="Load{T}(string, JsonSerializerOptions?)"/>, and additionally reports WHY
        /// the returned object is what it is.
        ///
        /// <para>The plain overload cannot distinguish "there is no file" from "the file did not
        /// parse" — both hand back <c>new T()</c>. That is fine for a settings file whose worst
        /// case is a reset preference, and it is a fail-OPEN for any caller that reads the result
        /// as evidence about the install's state. A truncated <c>rbac-users.json</c> deserialises
        /// to an empty user list, which reads identically to a brand-new install with no users
        /// yet; if a security predicate keys off that, corrupting one file relaxes the posture.
        /// Measured on 2026-08-03 — see <c>RbacService.IsBootstrapEligible</c>.</para>
        /// </summary>
        public static T Load<T>(string filePath, JsonSerializerOptions? options, out ConfigLoadOutcome outcome)
            where T : new()
            => Load<T>(filePath, options, out outcome, out _);

        /// <summary>
        /// As above, and additionally reports whether a <c>.rejected-</c> copy was actually taken —
        /// <c>null</c> when none exists.
        ///
        /// <para><b>Why this is an out-parameter and not an assumption.</b> Four separate surfaces
        /// told the operator "a copy of the unparseable one is kept beside it with a .rejected-
        /// suffix" whenever a store was damaged. That sentence was unconditional and the quarantine
        /// was not: it fires on <see cref="JsonException"/> only, so a <b>0-byte</b> file — which is
        /// damage, and is reported as damage — produced no copy at all, and the advice pointed at a
        /// file that was never written. Measured live by the 2026-08-04 cold gate. The copy can also
        /// simply fail (ACL, disk), which is a second way for the claim to be false.</para>
        ///
        /// <para>So callers get the FACT rather than a rule of thumb, and the prose is composed from
        /// it. This is the same correction the enforcement banner needed: a sentence beside a verdict
        /// must be conditioned on the same measurement as the verdict, or not printed.</para>
        /// </summary>
        public static T Load<T>(string filePath, JsonSerializerOptions? options,
            out ConfigLoadOutcome outcome, out string? quarantinedPath)
            where T : new()
            => Read<T>(filePath, options, out outcome, out quarantinedPath, recordDamage: true);

        /// <summary>
        /// The same read as <see cref="Load{T}(string, JsonSerializerOptions?, out ConfigLoadOutcome, out string?)"/>,
        /// reporting the outcome and NOTHING ELSE: no defaults handed back, no <c>.rejected-</c>
        /// copy taken, no log line written.
        ///
        /// <para><b>This is not a fourth mechanism — it is the same one, minus the side effects,
        /// for the one caller shape that cannot cache an outcome.</b> The guard is
        /// load-outcome → <see cref="WouldOverwriteUnreadStore"/> → refuse, and every stateful
        /// service THAT HAS ADOPTED IT caches the outcome from its startup load (the adoption list
        /// is on <see cref="StoreWriteIntent"/>; services not on it hold no outcome at all).
        /// <c>PortalPublishRunner</c> cannot: its
        /// settings seam is deliberately static and stateless, load-mutate-save inside each call,
        /// with four separate entry points and an injected save delegate. Its guard therefore sits
        /// in the write chokepoint and has to establish the outcome AT WRITE TIME.</para>
        ///
        /// <para>Doing that through <c>Load</c> would quarantine on the write path: every refused
        /// save would drop another timestamped <c>.rejected-</c> copy of the same damaged file, so
        /// the guard would litter the config directory in proportion to how often the operator
        /// tried. A probe takes no copy, which leaves the ONE copy taken at load as the thing the
        /// recovery advice names.</para>
        /// </summary>
        /// <summary>
        /// A cheap identity for the bytes on disk — SHA-256 of the file, or <c>null</c> when there
        /// is no file. A caller records this at load and re-takes it immediately before a write; if
        /// the two differ, the store it is about to overwrite is not the store it read.
        ///
        /// <para><b>Why a hash and not <see cref="InspectStore{T}"/>.</b> InspectStore answers "is
        /// the file damaged", which is a different question and misses the case that actually lost
        /// data: a perfectly healthy file that someone else legitimately added a connection to. That
        /// store parses fine — there is nothing for a damage probe to find — and a whole-file
        /// rewrite from a process-lifetime cache deletes the addition anyway.</para>
        ///
        /// <para>Not a lock and not a transaction: two writers can still interleave between this
        /// call and the write. It closes the window that matters here — a cache minutes or hours
        /// old rewriting a file edited since — and does not claim to close the microsecond one.
        /// Cost is one file read per save, on files measured in kilobytes.</para>
        /// </summary>
        public static string? FingerprintStore(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(filePath);
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch (Exception ex)
            {
                // Unreadable for some other reason (locked, ACL). Returning null here would read as
                // "no file", which would let a write proceed against a file we cannot account for -
                // so return a sentinel that can never equal a real hash, and the caller refuses.
                Serilog.Log.Warning(ex, "[ConfigFileHelper] Could not fingerprint {File}", filePath);
                return "UNREADABLE:" + Guid.NewGuid().ToString("N");
            }
        }

        public static ConfigLoadOutcome InspectStore<T>(string filePath, JsonSerializerOptions? options = null)
            where T : new()
        {
            Read<T>(filePath, options, out var outcome, out _, recordDamage: false);
            return outcome;
        }

        /// <summary>
        /// The single implementation behind both entry points. <paramref name="recordDamage"/> is
        /// the ONLY difference between them, and it is a parameter rather than a second copy of
        /// this method because two hand-maintained classifiers would drift — and a probe that
        /// disagreed with the loader about what counts as damage would be a guard that fires on
        /// files the loader read fine, or misses files it did not.
        /// </summary>
        private static T Read<T>(string filePath, JsonSerializerOptions? options,
            out ConfigLoadOutcome outcome, out string? quarantinedPath, bool recordDamage)
            where T : new()
        {
            quarantinedPath = null;
            try
            {
                if (!File.Exists(filePath))
                {
                    outcome = ConfigLoadOutcome.Missing;
                    return new T();
                }

                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    // Every writer here goes through Save, which serialises a real object and moves
                    // it into place atomically — an empty file is not a state this code produces,
                    // so it is damage (an interrupted non-atomic write, a truncating editor, a full
                    // disk), not "nothing configured yet".
                    // Deliberately NOT quarantined, and this is the honest choice rather than the
                    // convenient one. Copying a 0-byte (or whitespace) file aside produces a 0-byte
                    // copy: it preserves no evidence and there is nothing to restore FROM, so
                    // "restore it from the copy" would be advice that cannot be followed. Callers
                    // get quarantinedPath = null and say something true instead.
                    outcome = ConfigLoadOutcome.Empty;
                    if (recordDamage)
                        Serilog.Log.Warning(
                            "[ConfigFileHelper] {File} exists but is empty — running on defaults for {Type}. "
                            + "No .rejected- copy was taken: an empty file has nothing to preserve.",
                            filePath, typeof(T).Name);
                    return new T();
                }

                var loaded = JsonSerializer.Deserialize<T>(json, options ?? DefaultReadOptions);
                if (loaded == null)
                {
                    // Literal `null` in the file. Valid JSON, no object — so no JsonException fires
                    // and the catch below never runs. This IS quarantined: unlike the empty case
                    // there is content, and preserving it is what distinguishes "someone wrote null"
                    // from "the file was truncated to nothing" when this is investigated later.
                    if (recordDamage)
                    {
                        quarantinedPath = QuarantineUnreadableFile(filePath);
                        Serilog.Log.Warning(
                            "[ConfigFileHelper] {File} deserialised to null — running on defaults for {Type}. Original kept at {Rejected}.",
                            filePath, typeof(T).Name, quarantinedPath ?? "(copy failed)");
                    }
                    outcome = ConfigLoadOutcome.Unreadable;
                    return new T();
                }

                outcome = ConfigLoadOutcome.Loaded;
                return loaded;
            }
            catch (JsonException ex)
            {
                // Malformed / wrong-typed content: keep a copy before the caller's next save
                // overwrites it, and say so loudly. Callers still get defaults so startup survives.
                if (recordDamage)
                {
                    quarantinedPath = QuarantineUnreadableFile(filePath);
                    Serilog.Log.Warning(ex,
                        "[ConfigFileHelper] {File} could not be parsed and was NOT loaded — running on defaults for {Type}. Original kept at {Rejected}.",
                        filePath, typeof(T).Name, quarantinedPath ?? "(copy failed)");
                }
                outcome = ConfigLoadOutcome.Unreadable;
            }
            catch (Exception ex)
            {
                if (recordDamage)
                    Serilog.Log.Warning(ex, "[ConfigFileHelper] Failed to load {File}", filePath);
                outcome = ConfigLoadOutcome.Unreadable;
            }

            return new T();
        }

        /// <summary>
        /// Copies an unparseable config file aside. Returns the copy's path, or null if the copy
        /// itself failed (in which case the caller reports that rather than claiming a backup).
        /// </summary>
        private static string? QuarantineUnreadableFile(string filePath)
        {
            try
            {
                var rejectedPath = $"{filePath}.rejected-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
                File.Copy(filePath, rejectedPath, overwrite: true);
                return rejectedPath;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[ConfigFileHelper] Could not quarantine unreadable file {File}", filePath);
                return null;
            }
        }

        /// <summary>
        /// Serializes and writes a config object to a JSON file. Creates the directory if needed.
        /// Uses atomic write (temp file + move) to prevent corruption on crash.
        ///
        /// <para><b>This write is UNCONDITIONAL and it is not the guard.</b> It does not know the
        /// load outcome of the file it is about to replace and it cannot refuse anything. If the
        /// object you are handing it descends from a <see cref="Load{T}(string, JsonSerializerOptions?)"/>
        /// that may have returned defaults, the refusal has to live in your service, keyed off the
        /// outcome you cached — see <see cref="StoreWriteIntent"/> for the rule, the adoption list,
        /// and what it would take to make this method itself the boundary.</para>
        /// </summary>
        public static void Save<T>(string filePath, T config, JsonSerializerOptions? options = null)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config, options ?? DefaultOptions);

            // Atomic write: write to temp file, then move to target.
            //
            // The temp file holds a COMPLETE serialized copy of the config object. For the portal
            // settings that includes DailyIntakeSasProtected, the wrapped intake credential. A write
            // or a move that throws used to leave that copy on disk at a predictable name
            // (<file>.tmp), outside every path that manages the real file: nothing rotates it,
            // nothing overwrites it on the next successful save (the move consumes the temp), and no
            // caller knows it is there. So the failure path removes it before it propagates.
            //
            // The contract is UNCHANGED: the exception still leaves this method. Only the stray file
            // goes. A delete that itself fails is logged and named rather than swallowed, because a
            // silent failure here is the one case where a credential copy really does linger.
            var tempPath = filePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch
            {
                DiscardTempFile(tempPath);
                throw;
            }
        }

        /// <summary>
        /// Removes the atomic-write temp file after a failed save. Never throws: it runs on a path
        /// that is already propagating an exception, and masking that one with a delete failure would
        /// lose the reason the save failed.
        /// </summary>
        private static void DiscardTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex,
                    "[ConfigFileHelper] A config save failed and its temporary file {TempFile} could not be "
                    + "removed. That file holds a full copy of the config being written, which for some "
                    + "config files includes a wrapped credential. Delete it by hand.",
                    tempPath);
            }
        }
    }
}
