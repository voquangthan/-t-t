import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { approveProposal, evaluateProposal, listProposals, prepareSafeSkillRebuild, rejectProposal, rollbackSkill } from "../tools/autoskill/pipeline.mjs";
import { planConsentRevocation } from "../tools/autoskill/governance.mjs";
import { prepareRevocation, resumeRevocation } from "../tools/autoskill/revocation-store.mjs";
import { sanitizeExperienceText, sanitizeSkillCandidate } from "../tools/autoskill/privacy.mjs";

function makeStage() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "detot-autoskill-stage-"));
  return { root, owner: "detot-owner" };
}

function addProposal(stage, { proposalId, skillId, prompt, name = skillId }) {
  const ownerRoot = path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner);
  fs.mkdirSync(ownerRoot, { recursive: true });
  const proposal = {
    proposal_id: proposalId,
    owner_id: stage.owner,
    status: "pending_review",
    version: "0.1.0",
    created_at: new Date().toISOString(),
    source: "autoskill-openclaw-adapter",
    evidence: { session_id: `session-${proposalId}`, turn_count: 3, successful_main_turn: true, source_hash: "evidence-hash" },
    provenance: {
      artifact_id: `proposal:${proposalId}`,
      artifact_type: "proposal",
      source_ids: [`conversation:${proposalId}`],
      dependency_ids: [`conversation:${proposalId}`],
      provenance_type: "conversation",
      consent_scope: { revision: 1, requirements: [{ source: "chat_history", purpose: "skill_learning", destination: "local_only" }, { source: "chat_history", purpose: "skill_learning", destination: "DeepSeek" }] },
      consent_revision: 1,
      validation_status: "pending_review",
      activation_status: "inactive",
      retention_state: "retained",
    },
    content_segments: [],
    candidate: { skill_id: skillId, name, description: "Validated file workflow", prompt, files: {} },
    history: [{ action: "proposed", version: "0.1.0" }],
  };
  fs.writeFileSync(path.join(ownerRoot, `${proposalId}.json`), JSON.stringify(proposal));
}

function replayReport(stage, proposalId, candidateScores, baselineScores) {
  const skillId = "safe-file-workflow";
  const proposal = JSON.parse(fs.readFileSync(path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner, `${proposalId}.json`), "utf8"));
  const activePath = path.join(stage.root, "state", "autoskill", "active", stage.owner, `${skillId}.json`);
  const active = fs.existsSync(activePath) ? JSON.parse(fs.readFileSync(activePath, "utf8")) : null;
  return {
    replay_id: `replay-${proposalId}`,
    runner: "fixture-replay-runner",
    candidate_hash: crypto.createHash("sha256").update(JSON.stringify(proposal.candidate)).digest("hex"),
    baseline_version: active?.version || null,
    baseline_hash: active?.skill_hash || "",
    cases: candidateScores.map((candidate_score, index) => ({
      case_id: `case-${index + 1}`,
      candidate_score,
      baseline_score: baselineScores[index],
      safety: "pass",
    })),
  };
}

