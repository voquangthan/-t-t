import assert from "node:assert/strict";
import test from "node:test";
import { migrateLegacyArtifact } from "../tools/autoskill/migration.mjs";
import { assertArtifactUsable } from "../tools/autoskill/governance.mjs";
import { migrateLegacyArtifacts } from "../tools/autoskill/pipeline.mjs";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

test("legacy artifacts keep their content and history while becoming quarantined UNKNOWN", () => {
  const legacy = {
    proposal_id: "legacy-proposal-1",
    owner_id: "synthetic-owner",
    candidate: { name: "Synthetic checklist", prompt: "Verify a copy and report the result." },
    history: [{ action: "proposed", at: "2020-01-01T00:00:00Z" }],
  };
  const result = migrateLegacyArtifact({ artifact: legacy, ownerId: "synthetic-owner", artifactType: "proposal", artifactKey: "legacy-proposal-1" });
  assert.equal(result.changed, true);
  assert.deepEqual(result.artifact.candidate, legacy.candidate);
  assert.deepEqual(result.artifact.history, legacy.history);
  assert.equal(result.artifact.provenance.provenance_type, "unknown");
  assert.equal(result.artifact.provenance.retention_state, "quarantined");
  assert.equal(result.artifact.provenance.activation_status, "blocked");
  assert.ok(result.artifact.content_segments.length > 0);
  assert.ok(result.artifact.content_segments.every((segment) => segment.provenance_type === "unknown" && segment.retention_state === "quarantined"));
  assert.throws(() => assertArtifactUsable(result.artifact), /artifact_not_usable/);
  assert.equal(migrateLegacyArtifact({ artifact: result.artifact, ownerId: "synthetic-owner", artifactType: "proposal", artifactKey: "legacy-proposal-1" }).changed, false);
});

test("legacy segment content is preserved but its unsupported source claims are quarantined", () => {
  const legacy = { content_segments: [{ segment_id: "old-segment", content: "synthetic segment", provenance_type: "independent",
    independence_evidence: { verified: true, source_ids: ["unverified"] } }] };
  const result = migrateLegacyArtifact({ artifact: legacy, ownerId: "synthetic-owner", artifactType: "skill_version", artifactKey: "legacy-version-1" });
  assert.equal(result.artifact.content_segments[0].content, "synthetic segment");
  assert.equal(result.artifact.content_segments[0].provenance_type, "unknown");
  assert.equal(result.artifact.content_segments[0].independence_evidence.verified, true);
  assert.equal(result.artifact.content_segments[0].retention_state, "quarantined");
  assert.equal(result.artifact.content_segments[0].source_ids.length, 0);
});

test("staging migration dry-runs first, preserves content, and is restart-idempotent", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-autoskill-migrate-"));
  const owner = "synthetic-owner";
  const proposalDir = path.join(root, "state", "autoskill", "proposals", "Users", owner);
  const versionDir = path.join(root, "state", "autoskill", "SkillBank", "Users", owner, "synthetic-skill", "versions");
  fs.mkdirSync(proposalDir, { recursive: true });
  fs.mkdirSync(versionDir, { recursive: true });
  const proposalPath = path.join(proposalDir, "old-proposal.json");
  const versionPath = path.join(versionDir, "1.0.0.json");
  const proposal = { owner_id: owner, proposal_id: "old-proposal", candidate: { name: "Synthetic", prompt: "Use a safe copy." }, history: [{ action: "proposed" }] };
  const version = { owner_id: owner, skill_id: "synthetic-skill", version: "1.0.0", skill_markdown: "Synthetic old knowledge." };
  fs.writeFileSync(proposalPath, JSON.stringify(proposal));
  fs.writeFileSync(versionPath, JSON.stringify(version));

  const preview = migrateLegacyArtifacts({ root, ownerId: owner });
  assert.equal(preview.mode, "dry_run");
  assert.equal(preview.quarantined, 2);
  assert.equal(JSON.parse(fs.readFileSync(proposalPath, "utf8")).provenance, undefined);
  const applied = migrateLegacyArtifacts({ root, ownerId: owner, apply: true });
  assert.equal(applied.changed, 2);
  for (const [file, original, contentField] of [[proposalPath, proposal, "candidate"], [versionPath, version, "skill_markdown"]]) {
    const migrated = JSON.parse(fs.readFileSync(file, "utf8"));
    assert.deepEqual(migrated[contentField], original[contentField]);
    assert.equal(migrated.provenance.provenance_type, "unknown");
    assert.equal(migrated.provenance.retention_state, "quarantined");
    assert.equal(migrated.provenance.activation_status, "blocked");
  }
  const retried = migrateLegacyArtifacts({ root, ownerId: owner, apply: true });
  assert.equal(retried.changed, 0);
  assert.equal(retried.already_classified, 2);
  const audit = fs.readFileSync(path.join(root, "state", "autoskill", "audit", `${owner}.jsonl`), "utf8");
  assert.match(audit, /legacy_artifacts_quarantined/);
  assert.doesNotMatch(audit, /Synthetic old knowledge|Use a safe copy/);
});

test("migration is owner-isolated and refuses writes when an owner mismatch is found", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-autoskill-owner-migrate-"));
  const owner = "synthetic-owner";
  const ownerDir = path.join(root, "state", "autoskill", "proposals", "Users", owner);
  fs.mkdirSync(ownerDir, { recursive: true });
  const mismatchPath = path.join(ownerDir, "mismatch.json");
  fs.writeFileSync(mismatchPath, JSON.stringify({ owner_id: "other-owner", proposal_id: "mismatch", candidate: { prompt: "synthetic" } }));
  const otherDir = path.join(root, "state", "autoskill", "proposals", "Users", "other-owner");
  fs.mkdirSync(otherDir, { recursive: true });
  fs.writeFileSync(path.join(otherDir, "other.json"), JSON.stringify({ owner_id: "other-owner", proposal_id: "other" }));
  const result = migrateLegacyArtifacts({ root, ownerId: owner, apply: true });
  assert.equal(result.scanned, 1);
  assert.equal(result.blocked, true);
  assert.equal(result.changed, 0);
  assert.equal(JSON.parse(fs.readFileSync(mismatchPath, "utf8")).provenance, undefined);
  assert.equal(JSON.parse(fs.readFileSync(path.join(otherDir, "other.json"), "utf8")).provenance, undefined);
});
