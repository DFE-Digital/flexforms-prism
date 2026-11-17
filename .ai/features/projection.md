# Feature: Projection and CAS

Entry: `ProjectionFunction.cs` → `ProjectionService.cs` → `ProjectionWriter.cs`.

Reasons: Saved, Submitted, Deleted, Resync (backfill only). Hash reuse vs new generation. Submit writes a submission generation **and** current. Delete writes tombstone.

Tests: Projector tests + Scenario tests (ordering, delete overtaking, CAS conflict).

Do not project inside `OperationProcessor` — it only enqueues Resync messages.

## Template versions (**Verified**)

The topic also carries `TemplateVersionPublishedEvent`, published by the API when a template or template version is created. Messages are told apart by the envelope `messageType` URN (`ProjectionMessages.IsTemplateVersion`). `CatalogueTemplateVersionAsync` re-reads `template-versions/{id}`, checks it belongs to the named template (`source_mismatch` otherwise), and writes the field catalogue so fields can be classified before any application uses the version. Template messages use session `{tenantId}:template:{templateId}` (CoreLibs `ApplicationProjectionIdentifiers.TemplateSessionId`).

## Settlement (**Verified**, `ProjectionFunction`)

Excluded tenant → complete unread. Success or skip → complete. `PermanentProjectionException` → dead-letter with application property `PrismFailure` = reason (`unsupported_contract`, `invalid_message`, `source_not_found`, `source_rejected`, `source_mismatch`, `unreadable_template`, `unreadable_response`). Anything else → wait `Prism:Projection:RetryDelay` doubled per delivery (capped at `MaxRetryDelay`) while holding the session, then abandon and rethrow, so Service Bus redelivers until `MaxDeliveryCount`.

## Writer (**Verified**, `ProjectionWriter`)

One transaction per write. The state or snapshot row is locked `UPDLOCK, HOLDLOCK` before anything is written, which also serialises the first insert for an application, so the version-aware compare-and-swap is decided before facts are bulk-copied. SQL deadlock victims are re-run from the start.

Code map for the whole repo: [`../code-map.md`](../code-map.md).
