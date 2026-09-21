<!-- In the name of God, the Merciful, the Compassionate -->

# Dashboards

Top-level dashboard composition components — page-shell wrappers that arrange `Components/Shared/DynamicPanel` instances into the live operational and repository views.

| File | Purpose |
|------|---------|
| LiveDashboard.razor | Live-monitoring dashboard wrapper (CPU/IO/wait gauges, sessions, blocking) |
| RepositoryDashboard.razor | Repository-mode dashboard wrapper (historical/aggregated views) |

Both delegate panel rendering to `DynamicPanel` / `DynamicDashboard` via `DashboardConfig` from `Config/dashboard-config.json`.