test("new successful procedure requires three passing replay cases and explicit approval", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-success", skillId: "safe-file-workflow", prompt: "Inspect a copy, validate the result, and report the path." });
  const report = replayReport(stage, "proposal-success", [0.9, 0.8, 1], [0, 0, 0]);
  const evaluated = evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-success", report });
  assert.equal(evaluated.outcome, "pass");
  assert.throws(() => approveProposal({ root: stage.root, ownerId: "other-owner", proposalId: "proposal-success" }), /ENOENT|no such file/i);
  const approved = approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-success" });
  assert.equal(approved.version, "1.0.0");
  const skillFile = path.join(stage.root, "state", "workspace", "skills", "autoskill-safe-file-workflow", "SKILL.md");
  assert.match(fs.readFileSync(skillFile, "utf8"), /name: autoskill-safe-file-workflow/);
  assert.match(fs.readFileSync(skillFile, "utf8"), /AutoSkill version 1\.0\.0/);
  const active = JSON.parse(fs.readFileSync(path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json"), "utf8"));
  assert.equal(active.provenance.artifact_type, "active_pointer");
  assert.equal(active.provenance.activation_status, "active");
  assert.ok(active.provenance.dependency_ids.includes(`skill_version:${crypto.createHash("sha256").update(["skill_version", stage.owner, "safe-file-workflow", "1.0.0"].join("\0")).digest("hex").slice(0, 32)}`));
  const markerPath = path.join(stage.root, "state", "workspace", "skills", "autoskill-safe-file-workflow", ".detot-autoskill-managed.json");
  const marker = JSON.parse(fs.readFileSync(markerPath, "utf8"));
  assert.equal(marker.provenance.artifact_type, "runtime_mirror");
  assert.equal(marker.provenance.activation_status, "active");
  assert.equal(marker.content_segments.length, 1);
  assert.equal(marker.content_segments[0].content_hash, crypto.createHash("sha256").update(fs.readFileSync(skillFile)).digest("hex"));
  assert.equal("content" in marker.content_segments[0], false);
  const audit = fs.readFileSync(path.join(stage.root, "state", "autoskill", "audit", `${stage.owner}.jsonl`), "utf8");
  assert.match(audit, /artifact_policy_allowed/);
  assert.match(audit, /artifact_path_allowed/);
  assert.doesNotMatch(audit, /Inspect a copy, validate the result/);
});

test("failed then corrected procedure can be proposed as a new reviewed version", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-v1", skillId: "safe-file-workflow", prompt: "Inspect the source, copy to staging, verify output." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v1", report: replayReport(stage, "proposal-v1", [0.8, 0.8, 0.8], [0, 0, 0]) });
  approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v1" });

  addProposal(stage, { proposalId: "proposal-v2", skillId: "safe-file-workflow", prompt: "Inspect the source, copy to staging, compare checksums, and verify output." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v2", report: replayReport(stage, "proposal-v2", [1, 0.9, 1], [0.8, 0.8, 0.8]) });
  assert.equal(approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-v2" }).version, "1.0.1");
  assert.equal(rollbackSkill({ root: stage.root, ownerId: stage.owner, skillId: "safe-file-workflow", version: "1.0.0" }).rolled_back, true);
  const active = JSON.parse(fs.readFileSync(path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json"), "utf8"));
  assert.equal(active.version, "1.0.0");
});

test("worse replay result is rejected and preserves the current active version", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-good", skillId: "safe-file-workflow", prompt: "Verify a staging copy before reporting completion." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-good", report: replayReport(stage, "proposal-good", [0.9, 0.9, 0.9], [0, 0, 0]) });
  approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-good" });
  const pointerPath = path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json");
  const before = fs.readFileSync(pointerPath, "utf8");

  addProposal(stage, { proposalId: "proposal-weaker", skillId: "safe-file-workflow", prompt: "Overwrite the source file directly." });
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-weaker", report: replayReport(stage, "proposal-weaker", [0.4, 0.6, 0.5], [0.9, 0.9, 0.9]) }), /candidate_regresses_case/);
  assert.equal(fs.readFileSync(pointerPath, "utf8"), before);
  assert.throws(() => approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-weaker" }), /passing_replay_required/);
});

test("privacy gate omits sensitive sessions and rejects credentials or executable skill files", () => {
  assert.equal(sanitizeExperienceText("My bank account is 1234567890123456"), "[PRIVATE_SESSION_OMITTED]");
  assert.equal(sanitizeExperienceText("api_key: sk-abcdefghijklmnopqrstuvwxyz123456"), "[REDACTED_CREDENTIAL]");
  assert.equal(sanitizeSkillCandidate({ name: "Routine", prompt: "Use token=secret-value to log in" }).safe, false);
  assert.equal(sanitizeSkillCandidate({ name: "Routine", prompt: "Check the result", files: { "scripts/run.ps1": "Remove-Item *" } }).safe, false);
});

test("a report with missing cases or safety failure cannot approve a proposal", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-safety", skillId: "safe-file-workflow", prompt: "Check before changing files." });
  const tooFew = replayReport(stage, "proposal-safety", [1, 1], [0, 0]);
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-safety", report: tooFew }), /three_unique_cases/);
  const safetyFail = replayReport(stage, "proposal-safety", [1, 1, 1], [0, 0, 0]);
  safetyFail.cases[2].safety = "fail";
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-safety", report: safetyFail }), /replay_safety_failed/);
  assert.throws(() => approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-safety" }), /passing_replay_required/);
});

test("approval refuses a candidate changed after replay and refuses unmanaged skill collisions", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-tamper", skillId: "safe-file-workflow", prompt: "Validate a staging copy." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-tamper", report: replayReport(stage, "proposal-tamper", [1, 1, 1], [0, 0, 0]) });
  const proposalPath = path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner, "proposal-tamper.json");
  const proposal = JSON.parse(fs.readFileSync(proposalPath, "utf8"));
  proposal.candidate.prompt = "Changed after replay.";
  fs.writeFileSync(proposalPath, JSON.stringify(proposal));
  assert.throws(() => approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-tamper" }), /candidate_changed_after_replay/);

  const collision = makeStage();
  addProposal(collision, { proposalId: "proposal-collision", skillId: "safe-file-workflow", prompt: "Validate a staging copy." });
  evaluateProposal({ root: collision.root, ownerId: collision.owner, proposalId: "proposal-collision", report: replayReport(collision, "proposal-collision", [1, 1, 1], [0, 0, 0]) });
  const userSkillDir = path.join(collision.root, "state", "workspace", "skills", "autoskill-safe-file-workflow");
  fs.mkdirSync(userSkillDir, { recursive: true });
  const userSkill = path.join(userSkillDir, "SKILL.md");
  fs.writeFileSync(userSkill, "user-owned skill\n");
  assert.throws(() => approveProposal({ root: collision.root, ownerId: collision.owner, proposalId: "proposal-collision" }), /refuse_to_overwrite_unmanaged_skill_directory/);
  assert.equal(fs.readFileSync(userSkill, "utf8"), "user-owned skill\n");
  assert.equal(fs.existsSync(path.join(collision.root, "state", "autoskill", "active", collision.owner, "safe-file-workflow.json")), false);
});

