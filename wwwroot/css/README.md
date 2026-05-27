<!-- In the name of God, the Merciful, the Compassionate -->

# CSS Architecture

The CSS follows a layered cascade: **tokens → primitives → per-page → per-component scoped**.

## Layer Cake

```
tokens (:root in app.css)          ← Design variables (colours, fonts, radii, motion)
   ↓
primitives (app.css, Glass.css)    ← Global utility classes, glassmorphism, skip-link
   ↓
per-page CSS (one file per Razor   ← Page-specific layout imported from app.css via @import
   page, @import-ed from app.css)
   ↓
per-component scoped CSS           ← Blazor CSS isolation (*.razor.css) auto-bundled
```

`app.css` defines `:root` custom properties for surfaces, borders, text, accent (teal `#2dd4bf`), status colours, typography, radii, and motion curves. All page CSS files reference these variables — never hard-coded hex values.

**Design tokens locked 2026-05-11.** The colour palette replaced the old VS Code dark scheme with a deeper charcoal surface stack and a teal accent. The old palette is backed up at `.ignore/app.css.pre-design-swap-2026-05-11`.

**Color-blind override path:** `body.colorblind-mode` in `Accessibility.css` remaps the canonical red/green/orange/yellow variables using Wong's (2011) palette (blue for success, orange-red for danger, amber for warning). This cascades cleanly because all components consume these variables, not raw colours.

## File Listing

| File | Purpose |
|------|---------|
| app.css | Root design tokens, global resets, skip-link, print styles, and `@import` aggregator |
| Accessibility.css | `body.colorblind-mode` variable overrides using Wong-safe palette |
| Glass.css | Glassmorphism primitives — frosted card variant and ambient background image |
| tailwind-input.css | Tailwind CSS input source with `@tailwind` directives |
| tailwind.css | Tailwind utility-class output (compiled from tailwind-input.css) |
| cssfromblazor.css | Blazor framework auto-generated CSS (component isolation base) |
| About.css | About page hero layout and version-card styling |
| AuditLogViewer.css | Audit log viewer timeline and event-detail panel |
| CheckValidator.css | Check validation results grid and diff-viewer styles |
| CioDashboard.css | CIO dashboard KPI cards, trend sparklines, and score breakdown |
| CodeHotspots.css | Code hotspot heatmap and commit-churn visualisation |
| CommandPalette.css | Command palette overlay, search input, and result-item highlight |
| ComplianceMap.css | Compliance framework coverage matrix and heatmap cells |
| DiagnosticsRoadmap.css | Diagnostics roadmap step cards, progress indicators, and timeline |
| DynamicDashboard.css | Panel grid layout, drag handles, resize chrome, and section headers |
| FullAudit.css | Full audit results layout, finding severity badges, and accordion sections |
| Governance.css | Policy editor form, scorecard gauge, and remediation checklist |
| Guide.css | Interactive guide step layout with progress dots and code blocks |
| HealthBadge.css | Health status indicator dot/badge with pulse animation for critical |
| IndexAnalysis.css | Index usage and missing-index recommendation table |
| NavMenu.css | Sidebar navigation tree with active-route highlight and collapse animation |
| PerfInspector.css | PerfMon counter grid layout and comparison delta styling |
| Playbooks.css | Playbook card grid with step numbering and status markers |
| QuickCheck.css | QuickCheck result summary, pass/warn/fail row colours, and expandable details |
| Settings.css | Settings form layout, tab navigation, and toggle-switch styling |
| StatusBadges.css | Fixed-position status indicator chips (mute, no-pants) with hover tooltips |
| qp.css | SQL Server ShowPlan XML rendering — operator boxes, arrows, and cost labels |
| qp_icons_v2.css | Query plan operator icon font and glyph mapping |
