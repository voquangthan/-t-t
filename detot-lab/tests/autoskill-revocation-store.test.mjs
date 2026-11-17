import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { planConsentRevocation } from "../tools/autoskill/governance.mjs";
import { isArtifactRevoked, isPathRevoked, prepareRevocation, resumeRevocation } from "../tools/autoskill/revocation-store.mjs";

const revokedScope = { source: "chat_history", purpose: "skill_learning", destination: "local_only" };

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "autoskill-revoke-")).replaceAll("\\", "/");
  const ownerId = "synthetic-owner";
  const conversation = {
    owner_id: ownerId,
    provenance: { artifact_id: "conversation-1", provenance_type: "conversation", source_ids: ["conversation-1"], dependency_ids: [], consent_scope: { revision: 7, requirements: [revokedScope] }, retention_state: "retained" },
  };
  const proposal = {
    owner_id: ownerId,
    proposal_id: "proposal-1",
    candidate: { name: "synthetic", prompt: "Synthetic private-derived body" },
    provenance: { artifact_id: "proposal-1", provenance_type: "conversation", source_ids: ["conversation-1"], dependency_ids: ["conversation-1"], consent_scope: { revision: 7, requirements: [revokedScope] }, retention_state: "retained" },
  };
  const version = {
    owner_id: ownerId,
    skill_id: "synthetic",
    version: "1.0.0",
    skill_markdown: "Synthetic private-derived body",
    provenance: { artifact_id: "version-1", provenance_type: "conversation", source_ids: ["conversation-1"], dependency_ids: ["proposal-1"], consent_scope: { revision: 7, requirements: [revokedScope] }, retention_state: "retained" },
  };
  const proposalPath = path.join(root, "state/autoskill/proposals/Users", ownerId, "proposal-1.json");
  const versionPath = path.join(root, "state/autoskill/SkillBank/Users", ownerId, "synthetic/versions/1.0.0.json");
  fs.mkdirSync(path.dirname(proposalPath), { recursive: true });
  fs.mkdirSync(path.dirname(versionPath), { recursive: true });
  fs.writeFileSync(proposalPath, JSON.stringify(proposal));
  fs.writeFileSync(versionPath, JSON.stringify(version));
  const artifacts = [
    { ...proposal, artifact_id: "proposal-1", path: proposalPath },
    { ...version, artifact_id: "version-1", path: versionPath },
  ];
  const plan = planConsentRevocation({ artifacts, revokedScope });
  return { root, ownerId, artifacts, plan, proposalPath, versionPath };
}

function tombstonePath(root, ownerId) {
  const key = crypto.createHash("sha256").update(ownerId).digest("hex").slice(0, 32);
  return path.join(root, "state", "autoskill", "revocations", key, "tombstones.json");
}

test("revocation tombstones before idempotent deletion and resumes after interruption", () => {
  const f = fixture();
  try {
    const journal = prepareRevocation({ root: f.root, ownerId: f.ownerId, consentRevision: 8, revokedScope, plan: f.plan, artifacts: f.artifacts });
    assert.equal(journal.status, "prepared");
    assert.equal(journal.targets.length, 2);
    assert.ok(!JSON.stringify(journal).includes("Synthetic private-derived body"));
    const preview = resumeRevocation({ root: f.root, ownerId: f.ownerId, revocationId: journal.revocation_id });
    assert.equal(preview.mode, "dry_run");
    assert.equal(isArtifactRevoked({ root: f.root, ownerId: f.ownerId, artifactId: "proposal-1" }), true);
    assert.ok(fs.existsSync(f.proposalPath));

    const interrupted = resumeRevocation({ root: f.root, ownerId: f.ownerId, revocationId: journal.revocation_id, apply: true, stopAfter: 1 });
    assert.equal(interrupted.mode, "interrupted");
    assert.equal(interrupted.completed_paths.length, 1);
    assert.equal(isArtifactRevoked({ root: f.root, ownerId: f.ownerId, artifactId: "proposal-1" }), true);
    assert.equal(isArtifactRevoked({ root: f.root, ownerId: f.ownerId, artifactId: "version-1" }), true);
    assert.equal(isArtifactRevoked({ root: f.root, ownerId: "other-owner", artifactId: "proposal-1" }), false);
    assert.equal(isPathRevoked({ root: f.root, ownerId: f.ownerId, artifactPath: f.proposalPath }), true);
    assert.equal(fs.existsSync(f.proposalPath), false);
    assert.equal(fs.existsSync(f.versionPath), true);

    const moduleUrl = new URL("../tools/autoskill/revocation-store.mjs", import.meta.url).href;
    const recoveryScript = `import { resumeRevocation } from ${JSON.stringify(moduleUrl)}; process.stdout.write(JSON.stringify(resumeRevocation(${JSON.stringify({ root: f.root, ownerId: f.ownerId, revocationId: journal.revocation_id, apply: true })})));`;
    const recovery = spawnSync(process.execPath, ["--input-type=module", "-e", recoveryScript], { encoding: "utf8" });
    assert.equal(recovery.status, 0, recovery.stderr);
    const resumed = JSON.parse(recovery.stdout);
    assert.equal(resumed.mode, "complete");
    assert.equal(fs.existsSync(f.versionPath), false);
    const repeated = resumeRevocation({ root: f.root, ownerId: f.ownerId, revocationId: journal.revocation_id, apply: true });
    assert.equal(repeated.mode, "already_complete");
    // A later consent revision does not erase artifact tombstones or restore deleted bytes.
    assert.equal(isArtifactRevoked({ root: f.root, ownerId: f.ownerId, artifactId: "proposal-1" }), true);
  } finally { fs.rmSync(f.root, { recursive: true, force: true }); }
});

