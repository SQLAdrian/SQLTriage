<!-- In the name of God, the Merciful, the Compassionate -->

# Parser

Parsers for opaque binary/text artefacts that need structured access.

| File | Purpose |
|------|---------|
| BundleReader | Reads the encrypted entitlement bundle format (free + per-license corpus packs). Pairs with `Data/Services/Licensing/BundleManifest` and `CorpusEncryptor`. See memory `project_entitlement_bundle_design_2026-05-19`. |

ShowPlan XML parsing lives at `Data/Services/ExecutionPlanParser.cs` (kept with services because it depends on `Microsoft.SqlServer.Management.SqlParser` and feeds the QueryPlan UI directly).
