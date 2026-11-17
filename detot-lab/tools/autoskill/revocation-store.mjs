import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { withOwnerLockSync } from "./owner-lock.mjs";

const ID_RE = /^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,159}$/u;

function requireSafeId(value, label) {
  const id = String(value ?? "");
  if (!ID_RE.test(id)) throw new Error(`invalid_${label}`);
  return id;
}

function ensureInside(root, target) {
  const base = path.resolve(root);
  const resolved = path.resolve(target);
  try {
    if (fs.lstatSync(base).isSymbolicLink()) throw new Error("revocation_symlink_not_allowed");
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }
  const relative = path.relative(base, resolved);
  if (!relative || relative === "." || relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative))
    throw new Error("revocation_path_outside_root");
  let current = resolved;
  while (current !== base) {
    try {
      if (fs.lstatSync(current).isSymbolicLink()) throw new Error("revocation_symlink_not_allowed");
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    current = path.dirname(current);
  }
  return resolved;
}

function writeAtomic(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.${process.pid}.${crypto.randomBytes(5).toString("hex")}.tmp`;
  try {
    fs.writeFileSync(tempPath, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
    fs.renameSync(tempPath, filePath);
  } finally {
    try { if (fs.existsSync(tempPath)) fs.unlinkSync(tempPath); } catch { /* preserve the original write error */ }
  }
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function readOptionalJson(filePath) {
  try { return readJson(filePath); }
  catch (error) {
    if (error?.code === "ENOENT") return null;
    throw error;
  }
}

function ownerKey(ownerId) {
  return crypto.createHash("sha256").update(ownerId).digest("hex").slice(0, 32);
}

function journalPath(root, ownerId, revocationId) {
  return ensureInside(root, path.join(root, "state", "autoskill", "revocations", ownerKey(ownerId), `${revocationId}.json`));
}

function tombstonePath(root, ownerId) {
  return ensureInside(root, path.join(root, "state", "autoskill", "revocations", ownerKey(ownerId), "tombstones.json"));
}

function validateScope(scope) {
  if (![scope?.source, scope?.purpose, scope?.destination].every((part) => typeof part === "string" && part.length > 0 && part.length <= 128))
    throw new Error("invalid_revoked_scope");
  return { source: scope.source, purpose: scope.purpose, destination: scope.destination };
}

function validateTombstones(root, ownerId, data) {
  if (data?.schema !== 1 || data.owner_id !== ownerId || !Array.isArray(data.artifact_ids) ||
      !Array.isArray(data.relative_paths) || typeof data.owner_quarantined !== "boolean" || !Array.isArray(data.revocations))
    throw new Error("invalid_revocation_tombstones");
  if (data.artifact_ids.some((id) => typeof id !== "string" || !ID_RE.test(id)) ||
      new Set(data.artifact_ids).size !== data.artifact_ids.length)
    throw new Error("invalid_revocation_tombstones");
  const allowed = [
    path.join("state", "autoskill", "proposals", "Users", ownerId) + path.sep,
    path.join("state", "autoskill", "SkillBank", "Users", ownerId) + path.sep,
  ];
  const seenPaths = new Set();
  for (const item of data.relative_paths) {
    if (typeof item !== "string" || !item || path.isAbsolute(item)) throw new Error("invalid_revocation_tombstones");
    const absolute = path.resolve(root, item);
    const relative = path.relative(path.resolve(root), absolute);
    if (relative !== item || !allowed.some((prefix) => relative.startsWith(prefix)) || path.extname(relative).toLowerCase() !== ".json")
      throw new Error("invalid_revocation_tombstones");
    ensureInside(root, absolute);
    if (seenPaths.has(relative)) throw new Error("invalid_revocation_tombstones");
    seenPaths.add(relative);
  }
  const seenRevocations = new Set();
  for (const entry of data.revocations) {
    if (!ID_RE.test(String(entry?.revocation_id || "")) || !Number.isSafeInteger(entry?.consent_revision) || entry.consent_revision < 0 ||
        seenRevocations.has(entry.revocation_id)) throw new Error("invalid_revocation_tombstones");
    seenRevocations.add(entry.revocation_id);
  }
  return data;
}

function validateJournal(root, ownerId, journal) {
  if (!journal || journal.schema !== 1 || journal.owner_id !== ownerId || !ID_RE.test(String(journal.revocation_id || "")) ||
      !Number.isSafeInteger(journal.consent_revision) || journal.consent_revision < 0 ||
      !["prepared", "deleting", "complete"].includes(journal.status) || !Array.isArray(journal.targets) ||
      !Array.isArray(journal.completed_paths) || !Array.isArray(journal.blocked_artifact_ids) ||
      !Array.isArray(journal.blocked_relative_paths) || typeof journal.quarantine_owner !== "boolean")
    throw new Error("invalid_revocation_journal");
  validateScope(journal.revoked_scope);
  const seen = new Set();
  for (const target of journal.targets) {
    if (!ID_RE.test(String(target?.artifact_id || "")) || !Number.isSafeInteger(target?.expected_size) || target.expected_size < 0 ||
        !/^[a-f0-9]{64}$/u.test(String(target?.expected_sha256 || ""))) throw new Error("invalid_revocation_target");
    const targetPath = path.resolve(root, target.relative_path);
    const relative = path.relative(path.resolve(root), targetPath);
    if (relative.startsWith(`..${path.sep}`) || relative === ".." || path.isAbsolute(relative)) throw new Error("revocation_path_outside_root");
    const allowed = [
      path.join("state", "autoskill", "proposals", "Users", ownerId) + path.sep,
      path.join("state", "autoskill", "SkillBank", "Users", ownerId) + path.sep,
    ];
    if (!allowed.some((prefix) => relative.startsWith(prefix)) || path.extname(relative).toLowerCase() !== ".json")
      throw new Error("revocation_target_not_managed_artifact");
    ensureInside(root, targetPath);
    if (seen.has(relative)) throw new Error("duplicate_revocation_target");
    seen.add(relative);
  }
  if (journal.completed_paths.some((item) => !seen.has(item))) throw new Error("invalid_completed_revocation_target");
  if (journal.blocked_artifact_ids.some((item) => !ID_RE.test(String(item)))) throw new Error("invalid_blocked_artifact_id");
  const seenBlockedPaths = new Set();
  for (const item of journal.blocked_relative_paths) {
    if (typeof item !== "string") throw new Error("invalid_blocked_artifact_path");
    const absolute = path.resolve(root, item);
    const relative = path.relative(path.resolve(root), absolute);
    const allowed = [
      path.join("state", "autoskill", "proposals", "Users", ownerId) + path.sep,
      path.join("state", "autoskill", "SkillBank", "Users", ownerId) + path.sep,
    ];
    if (!allowed.some((prefix) => relative.startsWith(prefix)) || path.extname(relative).toLowerCase() !== ".json")
      throw new Error("blocked_path_not_managed_artifact");
    ensureInside(root, absolute);
    if (seenBlockedPaths.has(relative)) throw new Error("duplicate_blocked_artifact_path");
    seenBlockedPaths.add(relative);
  }
  return journal;
}

function writeTombstones(root, ownerId, journal) {
  const filePath = tombstonePath(root, ownerId);
  let current = { schema: 1, owner_id: ownerId, artifact_ids: [], relative_paths: [], owner_quarantined: false, revocations: [] };
  current = validateTombstones(root, ownerId, readOptionalJson(filePath) || current);
  const next = {
    schema: 1,
    owner_id: ownerId,
    artifact_ids: [...new Set([...current.artifact_ids, ...journal.blocked_artifact_ids])].sort(),
    relative_paths: [...new Set([...current.relative_paths, ...journal.blocked_relative_paths])].sort(),
    owner_quarantined: current.owner_quarantined || journal.quarantine_owner,
    revocations: [...current.revocations.filter((entry) => entry.revocation_id !== journal.revocation_id),
      { revocation_id: journal.revocation_id, consent_revision: journal.consent_revision }],
  };
  validateTombstones(root, ownerId, next);
  writeAtomic(filePath, next);
}

/** Create a durable, metadata-only journal from the deterministic policy plan. */
export function prepareRevocation(options) {
  return withOwnerLockSync(options?.root, options?.ownerId, () => prepareRevocationLocked(options));
}

function prepareRevocationLocked({ root, ownerId, consentRevision, revokedScope, plan, artifacts }) {
  const base = path.resolve(root);
  const owner = requireSafeId(ownerId, "owner_id");
  if (!Number.isSafeInteger(consentRevision) || consentRevision < 0) throw new Error("invalid_consent_revision");
  if (!plan || plan.blocked === true || !Array.isArray(plan.actions)) throw new Error("revocation_plan_blocked");
  const scope = validateScope(revokedScope);
  const planScope = validateScope(plan.revoked_scope);
  if (planScope.source !== scope.source || planScope.purpose !== scope.purpose || planScope.destination !== scope.destination)
    throw new Error("revocation_scope_mismatch");
  const records = Array.isArray(artifacts) ? artifacts : [];
  const blockingActions = plan.actions.filter((action) => ["invalidate_and_delete", "quarantine", "separate_verified_segments"].includes(action?.action));
  const actionById = new Map(blockingActions.filter((action) => action.action === "invalidate_and_delete").map((action) => [action.artifact_id, action]));
  const recordById = new Map();
  for (const artifact of records) {
    const artifactId = String(artifact?.provenance?.artifact_id || artifact?.artifact_id || "");
    if (artifactId && recordById.has(artifactId)) throw new Error("duplicate_revocation_artifact_id");
    if (artifactId) recordById.set(artifactId, artifact);
  }
  const blockedArtifactIds = [...new Set(blockingActions.map((action) => String(action.artifact_id || "")).filter((id) => ID_RE.test(id)))].sort();
  const blockedRelativePaths = [];
  let quarantineOwner = blockingActions.some((action) => !recordById.has(String(action.artifact_id || "")));
  for (const action of blockingActions) {
    const record = recordById.get(String(action.artifact_id || ""));
    if (!record || typeof record.path !== "string") continue;
    if (record.owner_id !== owner) { quarantineOwner = true; continue; }
    try {
      const absolute = ensureInside(base, path.resolve(base, record.path));
      const relative = path.relative(base, absolute);
      const allowed = [
        path.join("state", "autoskill", "proposals", "Users", owner) + path.sep,
        path.join("state", "autoskill", "SkillBank", "Users", owner) + path.sep,
      ];
      if (!allowed.some((prefix) => relative.startsWith(prefix)) || path.extname(relative).toLowerCase() !== ".json")
        quarantineOwner = true;
      else blockedRelativePaths.push(relative);
    } catch { quarantineOwner = true; }
  }
  const targets = [...actionById.keys()].sort().map((artifactId) => {
    const record = recordById.get(artifactId);
    if (!record || record.owner_id !== owner || typeof record.path !== "string") throw new Error("revocation_artifact_path_missing");
    const absolute = ensureInside(base, path.resolve(base, record.path));
    const relative = path.relative(base, absolute);
    const allowed = [
      path.join("state", "autoskill", "proposals", "Users", owner) + path.sep,
      path.join("state", "autoskill", "SkillBank", "Users", owner) + path.sep,
    ];
    if (!allowed.some((prefix) => relative.startsWith(prefix)) || path.extname(relative).toLowerCase() !== ".json")
      throw new Error("revocation_target_not_managed_artifact");
    const bytes = fs.readFileSync(absolute);
    const disk = JSON.parse(bytes.toString("utf8"));
    if (disk.owner_id !== owner || disk.provenance?.artifact_id !== artifactId) throw new Error("revocation_artifact_identity_mismatch");
    return {
      artifact_id: artifactId,
      relative_path: relative,
      expected_size: bytes.length,
      expected_sha256: crypto.createHash("sha256").update(bytes).digest("hex"),
    };
  });
  const revocationId = `revoke-${crypto.createHash("sha256").update(JSON.stringify([owner, consentRevision, scope, targets.map((target) => target.artifact_id)])).digest("hex").slice(0, 32)}`;
  const filePath = journalPath(base, owner, revocationId);
  if (fs.existsSync(filePath)) {
    const existing = validateJournal(base, owner, readJson(filePath));
    if (existing.consent_revision !== consentRevision || JSON.stringify(existing.revoked_scope) !== JSON.stringify(scope) ||
        JSON.stringify(existing.targets) !== JSON.stringify(targets)) throw new Error("revocation_id_collision");
    writeTombstones(base, owner, existing);
    return existing;
  }
  const journal = {
    schema: 1,
    revocation_id: revocationId,
    owner_id: owner,
    consent_revision: consentRevision,
    revoked_scope: scope,
    status: "prepared",
    targets,
    blocked_artifact_ids: blockedArtifactIds,
    blocked_relative_paths: [...new Set(blockedRelativePaths)].sort(),
    quarantine_owner: quarantineOwner,
    completed_paths: [],
    created_at: new Date().toISOString(),
    updated_at: new Date().toISOString(),
  };
  // The deny marker is durable before the journal can permit any cleanup work.
  writeTombstones(base, owner, journal);
  writeAtomic(filePath, journal);
  return journal;
}

/** Persist artifact tombstones before any deletion. Repeated calls safely resume after interruption. */
export function resumeRevocation(options) {
  return withOwnerLockSync(options?.root, options?.ownerId, () => resumeRevocationLocked(options));
}

function resumeRevocationLocked({ root, ownerId, revocationId, apply = false, stopAfter = Number.POSITIVE_INFINITY }) {
  const base = path.resolve(root);
  const owner = requireSafeId(ownerId, "owner_id");
  const id = requireSafeId(revocationId, "revocation_id");
  const filePath = journalPath(base, owner, id);
  if (!fs.existsSync(filePath)) throw new Error("revocation_journal_not_found");
  let journal = validateJournal(base, owner, readJson(filePath));
  if (!apply) return { ...journal, mode: "dry_run" };
  writeTombstones(base, owner, journal);
  if (journal.status === "complete") return { ...journal, mode: "already_complete" };
  journal = { ...journal, status: "deleting", updated_at: new Date().toISOString() };
  writeAtomic(filePath, journal);
  let processed = 0;
  for (const target of journal.targets) {
    if (journal.completed_paths.includes(target.relative_path)) continue;
    if (processed >= stopAfter) break;
    const absolute = ensureInside(base, path.resolve(base, target.relative_path));
    if (fs.existsSync(absolute)) {
      const bytes = fs.readFileSync(absolute);
      const actualHash = crypto.createHash("sha256").update(bytes).digest("hex");
      if (bytes.length !== target.expected_size || actualHash !== target.expected_sha256)
        throw new Error("revocation_target_changed_after_plan");
      const artifact = JSON.parse(bytes.toString("utf8"));
      if (artifact.owner_id !== owner || artifact.provenance?.artifact_id !== target.artifact_id)
        throw new Error("revocation_target_identity_mismatch");
      fs.unlinkSync(absolute);
    }
    journal = { ...journal, completed_paths: [...journal.completed_paths, target.relative_path], updated_at: new Date().toISOString() };
    writeAtomic(filePath, journal);
    processed += 1;
  }
  const complete = journal.completed_paths.length === journal.targets.length;
  if (complete) journal = { ...journal, status: "complete", completed_at: new Date().toISOString(), updated_at: new Date().toISOString() };
  writeAtomic(filePath, journal);
  return { ...journal, mode: complete ? "complete" : "interrupted" };
}

/** Tombstones are monotonic: granting consent again never makes a deleted artifact usable. */
export function isArtifactRevoked({ root, ownerId, artifactId }) {
  const owner = requireSafeId(ownerId, "owner_id");
  const filePath = tombstonePath(path.resolve(root), owner);
  const data = readOptionalJson(filePath);
  if (!data) return false;
  validateTombstones(path.resolve(root), owner, data);
  return data.owner_quarantined || data.artifact_ids.includes(String(artifactId || ""));
}

/** Check a known managed path before parsing its artifact bytes. */
export function isPathRevoked({ root, ownerId, artifactPath }) {
  const base = path.resolve(root);
  const owner = requireSafeId(ownerId, "owner_id");
  const candidate = ensureInside(base, path.resolve(base, artifactPath));
  const relative = path.relative(base, candidate);
  const filePath = tombstonePath(base, owner);
  const data = readOptionalJson(filePath);
  if (!data) return false;
  validateTombstones(base, owner, data);
  return data.owner_quarantined || data.relative_paths.includes(relative);
}
