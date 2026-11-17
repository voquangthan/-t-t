# AutoSkill Intelligent Governance

Status: **PARTIAL — provenance governance and a staging-only revocation executor are implemented; trusted Backend/Gateway consent integration, full-store cleanup and safe rebuild remain incomplete.**

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

The staging pipeline has an owner-scoped legacy migration command. It scans only that staging owner's proposal and skill-version records, defaults to dry-run, and on explicit `--apply true` adds UNKNOWN/quarantined provenance while preserving candidate/version content and history. It refuses to apply if records are unreadable or owner IDs do not match. It does not touch archives, active pointers, runtime mirrors, backups, or any real owner data.

## Deterministic policy

`tools/autoskill/governance.mjs` implements the policy used by the staging proposal/version pipeline:

- **INDEPENDENT:** retain only with explicit, verified independent source evidence.
- **DERIVED:** trace dependencies and produce `invalidate_and_delete` actions for dependent artifacts under a revoked scope.
- **MIXED:** separate only independently verified, hash-backed content segments; otherwise quarantine.
- **UNKNOWN:** quarantine and block use.

`planConsentRevocation` follows both source and dependency links, quarantines nodes without artifact IDs, and blocks the plan if IDs collide. For a mixed artifact, a hash-backed segment can be selected for separation only when its source/dependency links do not intersect the revoked dependency closure and its consent scope does not include the revoked tuple. The staging executor does not yet split content, so it quarantines the whole mixed artifact instead of retaining selected segments.

The staging-only `revocation-store.mjs` persists an owner-scoped journal and monotonic tombstones before cleanup. Deletion is limited to exact proposal/version JSON files under that owner's managed directories; it rechecks the planned content hash and embedded owner/artifact IDs before unlinking. Repeated execution resumes missing targets idempotently. Tombstones block proposal/version evaluation and activation, including a restored file at a tombstoned path. Unknown artifacts are blocked by ID; if an artifact cannot be mapped safely, the owner is quarantined until review. Journal/tombstone files contain IDs, scope, relative managed paths, hashes, revisions and state, not artifact content.

This executor is not connected to the live Backend consent lease or Gateway adapter and is not a production revocation path. It does not yet traverse session archives, active pointers, runtime mirrors/caches, embeddings, or backups. A production integration still needs authenticated owner binding, cancellation/recheck during extraction and promotion, mirror/cache invalidation, archive cleanup, and restore protection across every managed store. Local revocation cannot retract content already sent to external providers. Audit records contain artifact IDs, action names and reasons only.

## Runtime enforcement and current limits

- The isolated adapter checks explicit, exact-scope capture and analysis grants and validates consent generation.
- Conversation archive writes include a provenance sidecar and segment hashes. A stale generation is rejected before archive writes or content reads.
- Analysis verifies the sidecar and current generation before reading an archive. It checks consent again before provider invocation and before proposal persistence.
- Runtime retrieval blocks skills with missing, quarantined, unvalidated, inactive, or inconsistent provenance. Only verified independent sources or conversation-derived skills matching the current analysis revision can be injected.
- Runtime cache snapshots carry their own provenance, source/dependency IDs and segment hashes. At `agent_end`, snapshots from stale generations or with invalid segment metadata are cleared and excluded from skill-learning evidence.
- AutoSkill remains disabled in the primary Gateway. The adapter does not yet have a trusted Backend callback that can resolve the authenticated owner and obtain an atomic, revocable Backend lease. A mock callback in tests is not production integration.
- The staging proposal/version revocation executor is not wired into the Backend lease or Gateway runtime. It does not provide an immediate Backend-to-Gateway revoke signal or cover caches, embeddings, mirrors, archives, or backup copies end-to-end. Local revocation must not claim removal from external model/provider logs.
- Re-consent never reconstructs a missing archive or revives an unknown artifact.

## Verification evidence

- Project governance/provenance/pipeline tests: synthetic fixtures only.
- Pinned upstream adapter tests: synthetic temporary directories only.
- Legacy archive test verifies no JSONL content read without a valid manifest.
- No user conversation, real AutoSkill archive, primary Gateway or provider was used.

Before enabling AutoSkill, implement the trusted Backend lease bridge and connect revocation to archives, proposals, versions, runtime caches/mirrors and restore paths; then test revoke during extraction/promotion, stale workers, restart recovery, retries, and backup restoration. Until then, keep the feature disabled and treat this checkpoint as partial.
