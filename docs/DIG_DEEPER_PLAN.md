# Dig Deeper — Environment Topology Discovery (design + plan)

Status: DRAFT 2026-06-10 (Opus). Feature owner: Adrian. Security review (step 2) to be slotted by Adrian
before or after the MVP build.

## Goal
From a single seed SQL server, actively discover the surrounding SQL estate and walk the
**replication / mirroring / Always On AG / log-shipping / linked-server daisy chain**, presenting a live,
expanding global map of how data moves. Replication is the driving use case; built for the whole estate.

## Non-negotiable principles ("no-pants" = explicit, ephemeral, atomic)
- **Consent-gated.** A modal must be accepted every run. No "don't ask again", no stored consent.
- **Ephemeral.** Discovery results are held in memory for the current view only and discarded on
  navigate-away / Clear. Nothing persisted unless the user explicitly clicks "Add discovered servers to
  catalogue".
- **Atomic.** Each run starts from empty and re-discovers; no incremental state carried between runs.
- **AD, never blind scan.** Enumerate SQL servers via Active Directory SPNs (`MSSQLSvc/*`). No port scanning.
- **Read-only.** Every SQL probe is read-only (catalog/DMV SELECTs). No writes, ever.
- **Auditable.** Log the discovery operation (start, seed, server count, who) via `AuditLogService`.

## What already exists (reuse — do NOT rebuild)
- `Pages/EnvironmentView.razor` (`/environment`, "Operations Hub") — toolbar (`Scan`/`Clear`/`Export PNG`),
  `ServerSelector` with All-scope, force-directed canvas `#env-topo-canvas`, host-click detail panel,
  progress UI, `IDisposable`/`DotNetObjectReference` interop.
- `wwwroot/scripts/environmentView.js` — force-directed canvas renderer. API:
  `environmentView.renderTopology(canvasId, json)` where json = `{ servers:[{name,error,counts,hosts:[...]}],
  crossLinks:[{fromServer,toServer,connectionCount}] }`; `environmentView.setHostCallback(dotNetRef)` →
  calls `[JSInvokable] OnHostNodeClicked(hostname)`; `environmentView.exportPng(canvasId)`.
- `Data/ConnectionManager.cs` (`ServerConnectionManager`, Singleton) — `AddConnection(ServerConnection)`,
  persists to `Config/server-connections.json`. **This is the add-to-catalogue API.**
- `Data/Models/ServerConnection.cs` — `ServerNames` (newline-sep), `GetServerList()`,
  `GetConnectionString(server, db)`, AuthenticationType (Windows/SqlServer/EntraMFA).
- `Data/SqlServerConnectionFactory.cs` + `Data/QueryExecutor.cs` — connection/query plumbing.
- DI: `Data/ServiceCollectionExtensions.cs` (Singletons for managers/factories).
- `Pages/ReplicationMap.razor` — existing transactional-replication view (reference for repl semantics).
- TFM `net8.0-windows`; Radzen.Blazor 5 (DialogService available for the consent modal);
  Microsoft.Data.SqlClient 6. `System.DirectoryServices` is built-in (add reference).

The current `Scan` maps **clients connecting to catalogued servers** (via `dm_exec_sessions/connections`).
Dig Deeper is different: it **discovers servers not in the catalogue** and **server-to-server** topology.

## New components
1. **`AdServerLocator`** (`Data/Services/Discovery/AdServerLocator.cs`, Singleton)
   - LDAP query against the current domain: `(servicePrincipalName=MSSQLSvc/*)` via
     `System.DirectoryServices.DirectorySearcher` (or `DirectoryServices.Protocols`).
   - Parse SPNs → `host:port` / `host:instance`. Returns candidate instance names.
   - Graceful: not domain-joined / no AD → returns empty (feature falls back to seed-only crawl).
