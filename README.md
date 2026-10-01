# FlexForms Prism

Prism projects FlexForms applications into a queryable SQL Server database (schema `prism`) for the data team.

The FlexForms API publishes an `ApplicationProjectionRequestedEvent` through its transactional outbox whenever an
application is saved, submitted or deleted. The event goes to the Service Bus topic `flexforms-prism`, sessioned by
`{tenantId}:{applicationId}`. Prism receives it, reads the authoritative state from the API's internal Prism endpoints
(`v1/internal/prism`, app role `Prism.Read`), flattens the response into typed answer facts and writes a new
generation with a revision-aware compare-and-swap.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/GovUK.Dfe.FlexForms.Prism.Flattener` | Pure flattening: template catalogue, response parsing, typed facts, hashing |
| `src/GovUK.Dfe.FlexForms.Prism.Data` | `PrismDbContext`, migrations and the projection writer |
| `src/GovUK.Dfe.FlexForms.Prism.Source` | Reads source state through `GovUK.Dfe.FlexForms.Api.Client` |
| `src/GovUK.Dfe.FlexForms.Prism.Projector` | Projection orchestration: revision gate, flatten, write |
| `src/GovUK.Dfe.FlexForms.Prism.Functions` | Azure Functions host (isolated worker) |
| `tests/` | Unit, Testcontainers SQL Server and scenario tests |

## Prerequisites

- .NET SDK 10.0.401 or later (see `global.json`)
- Docker, for the Data and Scenario tests (Testcontainers starts SQL Server)
- Azure Functions Core Tools v4, to run the Functions host locally

### Sibling repositories (temporary)

Until the Prism contract and endpoints are published as packages, Prism references these projects directly.
Clone them next to this repository:

```text
repos/
  flexforms-prism/      this repository
  flexforms-api/        branch feature/prism (GovUK.Dfe.FlexForms.Api.Client)
  DfE.CoreLibs/         rsd-core-libs, branch feature/prism (Contracts, Messaging.Contracts)
```

CI checks out the same layout; the branches are set in `.github/workflows/ci.yml`.

## Build and test

```bash
dotnet build GovUK.Dfe.FlexForms.Prism.slnx
dotnet test GovUK.Dfe.FlexForms.Prism.slnx
```

Package versions are managed centrally in `Directory.Packages.props`. Warnings are treated as errors.

## Flattening

`GovUK.Dfe.FlexForms.Prism.Flattener` turns a template version and a response body into typed answer facts.
It follows the web front end's `FormTemplate` model and the response format written by `TransformToResponseJson`:

- Top-level fields become facts directly. Multi-collection items sit under `{collectionFieldId}/{itemId}`, and
  derived collection items are read from `{fieldId}_status_{itemId}` and `{fieldId}_data_{itemId}`.
- Stored values are HTML-decoded. Dates, numbers and booleans go to typed columns, and checkbox codes,
  autocomplete properties and upload files each get their own `nested_path`.
- Only fields with an explicit Allowed decision in the export policy produce facts; a nested field also needs
  its collection to be allowed.
- Generations are compared by a canonical hash of the facts, so property order in the source JSON never matters.

The Transfer template and response in `tests/GovUK.Dfe.FlexForms.Prism.Flattener.Tests/Fixtures/transfer` have
golden outputs. If flattening output changes on purpose, regenerate them with `UPDATE_GOLDEN=1 dotnet test`,
review the diff, and bump `PrismVersions.ProjectorVersion`.

## Database

The Prism database lives in schema `prism` and is owned by EF Core migrations in
`src/GovUK.Dfe.FlexForms.Prism.Data/Migrations`. Consumers read only the views:

- `prism.v_current_answer_facts`: facts of each application's active generation, excluding deleted applications.
- `prism.v_submission_answer_facts`: facts of each submission's selected generation, excluding deleted applications.

Generations that are still being built, or have been superseded, are never visible through the views.

```bash
dotnet tool restore

# Add a migration
dotnet ef migrations add <Name> --project src/GovUK.Dfe.FlexForms.Prism.Data --startup-project src/GovUK.Dfe.FlexForms.Prism.Data

# Apply to a local database
PRISM_DB_CONNECTION="Server=localhost,1433;Database=prism;User Id=sa;Password=...;TrustServerCertificate=true" \
  dotnet ef database update --project src/GovUK.Dfe.FlexForms.Prism.Data --startup-project src/GovUK.Dfe.FlexForms.Prism.Data
```

Deployments apply migrations with a self-contained bundle (`prism-migrations` CI artifact, or
`Dockerfile.migrations`) through `script/migrate-prism-db.sh`, which reads `PRISM_DB_CONNECTION`.

## Run the Functions host locally

```bash
cd src/GovUK.Dfe.FlexForms.Prism.Functions
cp local.settings.example.json local.settings.json
func start
```

`local.settings.json` is git-ignored.
