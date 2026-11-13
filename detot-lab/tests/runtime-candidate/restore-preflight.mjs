import { DatabaseSync } from "node:sqlite";
import { lstat, readFile, readdir, realpath, unlink } from "node:fs/promises";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { gunzipSync, zstdDecompressSync } from "node:zlib";

const limit = 64 * 1024 * 1024;
const sqlite = /\.(?:sqlite3?|db)$/i;
const archive = /\.(?:zst|gz)$/i;
const unsupported = /\.(?:zip|7z|rar|tar|tgz|bak|backup)$/i;
const sidecar = /(?:-wal|-shm|-journal)$/;

function within(root, path) {
  const rel = relative(root, path);
  return rel === "" || (!isAbsolute(rel) && rel !== ".." && !rel.startsWith(".." + sep));
}

async function scan(root, authority, copiesOnly) {
  root = await realpath(root);
  authority = await realpath(authority);
  if (within(root, authority) || within(authority, root))
    throw new Error("The revocation authority must be external.");
  const ids = await revokedSessionIds(authority);
  const matches = [];
  let inspected = 0;
  const contains = bytes => ids.some(id => bytes.includes(Buffer.from(id)) ||
    bytes.includes(Buffer.from(id, "utf16le")) || bytes.includes(Buffer.from(id, "utf16le").swap16()));
  async function walk(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const file = join(directory, entry.name);
      if (entry.isSymbolicLink() || !within(root, await realpath(file)))
        throw new Error("Unexpected link in scan root.");
      if (entry.isDirectory()) { await walk(file); continue; }
      if (!entry.isFile()) throw new Error("Unexpected file type in scan root.");
      if (unsupported.test(entry.name)) throw new Error("Unsupported backup format: " + relative(root, file));
      if (copiesOnly && !sqlite.test(entry.name) && !archive.test(entry.name) && !sidecar.test(entry.name))
        throw new Error("Unclassified file in backup root: " + relative(root, file));
      const stat = await lstat(file);
      if (stat.size > limit) throw new Error("File exceeds scan limit: " + relative(root, file));
      let bytes = await readFile(file);
      if (archive.test(file)) bytes = (file.toLowerCase().endsWith(".gz") ? gunzipSync : zstdDecompressSync)
        (bytes, { maxOutputLength: limit });
      if (sqlite.test(file) && !bytes.subarray(0, 16).equals(Buffer.from("SQLite format 3\0")))
        throw new Error("Invalid SQLite copy: " + relative(root, file));
      inspected++;
      if (contains(Buffer.from(relative(root, file))) || contains(bytes)) {
        matches.push(file);
      } else if (archive.test(file) && !/^Session ID: [a-zA-Z0-9-]{1,128}\r?$/m.test(bytes.toString("utf8"))) {
        throw new Error("Archive has no verifiable session reference: " + relative(root, file));
      }
    }
  }
  await walk(root);
  return { root, matches, inspected };
}

export async function verifyRestoreState(state, authority) {
  const result = await scan(state, authority, false);
  if (result.matches.length) throw new Error("Snapshot contains a revoked session.");
  return result.inspected;
}

export async function purgeConfiguredCopies(roots, authority, { apply = false, log = () => {} } = {}) {
  if (!Array.isArray(roots) || !roots.length || !roots.every(isAbsolute))
    throw new Error("Configure at least one absolute backup directory.");
  const scans = await Promise.all(roots.map(root => scan(root, authority, true)));
  for (const scan of scans) log({ action: "inventory", root: scan.root, inspected: scan.inspected, matches: scan.matches.length });
  if (!apply) return;
  const targets = new Set();
  for (const result of scans) for (const file of result.matches) {
    const base = file.replace(sidecar, "");
    targets.add(base);
    if (sqlite.test(base)) for (const suffix of ["-wal", "-shm", "-journal"]) targets.add(base + suffix);
  }
  for (const file of targets) {
    const stat = await lstat(file).catch(error => { if (error.code !== "ENOENT") throw error; });
    if (stat && (!stat.isFile() || stat.nlink !== 1 || await realpath(file) !== file))
      throw new Error("Refusing to purge a linked copy.");
  }
  for (const file of targets) {
    try { await unlink(file); log({ action: "purged", file }); }
    catch (error) { if (error.code !== "ENOENT") throw error; }
  }
  for (const { root } of scans) {
    const result = await scan(root, authority, true);
    if (result.matches.length) throw new Error("Revoked references remain in backup root.");
    log({ action: "verified", root, inspected: result.inspected });
  }
}

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