2. **`SqlTopologyProbe`** (`Data/Services/Discovery/SqlTopologyProbe.cs`)
   - One read-only SQL batch per server returning typed neighbor edges:
     - Linked servers: `sys.servers` (is_linked=1).
     - Replication: `sys.servers` is_publisher/subscriber/distributor + (in distribution DB)
       `MSpublications`/`MSsubscriptions`/`MSdistribution_agents`; publisher/subscriber server names.
     - AG: `sys.availability_replicas.replica_server_name`, `sys.dm_hadr_availability_replica_states`.
     - Mirroring: `sys.database_mirroring.mirroring_partner_instance` + endpoints.
     - Log shipping: `msdb.dbo.log_shipping_primary_databases` / `log_shipping_secondary`.
   - Returns `{ serverName, edges: [{ targetServer, kind, detail }], reachable, error }`.
     kind ∈ replication-pub|replication-sub|ag-replica|mirror-partner|linked-server|logship-primary|logship-secondary.
   - Connection via the seed `ServerConnection.GetConnectionString(targetServer, "master")` (integrated auth),
     short `CommandTimeout`. Auth/timeout failure → node flagged unreachable, crawl continues.
3. **`EnvironmentDiscoveryService`** (`Data/Services/Discovery/EnvironmentDiscoveryService.cs`, Singleton)
   - Orchestrates: optional AD seed set + the user-selected seed server → **BFS crawl**.
   - For each unvisited server: probe → emit `TopologyNode` + `TopologyEdge`s → enqueue new neighbor servers.
   - Dedup by normalized instance name (case-insensitive, strip default-instance suffix).
   - Bounds (config consts): MaxServers (e.g. 250), MaxDepth (e.g. 6), per-server timeout (e.g. 10s),
     concurrency cap (e.g. 8), overall `CancellationToken`.
   - Exposes an event/`IProgress<TopologyDelta>` (or `Channel`) the page subscribes to for live updates.
   - Holds the in-memory graph for the run; cleared on dispose / new run (ephemeral).

## UI changes (`EnvironmentView.razor`)
- Add a **"Dig deeper"** button to the LEFT of `ServerSelector` in `env-toolbar-right` (or a new left slot).
  Distinct styling (e.g. `btn-accent` + `fa-sitemap`) and a tooltip.
- **Consent modal** (Radzen `DialogService.OpenAsync` or a simple in-page overlay): explains that SQLTriage
  will (a) query Active Directory to enumerate SQL servers, (b) connect read-only to each discovered server,
  (c) read server-to-server topology + currently-connected clients, (d) resolve client hostnames; that it
  does NOT store anything unless you choose to; and that it runs under your Windows credentials. Buttons:
  **Map my environment** / Cancel. No persistence of the choice.
- On accept: start `EnvironmentDiscoveryService`, subscribe to deltas, and **re-render the canvas
  incrementally** — call `environmentView.renderTopology` with the growing snapshot after each new server
  (the force layout re-settles → the "slow expand" effect). MVP keeps the existing JSON shape (servers +
  crossLinks) and adds an `edgeKind` to crossLinks so the renderer can colour replication vs AG vs linked.
- Progress: reuse `_isScanning`/`_scanProgress` pattern ("Discovered N servers, probing X…"), cancellable.
- **Post-discovery: "Add discovered servers to catalogue"** — a panel listing discovered servers not already
  in the catalogue, with select-all; on confirm, create one `ServerConnection` (or append ServerNames) per
  selection and call `ConnectionManager.AddConnection(...)`. Dedupe against existing `GetServerList()`.

## JS (`environmentView.js`)
- MVP: no new lib. Either (a) re-call `renderTopology` with the full snapshot each delta (simplest), or
  (b) add `environmentView.upsertTopology(canvasId, json)` that merges nodes/edges and nudges the layout
  (smoother "expand"). Start with (a); add (b) if the re-render flicker is poor.
- Add edge colouring by `edgeKind` (replication/AG/mirror/linked/client) + a small legend.

## Density & client grouping (UX — known problem)
The current canvas clumps badly at ~100 clients on one server and becomes unusable; Dig Deeper makes this
worse (whole estate). Fix is primarily a **data-model decision: never emit one graph node per client.**
- **Primary graph = SQL servers + server-to-server edges** (replication/AG/mirror/linked/log-shipping). This
  is the daisy chain the feature is for, and it stays sparse/readable even at estate scale (tens–low-hundreds
  of server nodes).