test("active pointer and runtime mirror reject forged lineage before rollback or promotion", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-pointer", skillId: "safe-file-workflow", prompt: "Inspect the staged copy and validate its checksum." });
  evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-pointer", report: replayReport(stage, "proposal-pointer", [1, 1, 1], [0, 0, 0]) });
  approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-pointer" });

  const pointerPath = path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json");
  const pointer = JSON.parse(fs.readFileSync(pointerPath, "utf8"));
  pointer.provenance.dependency_ids = [];
  fs.writeFileSync(pointerPath, JSON.stringify(pointer));
  assert.throws(() => rollbackSkill({ root: stage.root, ownerId: stage.owner, skillId: "safe-file-workflow", version: "1.0.0" }), /active_pointer_provenance_mismatch/);

  const fresh = makeStage();
  addProposal(fresh, { proposalId: "proposal-mirror-1", skillId: "safe-file-workflow", prompt: "Inspect the staged copy and validate its checksum." });
  evaluateProposal({ root: fresh.root, ownerId: fresh.owner, proposalId: "proposal-mirror-1", report: replayReport(fresh, "proposal-mirror-1", [1, 1, 1], [0, 0, 0]) });
  approveProposal({ root: fresh.root, ownerId: fresh.owner, proposalId: "proposal-mirror-1" });
  const markerPath = path.join(fresh.root, "state", "workspace", "skills", "autoskill-safe-file-workflow", ".detot-autoskill-managed.json");
  const marker = JSON.parse(fs.readFileSync(markerPath, "utf8"));
  marker.provenance.source_ids = [];
  fs.writeFileSync(markerPath, JSON.stringify(marker));
  addProposal(fresh, { proposalId: "proposal-mirror-2", skillId: "safe-file-workflow", prompt: "Inspect the staged copy, compare hashes, and report the verified result." });
  evaluateProposal({ root: fresh.root, ownerId: fresh.owner, proposalId: "proposal-mirror-2", report: replayReport(fresh, "proposal-mirror-2", [1, 1, 1], [0, 0, 0]) });
  assert.throws(() => approveProposal({ root: fresh.root, ownerId: fresh.owner, proposalId: "proposal-mirror-2" }), /managed_skill_provenance_mismatch/);
  const active = JSON.parse(fs.readFileSync(path.join(fresh.root, "state", "autoskill", "active", fresh.owner, "safe-file-workflow.json"), "utf8"));
  assert.equal(active.version, "1.0.0");
});

test("rejection is auditable without exposing candidate text", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-reject", skillId: "safe-file-workflow", prompt: "Sensitive text never belongs in audit." });
  rejectProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-reject", reason: "review rejected" });
  const audit = fs.readFileSync(path.join(stage.root, "state", "autoskill", "audit", `${stage.owner}.jsonl`), "utf8");
  assert.match(audit, /proposal_rejected/);
  assert.doesNotMatch(audit, /Sensitive text never belongs/);
});

