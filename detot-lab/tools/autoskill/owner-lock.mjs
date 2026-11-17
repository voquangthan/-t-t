import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

function assertNoSymlink(base, target) {
  let current = target;
  while (current !== base) {
    try {
      if (fs.lstatSync(current).isSymbolicLink()) throw new Error("autoskill_lock_symlink_not_allowed");
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    current = path.dirname(current);
  }
  try {
    if (fs.lstatSync(base).isSymbolicLink()) throw new Error("autoskill_lock_symlink_not_allowed");
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }
}

function lockPaths(root, ownerId) {
  if (typeof ownerId !== "string" || !ownerId.trim() || ownerId.length > 256) throw new Error("invalid_owner_id");
  const ownerKey = crypto.createHash("sha256").update(ownerId).digest("hex").slice(0, 32);
  const base = path.resolve(root);
  const directory = path.join(base, "state", "autoskill", "locks");
  assertNoSymlink(base, directory);
  fs.mkdirSync(directory, { recursive: true });
  assertNoSymlink(base, directory);
  const lock = path.join(directory, `${ownerKey}.lock`);
  return { lock, recovery: `${lock}.recovering` };
}

function parseLock(filePath) {
  let value;
  try {
    if (fs.lstatSync(filePath).isSymbolicLink()) throw new Error("autoskill_owner_lock_symlink");
    value = JSON.parse(fs.readFileSync(filePath, "utf8"));
  }
  catch { throw new Error("autoskill_owner_lock_invalid"); }
  if (value?.schema !== 1 || !Number.isSafeInteger(value.pid) || value.pid < 1 ||
      typeof value.token !== "string" || !/^[a-f0-9-]{36}$/u.test(value.token))
    throw new Error("autoskill_owner_lock_invalid");
  return value;
}

function processIsAlive(pid) {
  try { process.kill(pid, 0); return true; }
  catch (error) { return error?.code !== "ESRCH"; }
}

function createLock(filePath, token) {
  const descriptor = fs.openSync(filePath, "wx", 0o600);
  try {
    fs.writeFileSync(descriptor, `${JSON.stringify({ schema: 1, pid: process.pid, token, created_at: new Date().toISOString() })}\n`, "utf8");
    fs.fsyncSync(descriptor);
  } finally { fs.closeSync(descriptor); }
}

function claimOrRecover(paths, token) {
  if (fs.existsSync(paths.recovery)) throw new Error("autoskill_owner_recovery_in_progress");
  try {
    createLock(paths.lock, token);
    if (fs.existsSync(paths.recovery)) {
      const held = parseLock(paths.lock);
      if (held.token === token) fs.unlinkSync(paths.lock);
      throw new Error("autoskill_owner_recovery_in_progress");
    }
    return;
  } catch (error) {
    if (error?.code !== "EEXIST") throw error;
  }

  const observed = parseLock(paths.lock);
  if (processIsAlive(observed.pid)) throw new Error("autoskill_owner_operation_in_progress");
  const recoveryToken = crypto.randomUUID();
  let recoveryDescriptor;
  try { recoveryDescriptor = fs.openSync(paths.recovery, "wx", 0o600); }
  catch (error) {
    if (error?.code === "EEXIST") throw new Error("autoskill_owner_recovery_in_progress");
    throw error;
  }
  try {
    fs.writeFileSync(recoveryDescriptor, `${JSON.stringify({ schema: 1, pid: process.pid, token: recoveryToken })}\n`, "utf8");
    fs.fsyncSync(recoveryDescriptor);
    fs.closeSync(recoveryDescriptor);
    recoveryDescriptor = undefined;

    const latest = parseLock(paths.lock);
    if (latest.token !== observed.token || processIsAlive(latest.pid))
      throw new Error("autoskill_owner_operation_in_progress");
    fs.unlinkSync(paths.lock);
    try { createLock(paths.lock, token); }
    catch (error) {
      if (error?.code === "EEXIST") throw new Error("autoskill_owner_operation_in_progress");
      throw error;
    }
  } finally {
    if (recoveryDescriptor !== undefined) fs.closeSync(recoveryDescriptor);
    try {
      const markerPath = fs.lstatSync(paths.recovery);
      if (!markerPath.isSymbolicLink()) {
        const marker = JSON.parse(fs.readFileSync(paths.recovery, "utf8"));
        if (marker?.token === recoveryToken) fs.unlinkSync(paths.recovery);
      }
    } catch { /* leave malformed recovery markers in place to fail closed */ }
  }
}

/** Serialize staging writes per owner; a dead-process lock is reclaimed under a recovery marker. */
export function withOwnerLockSync(root, ownerId, operation) {
  if (typeof operation !== "function") throw new Error("owner_lock_operation_required");
  const paths = lockPaths(root, ownerId);
  const token = crypto.randomUUID();
  claimOrRecover(paths, token);
  try {
    const result = operation();
    if (result && typeof result.then === "function") throw new Error("owner_lock_operation_must_be_synchronous");
    return result;
  } finally {
    try {
      const held = parseLock(paths.lock);
      if (held.token === token) fs.unlinkSync(paths.lock);
    } catch { /* never remove a lock not owned by this operation */ }
  }
}
