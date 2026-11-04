# Security (Prism)

- Prism.Read on **platform** API app, assigned to the Function managed identity. Audience must match `Platform:AzureAd`.
- Admin: Entra bearer `Prism.Admin`. Do not expose admin HTTP anonymously in deployed environments.
- Tenant header required except list-tenants.
- Export policy is a data-protection control: Denied/Unclassified fields must not appear in facts.
