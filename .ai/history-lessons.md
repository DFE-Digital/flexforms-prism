# History lessons (Prism)

Lessons from the data-pipeline design review and later Prism work.

| Lesson | Status |
|--------|--------|
| Long facts + field catalogue, not wide tables; EventMappings are not the reporting contract; outbox + ids-only events | **Verified** as Prism |
| Draft-save vs submit-only was debated; **current** API publishes Saved, Submitted and Deleted | **Verified** |
| Submit snapshots use the submitted `response_id`, not “latest response” | **Verified** in contract (`GET responses/{id}`) |
| `v_applications.template_name` comes from `ea.Templates.Name` via the API DTO | **Verified** |
| `Prism.Read` is on the platform app registration | **Verified** |
| Do not build a second flatten-to-lake pipeline alongside Prism | **Historical** decision, implemented as this repo |

**Unverified:** whether any separate lake/Databricks jobs still run beside Prism. None are in these repositories.
