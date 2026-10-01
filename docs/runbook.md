# Prism runbook

## What runs where

| Function | Trigger | Job |
|---|---|---|
| `ProjectionFunction` | Service Bus `flexforms-prism` / `prism-projector`, sessions | Projects one application per message. |
| `OperationWorkerFunction` | Every minute | Pages through FlexForms for the oldest pending backfill or reconciliation operation, and enqueues `Resync` messages. |
| `ReconciliationFunction` | 02:00 UTC daily | Records a reconciliation operation for all tenants (the worker runs it). |
| `GenerationCleanupFunction` | 03:30 UTC daily | Deletes superseded generations older than `Prism:Cleanup:RetentionDays` (30 by default). |
| `CreateBackfill`, `CreateReconciliation`, `GetBackfill`, `CancelBackfill` | HTTP, `Prism.Admin` | Control plane, see below. |
| `GetExportPolicy`, `ChangeExportPolicy` | HTTP, `Prism.Admin` | See [export-policy.md](export-policy.md). |

Backfill and reconciliation never write projections themselves. They only enqueue messages, so all writes go
through the same ordered, retried and dead-lettered path as live events.

## Telemetry

Logs go to Application Insights through the Functions integration. Projection logs carry `tenant_id`,
`application_id`, `projection_reason`, `source_revision`, `response_id`, `submission_id`, `operation_id`,
`projector_version`, `contract_version`, `message_id`, `session_id` and `delivery_count` in `customDimensions`.
Admin actions are logged as `Prism admin audit: ...`.

Metrics are exported with OpenTelemetry when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set, and land in `customMetrics`:

| Metric | Dimensions | Watch for |
|---|---|---|
| `prism.projection.lag` (s) | `reason` | Freshness. Alert on p95 above the agreed SLO. |
| `prism.projection.succeeded` | `reason`, `status` | Throughput. |
| `prism.projection.failed` | `reason`, `failure`, `permanent` | Any `permanent = true` means a dead letter. |
| `prism.projection.retried` | `reason` | Rising retries mean SQL or FlexForms trouble. |
| `prism.projection.skipped` | `skip_reason` | Mostly `already_projected`; that's normal. |
| `prism.projection.cas_conflicts` | | Should be rare outside backfills. |
| `prism.projection.hash_reuses` | | Saves that didn't change any exported answer. |
| `prism.generations.created` / `.superseded` | `kind` | |
| `prism.source.duration` (ms) | `operation` | FlexForms API latency. |
| `prism.flatten.duration`, `prism.write.duration` (ms) | `operation` on write | Cost of large applications. |
| `prism.operations.enqueued` | `kind` | |
| `prism.operations.finished` | `kind`, `status` | Alert on `Failed`. |
| `prism.reconciliation.drift` | `drift_kind` | See "Reconciliation found drift". |
| `prism.cleanup.generations_deleted`, `prism.cleanup.facts_deleted` | | |

```kusto
// Projection lag (p95 of per-minute averages) and failures over the last day
customMetrics
| where timestamp > ago(1d) and name == "prism.projection.lag"
| summarize lag_s = sum(valueSum) / sum(valueCount) by bin(timestamp, 1m)
| summarize p95 = percentile(lag_s, 95)

customMetrics
| where timestamp > ago(1d) and name == "prism.projection.failed"
| extend failure = tostring(customDimensions.failure), permanent = tostring(customDimensions.permanent)
| summarize count = sum(valueSum) by failure, permanent
```

Suggested alerts:
- Dead-letter count on `prism-projector` above 0.
- Active message count on `prism-projector` growing for 15 minutes.
- p95 lag above the SLO.
- `prism.operations.finished` with `status = Failed`.
- No `ReconciliationFunction` run in 26 hours.

## Common tasks

All admin calls need an Entra token for the Prism app registration with the `Prism.Admin` role:

```bash
TOKEN=$(az account get-access-token --scope "api://<prism-client-id>/.default" --query accessToken -o tsv)
PRISM=https://<function-app>.azurewebsites.net/api
```

### Backfill

```bash
# One tenant, optionally only applications modified since a date
curl -X POST "$PRISM/admin/backfill" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{ "tenantId": "<tenant-id>", "modifiedSince": "2026-01-01T00:00:00Z" }'

# Every tenant
curl -X POST "$PRISM/admin/backfill" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{}'

# Progress, then cancel if needed
curl "$PRISM/admin/backfill/<operation-id>" -H "Authorization: Bearer $TOKEN"
curl -X POST "$PRISM/admin/backfill/<operation-id>/cancel" -H "Authorization: Bearer $TOKEN"
```

A backfill re-sends every application in scope. Applications that are already up to date are skipped cheaply.
Progress (`pagesProcessed`, `applicationsScanned`, `messagesEnqueued`) is saved after each page, so a backfill
picks up where it left off after a restart. Only one operation runs at a time; the others wait as `Pending`.

### Reconciliation on demand

```bash
curl -X POST "$PRISM/admin/reconciliation" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{}'
```

Reconciliation compares every FlexForms application with Prism and only enqueues the ones that drifted.

## Incidents

### Messages are dead-lettered

Find the reason in the message's `DeadLetterReason` (or the `PrismFailure` property) and in the
`Projection failed permanently` log.

