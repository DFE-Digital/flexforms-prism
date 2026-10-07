# Azure setup for Prism

Infrastructure is managed outside this repository. This page lists everything Prism needs in Azure, so the
resources can be created in whatever tooling the platform team uses. Names in `<angle brackets>` are placeholders.

## Service Bus

Use the existing FlexForms namespace (Standard or Premium tier; duplicate detection needs at least Standard).

### Topic `flexforms-prism`

| Setting | Value | Why |
|---|---|---|
| Duplicate detection | **On** | Drops outbox redeliveries. **It can only be set when the topic is created.** |
| Duplicate detection window | `1 day` | Covers outbox retries after an outage. |
| Default message time-to-live | `14 days` | Lets events wait while Prism is down or not yet deployed. |
| Partitioning | Off | Not needed. |

The FlexForms API has `AutoCreateEntities` off. The topic must exist **before** the API is deployed with Prism
publishing, otherwise the outbox keeps retrying.

### Subscription `prism-projector` on `flexforms-prism`

| Setting | Value | Why |
|---|---|---|
| Requires session | **On** | Messages for one application (or one template's versions) are handled in order. **Creation-time only.** |
| Max delivery count | `10` | After that, a transiently failing message is dead-lettered. |
| Lock duration | `1 minute` | The Function renews the lock for up to 10 minutes. |
| Dead-lettering on message expiration | On | |
| Default message time-to-live | `14 days` | |
| Filter | None (the default "match all" rule) | The topic only carries Prism events (`ApplicationProjectionRequestedEvent` and `TemplateVersionPublishedEvent`). |

### Access

| Identity | Role | Scope |
|---|---|---|
| FlexForms API | Azure Service Bus Data Sender | Topic `flexforms-prism` (it may already have this at namespace level) |
| Prism Function | Azure Service Bus Data Receiver | Subscription `prism-projector` |
| Prism Function | Azure Service Bus Data Sender | Topic `flexforms-prism` (backfill and reconciliation enqueue `Resync` messages) |

## Prism SQL database

One shared Azure SQL database for all tenants. Every key starts with `tenant_id`.

- Size: start with General Purpose serverless (2 vCores max) or Standard S2. Check during the load test in the rollout.
- Microsoft Entra authentication. SQL logins aren't needed.
- Private endpoint if the platform uses them, plus network access for the Function and the migration runner.

After the database exists, an Entra admin runs:

```sql
-- The Function's managed identity (use the identity's display name)
CREATE USER [<prism-function-identity>] FROM EXTERNAL PROVIDER;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::prism TO [<prism-function-identity>];

-- The identity that runs migrations from the pipeline
CREATE USER [<prism-migration-identity>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner ADD MEMBER [<prism-migration-identity>];

-- Data team readers: the views only (plus the catalogue, which has no answer values).
-- An existing database only needs the v_template_field_changes grant after the AddTemplateVersionsAndFieldChanges migration.
CREATE ROLE prism_reader;
GRANT SELECT ON prism.v_applications TO prism_reader;
GRANT SELECT ON prism.v_current_answer_facts TO prism_reader;
GRANT SELECT ON prism.v_submission_answer_facts TO prism_reader;
GRANT SELECT ON prism.field_catalog TO prism_reader;
GRANT SELECT ON prism.v_template_field_changes TO prism_reader;
CREATE USER [<data-team-identity-or-group>] FROM EXTERNAL PROVIDER;
ALTER ROLE prism_reader ADD MEMBER [<data-team-identity-or-group>];
```

The `prism` schema is created by the first migration. Run the grants on the schema after the first migration, or
create the schema first with `CREATE SCHEMA prism;`.

The schema is applied by the migration job below, or by hand with the `prism-migrations` artifact from CI
(`migrate-prism-db.sh`).

## Migration job

The `Migrate Prism database` workflow (`.github/workflows/migrate.yml`) runs on every push to `main`
(environment `development`), or by hand for any environment. It works like the FlexForms API's init container:

1. It builds `Dockerfile.migrations` and imports it into ACR as `flexformsprism:migrations-sha-<commit>`.
2. It points a Container Apps Job at that image and starts it.
3. It waits for the execution to succeed, and fails the workflow if it fails.

The job runs inside Azure, so it reaches SQL over the private network. The connection string lives on the job,
never in GitHub. Create the job once per environment:

| Setting | Value |
|---|---|
| Type | Container Apps Job, trigger type **Manual**, in the same environment as the FlexForms API |
| Image | Any `flexformsprism:migrations-*` tag (the workflow replaces it on each run) |
| Replica timeout | `1800` seconds. Replica retry limit `0`, parallelism `1` |
| Identity | A user-assigned managed identity: the `<prism-migration-identity>` above, plus `AcrPull` on the registry |
| Secret `prism-db-connection` | `Server=tcp:<server>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Managed Identity;User Id=<identity-client-id>;Encrypt=True` |
| Environment variable | `PRISM_DB_CONNECTION` = `secretref:prism-db-connection` |

The workflow uses these GitHub environment secrets:

| Secret | Value |
|---|---|
| `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `ACR_NAME`, `ACR_CLIENT_ID`, `ACA_CLIENT_ID` | Same as the FlexForms API |
| `PRISM_MIGRATION_JOB_NAME` | The job's name |
| `PRISM_MIGRATION_JOB_RESOURCE_GROUP` | The job's resource group |

The `ACA_CLIENT_ID` identity needs `Contributor` on the job, to update its image and start it. A failed run's
output is in the job's execution history and the environment's Log Analytics workspace.

## Entra ID

### FlexForms API app registration (existing)

- Add the app role **`Prism.Read`**, with allowed member type **Applications**.
- Assign it to Prism's client application (below) and grant admin consent.

Prism calls the API with a token for this registration, so the API's `PlatformBearer` scheme must accept it. It
already accepts tokens for its own app registration.

### Prism app registration (new)

- Application ID URI: `api://<prism-client-id>`.
- App role **`Prism.Admin`**, with allowed member types **Users/Groups** and **Applications**. Assign it to the
  people or group who run backfills and classify fields, and to the **FlexForms API's managed identity**, which calls
  the export endpoints for the tenant admin "Reporting export" screens.
- App role **`Prism.Delegate`**, allowed member type **Applications**. Assign it only to the FlexForms API's managed
  identity. It lets the API send `X-Prism-Acting-User` so the audit trail records the tenant admin who made a change
  (as `person via api-identity`). A caller without it that sends the header is refused.
- A client secret (kept in Key Vault). Prism uses this registration's client ID and secret to call the FlexForms API.

## Function App

| Setting | Value |
|---|---|
| Runtime | .NET isolated, .NET 10 |
| Plan | Flex Consumption, **if** it supports the Service Bus trigger with sessions in your region; otherwise Elastic Premium EP1 |
| Identity | A user-assigned managed identity (used for SQL, Service Bus and storage) |
| Storage | A storage account for the Functions host (the identity needs Storage Blob Data Owner, Storage Queue Data Contributor and Storage Table Data Contributor) |
| Networking | VNet integration so it can reach the FlexForms API, SQL and Service Bus privately |
| Monitoring | Application Insights (workspace-based) |

Deploy the `prism-functions` artifact from CI (`prism-functions.zip`).

### App settings

| Setting | Value |
|---|---|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `AzureWebJobsStorage__accountName` | `<storage-account>` |
| `AzureWebJobsStorage__credential` | `managedidentity` |
| `AzureWebJobsStorage__clientId` | `<identity-client-id>` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | From Application Insights. This also turns on the metrics export. |
| `ConnectionStrings__Prism` | `Server=tcp:<server>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Managed Identity;User Id=<identity-client-id>;Encrypt=True` |
| `ServiceBus__fullyQualifiedNamespace` | `<namespace>.servicebus.windows.net` |
| `ServiceBus__credential` | `managedidentity` |
| `ServiceBus__clientId` | `<identity-client-id>` |
| `Prism__ServiceBusTransport` | `AmqpWebSockets` (default) or `AmqpTcp` if port 5671 is open. If you change it, change `transportType` in `host.json` too. |
| `ExternalApplicationsApiClient__BaseUrl` | `https://<flexforms-api-host>/` |
| `ExternalApplicationsApiClient__Authority` | `https://login.microsoftonline.com/<entra-tenant-id>` |
| `ExternalApplicationsApiClient__ClientId` | `<prism-client-id>` |
| `ExternalApplicationsApiClient__ClientSecret` | Key Vault reference to the Prism client secret |
| `ExternalApplicationsApiClient__Scope` | `api://<flexforms-api-client-id>/.default` |
| `Prism__Admin__Authority` | `https://login.microsoftonline.com/<entra-tenant-id>/v2.0` |
| `Prism__Admin__Audience` | `api://<prism-client-id>` |
| `Prism__Admin__ValidIssuers__0` | `https://sts.windows.net/<entra-tenant-id>/` (for v1 tokens, the default for managed identities) |

Optional, with their defaults:

| Setting | Default |
|---|---|
| `Prism__Backfill__PageSize` | `200` |
| `Prism__Backfill__TimeBudget` | `00:04:00` |
| `Prism__Cleanup__RetentionDays` | `30` |
| `Prism__Cleanup__MaxGenerationsPerRun` | `2000` |
| `Prism__Admin__RequiredRole` | `Prism.Admin` |
| `Prism__Admin__DelegateRole` | `Prism.Delegate` |
| `Prism__ExcludedTenantIds__0`, `__1`, ... | the Playwright tenant, from `appsettings.json` |

`Prism:ExcludedTenantIds` lists tenants Prism never reports on, such as test and Playwright tenants. Their messages
are completed without projecting, and backfills and reconciliations skip them. Rows already projected for an excluded
tenant stay in the database until you delete them. The shared list lives in the Functions project's `appsettings.json`
and deploys with the code. App settings override it entry by entry, so `Prism__ExcludedTenantIds__0` replaces the
first entry; use `__1` and up to add tenants for one environment.

Worker log levels come from `Program.cs` (Entity Framework and HttpClient at Warning). Override them with
`Logging__LogLevel__<category>` app settings; `host.json` only controls the host's own logs.

Without `Prism__Admin__Authority` and `Prism__Admin__Audience`, every admin request is rejected.

`Prism__Admin__DevelopmentKey` is for local development only: when the Functions environment is `Development`,
a request with the same value in `X-Prism-Development-Key` is accepted with both roles, so the FlexForms API can
call Prism without Entra. It is ignored (with a warning) in any other environment. Never set it in Azure.

## FlexForms API

| Setting | Value |
|---|---|
| `MassTransit__Outbox__Enabled` | `true`. Prism events always go through the outbox, but only once it's enabled. They don't need to be on the outbox allowlist. |

The outbox tables must exist in every tenant's EA database (part of the outbox migrations), along with the new
`SourceRevision` columns from the Prism migration.

The tenant admin "Reporting export" screens call Prism's export endpoints through the API, using the API's
managed identity:

| Setting | Value |
|---|---|
| `Prism__ControlApi__BaseUrl` | `https://<prism-function-host>/`. Without it the screens say reporting export isn't set up. |
| `Prism__ControlApi__Scope` | `api://<prism-client-id>/.default` |
| `Prism__ControlApi__ManagedIdentityClientId` | The API's user-assigned identity client ID. Leave unset for a system-assigned identity. |
| `Prism__ControlApi__Timeout` | Optional, default `00:00:30` |

`Prism__ControlApi__DevelopmentKey` is for local development only. Never set it in Azure.

Managed identity tokens are v1 tokens unless the Prism app registration's manifest sets
`"requestedAccessTokenVersion": 2`. Keep v1 and set `Prism__Admin__ValidIssuers__0` on the Function, otherwise every
call from the API is rejected as an invalid token. If you switch the manifest to v2 instead, the token's audience
becomes the bare Prism client ID, so `Prism__Admin__Audience` must change to `<prism-client-id>` as well.

## Checking the connection

1. **API to Prism:** open a template's "Reporting export" screen as a tenant admin. It should list the fields.
   The Function logs `Prism admin audit` entries when a decision is saved. A 401 or 403 in the API logs means the
   token audience, issuer or the `Prism.Admin`/`Prism.Delegate` assignments are wrong.
2. **API to Service Bus to Prism:** save an application. The Function logs a projection for it within seconds,
   and `prism.application_projection_state` has a row for it.
3. **Prism to API:** start a backfill for one tenant (see the runbook). Failures with `Missing Prism.Read app role`
   mean the `Prism.Read` assignment or admin consent is missing.
