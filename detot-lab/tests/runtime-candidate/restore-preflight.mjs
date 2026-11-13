import { DatabaseSync } from "node:sqlite";
import { lstat, readdir, realpath, unlink } from "node:fs/promises";
import { basename, dirname, join, resolve } from "node:path";

export async function revokedSessionIds(authority) {
  const entries = await readdir(authority, { withFileTypes: true });
  return entries.filter(entry => entry.name.endsWith(".revoked")).map(entry => {
    const id = entry.name.slice(0, -8);
    if (!entry.isFile() || !/^[a-zA-Z0-9-]{1,128}$/.test(id)) throw new Error("Invalid revocation record.");
    return id;
  });
}

export async function verifyRestoreSnapshot(snapshot, authority) {
  const ids = new Set(await revokedSessionIds(authority));
  const db = new DatabaseSync(snapshot, { readOnly: true });
  try {
    const sessions = db.prepare("SELECT current_session_id FROM session_nodes").iterate();
    for (const row of sessions) {
      if (ids.has(row.current_session_id)) throw new Error("Snapshot contains a revoked session.");
    }
  } finally { db.close(); }
}

// The inventory must come from the operator or the backup writer. Unknown copies
// remain outside the scope of this operation.
export async function purgeRegisteredPlaintextCopies(root, authority, inventory) {
  const directory = await realpath(root);
  const ids = new Set(await revokedSessionIds(authority));
  const targets = new Set();
  for (const { name, sessionIds } of inventory) {
    if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]*\.(?:sqlite|zst|gz)$/.test(name) ||
        basename(name) !== name || !Array.isArray(sessionIds) || !sessionIds.length ||
        !sessionIds.every(id => typeof id === "string" && /^[a-zA-Z0-9-]{1,128}$/.test(id)))
      throw new Error("Invalid plaintext copy inventory.");
    if (!sessionIds.some(id => ids.has(id))) continue;
    targets.add(name);
    if (name.endsWith(".sqlite"))
      for (const suffix of ["-wal", "-shm", "-journal"]) targets.add(name + suffix);
  }
  const files = [];
  for (const name of targets) {
    const file = join(directory, name);
    let stat;
    try { stat = await lstat(file); }
    catch (error) { if (error.code === "ENOENT") continue; throw error; }
    if (!stat.isFile() || stat.nlink !== 1 || dirname(await realpath(file)) !== resolve(directory))
      throw new Error("Copy is not a standalone regular file in the inventory directory.");
    files.push(file);
  }
  for (const file of files) await unlink(file);
  return files.length;
}