| Reason | Likely cause | Action |
|---|---|---|
| `unsupported_contract` | FlexForms published a newer `ContractVersion` than this Prism understands. | Deploy a Prism that supports it. |
| `invalid_message` | A malformed message or missing required fields. | Fix the publisher. |
| `source_not_found` | The application, response or template version doesn't exist in that tenant. | Check the tenant ID. Usually safe to discard. |
| `source_rejected` | FlexForms returned 400, 401 or 403. | Check Prism's `Prism.Read` role assignment and the API client settings. |
| `source_mismatch` | The response belongs to a different application. | Investigate in FlexForms; this should never happen. |
| `unreadable_template`, `unreadable_response` | JSON that the flattener can't read. | Fix the flattener, bump `ProjectorVersion` if the output changes, and deploy. |
| `MaxDeliveryCountExceeded` | A transient error that kept recurring. | Look at the `Projection failed and will be retried` logs. |

**Don't replay dead letters.** Messages are only notifications. Fix the cause, run a backfill (or a
reconciliation) for the affected tenant so the current state is re-read, and then purge the dead-letter queue.

### Projection is falling behind

1. Check the active message count and `prism.projection.retried`.
2. `prism.source.duration` high: FlexForms is slow. Prism's calls are retried with backoff.
3. `prism.write.duration` high: the Prism database is under pressure. Check DTU or vCore usage. Large backfills
   compete with live traffic; cancel the backfill and rerun it out of hours if needed.
4. Throughput scales with sessions (`maxConcurrentSessions` in `host.json`) and Function instances. One
   application is always processed one message at a time.

### Reconciliation found drift

`prism.reconciliation.drift` by `drift_kind`:
- `missing`: FlexForms has the application but Prism doesn't. This is expected for applications with no saved response yet.
- `behind`: Prism has an older revision, so a live event was lost or is still queued.
- `deletion_missing`: FlexForms deleted the application but Prism didn't record it.
- `outdated`: projected by an older projector or contract version, normal straight after an upgrade.

Reconciliation queues the repairs itself. Persistent `behind` or `deletion_missing` counts mean events are
being lost. Check the API outbox (`ea.OutboxMessage` rows not delivered) and the topic's metrics.

### A field must be removed from the views immediately

1. Deny the field through the export policy endpoint (this starts a backfill).
2. If it can't wait for the backfill, delete its facts directly:

```sql
DELETE f
FROM prism.answer_facts f
JOIN prism.projection_generations g ON g.generation_id = f.generation_id
WHERE g.tenant_id = @tenant AND g.template_id = @template
  AND f.parent_field_id = @parent_field_id AND f.field_id = @field_id;
```

This is safe: the policy version has already risen, so every application is re-projected without the field
rather than reusing the trimmed generation.

### An application must disappear (erasure request)

Delete it in FlexForms. The Deleted event, or the next reconciliation, records a tombstone and the views hide it
straight away. Superseded and tombstoned facts stay in the database until cleanup. For an erasure, delete the
application's generations by hand:

```sql
DELETE FROM prism.submission_snapshots WHERE tenant_id = @tenant AND application_id = @app;
UPDATE prism.application_projection_state SET active_generation_id = NULL WHERE tenant_id = @tenant AND application_id = @app;
DELETE f FROM prism.answer_facts f JOIN prism.projection_generations g ON g.generation_id = f.generation_id
WHERE g.tenant_id = @tenant AND g.application_id = @app;
DELETE FROM prism.projection_generations WHERE tenant_id = @tenant AND application_id = @app;
```

Keep the state row and the tombstone: they stop the application from coming back.

## Changes

### Database migrations

CI publishes a self-contained migration bundle (`prism-migrations` artifact). Run it before deploying a new
Function version:

```bash
PRISM_DB_CONNECTION="Server=tcp:<server>.database.windows.net;Database=<db>;Authentication=Active Directory Default;Encrypt=True" \
  ./migrate-prism-db.sh
```

Migrations are additive where possible. A migration that changes a view must recreate it in full.

### Projector upgrade

When flattening output changes for the same input:

1. Bump `PrismVersions.ProjectorVersion`, and regenerate the golden files.
2. Deploy.
3. Nightly reconciliation finds every application as `outdated` and re-projects it at its current revision. To
   do it straight away, run a backfill.

Old generations stay visible until each application is re-projected, so readers never see a gap.

### Export policy changes

See [export-policy.md](export-policy.md). A change starts a tenant backfill automatically.

## Rollout

1. Merge the FlexForms API changes (revisions, outbox publishing, internal endpoints) and CoreLibs. Make sure
   `MassTransit:Outbox:Enabled` is `true` in every environment.
2. Create the Azure resources in [azure-setup.md](azure-setup.md). The topic must exist, with duplicate detection,
   **before** the API publishes.
3. Deploy the API. Events accumulate in `prism-projector` until Prism runs, within the message time-to-live.
4. Run the migration bundle against the Prism database, then deploy the Function to dev.
5. Run a backfill for every tenant, and wait for it to finish and the subscription to drain.
6. Run a reconciliation. Expect no drift apart from `missing` for applications without responses.
7. Classify the export policy with the data owner. Check that the policy queries in export-policy.md return
   nothing unexpected.
8. Validate the views with data engineering against FlexForms for a sample of applications.
9. Run a load check with the largest real applications, and watch lag and write duration.
10. Repeat in staging, then in production.
