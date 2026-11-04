# Integrations (Prism)

Inbound: Service Bus MassTransit envelope (`ProjectionMessages.cs`). Outbound HTTP: `SourceClient` → API. Admin HTTP: backfill, reconciliation, export policy. Metrics: `PrismMetrics` OpenTelemetry.

Nightly: reconciliation ~02:00 UTC, generation cleanup ~03:30. Operation worker every minute pages the API and enqueues `Resync` — it does **not** project itself.

Email, files, ClamAV: none.
