# AutoSkill real E2E checkpoint

Status: **DISABLED — CONSENT BACKEND GATE REQUIRED BEFORE ANY E2E**

## Starting point and safety boundary

- Starting commit: `94864e3` (`Add staged AutoSkill learning pipeline`).
- No changes were made to `gateway.lab.json` or the primary Gateway state.
- Primary ports `5183` and `18793` were not started or modified.
- Staging uses port `19794`, config `detot-lab/.local/autoskill-staging/gateway.real-e2e.json`, state `detot-lab/.local/autoskill-staging/real-e2e-state`, synthetic owner `detot-synthetic-owner`, and synthetic ticket data only.
- Staging config pins DeepSeek to `deepseek-flash` through the existing `DEEPSEEK_API_KEY` environment reference. The credential itself is not copied into the staging config or logs.
- The only allowed agent tool is `detot_synthetic_case_check`; filesystem, shell, browser, network, Gateway administration, and other tool groups are denied.
- The isolated staging builder now emits `enabled: false`, `allowConversationAccess: false`, and `extractOnAgentEnd: false`; no staging or primary Gateway was started for the consent work.
- AutoSkill remains disabled; retrieval is also disabled until an evaluated proposal is approved for staging.

## Verified

- DeepSeek API was called directly with a synthetic prompt: HTTP 200, model `deepseek-flash`, 48 prompt tokens, 18 completion tokens, 1,066 ms. No secret or personal text was logged.
- Staging OpenClaw config passed `openclaw config validate` before Gateway created its state DB. A later validation against the initialized state failed in the read-only SQLite worker with `EPERM` creating a private snapshot.
- Synthetic checker contract returned `PASS` for its fixed training fixture.
- Helper syntax checks passed.
- A direct Node loopback bind on port `19795` returned `PASS`, so loopback sockets are available to a normal process.
- The non-elevated Gateway attempt logged SQLite snapshot `EIO`, plugin capture `EPERM` while linking the OpenClaw package, and a later config-backup `EIO`.
- A staging-only elevated attempt got past the snapshot/plugin-capture errors and reached `starting HTTP server`, but did not become ready within its 2-minute readiness window; no listening socket appeared. That exact staging process was stopped.
- A second elevated startup attempt stalled before producing a Gateway log or opening a listener and was interrupted. Its precise failure point is not known.
- No listener remains on port `19794`; no staging or primary Gateway process remains running from this work.

## Not verified

- No real DeepSeek request went through the Gateway staging session.
- No plugin-loaded Gateway health/readiness check passed.
- AutoSkill did not produce a proposal from an actual Gateway interaction.
- No baseline vs post-skill replay, activation, rollback, latency/token comparison, restart test, or E2E PASS exists.
- The existing 72 adapter tests and 8 pipeline tests from commit `94864e3` remain unit/integration-fixture evidence only.

## Files for continuation

- `tools/autoskill/real-e2e/prepare.mjs` builds the isolated config and the filesystem-free synthetic tool.
- `.local/autoskill-staging/logs/` contains staging-only startup diagnostics; inspect only with secret redaction.
- The latest startup log ends at Gateway HTTP server startup. Earlier non-elevated logs show `EIO`/`EPERM`; the elevated log has no final bind/readiness error before timeout.

## Next exact step

Keep AutoSkill disabled until its capture/archive/extraction path can call the owner-scoped consent backend before reading conversation content. Only then resume isolated staging diagnosis and synthetic E2E; do not alter the primary Gateway config or state.

Do not report SELF-IMPROVEMENT PASS until all evidence above is replaced with actual Gateway-backed DeepSeek, proposal, activation, comparative replay, and rollback results.

## Follow-up audit — 17/11/2026

