<!-- In the name of God, the Merciful, the Compassionate -->

# Components

The `Components/` folder is split into **Layout/** (app shell) and **Shared/** (reusable UI primitives). A third subfolder, **Dashboards/**, hosts full-page dashboard route components that delegate rendering to `DynamicDashboard`.

## Layout/ — App Shell

| Component | Description |
|-----------|-------------|
| MainLayout | Top-level Razor layout with sidebar, skip-link, print header, toast slot, and keyboard shortcut wiring |
| NavMenu | Collapsible left navigation tree with search, role-based visibility, and color-blind mode toggle |
| DashboardToolbar | Instance selector, time-range picker, print, auto-refresh, and PDF-export toolbar |

## Shared/ — Reusable UI

| Component | Description |
|-----------|-------------|
| DynamicPanel | Data-fetching panel that delegates to chart/table/gauge renderers based on `PanelType` |
| StatCard | Single-value card with label, formatted value, unit, colour threshold, and hover description |
| DeltaStatCard | StatCard variant that shows directional delta (up/down arrow) versus a baseline |
| BlockingTreeViewer | Recursive tree renderer for blocking chains with session detail tooltips |
| DeadlockViewer | Deadlock XML graph parser with victim/resource visualisation |
| QueryPlanModal | Modal overlay rendering ShowPlan XML via qp.css with operator cost annotations |
| TimeSeriesChart | ApexCharts wrapper for line/area time-series with series grouping and zoom |
| HorizontalBarChart | ApexCharts horizontal bar chart for category-ranked data |
| DonutChart | ApexCharts donut chart for proportion/percentage data |
| BarGauge | CSS-only horizontal bar with threshold-driven colour and label |
| DataGrid | Sortable, filterable DataTable renderer with top-N row limiter |
| CollapsibleSection | Expand/collapse wrapper with title and child-content slot |
| SessionBubbleView | Visual session-grid bubble chart showing active connections |
| SessionDetailPanel | Slide-out panel showing session properties, current statement, and plan |
| SessionLegend | Colour legend mapping session status to visual encoding |
| CommandPalette | Ctrl+K overlay for fuzzy page search and keyboard navigation |
| ToastContainer | Toast notification stack with auto-dismiss and alert/success/error variants |
| ConnectionDialog | Modal connection-string editor with test-connect and recent-servers list |
| RbacGuard | Conditional render wrapper that hides content based on user role |
| AdminGuard | Specialised guard that shows `AccessDenied` for non-admin users |
| LoadingIndicator | Inline skeleton/spinner placeholder for async panel loads |
| PageLoadingSpinner | Full-page spinner shown during initial page load |
| DashboardPanelWrapper | Decorator that adds drag/drop/resize chrome around a `DynamicPanel` |
| PanelEditorModal | Modal for editing panel type, query, thresholds, and display options |
| SectionEditorModal | Modal for editing dashboard section layout and title |
| AddSectionModal | Lightweight modal for adding a new named section to a dashboard |
| EditPageToolbar | Floating toolbar with edit/save/cancel actions for dashboard edit mode |
| PdfExportModal | Modal for configuring and triggering server-side PDF report generation |
| FileSelectModal | File-picker modal for .sql/.ps1 script selection |
| ReleaseNotesModal | Version changelog modal shown on first launch after update |
| ShortcutsDialog | Keyboard shortcut reference overlay triggered by `?` |
| HealthBadge | Small coloured dot/badge for instance health status |
| RateLimitBadge | Displays API rate-limit consumption as a progress bar |
| BaselineSeedingProgress | Progress bar showing historical-baseline data seeding status |
| ServerModeToggle | Toggle switch between connected/local/server modes |
| OnboardingWizard | Multi-step guided setup for first-time users |
| CheckValidatorTable | Results grid for check-script validation with pass/fail row colours |

## Dashboards/ — Full-Page Dashboards

| Component | Description |
|-----------|-------------|
| LiveDashboard | Route-based dashboard (`/live`) that delegates to `DynamicDashboard` with `DashboardId="live"` |
| RepositoryDashboard | Route-based dashboard for repository-scoped panels |

## Conventions

- Components don't fetch data; pages do. Components receive data via parameters and render it.
- Shared components use `[Parameter]` for inputs and `EventCallback<T>` for outputs.
- Layout components may inject services directly since they control app-wide concerns.
- Scoped CSS files (per-component) live alongside `.razor` files; global layout CSS lives in `wwwroot/css/`.
