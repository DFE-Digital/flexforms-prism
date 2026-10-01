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

## Run the Functions host locally

```bash
cd src/GovUK.Dfe.FlexForms.Prism.Functions
cp local.settings.example.json local.settings.json
func start
```

`local.settings.json` is git-ignored.
