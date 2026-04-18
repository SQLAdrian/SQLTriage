<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Contributing to SQL Health Assessment

Thank you for your interest in contributing! This project is a free tool for the SQL Server DBA community, and all forms of contribution are valued.

---

## Ways to Contribute

- **Bug reports** — found something broken? Open an issue.
- **Feature requests** — have an idea? Describe the use case in an issue.
- **Documentation** — fix typos, improve wording, add examples.
- **Dashboard panels** — new SQL queries or panel types in `dashboard-config.json`.
- **SQL diagnostic scripts** — useful T-SQL checks for the `scripts/` folder.
- **Code** — bug fixes, performance improvements, new features.
- **Screenshots** — help fill the Screenshots section of the README.

---

## Reporting Bugs

Before opening an issue, please:

1. Search [existing issues](https://github.com/SQLAdrian/SqlHealthAssessment/issues) to avoid duplicates.
2. Reproduce against the latest release if possible.
3. Include:
   - SQL Health Assessment version (shown in the About page)
   - SQL Server version(s) being monitored
   - Windows version
   - Steps to reproduce
   - Expected vs actual behaviour
   - Relevant log lines from `logs/app-*.log`

---

## Setting Up for Development

### Prerequisites

- [Visual Studio 2022](https://visualstudio.microsoft.com/) (Community edition works fine) with:
  - **.NET desktop development** workload
  - **ASP.NET and web development** workload
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2016+ instance (for testing)
- SQLWATCH deployed on the test instance (see [Deployment Guide](DEPLOYMENT_GUIDE.md))

### Build

```bash
git clone https://github.com/SQLAdrian/SqlHealthAssessment.git
cd SqlHealthAssessment
dotnet build SqlHealthAssessment.sln
```

Or open `SqlHealthAssessment.sln` in Visual Studio and press `F5`.

### Configuration for Local Development

Copy or edit `appsettings.json` to point at your local SQL Server:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=.;Database=SQLWATCH;Integrated Security=true;Application Name=SqlHealthAssessment;"
  }
}
```

### Running Tests

```bash
cd Tests
dotnet test
```

See [Tests/TESTING_GUIDE.md](Tests/TESTING_GUIDE.md) for the full testing guide.

---

## Making Changes

1. **Fork** the repository and create a branch from `master`.
   ```bash
   git checkout -b feature/my-new-panel
   ```

2. **Make your changes.** Keep commits focused and descriptive.

3. **Test your changes:**
   - Run the application against a real SQL Server instance.
   - Run `dotnet test` in the `Tests/` folder.
   - Check for regressions in existing dashboards.

4. **Submit a Pull Request** against `master`.
   - Describe what you changed and why.
   - Reference any related issues (`Fixes #123`).
   - Include before/after screenshots for UI changes.

---

## Code Style

This is a C# / Blazor project. General guidelines:

- Follow existing patterns in the codebase — consistency matters.
- Use `async`/`await` for any I/O operation; never `.Result` or `.Wait()`.
- Inject dependencies via constructor injection; avoid static state.
- Use `SqlParameter` for all user-supplied SQL values — no string interpolation.
- Log at `Debug` for diagnostic detail, `Information` for lifecycle events, `Warning` and `Error` for actionable conditions.
- Keep Razor components focused: data-fetching logic belongs in the `Data/` service layer, not in `.razor` files.

---

## Adding a Dashboard Panel

Panels are defined in `dashboard-config.json`. Each panel needs:

```jsonc
{
  "id": "my_new_panel",
  "title": "My New Panel",
  "type": "DataGrid",          // StatCard | BarGauge | TimeSeries | DataGrid
  "refreshGroup": "standard",
  "queryId": "my_query_id",
  "layout": { "row": 1, "col": 1, "width": 6, "height": 4 }
}
```

And a matching query entry:

```jsonc
{
  "id": "my_query_id",
  "sqlServer": "SELECT TOP 100 ... FROM SQLWATCH...",
  "sqlite": null,                   // optional: SQLite fallback for cached data
  "parameters": []
}
```

Edit `dashboard-config.json` directly, or use the in-app **Dashboard Editor** (`Ctrl+E`) and submit a pull request with the resulting JSON.

---

## Adding a SQL Diagnostic Script

Place `.sql` files in the `scripts/` folder. Scripts should:

- Be idempotent and read-only (SELECT only; no DDL/DML unless clearly flagged).
- Include a header comment describing purpose, author, and source.
- Target SQL Server 2016+.

---

## Commit Messages

Use imperative mood and be specific:

```
Add execution plan caching for top-queries panel
Fix session bubble view rendering on dark themes
Update SQLWATCH deployment to version 4.x dacpac
```

---

## License

By contributing, you agree that your contributions will be licensed under the [GNU General Public License v3.0](LICENSE.txt).

