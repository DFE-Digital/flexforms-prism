# Dependencies (Prism)

- **flexforms-api**: events + `v1/internal/prism` + export-policy client in the other direction.
- **GovUK.Dfe.FlexForms.Api.Client** NuGet, pinned at **0.0.50** in `Directory.Packages.props` with CoreLibs Contracts **1.0.112** and Messaging.Contracts **0.1.6** (2026-11-17). The API now publishes 0.0.58+ (net10.0, Contracts 2.x). Prism only uses `IInternalPrismClient` and the Prism DTOs, so the lag is harmless until a Prism DTO changes; then bump Api.Client and Contracts together.
- **GovUK.Dfe.CoreLibs.Messaging.Contracts** / **Contracts**.
- **No** flexforms-web project reference. JSON shape coupling is implicit.

CI (`.github/workflows/ci.yml`) still checks out `DFE-Digital/flexforms-api` at `FLEXFORMS_API_REF: feature/prism` under a "TEMPORARY" comment, but no csproj references it any more (**Verified**: no `ProjectReference` outside Prism projects). The checkout is vestigial; builds use the NuGet package.

CoreLibs local project refs will break on assembly version `1.0.0.0`.
