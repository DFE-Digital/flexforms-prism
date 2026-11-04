# Feature: Projection and CAS

Entry: `ProjectionFunction.cs` → `ProjectionService.cs` → `ProjectionWriter.cs`.

Reasons: Saved, Submitted, Deleted, Resync (backfill only). Hash reuse vs new generation. Submit writes a submission generation **and** current. Delete writes tombstone.

Tests: Projector tests + Scenario tests (ordering, delete overtaking, CAS conflict).

Do not project inside `OperationProcessor` — it only enqueues Resync messages.
