import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { sessionCopies } from "./session-copies.mjs";

test("old encrypted copies cannot restore revoked content with fresh authority reader", async t => {
  const root = await mkdtemp(join(tmpdir(), "detot-copies-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const authority = join(root, "authority");
  const copies = join(root, "copies");
  await mkdir(authority);
  await mkdir(copies);
  const writer = sessionCopies(authority);
  await writer.provision("deleted");
  await writer.provision("control");
  for (const name of ["snapshot", "archive"]) {
    await writeFile(join(copies, name), await writer.seal("deleted", Buffer.from("synthetic canary")));
  }
  const control = await writer.seal("control", Buffer.from("control"));
  const snapshot = await readFile(join(copies, "snapshot"));
  assert.equal((await writer.restore("deleted", snapshot)).toString(), "synthetic canary");
  await writer.revoke("deleted");
  const reader = sessionCopies(authority);
  for (const name of ["snapshot", "archive"]) {
    const bytes = await readFile(join(copies, name));
    await assert.rejects(reader.restore("deleted", bytes), /revoked/);
  }
  await assert.rejects(reader.provision("deleted"), /revoked/);
  assert.equal((await reader.restore("control", control)).toString(), "control");
  await assert.rejects(sessionCopies(join(root, "missing")).restore("control", control));
  control[control.length - 1] ^= 1;
  await assert.rejects(reader.restore("control", control));
});
