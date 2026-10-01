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
GRANT SELECT ON prism.v_current_answer_facts TO prism_reader;
GRANT SELECT ON prism.v_submission_answer_facts TO prism_reader;
GRANT SELECT ON prism.field_catalog TO prism_reader;
GRANT SELECT ON prism.v_template_field_changes TO prism_reader;
CREATE USER [<data-team-identity-or-group>] FROM EXTERNAL PROVIDER;
ALTER ROLE prism_reader ADD MEMBER [<data-team-identity-or-group>];
```

The `prism` schema is created by the first migration. Run the grants on the schema after the first migration, or
create the schema first with `CREATE SCHEMA prism;`.

The schema is applied by the `prism-migrations` artifact from CI (`migrate-prism-db.sh`). See the runbook.

## Entra ID

### FlexForms API app registration (existing)

- Add the app role **`Prism.Read`**, with allowed member type **Applications**.
- Assign it to Prism's client application (below) and grant admin consent.

Prism calls the API with a token for this registration, so the API's `PlatformBearer` scheme must accept it. It
already accepts tokens for its own app registration.

### Prism app registration (new)

- Application ID URI: `api://<prism-client-id>`.
- App role **`Prism.Admin`**, with allowed member types **Users/Groups** and **Applications**. Assign it to the
  people or group who run backfills and classify fields.
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
| `Prism__Admin__ValidIssuers__0` | `https://sts.windows.net/<entra-tenant-id>/` (only if v1 tokens are used) |

Optional, with their defaults:

| Setting | Default |
|---|---|
| `Prism__Backfill__PageSize` | `200` |
| `Prism__Backfill__TimeBudget` | `00:04:00` |
| `Prism__Cleanup__RetentionDays` | `30` |
| `Prism__Cleanup__MaxGenerationsPerRun` | `2000` |
| `Prism__Admin__RequiredRole` | `Prism.Admin` |

Without `Prism__Admin__Authority` and `Prism__Admin__Audience`, every admin request is rejected.

## FlexForms API

| Setting | Value |
|---|---|
| `MassTransit__Outbox__Enabled` | `true`. Prism events always go through the outbox, but only once it's enabled. They don't need to be on the outbox allowlist. |

The outbox tables must exist in every tenant's EA database (part of the outbox migrations), along with the new
`SourceRevision` columns from the Prism migration.
