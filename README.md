# FlexForms Prism

Prism projects FlexForms applications into a queryable SQL Server database (schema `prism`) for the data team.

The FlexForms API publishes an `ApplicationProjectionRequestedEvent` through its transactional outbox whenever an
application is saved, submitted or deleted. The event goes to the Service Bus topic `flexforms-prism`, sessioned by
`{tenantId}:{applicationId}`. Prism receives it, reads the authoritative state from the API's internal Prism endpoints
(`v1/internal/prism`, app role `Prism.Read`), flattens the response into typed answer facts and writes a new
generation with a revision-aware compare-and-swap.

## Documentation

| Document | For |
| --- | --- |
| [docs/prism-contract-v1.md](docs/prism-contract-v1.md) | The event, the internal API and the SQL views consumers read |
| [docs/export-policy.md](docs/export-policy.md) | Classifying which fields may be exported |
| [docs/runbook.md](docs/runbook.md) | Operating Prism: telemetry, backfills, incidents, upgrades and rollout |
| [docs/azure-setup.md](docs/azure-setup.md) | The Azure resources, roles and app settings Prism needs |

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

`GovUK.Dfe.FlexForms.Prism.Scenario.Tests` runs the real projector, writer and control plane against SQL Server,
with an in-memory FlexForms (`FakeSource`) that returns the event the API would publish for each save, submit or
delete. The test decides when, how often and in what order those events are delivered. The scenarios cover
duplicates, reordering, a crash between bulk copy and commit (simulated with a trigger), concurrent writers,
deletes that overtake or are lost, projector and export policy upgrades, cleanup and a large application.

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

## Projection

`ProjectionService` treats each message as a notification. It re-reads the source through the internal
FlexForms API and compares the source revision with what is already stored, so duplicate, reordered or replayed
messages all end in the same state.

- A stored projection is replaced by a higher source revision or, at the same revision, by higher
  (projector, contract, export policy) versions. A change to the export policy therefore re-projects
  applications the next time they are touched or resynced.
- When the flattened facts hash to the same value as the active generation, only the state metadata advances.
- Submitted freezes the exact submitted response as a submission snapshot. Resync recreates a missing one.
- Deleted writes a tombstone, and nothing is written for that application after it.
- Failures that retrying cannot fix throw `PermanentProjectionException` (and should be dead-lettered). Any other
  exception is transient.

`Prism.Source` calls the API with client credentials. It sets `X-Tenant-ID` on each call, retries transient
failures with Polly, and caches template versions in memory. It reads the `ExternalApplicationsApiClient`
configuration section.

Metrics are published on the `GovUK.Dfe.FlexForms.Prism` meter. The Functions host exports them to Application
Insights with OpenTelemetry when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set; the runbook lists them.

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

## Functions

| Function | Trigger | What it does |
| --- | --- | --- |
| `ProjectionFunction` | Topic `flexforms-prism`, session subscription `prism-projector` | Projects one application per message |
| `OperationWorkerFunction` | Every minute | Advances pending and running backfill and reconciliation operations |
| `ReconciliationFunction` | 02:00 UTC daily | Records a reconciliation operation for every tenant |
| `GenerationCleanupFunction` | 03:30 UTC daily | Deletes unreferenced superseded generations past retention |
| `CreateBackfill`, `CreateReconciliation`, `GetBackfill`, `CancelBackfill` | HTTP, `/api/admin/...` | Control plane |
| `GetExportPolicy`, `ChangeExportPolicy` | HTTP, `/api/admin/tenants/{tenantId}/templates/{templateId}/export-policy` | Field classification; a change starts a tenant backfill |

The projection function completes a message on success or skip. It dead-letters permanent failures straight
away, with the failure reason as the dead-letter reason. Transient failures are abandoned, so Service Bus
retries them until the subscription's `MaxDeliveryCount`.

### Control plane

Backfill and reconciliation never project anything themselves. They record an operation in
`prism.backfill_operations`, and the operation worker pages through the internal FlexForms list endpoint,
enqueuing `Resync` messages on the same topic. Those messages go through the normal projector, with session
ordering, retries and dead-lettering. Progress is saved after every page, so the worker can stop at its time
budget and resume on the next tick. A backfill enqueues every application. A reconciliation enqueues only
applications that are missing, behind the source revision, deleted at source without a tombstone, or projected
by an older projector or contract version.

Every admin call needs an Entra ID token for the Prism app registration with the `Prism.Admin` app role
(`Prism:Admin:Authority`, `Prism:Admin:Audience`). Without that configuration all admin calls are rejected.
Calls are audit-logged with the caller, who is also stored as `requested_by` or `cancelled_by`.

```http
POST /api/admin/backfill            { "tenantId": "<optional>", "modifiedSince": "<optional>" }
POST /api/admin/reconciliation      { "tenantId": "<optional>" }
GET  /api/admin/backfill/{operationId}
POST /api/admin/backfill/{operationId}/cancel
GET  /api/admin/tenants/{tenantId}/templates/{templateId}/export-policy
PUT  /api/admin/tenants/{tenantId}/templates/{templateId}/export-policy   { "decisions": [ ... ] }
```

### Configuration

- `ConnectionStrings:Prism`: the Prism database. Use `Authentication=Active Directory Default` for managed identity.
- `ServiceBus`: a connection string, or `ServiceBus:fullyQualifiedNamespace` for managed identity. Add
  `ServiceBus:clientId` for a user-assigned identity. The trigger and the control plane's sender share it,
  and the sender needs Send on the topic.
- `ExternalApplicationsApiClient`: base URL and client credentials for the internal FlexForms endpoints.
- `Prism:ServiceBusTransport` (`AmqpWebSockets` by default), `Prism:Backfill:PageSize`,
  `Prism:Backfill:TimeBudget`, `Prism:Cleanup:RetentionDays`, `Prism:Cleanup:MaxGenerationsPerRun`.
- `APPLICATIONINSIGHTS_CONNECTION_STRING`: logs and, through OpenTelemetry, metrics.

[docs/azure-setup.md](docs/azure-setup.md) has the full list of app settings for a deployed environment.

### Run locally

```bash
cd src/GovUK.Dfe.FlexForms.Prism.Functions
cp local.settings.example.json local.settings.json
func start
```

`local.settings.json` is git-ignored.
