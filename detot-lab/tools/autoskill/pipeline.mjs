import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { sanitizeSkillCandidate } from "./privacy.mjs";
import { assertArtifactUsable, evaluateArtifactPolicy } from "./governance.mjs";
import { createArtifactProvenance, createContentSegments, stableArtifactId } from "./provenance.mjs";
import { migrateLegacyArtifact } from "./migration.mjs";
import { isArtifactRevoked, isPathRevoked } from "./revocation-store.mjs";
import { withOwnerLockSync } from "./owner-lock.mjs";

const ID_RE = /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}$/u;
const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_ROOT = path.resolve(HERE, "../..");
export const DEFAULT_STAGING_ROOT = path.join(LAB_ROOT, ".local", "autoskill-staging");

function requireId(value, label) {
  const id = String(value ?? "");
  if (!ID_RE.test(id)) throw new Error(`invalid_${label}`);
  return id;
}

function ensureInside(root, target) {
  const resolvedRoot = path.resolve(root);
  const resolvedTarget = path.resolve(target);
  const rel = path.relative(resolvedRoot, resolvedTarget);
  if (!rel || rel === ".") return resolvedTarget;
  if (rel.startsWith(`..${path.sep}`) || rel === ".." || path.isAbsolute(rel)) {
    throw new Error("path_outside_staging_root");
  }
  let current = resolvedTarget;
  while (current !== resolvedRoot) {
    try {
      const stat = fs.lstatSync(current);
      if (stat.isSymbolicLink()) throw new Error("symlink_not_allowed");
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    current = path.dirname(current);
  }
  return resolvedTarget;
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJsonAtomic(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.${process.pid}.${crypto.randomBytes(5).toString("hex")}.tmp`;
  fs.writeFileSync(tempPath, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
  fs.renameSync(tempPath, filePath);
}

function appendAudit(root, ownerId, event) {
  const auditPath = ensureInside(root, path.join(root, "state", "autoskill", "audit", `${ownerId}.jsonl`));
  fs.mkdirSync(path.dirname(auditPath), { recursive: true });
  fs.appendFileSync(auditPath, `${JSON.stringify({ at: new Date().toISOString(), owner_id: ownerId, ...event })}\n`, "utf8");
}

function assertArtifactUsableForPipeline(root, ownerId, artifact, operation) {
  try {
    const artifactId = String(artifact?.provenance?.artifact_id || artifact?.artifact_id || "");
    if (isArtifactRevoked({ root, ownerId, artifactId })) throw new Error("artifact_revoked_by_consent");
    return assertArtifactUsable(artifact);
  }
  catch (error) {
    appendAudit(root, ownerId, {
      action: "artifact_policy_denied",
      artifact_id: String(artifact?.provenance?.artifact_id || artifact?.artifact_id || ""),
      proposal_id: String(artifact?.proposal_id || artifact?.source_proposal_id || ""),
      operation,
      reason: String(error?.message || "artifact_not_usable").slice(0, 120),
    });
    throw error;
  }
}

function assertArtifactPathReadable(root, ownerId, artifactPath, operation) {
  try {
    if (isPathRevoked({ root, ownerId, artifactPath })) throw new Error("artifact_revoked_by_consent");
  } catch (error) {
    appendAudit(root, ownerId, {
      action: "artifact_policy_denied",
      artifact_id: "",
      operation,
      reason: String(error?.message || "revocation_state_unavailable").slice(0, 120),
    });
    throw error;
  }
}

function proposalPath(root, ownerId, proposalId) {
  return ensureInside(root, path.join(root, "state", "autoskill", "proposals", "Users", ownerId, `${proposalId}.json`));
}

function activePath(root, ownerId, skillId) {
  return ensureInside(root, path.join(root, "state", "autoskill", "active", ownerId, `${skillId}.json`));
}

function versionPath(root, ownerId, skillId, version) {
  return ensureInside(root, path.join(root, "state", "autoskill", "SkillBank", "Users", ownerId, skillId, "versions", `${version}.json`));
}

function mirrorPaths(root, skillId) {
  const dir = ensureInside(root, path.join(root, "state", "workspace", "skills", `autoskill-${skillId}`));
  return {
    dir,
    marker: ensureInside(root, path.join(dir, ".detot-autoskill-managed.json")),
    skill: ensureInside(root, path.join(dir, "SKILL.md")),
  };
}

function copyProvenance({ ownerId, skillId, version, artifactType, sourceProvenance, dependencyIds }) {
  const sourceIds = Array.isArray(sourceProvenance?.source_ids) ? sourceProvenance.source_ids : [];
  const provenance = createArtifactProvenance({
    artifactId: stableArtifactId(artifactType, ownerId, skillId, version),
    artifactType,
    sourceIds,
    dependencyIds,
    provenanceType: sourceProvenance?.provenance_type || "unknown",
    consent: sourceProvenance?.consent_scope || null,
    validationStatus: "replay_passed",
    activationStatus: "active",
    retentionState: "retained",
  });
  if (sourceProvenance?.independence_evidence?.verified === true) {
    provenance.independence_evidence = {
      verified: true,
      source_ids: [...sourceProvenance.independence_evidence.source_ids],
    };
  }
  return provenance;
}

function activePointer(ownerId, skillId, snapshot, at, rollback = false) {
  const versionArtifactId = snapshot?.provenance?.artifact_id;
  if (!versionArtifactId) throw new Error("active_version_provenance_missing");
  return {
    owner_id: ownerId,
    skill_id: skillId,
    version: snapshot.version,
    skill_hash: snapshot.skill_hash,
    updated_at: at,
    ...(rollback ? { rollback: true } : {}),
    provenance: copyProvenance({
      ownerId,
      skillId,
      version: snapshot.version,
      artifactType: "active_pointer",
      sourceProvenance: snapshot.provenance,
      dependencyIds: [versionArtifactId],
    }),
    content_segment_hashes: (snapshot.content_segments || []).map((segment) => ({
      segment_id: segment.segment_id,
      content_hash: segment.content_hash,
    })),
  };
}

function writeManagedMirror(root, ownerId, skillId, snapshot) {
  const paths = mirrorPaths(root, skillId);
  if (fs.existsSync(paths.dir)) {
    if (!fs.existsSync(paths.marker)) throw new Error("refuse_to_overwrite_unmanaged_skill_directory");
    const marker = readJson(paths.marker);
    if (marker.managed_by !== "detot-autoskill-pipeline" || marker.owner_id !== ownerId || marker.skill_id !== skillId)
      throw new Error("managed_skill_owner_mismatch");
  } else {
    fs.mkdirSync(paths.dir, { recursive: true });
  }
  const temp = `${paths.skill}.${process.pid}.tmp`;
  fs.writeFileSync(temp, snapshot.skill_markdown, { flag: "wx" });
  fs.renameSync(temp, paths.skill);
  const sourceProvenance = snapshot.provenance;
  const mirrorProvenance = copyProvenance({
    ownerId,
    skillId,
    version: snapshot.version,
    artifactType: "runtime_mirror",
    sourceProvenance,
    dependencyIds: [sourceProvenance.artifact_id],
  });
  const mirrorSegments = createContentSegments({
    artifactId: mirrorProvenance.artifact_id,
    values: { skill_markdown: snapshot.skill_markdown },
    sourceIds: mirrorProvenance.source_ids,
    dependencyIds: mirrorProvenance.dependency_ids,
    provenanceType: mirrorProvenance.provenance_type,
    consent: mirrorProvenance.consent_scope,
  }).map((segment) => ({
    ...segment,
    validation_status: "replay_passed",
    activation_status: "active",
    ...(mirrorProvenance.independence_evidence ? { independence_evidence: mirrorProvenance.independence_evidence } : {}),
  }));
  writeJsonAtomic(paths.marker, {
    managed_by: "detot-autoskill-pipeline",
    owner_id: ownerId,
    skill_id: skillId,
    version: snapshot.version,
    skill_hash: snapshot.skill_hash,
    provenance: mirrorProvenance,
    content_segments: mirrorSegments,
  });
}

function assertMirrorWritable(root, ownerId, skillId) {
  const paths = mirrorPaths(root, skillId);
  if (!fs.existsSync(paths.dir)) return;
  if (!fs.existsSync(paths.marker)) throw new Error("refuse_to_overwrite_unmanaged_skill_directory");
  const marker = readJson(paths.marker);
  if (marker.managed_by !== "detot-autoskill-pipeline" || marker.owner_id !== ownerId || marker.skill_id !== skillId)
    throw new Error("managed_skill_owner_mismatch");
  const active = readActive(root, ownerId, skillId);
  if (!active) {
    if (fs.existsSync(paths.skill)) throw new Error("orphaned_managed_skill_requires_recovery");
    return;
  }
  if (marker.version !== active.pointer.version || marker.skill_hash !== active.pointer.skill_hash ||
      marker.provenance?.artifact_type !== "runtime_mirror" ||
      marker.provenance.artifact_id !== stableArtifactId("runtime_mirror", ownerId, skillId, active.snapshot.version) ||
      marker.provenance.activation_status !== "active" || marker.provenance.retention_state !== "retained" ||
      marker.provenance.provenance_type !== active.snapshot.provenance.provenance_type ||
      marker.provenance.consent_revision !== active.snapshot.provenance.consent_revision ||
      JSON.stringify(marker.provenance.consent_scope) !== JSON.stringify(active.snapshot.provenance.consent_scope) ||
      marker.provenance.validation_status !== "replay_passed" ||
      !marker.provenance.dependency_ids?.includes(active.snapshot.provenance.artifact_id) ||
      JSON.stringify(marker.provenance.source_ids) !== JSON.stringify(active.snapshot.provenance.source_ids) ||
      !Array.isArray(marker.content_segments) || marker.content_segments.length !== 1 ||
      marker.content_segments[0].artifact_id !== marker.provenance.artifact_id ||
      marker.content_segments[0].content_hash !== hash(active.snapshot.skill_markdown) ||
      marker.content_segments[0].activation_status !== "active" || marker.content_segments[0].retention_state !== "retained" ||
      marker.content_segments[0].provenance_type !== marker.provenance.provenance_type ||
      JSON.stringify(marker.content_segments[0].source_ids) !== JSON.stringify(marker.provenance.source_ids) ||
      JSON.stringify(marker.content_segments[0].dependency_ids) !== JSON.stringify(marker.provenance.dependency_ids))
    throw new Error("managed_skill_provenance_mismatch");
  if (!fs.existsSync(paths.skill) || hash(fs.readFileSync(paths.skill, "utf8")) !== active.snapshot.skill_hash) {
    throw new Error("active_skill_mirror_changed_outside_pipeline");
  }
}

function skillIdFor(candidate) {
  const raw = String(candidate?.skill_id || candidate?.name || "").trim().toLowerCase();
  const id = raw.replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 72);
  if (!ID_RE.test(id)) throw new Error("candidate_missing_valid_skill_name");
  return id;
}

function hash(value) {
  return crypto.createHash("sha256").update(typeof value === "string" ? value : JSON.stringify(value)).digest("hex");
}

function readActive(root, ownerId, skillId) {
  const pointerPath = activePath(root, ownerId, skillId);
  assertArtifactPathReadable(root, ownerId, pointerPath, "read_active_pointer");
  if (!fs.existsSync(pointerPath)) return null;
  const pointer = readJson(pointerPath);
  if (pointer.owner_id !== ownerId || pointer.skill_id !== skillId || typeof pointer.version !== "string")
    throw new Error("active_pointer_owner_or_skill_mismatch");
  const snapshotPath = versionPath(root, ownerId, skillId, pointer.version);
  assertArtifactPathReadable(root, ownerId, snapshotPath, "read_active_version");
  const snapshot = readJson(snapshotPath);
  if (snapshot.owner_id !== ownerId || snapshot.skill_id !== skillId || snapshot.version !== pointer.version ||
      snapshot.skill_hash !== pointer.skill_hash || !snapshot.provenance?.artifact_id)
    throw new Error("active_version_identity_mismatch");
  assertArtifactUsableForPipeline(root, ownerId, snapshot, "read_active_version");
  const pointerProvenance = pointer.provenance;
  if (pointerProvenance?.artifact_type !== "active_pointer" || pointerProvenance.activation_status !== "active" ||
      pointerProvenance.retention_state !== "retained" ||
      pointerProvenance.artifact_id !== stableArtifactId("active_pointer", ownerId, skillId, snapshot.version) ||
      pointerProvenance.provenance_type !== snapshot.provenance.provenance_type ||
      pointerProvenance.consent_revision !== snapshot.provenance.consent_revision ||
      JSON.stringify(pointerProvenance.consent_scope) !== JSON.stringify(snapshot.provenance.consent_scope) ||
      pointerProvenance.validation_status !== "replay_passed" ||
      !pointerProvenance.dependency_ids?.includes(snapshot.provenance.artifact_id) ||
      JSON.stringify(pointerProvenance.source_ids) !== JSON.stringify(snapshot.provenance.source_ids) ||
      !Array.isArray(pointer.content_segment_hashes) || pointer.content_segment_hashes.length !== (snapshot.content_segments || []).length ||
      pointer.content_segment_hashes.some((item, index) => item.segment_id !== snapshot.content_segments[index].segment_id ||
        item.content_hash !== snapshot.content_segments[index].content_hash))
    throw new Error("active_pointer_provenance_mismatch");
  return { pointer, snapshot, snapshotPath };
}

function nextVersion(current) {
  if (!current) return "1.0.0";
  const match = String(current).match(/^(\d+)\.(\d+)\.(\d+)$/u);
  if (!match) throw new Error("invalid_active_skill_version");
  return `${match[1]}.${match[2]}.${Number(match[3]) + 1}`;
}

function buildSkillMarkdown(candidate, skillId, version) {
  const frontmatterName = `autoskill-${skillId}`;
  const displayName = String(candidate.name || skillId).replace(/[\r\n"']/gu, " ").trim().slice(0, 120);
  const safeDescription = String(candidate.description || "Reusable approved skill.").replace(/[\r\n"']/gu, " ").trim().slice(0, 150);
  const prompt = String(candidate.prompt || "").trim();
  if (!prompt || prompt.length > 12000) throw new Error("invalid_skill_prompt");
  return [
    "---",
    `name: ${frontmatterName}`,
    `description: "${safeDescription}"`,
    "---",
    "",
    `<!-- AutoSkill version ${version} -->`,
    "",
    `# ${displayName}`,
    "",
    prompt,
    "",
  ].join("\n");
}

export function listProposals(options = {}) {
  const root = path.resolve(options.root || DEFAULT_STAGING_ROOT);
  const ownerId = requireId(options.ownerId || "detot-owner", "owner_id");
  return withOwnerLockSync(root, ownerId, () => listProposalsLocked({ root, ownerId }));
}

function listProposalsLocked({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner" } = {}) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const ownerRoot = ensureInside(stagingRoot, path.join(stagingRoot, "state", "autoskill", "proposals", "Users", owner));
  if (!fs.existsSync(ownerRoot)) return [];
  return fs.readdirSync(ownerRoot).filter((name) => name.endsWith(".json")).sort().map((name) => {
    const artifactPath = ensureInside(stagingRoot, path.join(ownerRoot, name));
    try { assertArtifactPathReadable(stagingRoot, owner, artifactPath, "list_proposals"); }
    catch (error) { if (error?.message === "artifact_revoked_by_consent") return null; throw error; }
    const proposal = readJson(artifactPath);
    return {
      proposal_id: proposal.proposal_id,
      status: proposal.status,
      version: proposal.version,
      created_at: proposal.created_at,
      skill_name: proposal.candidate?.name || "",
    };
  }).filter(Boolean);
}

export function evaluateProposal(options = {}) {
  const root = path.resolve(options.root || DEFAULT_STAGING_ROOT);
  const ownerId = requireId(options.ownerId || "detot-owner", "owner_id");
  return withOwnerLockSync(root, ownerId, () => evaluateProposalLocked({ ...options, root, ownerId }));
}

function evaluateProposalLocked({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", proposalId, report }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const id = requireId(proposalId, "proposal_id");
  const filePath = proposalPath(stagingRoot, owner, id);
  assertArtifactPathReadable(stagingRoot, owner, filePath, "evaluate_proposal");
  const proposal = readJson(filePath);
  if (proposal.owner_id !== owner || proposal.proposal_id !== id) throw new Error("proposal_owner_or_id_mismatch");
  assertArtifactUsableForPipeline(stagingRoot, owner, proposal, "evaluate_proposal");
  if (!["pending_review", "evaluated"].includes(proposal.status)) throw new Error("proposal_not_evaluable");
  const sanitized = sanitizeSkillCandidate(proposal.candidate);
  if (!sanitized.safe) throw new Error(`unsafe_candidate:${sanitized.reasons.join(",")}`);
  if (!report || typeof report !== "object" || !Array.isArray(report.cases)) throw new Error("invalid_replay_report");
  const cases = report.cases;
  const caseIds = new Set(cases.map((item) => String(item?.case_id || "")));
  if (cases.length < 3 || caseIds.size !== cases.length || caseIds.has("")) throw new Error("replay_requires_three_unique_cases");
  const expectedCandidateHash = hash(sanitized.candidate);
  if (report.candidate_hash !== expectedCandidateHash) throw new Error("replay_candidate_hash_mismatch");
  const isScore = (value) => typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 1;
  for (const item of cases) {
    if (!isScore(item.candidate_score) || !isScore(item.baseline_score)) throw new Error("invalid_replay_score");
    if (item.safety !== "pass") throw new Error("replay_safety_failed");
    if (item.candidate_score < item.baseline_score) throw new Error("candidate_regresses_case");
  }
  const candidateMean = cases.reduce((sum, item) => sum + item.candidate_score, 0) / cases.length;
  const baselineMean = cases.reduce((sum, item) => sum + item.baseline_score, 0) / cases.length;
  if (!(candidateMean > baselineMean)) throw new Error("candidate_not_better_than_baseline");
  const skillId = skillIdFor(proposal.candidate);
  const active = readActive(stagingRoot, owner, skillId);
  const currentVersion = active?.pointer.version || null;
  const currentHash = active?.snapshot.skill_hash || "";
  if (report.baseline_version !== currentVersion || report.baseline_hash !== currentHash) {
    throw new Error("replay_baseline_is_stale");
  }
  const evaluation = {
    replay_id: String(report.replay_id || crypto.randomUUID()),
    runner: String(report.runner || "external-replay-runner"),
    case_count: cases.length,
    candidate_mean: Number(candidateMean.toFixed(4)),
    baseline_mean: Number(baselineMean.toFixed(4)),
    candidate_hash: expectedCandidateHash,
    baseline_version: currentVersion,
    baseline_hash: currentHash,
    safety: "pass",
    outcome: "pass",
    evaluated_at: new Date().toISOString(),
  };
  proposal.status = "evaluated";
  proposal.evaluation = evaluation;
  proposal.history = [...(Array.isArray(proposal.history) ? proposal.history : []), { action: "evaluated", ...evaluation }];
  writeJsonAtomic(filePath, proposal);
  appendAudit(stagingRoot, owner, { action: "proposal_evaluated", proposal_id: id, skill_id: skillId, replay_id: evaluation.replay_id, outcome: "pass", case_count: cases.length });
  return evaluation;
}

export function approveProposal(options = {}) {
  const root = path.resolve(options.root || DEFAULT_STAGING_ROOT);
  const ownerId = requireId(options.ownerId || "detot-owner", "owner_id");
  return withOwnerLockSync(root, ownerId, () => approveProposalLocked({ ...options, root, ownerId }));
}

function approveProposalLocked({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", proposalId }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const id = requireId(proposalId, "proposal_id");
  const filePath = proposalPath(stagingRoot, owner, id);
  assertArtifactPathReadable(stagingRoot, owner, filePath, "approve_proposal");
  const proposal = readJson(filePath);
  if (proposal.owner_id !== owner || proposal.proposal_id !== id) throw new Error("proposal_owner_or_id_mismatch");
  assertArtifactUsableForPipeline(stagingRoot, owner, proposal, "approve_proposal");
  if (proposal.status !== "evaluated" || proposal.evaluation?.outcome !== "pass") throw new Error("passing_replay_required");
  const safe = sanitizeSkillCandidate(proposal.candidate);
  if (!safe.safe || hash(safe.candidate) !== proposal.evaluation.candidate_hash) throw new Error("candidate_changed_after_replay");
  const skillId = skillIdFor(safe.candidate);
  const active = readActive(stagingRoot, owner, skillId);
  const currentVersion = active?.pointer.version || null;
  const currentHash = active?.snapshot.skill_hash || "";
  if (currentVersion !== proposal.evaluation.baseline_version || currentHash !== proposal.evaluation.baseline_hash) {
    throw new Error("active_skill_changed_after_replay");
  }
  const version = nextVersion(currentVersion);
  assertMirrorWritable(stagingRoot, owner, skillId);
  const markdown = buildSkillMarkdown(safe.candidate, skillId, version);
  const versionArtifactId = stableArtifactId("skill_version", owner, skillId, version);
  const sourceProvenance = proposal.provenance;
  const snapshot = {
    owner_id: owner,
    skill_id: skillId,
    version,
    skill_hash: hash(markdown),
    skill_markdown: markdown,
    source: proposal.source || "autoskill-openclaw-adapter",
    source_proposal_id: id,
    provenance: {
      ...sourceProvenance,
      artifact_id: versionArtifactId,
      artifact_type: "skill_version",
      dependency_ids: [...new Set([...(sourceProvenance.dependency_ids || []), sourceProvenance.artifact_id].filter(Boolean))],
      validation_status: "replay_passed",
      activation_status: "active",
    },
    content_segments: (proposal.content_segments || []).map((segment) => ({
      ...segment,
      segment_id: stableArtifactId("segment", versionArtifactId, segment.segment_id || ""),
      artifact_id: versionArtifactId,
      dependency_ids: [...new Set([...(segment.dependency_ids || []), sourceProvenance.artifact_id].filter(Boolean))],
      validation_status: "replay_passed",
      activation_status: "active",
    })),
    evidence: proposal.evidence || {},
    evaluation: proposal.evaluation,
    activated_at: new Date().toISOString(),
  };
  const snapshotFile = versionPath(stagingRoot, owner, skillId, version);
  if (fs.existsSync(snapshotFile)) throw new Error("skill_version_already_exists");
  writeJsonAtomic(snapshotFile, snapshot);
  writeJsonAtomic(activePath(stagingRoot, owner, skillId), activePointer(owner, skillId, snapshot, snapshot.activated_at));
  writeManagedMirror(stagingRoot, owner, skillId, snapshot);
  proposal.status = "approved";
  proposal.history = [...proposal.history, { action: "approved_and_activated", version, at: snapshot.activated_at }];
  writeJsonAtomic(filePath, proposal);
  appendAudit(stagingRoot, owner, { action: "skill_activated", proposal_id: id, skill_id: skillId, version, skill_hash: snapshot.skill_hash });
  return { skill_id: skillId, version, skill_hash: snapshot.skill_hash, path: snapshotFile };
}

export function rejectProposal(options = {}) {
  const root = path.resolve(options.root || DEFAULT_STAGING_ROOT);
  const ownerId = requireId(options.ownerId || "detot-owner", "owner_id");
  return withOwnerLockSync(root, ownerId, () => rejectProposalLocked({ ...options, root, ownerId }));
}

function rejectProposalLocked({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", proposalId, reason = "review_rejected" }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const id = requireId(proposalId, "proposal_id");
  const filePath = proposalPath(stagingRoot, owner, id);
  const proposal = readJson(filePath);
  if (proposal.owner_id !== owner || proposal.proposal_id !== id) throw new Error("proposal_owner_or_id_mismatch");
  if (["approved", "rejected"].includes(proposal.status)) throw new Error("proposal_already_finalized");
  const at = new Date().toISOString();
  const safeReason = ["review_rejected", "duplicate", "low_quality", "privacy_risk", "safety_failure", "obsolete"].includes(String(reason))
    ? String(reason)
    : "review_rejected";
  proposal.status = "rejected";
  proposal.history = [...(Array.isArray(proposal.history) ? proposal.history : []), { action: "rejected", reason: safeReason, at }];
  writeJsonAtomic(filePath, proposal);
  appendAudit(stagingRoot, owner, { action: "proposal_rejected", proposal_id: id, reason: safeReason });
  return { proposal_id: id, status: proposal.status };
}

export function rollbackSkill(options = {}) {
  const root = path.resolve(options.root || DEFAULT_STAGING_ROOT);
  const ownerId = requireId(options.ownerId || "detot-owner", "owner_id");
  return withOwnerLockSync(root, ownerId, () => rollbackSkillLocked({ ...options, root, ownerId }));
}

function rollbackSkillLocked({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", skillId, version }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const skill = requireId(skillId, "skill_id");
  const targetVersion = requireId(version, "version");
  const targetPath = versionPath(stagingRoot, owner, skill, targetVersion);
  assertArtifactPathReadable(stagingRoot, owner, targetPath, "rollback_skill");
  const target = readJson(targetPath);
  if (target.owner_id !== owner || target.skill_id !== skill || target.version !== targetVersion) throw new Error("rollback_target_mismatch");
  assertArtifactUsableForPipeline(stagingRoot, owner, target, "rollback_skill");
  assertMirrorWritable(stagingRoot, owner, skill);
  const at = new Date().toISOString();
  writeJsonAtomic(activePath(stagingRoot, owner, skill), activePointer(owner, skill, target, at, true));
  writeManagedMirror(stagingRoot, owner, skill, target);
  appendAudit(stagingRoot, owner, { action: "skill_rollback", skill_id: skill, version: targetVersion, skill_hash: target.skill_hash });
  return { skill_id: skill, version: targetVersion, skill_hash: target.skill_hash, rolled_back: true };
}

/** Create a fresh, inactive proposal from explicitly supplied verified independent source artifacts. */
export function prepareSafeSkillRebuild(options = {}) {
  const root = path.resolve(options.root || DEFAULT_STAGING_ROOT);
  const ownerId = requireId(options.ownerId || "detot-owner", "owner_id");
  return withOwnerLockSync(root, ownerId, () => prepareSafeSkillRebuildLocked({ ...options, root, ownerId }));
}

function prepareSafeSkillRebuildLocked({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", candidate, sourceArtifacts }) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  if (!Array.isArray(sourceArtifacts) || sourceArtifacts.length === 0 || sourceArtifacts.length > 32)
    throw new Error("independent_rebuild_sources_required");

  const sourceIds = [];
  const sourceHashes = [];
  for (const source of sourceArtifacts) {
    const provenance = source?.provenance;
    const sourceId = String(provenance?.artifact_id || "");
    if (source.owner_id !== owner || !ID_RE.test(sourceId)) throw new Error("independent_rebuild_source_owner_or_id_invalid");
    if (!new Set(["source_document", "source_code", "operational_rule"]).has(provenance.artifact_type))
      throw new Error("independent_rebuild_source_type_denied");
    if (typeof source.content !== "string" || !source.content.trim() || source.content.length > 1_000_000 ||
        source.content_hash !== hash(source.content)) throw new Error("independent_rebuild_source_hash_invalid");
    if (provenance.consent_scope !== null || provenance.consent_revision !== null ||
        !Array.isArray(provenance.dependency_ids) || provenance.dependency_ids.length !== 0)
      throw new Error("independent_rebuild_source_has_consent_or_dependencies");
    if (isArtifactRevoked({ root: stagingRoot, ownerId: owner, artifactId: sourceId }))
      throw new Error("independent_rebuild_source_revoked");
    const policy = evaluateArtifactPolicy(source);
    if (policy.decision !== "INDEPENDENT" || policy.action !== "retain")
      throw new Error(`independent_rebuild_source_not_verified:${policy.reason}`);
    sourceIds.push(sourceId);
    sourceHashes.push({ artifact_id: sourceId, content_hash: source.content_hash });
  }
  if (new Set(sourceIds).size !== sourceIds.length) throw new Error("duplicate_independent_rebuild_source");

  const supplied = candidate && typeof candidate === "object" && !Array.isArray(candidate) ? candidate : {};
  // Project only user-visible skill fields. Incoming provenance, history, files and prior approval state are ignored.
  const seed = {
    name: String(supplied.name || "").trim(),
    description: String(supplied.description || "").trim(),
    prompt: String(supplied.prompt || "").trim(),
    files: {},
  };
  const sanitized = sanitizeSkillCandidate(seed);
  if (!sanitized.safe) throw new Error(`unsafe_rebuild_candidate:${sanitized.reasons.join(",")}`);
  const sourceContent = sourceArtifacts.map((source) => source.content);
  for (const field of ["name", "description", "prompt"]) {
    const value = sanitized.candidate[field];
    if (value && !sourceContent.some((content) => content.includes(value)))
      throw new Error("rebuild_candidate_text_not_verbatim_in_independent_source");
  }
  const baseSkillId = skillIdFor({ name: seed.name });
  const rebuiltSkillId = `rebuilt-${baseSkillId}-${crypto.randomBytes(4).toString("hex")}`.slice(0, 95);
  const rebuiltCandidate = { ...sanitized.candidate, skill_id: rebuiltSkillId };
  const proposalId = crypto.randomUUID();
  const artifactId = stableArtifactId("rebuild_proposal", owner, proposalId);
  const provenance = {
    ...createArtifactProvenance({
      artifactId,
      artifactType: "proposal",
      sourceIds,
      dependencyIds: sourceIds,
      provenanceType: "independent",
      validationStatus: "pending_review",
      activationStatus: "inactive",
      retentionState: "retained",
    }),
    independence_evidence: { verified: true, source_ids: sourceIds },
  };
  const contentSegments = createContentSegments({
    artifactId,
    values: { candidate: rebuiltCandidate },
    sourceIds,
    dependencyIds: sourceIds,
    provenanceType: "independent",
  }).map((segment) => ({
    ...segment,
    independence_evidence: { verified: true, source_ids: sourceIds },
  }));
  const proposal = {
    proposal_id: proposalId,
    owner_id: owner,
    status: "pending_review",
    version: "0.1.0",
    created_at: new Date().toISOString(),
    source: "detot-safe-skill-rebuild",
    evidence: {
      rebuild: true,
      source_artifact_ids: sourceIds,
      source_hashes: sourceHashes,
      candidate_hash: hash(rebuiltCandidate),
    },
    provenance,
    content_segments: contentSegments,
    candidate: rebuiltCandidate,
    history: [{ action: "safe_rebuild_drafted", source_count: sourceIds.length }],
  };
  const filePath = proposalPath(stagingRoot, owner, proposalId);
  if (fs.existsSync(filePath)) throw new Error("rebuild_proposal_id_collision");
  writeJsonAtomic(filePath, proposal);
  appendAudit(stagingRoot, owner, {
    action: "skill_rebuild_drafted",
    proposal_id: proposalId,
    artifact_id: artifactId,
    source_ids: sourceIds,
    source_count: sourceIds.length,
    candidate_hash: proposal.evidence.candidate_hash,
  });
  return { proposal_id: proposalId, skill_id: rebuiltSkillId, status: proposal.status, artifact_id: artifactId, path: filePath };
}

function collectLegacyArtifactPaths(root, ownerId) {
  const proposals = ensureInside(root, path.join(root, "state", "autoskill", "proposals", "Users", ownerId));
  const versions = ensureInside(root, path.join(root, "state", "autoskill", "SkillBank", "Users", ownerId));
  const found = [];
  const walk = (directory, kind) => {
    if (!fs.existsSync(directory)) return;
    const entries = fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name));
    for (const entry of entries) {
      const target = ensureInside(root, path.join(directory, entry.name));
      if (entry.isSymbolicLink()) throw new Error("symlink_not_allowed");
      if (entry.isDirectory()) walk(target, kind);
      else if (entry.isFile() && entry.name.endsWith(".json")) found.push({ path: target, kind });
    }
  };
  walk(proposals, "proposal");
  if (fs.existsSync(versions)) {
    for (const entry of fs.readdirSync(versions, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      if (!entry.isDirectory()) continue;
      const skillDir = ensureInside(root, path.join(versions, entry.name));
      if (entry.isSymbolicLink()) throw new Error("symlink_not_allowed");
      walk(ensureInside(root, path.join(skillDir, "versions")), "skill_version");
    }
  }
  return found;
}

/** Dry-runs by default; apply only adds UNKNOWN/quarantine provenance to known owner-scoped records. */
export function migrateLegacyArtifacts(options = {}) {
  const root = path.resolve(options.root || DEFAULT_STAGING_ROOT);
  const ownerId = requireId(options.ownerId || "detot-owner", "owner_id");
  return withOwnerLockSync(root, ownerId, () => migrateLegacyArtifactsLocked({ ...options, root, ownerId }));
}

function migrateLegacyArtifactsLocked({ root = DEFAULT_STAGING_ROOT, ownerId = "detot-owner", apply = false } = {}) {
  const stagingRoot = path.resolve(root);
  const owner = requireId(ownerId, "owner_id");
  const files = collectLegacyArtifactPaths(stagingRoot, owner);
  const plan = [];
  for (const file of files) {
    try {
      const artifact = readJson(file.path);
      if (artifact.owner_id !== owner) {
        plan.push({ artifact_id: stableArtifactId("untrusted_legacy_artifact", owner, path.relative(stagingRoot, file.path)), action: "quarantine", reason: "owner_mismatch" });
        continue;
      }
      if (artifact.provenance && typeof artifact.provenance === "object") {
        plan.push({ artifact_id: String(artifact.provenance.artifact_id || stableArtifactId("classified_artifact", owner, path.relative(stagingRoot, file.path))), action: "retain_metadata", reason: "provenance_present" });
        continue;
      }
      const key = file.kind === "proposal"
        ? artifact.proposal_id || path.basename(file.path, ".json")
        : `${artifact.skill_id || "unknown"}:${artifact.version || path.basename(file.path, ".json")}`;
      const migrated = migrateLegacyArtifact({ artifact, ownerId: owner, artifactType: file.kind, artifactKey: key });
      plan.push({ artifact_id: migrated.artifact.provenance.artifact_id, action: "quarantine", reason: migrated.reason, path: file.path, migrated });
    } catch (error) {
      plan.push({ artifact_id: stableArtifactId("unreadable_legacy_artifact", owner, path.relative(stagingRoot, file.path)), action: "quarantine", reason: "record_unreadable" });
    }
  }
  const blocked = plan.some((item) => item.reason === "owner_mismatch" || item.reason === "record_unreadable");
  const eligible = plan.filter((item) => item.reason === "legacy_provenance_quarantined");
  if (apply && !blocked) {
    for (const item of eligible) writeJsonAtomic(item.path, item.migrated.artifact);
    appendAudit(stagingRoot, owner, { action: "legacy_artifacts_quarantined", artifact_count: eligible.length });
  }
  return {
    mode: apply ? "apply" : "dry_run",
    scanned: files.length,
    quarantined: eligible.length,
    already_classified: plan.filter((item) => item.reason === "provenance_present").length,
    blocked,
    artifacts: plan.map(({ artifact_id, action, reason }) => ({ artifact_id, action, reason })),
    changed: apply && !blocked ? eligible.length : 0,
  };
}

function parseArgs(args) {
  const out = { _: [] };
  for (let i = 0; i < args.length; i += 1) {
    const item = args[i];
    if (!item.startsWith("--")) { out._.push(item); continue; }
    const key = item.slice(2);
    out[key] = args[++i];
  }
  return out;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const root = path.resolve(args.root || DEFAULT_STAGING_ROOT);
  if (root !== path.resolve(DEFAULT_STAGING_ROOT)) throw new Error("cli_is_restricted_to_default_staging_root");
  const ownerId = args.owner || "detot-owner";
  const command = args._[0];
  let result;
  if (command === "list") result = listProposals({ root, ownerId });
  else if (command === "evaluate") result = evaluateProposal({ root, ownerId, proposalId: args.proposal, report: readJson(path.resolve(args.report)) });
  else if (command === "approve") result = approveProposal({ root, ownerId, proposalId: args.proposal });
  else if (command === "reject") result = rejectProposal({ root, ownerId, proposalId: args.proposal, reason: args.reason || "review_rejected" });
  else if (command === "rollback") result = rollbackSkill({ root, ownerId, skillId: args.skill, version: args.version });
  else if (command === "migrate-legacy") result = migrateLegacyArtifacts({ root, ownerId, apply: String(args.apply || "").toLowerCase() === "true" });
  else throw new Error("usage: node pipeline.mjs <list|evaluate|approve|reject|rollback|migrate-legacy> [options]");
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
