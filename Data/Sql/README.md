<!-- In the name of God, the Merciful, the Compassionate -->

# SQL — embedded check scripts

T-SQL scripts shipped as `<EmbeddedResource>` and loaded at runtime. **This is a SAMPLE / scaffold tree.** The production check corpus ships via the encrypted entitlement bundle (see `Data/Parser/BundleReader` and `Data/Services/Licensing/`), not from this folder.

| Subfolder | Sample script |
|-----------|---------------|
| HealthChecks/ | BackupValidation.sql, MemoryUsage.sql |
| PerformanceChecks/ | CpuUtilization.sql |
| SecurityChecks/ | LoginAudit.sql |

The folder remains because the existing build embeds these as a fallback for environments without a bundle. Per `CLAUDE.md`: do not edit `.sql` files via the LLM — Adrian owns the SQL.
