import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { withOwnerLockSync } from "../tools/autoskill/owner-lock.mjs";

function fixture() { return fs.mkdtempSync(path.join(os.tmpdir(), "autoskill-owner-lock-")); }
function lockPath(root, owner) {
  const key = crypto.createHash("sha256").update(owner).digest("hex").slice(0, 32);
  const dir = path.join(root, "state", "autoskill", "locks");
  fs.mkdirSync(dir, { recursive: true });
  return path.join(dir, `${key}.lock`);
}

test("owner lock serializes a concurrent process and releases after the operation", () => {
  const root = fixture();
  const owner = "synthetic-owner";
  try {
    const moduleUrl = new URL("../tools/autoskill/owner-lock.mjs", import.meta.url).href;
    withOwnerLockSync(root, owner, () => {
      const script = `import { withOwnerLockSync } from ${JSON.stringify(moduleUrl)}; try { withOwnerLockSync(${JSON.stringify(root)}, ${JSON.stringify(owner)}, () => 0); process.exitCode = 2; } catch (error) { process.stdout.write(error.message); }`;
      const child = spawnSync(process.execPath, ["--input-type=module", "-e", script], { encoding: "utf8" });
      assert.equal(child.status, 0, child.stderr);
      assert.equal(child.stdout, "autoskill_owner_operation_in_progress");
    });
    assert.equal(fs.existsSync(lockPath(root, owner)), false);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("owner lock reclaims a dead-process lock under a recovery marker", () => {
  const root = fixture();
  const owner = "synthetic-owner";
  const file = lockPath(root, owner);
  try {
    fs.writeFileSync(file, JSON.stringify({ schema: 1, pid: 2147483647, token: "deadbeef-dead-beef-dead-beefdeadbeef", created_at: "synthetic" }));
    assert.equal(withOwnerLockSync(root, owner, () => "recovered"), "recovered");
    assert.equal(fs.existsSync(file), false);
    assert.equal(fs.existsSync(`${file}.recovering`), false);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("owner lock fails closed on malformed lock metadata and does not overwrite it", () => {
  const root = fixture();
  const owner = "synthetic-owner";
  const file = lockPath(root, owner);
  try {
    fs.writeFileSync(file, "not-json");
    assert.throws(() => withOwnerLockSync(root, owner, () => "must-not-run"), /autoskill_owner_lock_invalid/);
    assert.equal(fs.readFileSync(file, "utf8"), "not-json");
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