test("legacy proposals without lineage remain quarantined and cannot be evaluated or activated", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-legacy", skillId: "safe-file-workflow", prompt: "A legacy candidate with no source trail." });
  const proposalPath = path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner, "proposal-legacy.json");
  const proposal = JSON.parse(fs.readFileSync(proposalPath, "utf8"));
  delete proposal.provenance;
  fs.writeFileSync(proposalPath, JSON.stringify(proposal));
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-legacy", report: replayReport(stage, "proposal-legacy", [1, 1, 1], [0, 0, 0]) }), /artifact_not_usable:provenance_missing_or_unknown/);
  assert.equal(fs.existsSync(path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json")), false);
  const audit = fs.readFileSync(path.join(stage.root, "state", "autoskill", "audit", `${stage.owner}.jsonl`), "utf8");
  assert.match(audit, /artifact_policy_denied/);
  assert.doesNotMatch(audit, /A legacy candidate with no source trail/);
});

test("revoked proposal tombstones block replay evaluation and activation", () => {
  const stage = makeStage();
  addProposal(stage, { proposalId: "proposal-revoked", skillId: "safe-file-workflow", prompt: "Must not be evaluated after revoke." });
  const proposalPath = path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner, "proposal-revoked.json");
  const proposal = JSON.parse(fs.readFileSync(proposalPath, "utf8"));
  const savedBytes = fs.readFileSync(proposalPath);
  const revokedScope = { source: "chat_history", purpose: "skill_learning", destination: "local_only" };
  const artifact = { ...proposal, artifact_id: proposal.provenance.artifact_id, path: proposalPath };
  const plan = planConsentRevocation({ artifacts: [artifact], revokedScope });
  const journal = prepareRevocation({ root: stage.root, ownerId: stage.owner, consentRevision: 2, revokedScope, plan, artifacts: [artifact] });
  assert.equal(journal.targets.length, 1);
  assert.equal(resumeRevocation({ root: stage.root, ownerId: stage.owner, revocationId: journal.revocation_id, apply: true }).mode, "complete");
  assert.equal(fs.existsSync(proposalPath), false);
  // A restored backup has the same artifact id and remains denied by the monotonic tombstone.
  fs.writeFileSync(proposalPath, savedBytes);
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-revoked", report: {} }), /artifact_revoked_by_consent/);
  assert.throws(() => approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-revoked" }), /artifact_revoked_by_consent/);
  fs.writeFileSync(proposalPath, "not-json");
  assert.throws(() => evaluateProposal({ root: stage.root, ownerId: stage.owner, proposalId: "proposal-revoked", report: {} }), /artifact_revoked_by_consent/);
  assert.deepEqual(listProposals({ root: stage.root, ownerId: stage.owner }), []);
  assert.equal(fs.existsSync(proposalPath), true);
  assert.equal(fs.existsSync(path.join(stage.root, "state", "autoskill", "active", stage.owner, "safe-file-workflow.json")), false);
  const audit = fs.readFileSync(path.join(stage.root, "state", "autoskill", "audit", `${stage.owner}.jsonl`), "utf8");
  assert.match(audit, /artifact_policy_denied/);
  assert.doesNotMatch(audit, /Must not be evaluated after revoke/);
});

function independentSource(stage, artifactId = "source-runbook-1") {
  const content = "Verified file routine\nUse a staged copy and verify the result.\nRead the approved runbook, work on a staging copy, verify the output, and report the result.\nSynthetic independent source fixture.";
  const sourceIds = [`document-${artifactId}`];
  return {
    owner_id: stage.owner,
    content,
    content_hash: crypto.createHash("sha256").update(content).digest("hex"),
    provenance: {
      artifact_id: artifactId,
      artifact_type: "source_document",
      source_ids: sourceIds,
      dependency_ids: [],
      provenance_type: "independent",
      consent_scope: null,
      consent_revision: null,
      validation_status: "validated",
      activation_status: "not_applicable",
      retention_state: "retained",
      independence_evidence: { verified: true, source_ids: sourceIds },
    },
  };
}

