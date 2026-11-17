# AutoSkill Intelligent Governance

Status: **PARTIAL — provenance barriers and deterministic staging policy implemented; Backend consent integration and revocation executor remain disabled/blocking.**

## Data lineage contract

```text
Conversation archive + manifest
  -> Extraction evidence
  -> Proposal + content-segment hashes
  -> Skill version snapshot
  -> Active pointer / runtime mirror
```

Each provenance-bearing object uses opaque artifact IDs and records `source_ids`, `dependency_ids`, `provenance_type`, `consent_scope`/revision, validation state, activation state and retention state. Session manifests retain hashes and metadata, not a second copy of message text. Proposals point to the conversation artifact; approved version snapshots point to the proposal and retain the original source IDs. Runtime mirror markers remain separate and must be linked by a future integrated revocation index.

Missing/corrupt legacy session manifests are treated as UNKNOWN. Recovery checks the manifest before reading JSONL content and skips an archive if lineage is absent or the owner consent generation changed. Legacy proposals and versions without provenance are denied evaluation, approval and rollback. No automatic migration reads old content or infers independence.

## Deterministic policy

`tools/autoskill/governance.mjs` implements the policy used by the staging proposal/version pipeline:

- **INDEPENDENT:** retain only with explicit, verified independent source evidence.
- **DERIVED:** trace dependencies and produce `invalidate_and_delete` actions for dependent artifacts under a revoked scope.
- **MIXED:** separate only independently verified, hash-backed content segments; otherwise quarantine.
- **UNKNOWN:** quarantine and block use.

`planConsentRevocation` is currently a pure, deterministic plan builder. It does not delete files. This prevents unclassified bulk deletion while the adapter/Backend owner identity and transactional revoke contract are unresolved. Audit records contain artifact IDs, action names and reasons only.

## Runtime enforcement and current limits

- The isolated adapter checks explicit, exact-scope capture and analysis grants and validates consent generation.
- Conversation archive writes include a provenance sidecar and segment hashes. A stale generation is rejected before archive writes or content reads.
- Analysis verifies the sidecar and current generation before reading an archive. It checks consent again before provider invocation and before proposal persistence.
- AutoSkill remains disabled in the primary Gateway. The adapter does not yet have a trusted Backend callback that can resolve the authenticated owner and obtain an atomic, revocable Backend lease. A mock callback in tests is not production integration.
- No deletion executor is enabled. Proposals, versions, embeddings/caches, mirrors and backup copies are not yet traversed/cleaned end-to-end. Local revocation must not claim removal from external model/provider logs.
- Re-consent never reconstructs a missing archive or revives an unknown artifact.

## Verification evidence

- Project governance/provenance/pipeline tests: synthetic fixtures only.
- Pinned upstream adapter tests: synthetic temporary directories only.
- Legacy archive test verifies no JSONL content read without a valid manifest.
- No user conversation, real AutoSkill archive, primary Gateway or provider was used.

Before enabling AutoSkill, implement the trusted Backend lease bridge and idempotent owner-scoped deletion workflow; then test revoke during extraction/promotion, restart recovery, retry, cache/mirror/version invalidation, and backup restoration. Until then, keep the feature disabled and treat this checkpoint as partial.
