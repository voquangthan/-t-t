import assert from "node:assert/strict";
import test from "node:test";
import {
  createArtifactProvenance,
  createContentSegments,
  normalizeConsentGrant,
  stableArtifactId,
} from "../tools/autoskill/provenance.mjs";

const localRequirement = { source: "chat_history", purpose: "skill_learning", destination: "local_only" };
const modelRequirement = { source: "chat_history", purpose: "skill_learning", destination: "DeepSeek" };

test("consent grants require the exact stage scope and a monotonic revision", () => {
  assert.equal(normalizeConsentGrant(true, "capture"), null);
  assert.equal(normalizeConsentGrant({ allowed: true, stage: "capture", revision: 3, requirements: [] }, "capture"), null);
  assert.equal(normalizeConsentGrant({ allowed: true, stage: "analysis", revision: 3,
    requirements: [localRequirement, modelRequirement] }, "capture"), null);
  assert.equal(normalizeConsentGrant({ allowed: true, stage: "analysis", revision: -1,
    requirements: [localRequirement, modelRequirement] }, "analysis"), null);
  const grant = normalizeConsentGrant({ allowed: true, stage: "analysis", revision: 4,
    requirements: [localRequirement, modelRequirement] }, "analysis");
  assert.equal(grant.revision, 4);
  assert.deepEqual(grant.requirements, [localRequirement, modelRequirement]);
});

test("provenance records opaque source/dependency links and unknown data stays unknown", () => {
  const sourceId = stableArtifactId("conversation", "synthetic-owner", "synthetic-session");
  assert.match(sourceId, /^conversation:[a-f0-9]{32}$/u);
  assert.doesNotMatch(sourceId, /synthetic-session/u);
  const provenance = createArtifactProvenance({
    artifactId: "proposal:synthetic",
    artifactType: "proposal",
    sourceIds: [sourceId],
    dependencyIds: [sourceId],
    provenanceType: "conversation",
    consent: { revision: 4, requirements: [localRequirement, modelRequirement] },
    validationStatus: "pending_review",
    activationStatus: "inactive",
    retentionState: "retained",
  });
  assert.equal(provenance.provenance_type, "conversation");
  assert.equal(provenance.consent_revision, 4);
  assert.deepEqual(provenance.source_ids, [sourceId]);
  assert.equal(createArtifactProvenance({ artifactType: "legacy" }).provenance_type, "unknown");
});

test("content segments link hashed text fields to exact provenance without duplicating their contents", () => {
  const sourceId = stableArtifactId("conversation", "synthetic-owner", "synthetic-session");
  const consent = { revision: 2, requirements: [localRequirement] };
  const segments = createContentSegments({
    artifactId: "proposal:synthetic",
    values: { prompt: "Check the synthetic copy.", triggers: ["file audit"] },
    sourceIds: [sourceId],
    dependencyIds: [sourceId],
    provenanceType: "conversation",
    consent,
  });
  assert.equal(segments.length, 2);
  assert.deepEqual(segments.map((segment) => segment.field_path), ["prompt", "triggers[0]"]);
  assert.ok(segments.every((segment) => /^[a-f0-9]{64}$/u.test(segment.content_hash)));
  assert.ok(segments.every((segment) => segment.provenance_type === "conversation"));
  assert.doesNotMatch(JSON.stringify(segments), /Check the synthetic copy/u);
});