- Repository baseline before this follow-up: `bb4520e`; working tree was clean before the source audit.
- The backend consent catalog contains `chat_history / skill_learning / local_only` and `DeepSeek` grants, but repository inspection found no AutoSkill adapter call into the owner-scoped Backend lease. This is not sufficient to enable capture.
- At the start of this audit the pinned upstream clone was absent. It has since been restored under `.local/research/AutoSkill` at exact commit `94c47ca488d4ba4117d20272e66d49b9877e68cf`; the tracked staging patch applied successfully.
- The backend consent catalog contains `chat_history / skill_learning / local_only` and `DeepSeek` grants, but the adapter still has no call into the owner-scoped Backend lease. This is not sufficient to enable capture. Staging config and primary Gateway remain untouched and AutoSkill remains disabled.
- Verification with synthetic/local fixtures: backend build PASS (0 warnings/errors); Weather checks PASS; consent audit PASS (test project reports CA1416 on its Windows-only DPAPI call); Cloud Storage checks PASS (24/24); AutoSkill pipeline PASS (7/7); upstream adapter tests PASS (72/72); proposal integration PASS (1/1); frontend tests PASS (2/2) and production build PASS.
- No live provider, personal data or Gateway was used. Google Public Alerts live smoke remains unavailable without a configured key.
- Frontend build reports existing non-failing warnings: `canonical-host.js` is not bundled because it is a classic script, and the JavaScript chunk is about 553 kB minified.
- Before enabling capture, define and test revocation cleanup for source archives and derived proposals. Current AutoSkill policy retains proposals/version history, while the consent UI promises removal of worker-owned derived data where possible; implementation must not silently choose one retention semantics over the other.

## Intelligent Governance checkpoint — 17/11/2026

- Added provenance metadata helpers, content-segment hashes, and deterministic `INDEPENDENT` / `DERIVED` / `MIXED` / `UNKNOWN` policy evaluation.
- The staging pipeline now blocks evaluation, approval, and rollback for missing/unknown provenance; approved versions carry a new version artifact ID and dependency link to their proposal.
- The pinned adapter patch now writes conversation provenance sidecars, checks owner consent generation before reading session content, rejects stale capture generations, and records proposal/version lineage. Legacy archives without a valid manifest are skipped before JSONL read; this is a read barrier, not a completed persistent quarantine migration.
- A pure revocation planner computes dependency closure and per-artifact actions. It does not execute deletions. No personal data was read or changed.
- Synthetic verification: project governance/provenance/pipeline/integration tests **17/17 PASS**; pinned upstream adapter tests **79/79 PASS**.
- Still blocked from full integration: the adapter has no trusted Backend consent lease callback bound to the authenticated owner, and there is no crash-safe deletion executor covering all managed copies. AutoSkill remains disabled. Do not claim governance or AutoSkill E2E PASS until those pieces and their revoke/restart/restore tests are implemented.
- See `AUTOSKILL_GOVERNANCE.md` for architecture, boundaries, and required next work.

### Revocation planner hardening — 17/11/2026

- The pure planner now follows `source_ids` as well as `dependency_ids`, quarantines unidentified artifacts, and blocks the entire plan on duplicate artifact IDs rather than risking ambiguous deletion.
- Repeated planning over the same synthetic graph is verified deterministic.
- Governance/provenance/pipeline/integration tests now **18/18 PASS**; adapter remains **79/79 PASS** from the preceding checkpoint, with no adapter source changes in this follow-up.

### Runtime cache governance — 17/11/2026

- Retrieval now refuses skill hints unless the artifact is active, validated, retained, and either has source-matched independent evidence or conversation provenance matching the current analysis consent generation.
- Runtime cache snapshots carry provenance, source/dependency IDs, consent revision and content-segment hashes. `agent_end` drops stale or malformed cache evidence and only forwards skill IDs backed by that validated cache.
- Unknown and forged independence metadata tests pass. Synthetic verification: pinned adapter **82/82 PASS**; project governance/provenance/pipeline/integration **18/18 PASS**.
- Revocation is still not immediate across Gateway caches because there is no authenticated Backend-to-Gateway lease/revoke channel. No primary Gateway or real user data used.

### Legacy artifact migration — 17/11/2026

- Added a staging-only, owner-scoped migration command. Dry-run is the default; explicit apply annotates legacy proposals and version snapshots as UNKNOWN/quarantined, preserves candidate/history bytes semantically, and blocks activation.
- The migrator does not touch archives, active pointers, mirrors, backup data, or real owner storage. It refuses to apply when an artifact is unreadable or the embedded owner ID does not match.
- Synthetic migration/restart-idempotence/owner-isolation tests pass; project governance/provenance/pipeline/integration/migration suite is **22/22 PASS**. Migration was not run against the existing `.local` tree.

### Mixed provenance revocation guard — 17/11/2026

- Revocation planning now filters mixed-content segments against the full revoked source/dependency closure and the revoked consent tuple before selecting segments for separation.
- If every independently labeled segment still has revoked lineage, the complete mixed artifact is quarantined.
- Synthetic governance/provenance/pipeline/integration/migration tests: **24/24 PASS**.
- This remains planning-only. No content was split or deleted, and no real owner data, Gateway or provider was used.

