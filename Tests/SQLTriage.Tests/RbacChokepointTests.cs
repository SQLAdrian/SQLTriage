/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The structural close of the RBAC fail-open surfaces — two methods on 2026-08-17, and the
    /// eligibility TERM on the same date, one lane later.
    ///
    /// <para><b>What is closed, precisely, and by WHICH instrument.</b> Two things by the language:
    /// the fail-open two-argument <c>IsAuthorized</c> is DELETED, and the raw matrix
    /// <c>HasPermission</c> is PRIVATE. One thing at runtime: the surviving gate's third parameter is
    /// <see cref="AppUserState.BootstrapEligibilityProof"/>, and the gate accepts only the ONE
    /// instance <see cref="AppUserState"/> minted (<c>AppUserState.IsTheMintedProof</c>). Until
    /// 2026-08-17 the gate took a plain <c>bool</c>, so a page could pass a literal <c>true</c> and
    /// reproduce the deleted overload exactly: MEASURED at 8959044 in <c>Pages/Settings.razor</c>,
    /// 0 errors. That plant is now <c>CS1503</c>.</para>
    ///
    /// <para><b>The type alone was NOT the close, and this file said it was for one commit.</b> A
    /// private constructor stops every spelling that NAMES it and nothing else:
    /// <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> is a declaration the compiler accepts,
    /// it uses no reflection API, and MEASURED on this tree it minted the token from a helper in
    /// <c>Data/Services</c> called by one line in a shipped page — 0 build errors, and a viewer
    /// granted <c>settings</c> on a dormant install. Presence was therefore manufacturable, so the
    /// question the gate asks changed from "is there a token" to "is it OUR token"
    /// (<see cref="AForgedProofIsDeniedWhereTheMintedProofGrants"/>).</para>
    ///
    /// <para><b>What is NOT closed, stated in the same breath so no sentence here reads as more.</b>
    /// (1) <c>null</c> is spellable everywhere and is the DENY case by design —
    /// <see cref="ANullProofDeniesWhereARealProofWouldHaveGranted"/> pins that it denies rather than
    /// being treated as unknown. (2) THEFT of the minted instance grants, and must: code that can
    /// read a private static field — reflection, or <c>UnsafeAccessorKind.StaticField</c> — holds the
    /// real token, and the test suite steals it in <c>BootstrapProofForTests</c> because that is the
    /// only way another assembly can exercise the eligible branch. Nothing in-process closes that;
    /// <see cref="NothingInTheAssemblyReachesTheBootstrapProof"/> and
    /// <see cref="NothingInTheAssemblyDeclaresAnUnsafeAccessor"/> are lints against it, and they are
    /// named as lints. (3) The ROLE argument is still a caller-supplied string, so a page holding the
    /// service could ask about a role it does not have. That shape is older than this lane and
    /// unchanged by it; what stands against it is <c>RbacServerModeLockoutTests</c>'
    /// <c>NoShippedUiComputesAnAuthorizationDecisionItself</c>, which bans a page calling the gate at
    /// all — and only in <c>Pages/</c> and <c>Components/</c>, which is the scope the same verifier
    /// walked around by putting the call one file away — plus the holder pin that keeps the service
    /// out of all but three files.</para>
    ///
    /// <para><b>What changed, and why this file is SHORT.</b> From 2026-08-01 to 2026-08-16 seven
    /// rounds of <see cref="RbacServerModeLockoutTests"/> tried to stop a shipped page reaching
    /// <c>RbacService</c>'s fail-open authorization overloads. Each round closed a way of SPELLING
    /// the call so a text scan could not see it; each was defeated by the next spelling, because
    /// spellings are unbounded and a text instrument can always be out-spelt. The lane that produced
    /// this file stopped chasing spellings and removed two call TARGETS instead:</para>
    /// <list type="number">
    ///   <item><description><c>public bool IsAuthorized(string role, string permission)</c> — the
    ///   fail-open two-argument overload, zero callers — DELETED.</description></item>
    ///   <item><description><c>public static bool HasPermission(string role, string permission)</c> —
    ///   the raw matrix — made <c>private</c>.</description></item>
    /// </list>
    /// <para>Pages and components compile into the same assembly as <c>RbacService</c>, so
    /// <c>internal</c> would have changed nothing; only <c>private</c> puts the boundary where a
    /// page cannot reach. MEASURED 2026-08-17: all eight of rounds 4-7's evasion shapes (the
    /// <c>@@</c> escape, an <c>@{ }</c> markup block, a raw string literal, a mid-line block comment
    /// in markup, an interpolation hole, a <c>global::</c>-qualified verbatim namespace alias, a
    /// two-line <c>[Inject]</c> split, and a run-away <c>@*</c> span) were planted one at a time in a
    /// shipped page. Every one built with <b>0 errors at 930c1a7</b> and every one now FAILS TO
    /// COMPILE — six with <c>CS0122</c> naming the private matrix, two with <c>CS7036</c> for the
    /// deleted overload. That control matters: a plant that never compiled would prove nothing.</para>
    ///
    /// <para><b>Why this file is not another census.</b> The surface THIS FILE lints is one named
    /// method, <see cref="RbacService.EvaluateApiKeyPermission"/>, which is <c>internal</c> because
    /// <c>ApiAuthorization</c> is a different file in the same assembly and therefore cannot be
    /// served by <c>private</c>. One internal method is a bounded thing to lint, unlike "every way a
    /// page might spell a call", so a plain substring scan is proportionate here and a
    /// comment-stripping machine is not. The same argument, and the same shape of scan, now covers a
    /// second bounded name: <see cref="AppUserState.BootstrapEligibilityProof"/>, which a page has no
    /// reason to mention either. Both scans are deliberately LOUD — they match the name anywhere in a
    /// shipped UI file, including inside a comment.</para>
    /// </summary>
    public class RbacChokepointTests
    {
        /// <summary>
        /// The one remaining internal surface stays out of the UI. The fix when this fires is to
        /// route the page through <see cref="AppUserState.IsAuthorized(string)"/> — never to add an
        /// allowlist, which is the failure mode the 2026-08-01 <c>KnownOffenders</c> entry recorded.
        /// </summary>
        [Fact]
        public void NoShippedUiNamesTheApiKeyPermissionEvaluator()
        {
            var root = RawPassedScan.RepoRoot();
            var offenders = new List<string>();

            foreach (var file in ShippedUiFiles(root))
            {
                var lines = File.ReadAllText(file.FullName).Split('\n');
                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains("EvaluateApiKeyPermission"))
                        offenders.Add(relative + ":" + (i + 1) + "  " + lines[i].Trim());
            }

            Assert.True(
                offenders.Count == 0,
                "RbacService.EvaluateApiKeyPermission is the API-key trust tier's door onto the raw "
                + "permission matrix. It answers from the table alone — it does not know whether RBAC "
                + "is enforced and it does not know whether the caller may use the bootstrap hatch, "
                + "both of which ApiAuthorization settles for itself and a page cannot. UI gates go "
                + "through AppUserState.IsAuthorized(\"permission\"). Offenders:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The two removed surfaces, asserted rather than assumed: the matrix is still
        /// <c>private</c>, and the two-argument overload is still gone. Widening either back reopens
        /// the spelling war those seven rounds fought, so it goes red here rather than being noticed
        /// later by a scan that was already proved out-spellable.
        ///
        /// <para><b>RENAMED 2026-08-17. The old name was
        /// <c>TheFailOpenSurfacesAreNotReachableFromOutsideRbacService</c>, and it asserted more than
        /// this body measures.</b> This test checks two modifiers, and the name now says so. The
        /// eligibility term is a separate fact with its own test —
        /// <see cref="NoIsAuthorizedOverloadTakesACallerSuppliedBool"/> — kept separate deliberately:
        /// one test, one measurement. A test name is an assertion like any other.</para>
        /// </summary>
        [Fact]
        public void TheMatrixStaysPrivateAndTheTwoArgOverloadStaysDeleted()
        {
            var rbac = typeof(RbacService);

            var matrix = rbac.GetMethod(
                "HasPermission",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(string), typeof(string) },
                modifiers: null);

            Assert.True(matrix != null, "RbacService.HasPermission(string, string) has gone missing.");
            Assert.True(
                matrix!.IsPrivate,
                "RbacService.HasPermission must stay PRIVATE. It is the raw matrix: it knows nothing "
                + "about enforcement and nothing about bootstrap eligibility. Pages and components "
                + "compile into this same assembly, so internal would make it callable from every one "
                + "of them and the compiler would stop refusing the shape seven rounds of "
                + "RbacServerModeLockoutTests could not reliably detect. Current modifier makes it "
                + "reachable: " + (matrix.IsPublic ? "public" : matrix.IsAssembly ? "internal" : "other") + ".");

            var failOpenOverload = rbac.GetMethod(
                "IsAuthorized",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(string), typeof(string) },
                modifiers: null);

            Assert.True(
                failOpenOverload == null,
                "RbacService.IsAuthorized(string, string) is back. It passed bootstrapEligible: true "
                + "unconditionally, so a page calling it returned true for ANY caller on an "
                + "unconfigured install — including one arriving over the LAN. It was deleted on "
                + "2026-08-17 with zero callers. The gate is IsAuthorized(role, permission, "
                + "bootstrapEligible), reached from the UI through AppUserState.IsAuthorized.");
        }

        /// <summary>
        /// The capability close, as a modifier fact: <b>no</b> <c>IsAuthorized</c> overload takes a
        /// <c>bool</c>. That is the whole shape of the defect this lane removed — an eligibility term
        /// the CALLER supplies. Re-adding one (an overload, a convenience wrapper, an optional
        /// parameter) puts the literal <c>true</c> back within a page's reach, so it goes red here.
        ///
        /// <para>Stated as "no bool anywhere in the signature" rather than "the third parameter is
        /// the proof type", because the defect is a caller-supplied boolean and not a position. The
        /// proof type's own properties are asserted separately in
        /// <see cref="TheBootstrapProofIsSealedWithNoConstructorACallerCanName"/>, and what the gate
        /// does with the token in
        /// <see cref="AForgedProofIsDeniedWhereTheMintedProofGrants"/>.</para>
        /// </summary>
        [Fact]
        public void NoIsAuthorizedOverloadTakesACallerSuppliedBool()
        {
            var offenders = typeof(RbacService)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "IsAuthorized")
                .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(bool)))
                .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "An RbacService.IsAuthorized overload takes a bool. Until 2026-08-17 the surviving "
                + "gate took bootstrapEligible as a plain bool, and a page passing a literal true "
                + "reproduced the deleted fail-open overload exactly — measured at 8959044 in "
                + "Pages/Settings.razor, 0 errors. The term is now AppUserState."
                + "BootstrapEligibilityProof, which a caller cannot write. Do not hand the bool back:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The token's own properties, each one load-bearing and each one a way the close could be
        /// undone quietly: it is a CLASS (a struct's <c>default</c> would be a free instance, so
        /// <c>IsAuthorized(role, perm, default)</c> would grant), it is SEALED (a subclass carries
        /// its own constructor), and every constructor it declares is PRIVATE (an internal one would
        /// be callable from every page, since pages compile into the same assembly).
        ///
        /// <para><b>What this does NOT say, and the name was corrected on 2026-08-17 to stop it
        /// saying so.</b> It was <c>TheBootstrapProofCannotBeConstructedByAnyCallerThatCompiles</c>,
        /// and that is false: the lane's verifier wrote a caller that compiles and constructs, using
        /// <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c>, which reaches a private
        /// constructor without naming it. This body checks three modifiers and the name now says
        /// exactly that. A test name is an assertion like any other — the same correction the parent
        /// commit made to <c>TheFailOpenSurfacesAreNotReachableFromOutsideRbacService</c>, repeated
        /// here because the lane made the identical mistake one commit later. What stops a
        /// constructed instance is <see cref="AForgedProofIsDeniedWhereTheMintedProofGrants"/>, not
        /// these modifiers.</para>
        /// </summary>
        [Fact]
        public void TheBootstrapProofIsSealedWithNoConstructorACallerCanName()
        {
            var proof = typeof(AppUserState.BootstrapEligibilityProof);

            Assert.True(proof.IsClass, "the bootstrap proof must be a reference type: default(T) on a struct is a free instance.");
            Assert.True(proof.IsSealed, "the bootstrap proof must be sealed: a subclass would carry its own constructor.");
            Assert.Same(typeof(AppUserState), proof.DeclaringType);

            var reachable = proof
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(c => !c.IsPrivate)
                .Select(c => c.IsAssembly ? "internal" : c.IsPublic ? "public" : "other")
                .ToList();

            Assert.True(
                reachable.Count == 0,
                "AppUserState.BootstrapEligibilityProof has a constructor a caller can name ("
                + string.Join(", ", reachable) + "). Pages and components compile into the same "
                + "assembly, so anything short of private hands every page a mintable hatch token.");
        }

        /// <summary>
        /// <b>The gate's runtime chokepoint, measured rather than described.</b> Same service, same
        /// role, same permission, same dormant install: the MINTED token grants the bootstrap hatch
        /// and a FORGED one — a second instance of the same type, carrying the same (absent) state —
        /// is refused. Only identity differs, so only identity can explain the difference.
        ///
        /// <para><b>Why this test exists at all (2026-08-17, the fix round).</b> The gate used to read
        /// the token as <c>bootstrapProof is not null</c>, and presence was manufacturable in source
        /// that compiles: <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> is a declaration,
        /// not a reflection call, so a helper anywhere in the assembly minted the type with 0 build
        /// errors and a shipped page calling it self-authorized on an unconfigured install — MEASURED
        /// on this tree, granted before the change and denied after it. No accessibility modifier
        /// stops that shape, which is why the close moved to runtime.</para>
        ///
        /// <para>What it does NOT say: that a caller cannot be granted by other means. Stealing the
        /// minted instance out of <c>AppUserState</c>'s private field grants, and must —
        /// <c>BootstrapProofForTests</c> does exactly that, because it is the only way another
        /// assembly can exercise the eligible branch at all.</para>
        /// </summary>
        [Fact]
        public void AForgedProofIsDeniedWhereTheMintedProofGrants()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rbac-forge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // No config file: dormant, so the hatch branch is live and the token decides.
                var rbac = new RbacService(
                    NullLogger<RbacService>.Instance,
                    Path.Combine(dir, "rbac-config.json"),
                    Path.Combine(dir, "rbac-users.json"));
                Assert.False(rbac.IsRbacEnforced());

                Assert.NotSame(BootstrapProofForTests.Minted, BootstrapProofForTests.Forged);

                Assert.True(
                    rbac.IsAuthorized(AppRoles.Viewer, "settings", BootstrapProofForTests.Minted),
                    "the token AppUserState actually minted must still open the bootstrap hatch — "
                    + "without this half, denying the forgery would prove only that the hatch is shut "
                    + "for everyone, which is a lockout and not a close.");

                Assert.False(
                    rbac.IsAuthorized(AppRoles.Viewer, "settings", BootstrapProofForTests.Forged),
                    "a forged proof GRANTED. The gate is back to testing presence rather than "
                    + "identity, and presence is manufacturable in compiling source: one "
                    + "[UnsafeAccessor(UnsafeAccessorKind.Constructor)] declaration mints this type "
                    + "from any file in the assembly. That is the deleted fail-open overload's "
                    + "semantics rebuilt — on an unconfigured install a viewer, including one arriving "
                    + "over the LAN, gets settings, manage_users, run_scripts and manage_servers. The "
                    + "gate must ask AppUserState.IsTheMintedProof.");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup; ignore */ }
            }
        }

        /// <summary>
        /// A closed constructor is worth nothing if something hands the instance out. Every member in
        /// the shipped assembly typed on the proof — <b>whatever its accessibility</b> — is censused,
        /// and the census must equal exactly one entry: the private field
        /// <c>AppUserState._mintedProof</c>, which is the single producer and the single holder.
        ///
        /// <para><b>Widened on 2026-08-17 after it was proved blind.</b> The first draft skipped
        /// private members (<c>if (method.IsPrivate) continue;</c>), and the lane's verifier walked
        /// straight through that: a PRIVATE <c>extern</c> declaration carrying
        /// <c>[UnsafeAccessor(UnsafeAccessorKind.Constructor)]</c> returns the proof and was invisible
        /// here. Accessibility was the wrong axis — it is the return TYPE that makes a member a
        /// producer, so accessibility is no longer consulted. Pinning the census to an exact set
        /// rather than a "no offenders" list also fails on the day the producer field is renamed or
        /// duplicated, which a subtractive lint cannot do.</para>
        ///
        /// <para>Both directions matter, and this is a LINT, not a boundary: a member that hands out
        /// the real token defeats the runtime identity check completely, and nothing in-process can
        /// stop it. What the census buys is that such a member fails on the day it is written.</para>
        /// </summary>
        [Fact]
        public void NothingInTheAssemblyReachesTheBootstrapProof()
        {
            var proof = typeof(AppUserState.BootstrapEligibilityProof);

            Type?[] types;
            try { types = proof.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            const BindingFlags All = BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // The one legitimate producer: the field the token type's static constructor writes.
            const string Producer = "SQLTriage.Data.Services.AppUserState._mintedProof (field)";

            var census = new List<string>();
            foreach (var type in types)
            {
                if (type == null) continue;

                foreach (var method in type.GetMethods(All))
                {
                    // By-REF return included deliberately: an UnsafeAccessorKind.StaticField accessor
                    // for the minted field returns 'ref proof', which a by-value check would miss.
                    if (method.ReturnType == proof || method.ReturnType == proof.MakeByRefType())
                        census.Add(type.FullName + "." + method.Name + " (returns the proof)");

                    foreach (var p in method.GetParameters())
                        if (p.ParameterType == proof.MakeByRefType())
                            census.Add(type.FullName + "." + method.Name + " (yields the proof through '" + p.Name + "')");
                }

                foreach (var field in type.GetFields(All))
                    if (field.FieldType == proof)
                        census.Add(type.FullName + "." + field.Name + " (field)");

                foreach (var property in type.GetProperties(All))
                    if (property.PropertyType == proof)
                        census.Add(type.FullName + "." + property.Name + " (property)");
            }

            var unexpected = census.Where(entry => entry != Producer).ToList();
            Assert.True(
                unexpected.Count == 0,
                "Something other than the single minted field reaches an "
                + "AppUserState.BootstrapEligibilityProof. A member that RETURNS one is a mint (a "
                + "private [UnsafeAccessor] extern is exactly that, and compiles clean); a member that "
                + "HOLDS one hands the real token to whoever can read it, which defeats the identity "
                + "check outright. Either way it is the fail-open hatch with one extra step. Route the "
                + "decision through AppUserState.IsAuthorized(\"permission\"). Offenders:\n  "
                + string.Join("\n  ", unexpected));

            Assert.True(
                census.Count(entry => entry == Producer) == 1,
                "The single producer " + Producer + " is missing or duplicated — found "
                + census.Count(entry => entry == Producer) + ". One mint, one holder, one hand-over "
                + "site is the whole design; if the field moved, this census must be re-pinned "
                + "deliberately rather than left matching nothing. Census:\n  "
                + string.Join("\n  ", census));
        }

        /// <summary>
        /// <b>No <c>[UnsafeAccessor]</c> anywhere in the shipped assembly.</b> That attribute reaches
        /// private members without naming them — it is reflection's power in a declaration the
        /// compiler accepts, so accessibility cannot answer it and neither can any source-text scan
        /// that greps for member names.
        ///
        /// <para>It is banned outright rather than banned near the proof type, because the dangerous
        /// use is not only <c>UnsafeAccessorKind.Constructor</c> on the token: <c>StaticField</c> on
        /// <c>AppUserState._mintedProof</c> steals the real token, and .NET 10's
        /// <c>[UnsafeAccessorType]</c> names its target as a STRING, so a check keyed on the type
        /// would be out-spelt the way seven rounds of name lints were. This is a whole CATEGORY —
        /// accessibility-bypassing declarations — and the assembly has never contained one.</para>
        ///
        /// <para>If a legitimate use ever arrives (it is a performance tool, and a fair one), this
        /// test is the place to record the decision, with the reason and the date, beside a statement
        /// of what it can reach. Do not exempt a folder: the plant that made this test necessary lived
        /// in <c>Data/Services</c>, one call away from a page.</para>
        /// </summary>
        [Fact]
        public void NothingInTheAssemblyDeclaresAnUnsafeAccessor()
        {
            var assembly = typeof(AppUserState).Assembly;

            Type?[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            const BindingFlags All = BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            static bool IsUnsafeAccessor(IEnumerable<CustomAttributeData> attributes) =>
                attributes.Any(a => a.AttributeType.Name.StartsWith("UnsafeAccessor", StringComparison.Ordinal));

            var offenders = new List<string>();
            foreach (var type in types)
            {
                if (type == null) continue;

                foreach (var member in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    if (IsUnsafeAccessor(member.GetCustomAttributesData()))
                        offenders.Add(type.FullName + "." + member.Name + " carries [UnsafeAccessor]");

                    foreach (var p in member.GetParameters())
                        if (IsUnsafeAccessor(p.GetCustomAttributesData()))
                            offenders.Add(type.FullName + "." + member.Name + " has [UnsafeAccessorType] on '" + p.Name + "'");
                }
            }

            Assert.True(
                offenders.Count == 0,
                "The shipped assembly declares an [UnsafeAccessor]. It bypasses accessibility by "
                + "design, so every private modifier in this file's story stops meaning what it says: "
                + "one such declaration mints AppUserState.BootstrapEligibilityProof, and another "
                + "reads the minted one straight out of its private field. MEASURED 2026-08-17 — both "
                + "shapes BUILD with 0 errors, and the first granted a viewer 'settings' on a dormant "
                + "install until the gate started checking identity. Offenders:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The null decision, pinned rather than described. <c>null</c> is the one spelling of the
        /// eligibility term a caller CAN write, so what it means is a security decision: it means NO
        /// HATCH, and it is read that way in both enforcement branches.
        ///
        /// <para>Measured against the case where the answer differs — a dormant install, where a real
        /// proof grants a viewer a permission the matrix refuses. Same service, same role, same
        /// permission; only the token changes. That is what makes this a fail-CLOSED proof rather
        /// than a restatement of the matrix.</para>
        /// </summary>
        [Fact]
        public void ANullProofDeniesWhereARealProofWouldHaveGranted()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rbac-proof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // No config file: dormant, so the hatch branch is live and the term decides.
                var rbac = new RbacService(
                    NullLogger<RbacService>.Instance,
                    Path.Combine(dir, "rbac-config.json"),
                    Path.Combine(dir, "rbac-users.json"));
                Assert.False(rbac.IsRbacEnforced());

                Assert.True(
                    rbac.IsAuthorized(AppRoles.Viewer, "settings", BootstrapProofForTests.Minted),
                    "a minted proof on a dormant install is the bootstrap hatch — it must still grant.");

                Assert.False(
                    rbac.IsAuthorized(AppRoles.Viewer, "settings", bootstrapProof: null),
                    "a null proof must FAIL CLOSED: no token means no hatch, so the answer is the "
                    + "permission matrix, which refuses a viewer the settings permission.");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup; ignore */ }
            }
        }

        /// <summary>
        /// The proof type's name stays out of the UI, for the same reason
        /// <see cref="NoShippedUiNamesTheApiKeyPermissionEvaluator"/> exists and with the same
        /// proportion: one bounded name, so a plain substring scan is enough and a comment-stripping
        /// machine is not.
        ///
        /// <para>This is not the guard that closes the hatch —
        /// <see cref="AForgedProofIsDeniedWhereTheMintedProofGrants"/> is, and the assembly-wide
        /// census beside it is the broader lint. This one is narrow and cheap: a UI file that writes
        /// the type's name down is reaching for the token by some route, and the name is the one part
        /// reflection and <c>[UnsafeAccessor]</c> both have to spell. It scans <c>Pages/</c> and
        /// <c>Components/</c> only, so it does not see a helper one folder away — the census does.</para>
        /// </summary>
        [Fact]
        public void NoShippedUiMintsABootstrapProof()
        {
            var root = RawPassedScan.RepoRoot();
            var offenders = new List<string>();

            foreach (var file in ShippedUiFiles(root))
            {
                var lines = File.ReadAllText(file.FullName).Split('\n');
                var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains("BootstrapEligibilityProof"))
                        offenders.Add(relative + ":" + (i + 1) + "  " + lines[i].Trim());
            }

            Assert.True(
                offenders.Count == 0,
                "A shipped UI file names AppUserState.BootstrapEligibilityProof. The token is the "
                + "bootstrap hatch in argument form: a page that holds one can authorise itself on an "
                + "unconfigured install exactly as it once could by passing a literal true. It cannot "
                + "construct one (CS0122), so a mention here means either reflection or a new way of "
                + "obtaining it — both are the defect. UI gates go through "
                + "AppUserState.IsAuthorized(\"permission\"). Offenders:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>Pages/ and Components/, .razor and .cs — the surface a shipped page lives on.</summary>
        private static List<FileInfo> ShippedUiFiles(DirectoryInfo root)
        {
            var files = new List<FileInfo>();
            foreach (var scanRoot in new[] { "Pages", "Components" })
            {
                var dir = new DirectoryInfo(Path.Combine(root.FullName, scanRoot));
                if (!dir.Exists) continue;

                files.AddRange(dir.EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.Extension is ".razor" or ".cs"));
            }

            Assert.True(files.Count > 0, "found no shipped UI files to scan — the scan is not reaching the tree.");
            return files;
        }
    }
}
