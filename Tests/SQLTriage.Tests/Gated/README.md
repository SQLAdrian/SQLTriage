<!-- In the name of God, the Merciful, the Compassionate -->

# `Tests/SQLTriage.Tests/Gated/`

Every file here binds a symbol the **community** build does not contain, so the whole folder is
`Compile Remove`d from a community test build by one glob in `SQLTriage.Tests.csproj`:

```xml
<ItemGroup Condition="'$(SQLTriageProfile)' == 'community'">
  <Compile Remove="Gated\**\*.cs" />
</ItemGroup>
```

## Why the folder exists

On 2026-08-04 CI on `main` had not passed for ~11 days — 15 failures, 5 cancelled, zero successes
back to 2026-07-23. It failed at **build**, so `dotnet test` never ran: `main`'s suite had been
unverified by CI for the whole period, and the community edition — the one that ships publicly —
did not compile. The cause was one test file binding `RoadmapReport`, a type
`buildprofile.targets` `Compile Remove`s from the community build. Fixing it uncovered three more
in two further categories. One missed file was never going to be the only one.

## The rule

The community edition withholds a feature **three** ways, and every one of them strands a test:

1. **Whole-file `Compile Remove`** in `buildprofile.targets` — e.g. `Data\Services\RoadmapPdfBuilder.cs`
   (which holds `RoadmapReport` and the other `Roadmap*` DTOs), or the `Data\Services\Portal\**`,
   `Data\Services\AccessSurface\**`, `Data\Services\RiskReport\**` and `Mcp\**` globs.
   *Fails at build: `CS0246` (missing type) or `CS0234` (missing namespace).*
2. **`#if`-fenced members inside a file that still compiles** — e.g. `AssessmentPdf.BuildFindingsReport`
   and `AssessmentPdf.BuildDbaHandoffBundle` live in the always-compiled `AssessmentPdf.cs` but sit
   behind `!SQLT_NO_REPORT_FINDINGS_PDF` / `!SQLT_NO_REPORT_DBA_HANDOFF`.
   *Fails at build: `CS0117` (type has no such member).*
3. **Runtime refusal** — the symbol exists and compiles everywhere, and the method throws.
   `ScheduledReportService.Render` is the case: under the community profile its entire body is
   `throw new NotSupportedException("Executive report generation is not included in this edition.")`.
   *Fails at RUN time. Nothing at build time and no source scan can see this one.*

Category 2 hides from anything that reasons about *files* — the file is still in the build. Category
3 hides from the compiler entirely, so it survived until the community suite was finally executed,
where it accounted for four of the seven runtime failures that first run produced (the other three
were an edition-dependent page count and a component the community edition does not ship).

Where a gate is behaviour rather than a symbol, do not merely step around it: pin it. See
`ScheduledReportTests.Render_MatchesThisEditionsBriefingGate`, which asserts `Render` agrees with
`BuildModules.Reports.ExecutiveBriefing` in **both** editions, and
`RbacRound4RegressionTests.DrivableHandlers`, which drops a case only when the app's own
`BuildModules.LiveMonitoring` const says the page is not in this build.

## What to do when you add a test

If a test binds a gated symbol, or asserts behaviour this edition withholds, put **only that test**
here — do not move the whole fixture. The four compile-time strandings sat in files carrying 53 test
methods between them, of which 5 were actually gated; whole-file removal would have deleted 48
profile-independent tests from the only CI lane that runs. Where a gated test needs a helper from
its origin fixture, make the helper `internal static` and call it across — same assembly, no
duplication.

## Do NOT condition on `SQLTExclude*`

`buildprofile.targets` is imported by `SQLTriage.csproj` only. Those properties are **undefined** in
the test project, so `Condition="'$(SQLTExcludeReportRoadmap)' == 'true'"` would silently evaluate
false and the test would keep compiling — a gate that looks present and enforces nothing. The test
project mirrors `$(SQLTriageProfile)` instead (it defaults to `full`, and CI's global
`-p:SQLTriageProfile=community` reaches it).

## The guard

`Tests/SQLTriage.Tests/Build/ProfileGatedTestSyncTests.cs` derives the gated symbol set from the
same build inputs the compiler uses — `buildprofile.targets`, `buildprofile.json` and the `#if`
fences in the app sources — and fails when a test file that is still compiled under community
references one of them. It is a **lint, not the boundary**: the boundary is the community build
itself, which CI already runs. The lint exists so the failure lands in a developer's local suite
(which runs the default `full` profile and was green throughout the outage) instead of only in a
CI run on `main` that nobody read.