test("revocation refuses changed artifact bytes and leaves the target intact", () => {
  const f = fixture();
  try {
    const journal = prepareRevocation({ root: f.root, ownerId: f.ownerId, consentRevision: 8, revokedScope, plan: f.plan, artifacts: f.artifacts });
    fs.appendFileSync(f.proposalPath, "changed");
    assert.throws(() => resumeRevocation({ root: f.root, ownerId: f.ownerId, revocationId: journal.revocation_id, apply: true }), /revocation_target_changed_after_plan/);
    assert.equal(fs.existsSync(f.proposalPath), true);
    assert.equal(isArtifactRevoked({ root: f.root, ownerId: f.ownerId, artifactId: "proposal-1" }), true);
  } finally { fs.rmSync(f.root, { recursive: true, force: true }); }
});

test("revocation refuses targets outside the owner managed artifact directories", () => {
  const f = fixture();
  try {
    const bad = f.artifacts.map((artifact) => artifact.artifact_id === "proposal-1"
      ? { ...artifact, path: path.join(f.root, "state/autoskill/proposals/Users/other-owner/proposal-1.json") }
      : artifact);
    assert.throws(() => prepareRevocation({ root: f.root, ownerId: f.ownerId, consentRevision: 8, revokedScope, plan: f.plan, artifacts: bad }), /revocation_target_not_managed_artifact/);
  } finally { fs.rmSync(f.root, { recursive: true, force: true }); }
});

test("policy-quarantined or retained artifacts are never added to deletion targets", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "autoskill-revoke-safe-")).replaceAll("\\", "/");
  try {
    const ownerId = "synthetic-owner";
    const artifact = { owner_id: ownerId, provenance: { artifact_id: "unknown-1", provenance_type: "unknown", source_ids: [], dependency_ids: [], consent_scope: null, retention_state: "quarantined" } };
    const plan = planConsentRevocation({ artifacts: [artifact], revokedScope });
    const journal = prepareRevocation({ root, ownerId, consentRevision: 8, revokedScope, plan, artifacts: [artifact] });
    assert.deepEqual(journal.targets, []);
    assert.equal(isArtifactRevoked({ root, ownerId, artifactId: "unknown-1" }), true);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("unidentified provenance quarantines the owner instead of allowing untraceable artifacts", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "autoskill-revoke-unknown-")).replaceAll("\\", "/");
  try {
    const ownerId = "synthetic-owner";
    const plan = planConsentRevocation({ artifacts: [{}], revokedScope });
    const journal = prepareRevocation({ root, ownerId, consentRevision: 8, revokedScope, plan, artifacts: [] });
    assert.equal(journal.quarantine_owner, true);
    assert.equal(isArtifactRevoked({ root, ownerId, artifactId: "otherwise-independent-skill" }), true);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("malformed tombstones fail closed for artifact and path lookups", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "autoskill-revoke-corrupt-")).replaceAll("\\", "/");
  const ownerId = "synthetic-owner";
  const filePath = tombstonePath(root, ownerId);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  try {
    fs.writeFileSync(filePath, JSON.stringify({
      schema: 1, owner_id: ownerId, artifact_ids: [{}], relative_paths: [], owner_quarantined: false, revocations: [],
    }));
    assert.throws(() => isArtifactRevoked({ root, ownerId, artifactId: "artifact-1" }), /invalid_revocation_tombstones/);

    fs.writeFileSync(filePath, JSON.stringify({
      schema: 1, owner_id: ownerId, artifact_ids: [], relative_paths: ["../outside/proposal.json"], owner_quarantined: false, revocations: [],
    }));
    assert.throws(() => isPathRevoked({ root, ownerId, artifactPath: path.join(root, "state/autoskill/proposals/Users", ownerId, "proposal.json") }), /invalid_revocation_tombstones/);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
