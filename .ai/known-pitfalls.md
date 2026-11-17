# Known pitfalls (Prism)

1. Projecting from the event body (wrong data on duplicates).
2. Subscription created **without** sessions, or topic without duplicate detection — cannot be added later easily.
3. Changing flatten output without `ProjectorVersion` bump → silent incomparable generations.
4. Export policy change without waiting for backfill → empty/missing columns in Power BI.
5. Recycling FlexForms `field_id`s — reports join across versions incorrectly.
6. Writing to FlexForms EA databases (never).
7. Including excluded test tenants in warehouse extracts.
8. README vs csproj: sibling API project reference may be outdated.
9. `v_applications.template_name` comes from EA `Templates.Name` via API DTO, not the JSON `templateName` after that change.
10. **Message type URNs are a contract.** `ProjectionMessages` matches `urn:message:{namespace}:{type}` of the CoreLibs event records. Moving `ApplicationProjectionRequestedEvent` / `TemplateVersionPublishedEvent` to another namespace or renaming them makes template events read as application events (or fail as `invalid_message`). Coordinate with CoreLibs and the API.
11. **Every HTTP function authenticates through `AdminAuthenticator`** before doing any work. Follow the existing control-plane functions when adding one.
