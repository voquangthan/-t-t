import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { open, readFile, unlink } from "node:fs/promises";
import { isAbsolute, join } from "node:path";

// Provision this directory separately. Restore jobs must never restore or create it.
export function sessionCopies(authorityDirectory) {
  if (!isAbsolute(authorityDirectory)) throw new Error("An absolute authority directory is required.");
  function paths(id) {
    if (!/^[a-zA-Z0-9-]{1,128}$/.test(id)) throw new Error("Invalid session ID.");
    return { key: join(authorityDirectory, id + ".key"), revoked: join(authorityDirectory, id + ".revoked") };
  }
  async function revoked(path) {
    try { await readFile(path); return true; }
    catch (error) { if (error.code === "ENOENT") return false; throw error; }
  }
  async function writeExclusive(path, bytes) {
    const file = await open(path, "wx", 0o600);
    try { await file.writeFile(bytes); await file.sync(); }
    finally { await file.close(); }
  }
  async function readKey(id) {
    const p = paths(id);
    if (await revoked(p.revoked)) throw new Error("Session revoked.");
    const key = await readFile(p.key);
    if (key.length !== 32 || await revoked(p.revoked)) {
      key.fill(0);
      throw new Error("Session key unavailable.");
    }
    return key;
  }
  return {
    async provision(id) {
      const p = paths(id);
      if (await revoked(p.revoked)) throw new Error("Session revoked.");
      await writeExclusive(p.key, randomBytes(32));
    },
    async revoke(id) {
      const p = paths(id);
      try { await writeExclusive(p.revoked, Buffer.from("revoked\n")); }
      catch (error) { if (error.code !== "EEXIST") throw error; }
      try { await unlink(p.key); }
      catch (error) { if (error.code !== "ENOENT") throw error; }
    },
    async seal(id, bytes) {
      const key = await readKey(id);
      try {
        const iv = randomBytes(12);
        const cipher = createCipheriv("aes-256-gcm", key, iv);
        cipher.setAAD(Buffer.from(id));
        const data = Buffer.concat([cipher.update(bytes), cipher.final()]);
        return Buffer.concat([iv, cipher.getAuthTag(), data]);
      } finally { key.fill(0); }
    },
    async restore(id, envelope) {
      if (envelope.length < 28) throw new Error("Invalid encrypted copy.");
      const key = await readKey(id);
      try {
        const decipher = createDecipheriv("aes-256-gcm", key, envelope.subarray(0, 12));
        decipher.setAAD(Buffer.from(id));
        decipher.setAuthTag(envelope.subarray(12, 28));
        const data = Buffer.concat([decipher.update(envelope.subarray(28)), decipher.final()]);
        if (await revoked(paths(id).revoked)) {
          data.fill(0);
          throw new Error("Session revoked.");
        }
        return data;
      } finally { key.fill(0); }
    }
  };
}
