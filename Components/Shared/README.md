<!-- In the name of God, the Merciful, the Compassionate -->

# Shared components

Reusable Razor components used across multiple pages. Includes modals, dialogs, viewers, gauges, gates, chart wrappers, dropdowns, and dashboard primitives.

Full catalogue at `.claude/docs/components-index.md`. Read that **before** searching this folder.

## Hot list (the heavy hitters)

| Component | Purpose |
|-----------|---------|
| DynamicPanel | The single rendering engine behind every dashboard panel — drives 30+ panel types via `panelType` switch |
| DynamicDashboard | Wraps `DynamicPanel`s with grid layout + drag/resize/edit affordances |
| ServerSelector | Universal server-picker dropdown (single, multi, all-scope) |
| QueryPlanModal | XML showplan → V2 renderer (consumes `Data/Services/ExecutionPlanParser`) |
| DeadlockViewer | `system_health` XEvent XML → tree view |
| DataGrid | Tabular display with sort/filter/group/CSV-export |
| CommandPalette | ⌘K-style command launcher |
| RbacGuard / AdminGuard | Permission gates wrapping privileged UI |

## Conventions

- Parameters declared with `[Parameter] public T X { get; set; } = default!;`
- No `<style>` blocks in `.razor` files — CSS goes in `wwwroot/css/<ComponentName>.css` and is `@import`-ed at the top of `app.css`
- Async event handlers return `Task`, sync handlers return `void`
