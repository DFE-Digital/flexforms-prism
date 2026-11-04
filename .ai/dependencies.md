# Dependencies (Prism)

- **flexforms-api**: events + `v1/internal/prism` + export-policy client in the other direction.
- **GovUK.Dfe.FlexForms.Api.Client** NuGet (pin in `Directory.Packages.props` — historically lagged API).
- **GovUK.Dfe.CoreLibs.Messaging.Contracts** / **Contracts**.
- **No** flexforms-web project reference. JSON shape coupling is implicit.

CI may still check out a sibling API branch — treat as possibly stale vs PackageReference.

CoreLibs local project refs will break on assembly version `1.0.0.0`.
