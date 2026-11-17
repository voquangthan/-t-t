import assert from "node:assert/strict";
import test from "node:test";
import { assertArtifactUsable, evaluateArtifactPolicy, planConsentRevocation } from "../tools/autoskill/governance.mjs";

const revokedScope = { source: "chat_history", purpose: "skill_learning", destination: "local_only" };
const source = (id, type, overrides = {}) => ({
  artifact_id: id,
  provenance_type: type,
  source_ids: [id],
  dependency_ids: [],
  consent_scope: { revision: 3, requirements: [revokedScope] },
  consent_revision: 3,
  retention_state: "retained",
  ...overrides,
});

test("unverified independence and unknown legacy artifacts are quarantined", () => {
  assert.equal(evaluateArtifactPolicy({ provenance: source("skill-a", "independent") }).action, "quarantine");
  assert.throws(() => assertArtifactUsable({}), /artifact_not_usable/);
});

test("verified independent skills survive a revoked conversation scope", () => {
  const independent = source("skill-independent", "independent", {
    source_ids: ["docs:public-runbook"],
    consent_scope: null,
    consent_revision: null,
    independence_evidence: { verified: true, source_ids: ["docs:public-runbook"] },
  });
  const plan = planConsentRevocation({ artifacts: [{ provenance: independent }], revokedScope });
  assert.deepEqual(plan.actions, [{ artifact_id: "skill-independent", action: "retain", reason: "verified_independent_source" }]);
});

test("an artifact with an explicit dependency on revoked data is invalidated even if tagged independent", () => {
  const conversation = source("conversation-r", "conversation");
  const dependent = source("skill-claimed-independent", "independent", {
    source_ids: ["docs:public-runbook"],
    consent_scope: null,
    consent_revision: null,
    dependency_ids: ["conversation-r"],
    independence_evidence: { verified: true, source_ids: ["docs:public-runbook"] },
  });
  const plan = planConsentRevocation({ artifacts: [{ provenance: conversation }, { provenance: dependent }], revokedScope });
  assert.equal(plan.actions.find((item) => item.artifact_id === dependent.artifact_id).action, "invalidate_and_delete");
});

test("revocation traverses proposal to version and quarantines unknown descendants", () => {
  const conversation = source("conversation-1", "conversation");
  const proposal = source("proposal-1", "conversation", { source_ids: ["conversation-1"], dependency_ids: ["conversation-1"] });
  const version = source("version-1", "conversation", { source_ids: ["conversation-1"], dependency_ids: ["proposal-1"] });
  const unknown = source("cache-legacy", "unknown", { source_ids: [], dependency_ids: ["version-1"] });
  const plan = planConsentRevocation({ artifacts: [
    { provenance: conversation }, { provenance: proposal }, { provenance: version }, { provenance: unknown },
  ], revokedScope });
  assert.deepEqual(plan.impacted_artifact_ids, ["cache-legacy", "conversation-1", "proposal-1", "version-1"]);
  assert.equal(plan.actions.find((item) => item.artifact_id === "version-1").action, "invalidate_and_delete");
  assert.equal(plan.actions.find((item) => item.artifact_id === "cache-legacy").action, "quarantine");
  assert.equal(plan.requires_review, true);
  assert.deepEqual(planConsentRevocation({ artifacts: [
    { provenance: conversation }, { provenance: proposal }, { provenance: version }, { provenance: unknown },
  ], revokedScope }), plan);
});

test("revocation plan quarantines unidentified artifacts and blocks ambiguous duplicate IDs", () => {
  const missing = planConsentRevocation({ artifacts: [{}], revokedScope });
  assert.equal(missing.actions[0].action, "quarantine");
  assert.equal(missing.actions[0].reason, "artifact_id_missing");
  const duplicate = source("same-id", "conversation");
  const blocked = planConsentRevocation({ artifacts: [{ provenance: duplicate }, { provenance: duplicate }], revokedScope });
  assert.equal(blocked.blocked, true);
  assert.deepEqual(blocked.actions.map((item) => item.action), ["quarantine", "quarantine"]);
  assert.deepEqual(blocked.impacted_artifact_ids, []);
});

test("mixed artifacts separate only hash backed independently verified segments", () => {
  const mixed = {
    provenance: source("mixed-1", "mixed"),
    content_segments: [
      { segment_id: "safe-segment", content_hash: "hash", provenance_type: "independent", source_ids: ["docs:1"], independence_evidence: { verified: true, source_ids: ["docs:1"] } },
      { segment_id: "unverified-segment", content_hash: "hash", provenance_type: "independent" },
    ],
  };
  assert.deepEqual(evaluateArtifactPolicy(mixed).segment_ids, ["safe-segment"]);
  assert.equal(evaluateArtifactPolicy({ provenance: source("mixed-2", "mixed") }).action, "quarantine");
});

test("revocation excludes mixed segments linked to revoked sources or dependencies", () => {
  const conversation = source("conversation-mixed", "conversation");
  const mixed = {
    provenance: source("mixed-revoke", "mixed", { source_ids: ["conversation-mixed"] }),
    content_segments: [
      { segment_id: "public-safe", content_hash: "hash-a", provenance_type: "independent", source_ids: ["docs:public"], dependency_ids: [], independence_evidence: { verified: true, source_ids: ["docs:public"] } },
      { segment_id: "linked-by-source", content_hash: "hash-b", provenance_type: "independent", source_ids: ["conversation-mixed"], dependency_ids: [], independence_evidence: { verified: true, source_ids: ["conversation-mixed"] } },
      { segment_id: "linked-by-dependency", content_hash: "hash-c", provenance_type: "independent", source_ids: ["docs:derived"], dependency_ids: ["conversation-mixed"], independence_evidence: { verified: true, source_ids: ["docs:derived"] } },
      { segment_id: "linked-by-consent", content_hash: "hash-d", provenance_type: "independent", source_ids: ["docs:other"], dependency_ids: [], consent_scope: { revision: 3, requirements: [revokedScope] }, independence_evidence: { verified: true, source_ids: ["docs:other"] } },
    ],
  };
  const plan = planConsentRevocation({ artifacts: [{ provenance: conversation }, mixed], revokedScope });
  assert.deepEqual(plan.actions.find((item) => item.artifact_id === "mixed-revoke"), {
    artifact_id: "mixed-revoke",
    action: "separate_verified_segments",
    reason: "revoked_mixed_source_separate_verified_segments",
    segment_ids: ["public-safe"],
  });
});

test("mixed artifact is quarantined when every independently labeled segment has revoked lineage", () => {
  const conversation = source("conversation-only", "conversation");
  const mixed = {
    provenance: source("mixed-only", "mixed", { source_ids: ["conversation-only"] }),
    content_segments: [
      { segment_id: "linked", content_hash: "hash", provenance_type: "independent", source_ids: ["docs:public"], dependency_ids: ["conversation-only"], independence_evidence: { verified: true, source_ids: ["docs:public"] } },
    ],
  };
  const plan = planConsentRevocation({ artifacts: [{ provenance: conversation }, mixed], revokedScope });
  assert.equal(plan.actions.find((item) => item.artifact_id === "mixed-only").action, "quarantine");
  assert.equal(plan.actions.find((item) => item.artifact_id === "mixed-only").reason, "no_independent_segments_outside_revoked_lineage");
});
