<!-- In the name of God, the Merciful, the Compassionate -->

# Layout

App-shell layout primitives. These wrap every route and provide the persistent navigation/toolbar chrome.

| File | Purpose |
|------|---------|
| MainLayout.razor | Top-level layout — sidebar nav, content area, status banner |
| NavMenu.razor | Left-rail navigation tree with grouping, RBAC gating, colour-blind toggle |
| DashboardToolbar.razor | Top toolbar for dashboard pages — server picker, baseline overlay, refresh |

`NavMenu.razor.backup-*` files are pre-refactor snapshots; do not edit, ignore at publish (`.publicignore`).