- **Clients are aggregated, not individual nodes.** Each server node carries a client **count badge**
  ("87 clients"). Clicking a server opens the **existing host-detail panel** (already a grouped connection
  table) — that is the client drill-down, with zero graph clumping.
- **Optional on-graph client view (drill-in only):** when the user explicitly expands a server, show client
  **cluster nodes grouped by application (or subnet)** — e.g. `WebApp ×42`, `10.2.3.x ×15` — capped at top-N
  with a `+M more`, expand/collapse per cluster. Never raw per-client fans.
- **Level-of-detail by zoom:** estate level → servers + server edges only; zoom/click into a server → its
  client clusters; click a cluster → the detail table. Collapse everything by default.
- This also fixes the **current `Scan` clumping** — apply the same aggregation to the existing flow.
- Renderer note: `environmentView.js` (custom canvas) is fine once clients are aggregated. If we later want
  rich on-graph expand/collapse of client clusters, **Cytoscape.js + `cytoscape-expand-collapse`** (compound
  nodes) is the cleaner path — defer to Phase 1/2; not needed for MVP.
- Caps: server node cap (e.g. 250, then cluster by AD site / subnet); per-server client clusters cap (top N
  apps + overflow); log when anything is truncated so the map never silently implies completeness.

## Credentials & safety
- MVP uses **integrated Windows auth** (the running user's Kerberos identity) for discovered servers, via the
  seed `ServerConnection`'s auth settings. Servers the user can't reach → shown as unreachable nodes. No
  credential prompting/storage in MVP.
- AD query uses the current user context; no credentials stored.
- Everything read-only; per-server timeout; global cancel; bounded crawl. Audit-log the run.

## Phasing
- **Phase 0 / MVP (this build):** Dig Deeper button + consent modal + AD-or-seed discovery + server-to-server
  topology crawl (linked/replication/AG/mirror/log-shipping) + live re-render on the existing canvas +
  add-to-catalogue. **Clients aggregated to a per-server count badge (no per-client nodes); drill-down via the
  existing detail panel.** Integrated auth. Bounded + cancellable + ephemeral + audited.
- **Phase 1:** fold in client/app discovery (reuse `EnvironmentQuery`) + reverse-DNS of client IPs; richer
  edge typing + legend; clustering by site/AG when node count is high.
- **Phase 2:** edge labels (replication direction, AG sync health, latency), opt-in snapshot export/save,
  alternate-credential entry (ephemeral) for cross-domain reach.

## Risks / limitations
- **Cannot compile/test on this box (no net8 SDK).** Code will be written for Adrian to build + run.
- AD enumeration needs a domain-joined context + read access; non-domain falls back to seed-only crawl.
- Access-denied/firewalled servers yield partial maps (by design — shown as unreachable nodes).
- Client/app discovery is point-in-time (currently-connected only); server-to-server topology is config-based
  and complete.
- `System.DirectoryServices` is Windows-only — fine (TFM is net8.0-windows).

## Security review hooks (for step 2)
Focus areas: AD query scope/filter correctness; guarantee no port scanning anywhere; read-only enforcement on
every probe; consent copy accuracy + no persisted consent; credential handling (no storage, no logging of
secrets); crawl bounds (no runaway/amplification); audit-log completeness; safe handling of hostile/garbage
results from untrusted servers (parameterize, bound sizes, treat names as data).

## File touchpoints
- NEW: `Data/Services/Discovery/AdServerLocator.cs`, `SqlTopologyProbe.cs`, `EnvironmentDiscoveryService.cs`,
  `TopologyModels.cs`.
- EDIT: `Data/ServiceCollectionExtensions.cs` (register the 3 services),
  `Pages/EnvironmentView.razor` (button + modal + wiring + add-to-catalogue),
  `wwwroot/scripts/environmentView.js` (edge kinds / optional upsert), `SQLTriage.csproj` (add
  `System.DirectoryServices` reference if not implicit).
</content>
