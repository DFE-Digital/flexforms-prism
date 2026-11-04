# Development workflows (Prism)

Local: `local.settings.example.json`, SQL Server, Azurite/Service Bus emulator as documented in README.

Tests: Flattener (golden TSV), Data (Testcontainers SQL), Projector, Functions, Scenario (`FakeSource`). Docker required for Data/Scenario in CI.

Update golden fixtures: `UPDATE_GOLDEN=1 dotnet test` then bump `ProjectorVersion`.

Migrations: `script/migrate-prism-db.sh`, `Dockerfile.migrations`, workflow `migrate.yml` on `main`.

Warnings as errors (`Directory.Build.props`).