### Staging revocation journal — 17/11/2026

- Added an owner-scoped metadata journal and monotonic artifact/path tombstones. The staging pipeline checks tombstones before parsing affected proposal/version files and rejects replay evaluation, approval, and rollback for revoked artifacts.
- Resume is idempotent. It revalidates the embedded owner/artifact IDs and exact planned file hash before deleting an allowlisted proposal/version JSON file. A changed target stays in place and the revoke remains blocked. Unknown/ambiguous lineage is quarantined; an unmappable node quarantines that owner's AutoSkill pipeline.
- Mixed artifacts are kept quarantined because the current executor cannot safely split file contents, even when the policy planner identifies candidate independent segments.
- Owner-scoped locks serialize staging writes and revocation across processes; dead-process lock recovery is tested, while malformed lock metadata remains fail-closed for manual recovery.
- Synthetic tests cover interruption/resume, repeated revoke, owner isolation, concurrent process denial, changed-file refusal, restore-at-tombstoned-path denial, and no deletion of quarantined records. Project AutoSkill suite: **35/35 PASS**; pinned adapter suite: **82/82 PASS**.
- This is staging pipeline integration only. It is not bound to Backend-authenticated consent, and it does not cover conversation archives, runtime mirrors/caches, embeddings, active pointers, or backups. No real data was migrated/deleted, no Gateway was started/changed, and no provider was used.

### Independent-source rebuild draft — 17/11/2026

- Added a staging utility that accepts only owner-matched, hash-checked source artifacts already classified as verified independent `source_document`, `source_code`, or `operational_rule` records with no consent scope or dependencies.
- Candidate name, description and prompt must appear verbatim in the supplied source text. The utility does not accept old skill history, provenance, files or approval state; it writes fresh provenance/segment hashes and an inactive pending-review proposal without copying source text or calling a model.
- The new draft must pass the normal replay gate and explicit approval. Synthetic AutoSkill suite: **32/32 PASS**.
- This is not a source-registration UI/API and does not establish trusted Backend source evidence. It does not enable capture or retrieval and remains staging-only.

### Mixed-segment metadata gate — 17/11/2026

- Mixed-content separation now requires a SHA-256 hash, exact independent source evidence, dependency metadata, explicit consent-scope metadata, non-unknown validation/activation states, and retained content. Incomplete or withdrawn segments are ineligible and the artifact is quarantined.
- Synthetic governance and full AutoSkill suite: **36/36 PASS**.

### Active pointer and mirror provenance — 17/11/2026

- Active pointers and managed runtime mirror markers now carry opaque artifact IDs, source/dependency links, consent scope/revision, validation/activation/retention state, and hashes for their content segments.
- Staging checks active pointer/version consistency and managed mirror lineage before using or updating them; forged pointer or mirror lineage is rejected before rollback/promotion. Synthetic suite: **36/36 PASS**.
- Revocation still does not delete or sweep active pointers and mirror copies; this metadata closes traceability gaps only and is not a runtime revoke signal.

### Mixed artifact runtime deny gate — 17/11/2026

- Audit found that the shared usability assertion could accept a MIXED artifact after the planner identified safe-looking independent segments. Since no verified splitter materializes a segment-only artifact, this could expose the full mixed body.
- Updated the shared gate to deny MIXED and every non-retain policy action, and to honor top-level as well as nested activation/retention state. The artifact remains quarantined until a separately validated rebuild/split exists.
- Synthetic governance, pipeline, owner-lock, revocation-store, provenance, migration and integration tests: **38/38 PASS**; `git diff --check` PASS. Adapter suite SKIP because the pinned upstream checkout is absent from this workspace.
- Local commit: `a6a5f42`. No real owner data, provider, Gateway or AutoSkill capture was used; AutoSkill remains disabled. Backend consent lease binding and full-store revocation remain blockers.

### Policy decision audit metadata — 17/11/2026

- Staging artifact usability and tombstone path checks now record both allow and deny decisions with operation/policy codes. Denial reasons are normalized to fixed codes; arbitrary exception text and artifact bodies are not copied into the audit log.
- The synthetic pipeline regression verifies allow metadata and confirms a candidate prompt is absent from audit output. AutoSkill suite: **38/38 PASS**; syntax and diff checks PASS.
- This is staging pipeline audit evidence, not a durable consent-decision log from the live Backend/Gateway lease, which remains unimplemented.
