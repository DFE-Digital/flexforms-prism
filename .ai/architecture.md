# Architecture (Prism)

**Verified** from README and `src/`.

Runtime: **.NET 10**, Azure Functions v4 isolated worker.

| Project | Role |
|---------|------|
| Functions | Triggers: `ProjectionFunction`, timers, admin HTTP (`Program.cs`, `AddPrismFunctions`) |
| Projector | `ProjectionService` — revision gate, flatten, catalogue, failures |
| Source | `SourceClient` — `IInternalPrismClient`, `X-Tenant-ID`, Polly, template cache |
| Flattener | Pure transform (no I/O) |
| Data | `PrismDbContext` schema `prism`, `ProjectionWriter` CAS + bulk copy |

Auth: Prism→API client credentials + `Prism.Read`. Operators→admin: `Prism.Admin` (optional `Prism.Delegate` + `X-Prism-Acting-User`). Local: `X-Prism-Development-Key`.

Topology: topic `flexforms-prism`, subscription `prism-projector`, sessions `{tenantId}:{applicationId}`.

Infra for the Function App is **documented in `docs/azure-setup.md`**, not fully in this repo’s Terraform.
