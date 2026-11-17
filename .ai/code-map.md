# Code map (flexforms-prism)

**Verified** against source on 2026-11-17. Platform-wide context: [`ecosystem.md`](ecosystem.md). Contract: [`../docs/prism-contract-v1.md`](../docs/prism-contract-v1.md).

## Projects (`src/`, solution `GovUK.Dfe.FlexForms.Prism.slnx`, central versions in `Directory.Packages.props`, warnings as errors)

| Project | Role | Key types |
|---------|------|-----------|
| `Prism.Functions` | Function App host (isolated, Functions v4) | `Program.cs`, `PrismFunctionsServiceCollectionExtensions` (`AddPrismFunctions`), `PrismFunctionsOptions` (section `Prism`), `PrismTopology` (topic `flexforms-prism`, subscription `prism-projector`, connection `ServiceBus`), `Functions/ProjectionFunction`, `Functions/AdminApi`, `Functions/ExportPolicyApi`, timers, `Messaging/ProjectionMessages` (MassTransit envelope read/write), `ControlPlane/` (`OperationService`, `OperationProcessor`, `ExportPolicyService`, `AdminAuthenticator`) |
| `Prism.Projector` | Orchestration, no HTTP/SQL details | `ProjectionService` (`ProjectAsync`, `CatalogueTemplateVersionAsync`), `CatalogueCache`, `PermanentProjectionException` + `PermanentFailureReasons`, `ProjectionOutcome`, `PrismMetrics` (OpenTelemetry meter) |
| `Prism.Source` | Reads FlexForms API | `SourceClient` (`ISourceClient`) over Api.Client `IInternalPrismClient`, `TenantScope` (AsyncLocal tenant → `X-Tenant-ID` via `TenantScopedApiClientSettingsProvider`), Polly retries, `SourceExceptions` |
| `Prism.Flattener` | Pure transform, no I/O | `Catalogue/TemplateCatalogueBuilder`, `TemplateModel`, `Responses/ResponseParser`, `Flattening/ResponseFlattener`, `ValueInterpreter`, `Facts/FactHasher`, `Text/CanonicalJson`, `PathSegment`, `Policy/ExportPolicy`, `PrismVersions` |
| `Prism.Data` | EF Core, schema `prism` | `PrismDbContext`, `Writing/ProjectionWriter` (CAS, bulk copy, deadlock retry), `Reading/ProjectionStore`, `Catalog/FieldCatalogWriter`, `Maintenance/GenerationCleaner`, `Migrations/` (incl. `PrismViews`) |

Tests (`tests/`): Flattener (golden TSV fixtures under `Fixtures/transfer/`), Data (Testcontainers SQL: writer, views, cleaner, catalogue), Projector (`ProjectorHarness`, `SourceClientTests`), Functions (envelope, admin auth, export policy API, operation processor, DI), Scenario (`ScenarioHarness` + `FakeSource`: ordering, failures, template changes, versioning).

## Functions

| Function | Trigger | Does |
|----------|---------|------|
| `ProjectionFunction` | Service Bus `flexforms-prism` / `prism-projector`, sessions | Reads envelope; excluded tenant → complete; `ApplicationProjectionRequestedEvent` → `ProjectAsync`; `TemplateVersionPublishedEvent` → `CatalogueTemplateVersionAsync`; complete on success/skip; `PermanentProjectionException` → dead-letter with property `PrismFailure`; other errors → back off (`Prism:Projection:RetryDelay` doubling to `MaxRetryDelay`) then abandon |
| `OperationWorkerFunction` | timer every minute | `OperationProcessor` pages the API (`ListPrismApplications`) for pending backfill/reconciliation operations and enqueues `Resync` messages (rate-limited, `Prism:Backfill:*`). Never projects |
| `ReconciliationFunction` | timer 02:00 UTC | creates a reconciliation operation |
| `GenerationCleanupFunction` | timer 03:30 UTC | deletes superseded generations older than `Prism:Cleanup:RetentionDays` (30) |
| `CreateBackfill`, `CreateReconciliation`, `GetBackfill`, `CancelBackfill` | HTTP `control/backfill*`, `control/reconciliation` | control plane (`AdminAuthenticator`) |
| `Get/ChangeExportPolicy`, `Get/ChangeTenantExportDefault`, `Get/ChangeTemplateExportDefault` | HTTP `control/tenants/{tenantId}/…export-policy|export-default` | export policy; changes start a backfill. Called by the API's `PrismReportingExportPolicyService` |

Every HTTP function authenticates through `ControlPlane/AdminAuthenticator` (Entra bearer with `Prism.Admin`, optional `Prism.Delegate` + `X-Prism-Acting-User`; a development key exists for local use only). New HTTP functions must do the same.

## Projection logic (`ProjectionService`)

- `Saved` / `Resync` → `ProjectCurrentAsync`: read `GET applications/{id}/current`, compare `SourceRevision` and versions with stored state, fetch response + template version, flatten under export policy, write a Current generation (reuse when the fact hash matches).
- `Submitted` → freeze the exact submitted response (`responses/{responseId}`) as a submission snapshot (`SubmissionId` deterministic), then update Current if still latest.
- `Deleted` → `RecordDeletionAsync` tombstone; later messages for that application are skipped.
- `TemplateVersionPublishedEvent` → build and store the field catalogue (with `semanticKey`, `retiredFields`) before any application uses the version.
- Permanent failure reasons: `unsupported_contract`, `invalid_message`, `source_not_found`, `source_rejected`, `source_mismatch`, `unreadable_template`, `unreadable_response`.
- Versions: `PrismVersions.ProjectorVersion = 4` (2: application details + semantic keys/retirements; 3: lead applicant user id; 4: lead applicant name/email), `ContractVersion = 1`. A bump makes already-projected revisions re-project.

## Tables (schema `prism`)

`projection_generations`, `application_projection_state`, `submission_snapshots`, `answer_facts`, `field_catalog`, `template_versions`, `tenants`, `template_field_retirements`, `field_export_policy`, `export_defaults`, `deletion_tombstones`, `backfill_operations`, `schema_info`. Views for consumers: `v_applications`, `v_current_answer_facts`, `v_submission_answer_facts`, `v_template_field_changes`.

## Configuration

`Prism` section (`PrismFunctionsOptions`): `TopicName`, `ServiceBusTransport` (AMQP WebSockets), `ExcludedTenantIds`, `Backfill` (`PageSize` 200, `TimeBudget` 4 min, `MaxMessagesPerMinute` 240), `Projection` (`RetryDelay` 5 s, `MaxRetryDelay` 2 min), `Cleanup` (`RetentionDays` 30, `MaxGenerationsPerRun` 2000), `Admin` (`Authority`, `Audience`, `ValidIssuers`, `RequiredRole` `Prism.Admin`, `DelegateRole` `Prism.Delegate`, `DevelopmentKey`). Source: `ExternalApplicationsApiClient__BaseUrl/Authority/ClientId/Scope` (scope is the platform API app `/.default`). Connection strings: `ServiceBus`, Prism SQL. `host.json`: `autoCompleteMessages: false`, `maxConcurrentSessions: 4`, function timeout 10 min, AMQP WebSockets. See `local.settings.example.json` and `docs/azure-setup.md`.