test("safe rebuild creates a fresh inactive proposal from verified independent sources and requires replay plus approval", () => {
  const stage = makeStage();
  const priorRevokedId = "version-revoked-old";
  const result = prepareSafeSkillRebuild({
    root: stage.root,
    ownerId: stage.owner,
    sourceArtifacts: [independentSource(stage)],
    candidate: {
      name: "Verified file routine",
      description: "Use a staged copy and verify the result.",
      prompt: "Read the approved runbook, work on a staging copy, verify the output, and report the result.",
      provenance: { artifact_id: priorRevokedId, retention_state: "retained" },
      history: [{ action: "previously_approved" }],
      files: { "scripts/run.ps1": "must never be copied" },
    },
  });
  const proposal = JSON.parse(fs.readFileSync(result.path, "utf8"));
  assert.equal(result.status, "pending_review");
  assert.equal(proposal.provenance.provenance_type, "independent");
  assert.equal(proposal.provenance.validation_status, "pending_review");
  assert.equal(proposal.provenance.activation_status, "inactive");
  assert.deepEqual(proposal.provenance.source_ids, ["source-runbook-1"]);
  assert.deepEqual(proposal.provenance.dependency_ids, ["source-runbook-1"]);
  assert.notEqual(proposal.provenance.artifact_id, priorRevokedId);
  assert.equal(proposal.candidate.files && Object.keys(proposal.candidate.files).length, 0);
  assert.equal("history" in proposal.candidate, false);
  assert.equal("provenance" in proposal.candidate, false);
  assert.equal(JSON.stringify(proposal).includes("Synthetic independent source fixture"), false);
  assert.equal(proposal.evidence.source_hashes[0].content_hash, independentSource(stage).content_hash);

  const candidateHash = crypto.createHash("sha256").update(JSON.stringify(proposal.candidate)).digest("hex");
  evaluateProposal({
    root: stage.root,
    ownerId: stage.owner,
    proposalId: result.proposal_id,
    report: {
      candidate_hash: candidateHash,
      baseline_version: null,
      baseline_hash: "",
      cases: [
        { case_id: "rebuild-normal", candidate_score: 1, baseline_score: 0, safety: "pass" },
        { case_id: "rebuild-retry", candidate_score: 0.9, baseline_score: 0, safety: "pass" },
        { case_id: "rebuild-invalid", candidate_score: 0.8, baseline_score: 0, safety: "pass" },
      ],
    },
  });
  assert.equal(approveProposal({ root: stage.root, ownerId: stage.owner, proposalId: result.proposal_id }).version, "1.0.0");
});

test("safe rebuild rejects unknown, conversation-derived, revoked and invalid independent sources", () => {
  const stage = makeStage();
  const candidate = { name: "Verified file routine", prompt: "Read the approved runbook, work on a staging copy, verify the output, and report the result." };
  const valid = independentSource(stage);
  const unknown = structuredClone(valid);
  unknown.provenance.provenance_type = "unknown";
  unknown.provenance.independence_evidence = undefined;
  assert.throws(() => prepareSafeSkillRebuild({ root: stage.root, ownerId: stage.owner, candidate, sourceArtifacts: [unknown] }), /not_verified/);

  const conversation = structuredClone(valid);
  conversation.provenance.provenance_type = "conversation";
  assert.throws(() => prepareSafeSkillRebuild({ root: stage.root, ownerId: stage.owner, candidate, sourceArtifacts: [conversation] }), /not_verified/);

  const revokedId = valid.provenance.artifact_id;
  const tombstonePlan = planConsentRevocation({
    artifacts: [{ ...valid, artifact_id: revokedId, provenance: { ...valid.provenance, provenance_type: "conversation", consent_scope: { revision: 1, requirements: [{ source: "chat_history", purpose: "skill_learning", destination: "local_only" }] }, consent_revision: 1, dependency_ids: ["conversation-source"] } }],
    revokedScope: { source: "chat_history", purpose: "skill_learning", destination: "local_only" },
  });
  const revokedRecord = { ...valid, artifact_id: revokedId, path: path.join(stage.root, "state", "autoskill", "proposals", "Users", stage.owner, "source.json"), provenance: { ...valid.provenance, provenance_type: "conversation", consent_scope: { revision: 1, requirements: [{ source: "chat_history", purpose: "skill_learning", destination: "local_only" }] }, consent_revision: 1, dependency_ids: ["conversation-source"] } };
  fs.mkdirSync(path.dirname(revokedRecord.path), { recursive: true });
  fs.writeFileSync(revokedRecord.path, JSON.stringify(revokedRecord));
  const revoke = prepareRevocation({ root: stage.root, ownerId: stage.owner, consentRevision: 2, revokedScope: tombstonePlan.revoked_scope, plan: tombstonePlan, artifacts: [revokedRecord] });
  assert.ok(revoke);
  assert.throws(() => prepareSafeSkillRebuild({ root: stage.root, ownerId: stage.owner, candidate, sourceArtifacts: [valid] }), /source_revoked/);

  const tampered = { ...independentSource(stage, "source-hash-bad"), content_hash: "0".repeat(64) };
  assert.throws(() => prepareSafeSkillRebuild({ root: stage.root, ownerId: stage.owner, candidate, sourceArtifacts: [tampered] }), /source_hash_invalid/);
});
