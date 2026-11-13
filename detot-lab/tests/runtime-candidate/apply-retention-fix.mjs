import assert from "node:assert/strict";
import { copyFile, readFile, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const runtime = resolve(process.argv[2] ?? "");
assert(runtime.endsWith("openclaw.mjs"), "Pass the OpenClaw candidate entrypoint.");
const packageRoot = dirname(runtime);
const manifest = JSON.parse(await readFile(join(packageRoot, "package.json"), "utf8"));
assert.equal(manifest.version, "2026.11.3", "Retention patch is pinned to the verified candidate version.");

async function replaceExactly(file, before, after, label) {
  let source = await readFile(file, "utf8");
  if (source.includes(after)) return;
  assert(source.includes(before), `Cannot patch ${label}: candidate source anchor changed.`);
  source = source.replace(before, after);
  await writeFile(file, source, "utf8");
}

const responsesPath = join(packageRoot, "dist", "openresponses-http-DLg7g4Pa.mjs");
await replaceExactly(
  responsesPath,
  "export { handleOpenResponsesHttpRequest };",
  `async function revokeOpenResponsesForDeletedSession(params) {
\tconst sessionKeys = new Set(params.sessionKeys);
\tconst store = openStore();
\tconst entries = await store.entries();
\tconst matches = entries.filter((entry) => entry.value?.agentId === params.agentId && (sessionKeys.has(entry.value.sessionKey) || sessionKeys.has(entry.value.requestedSessionKey)));
\tfor (const entry of matches) await store.delete(entry.key, { assertCurrent: params.assertCurrent });
\treturn matches.length;
}
export { handleOpenResponsesHttpRequest, revokeOpenResponsesForDeletedSession };`,
  "OpenResponses continuation revocation",
);

const deletionPath = join(packageRoot, "dist", "sessions-delete-DbGyBrAQ.mjs");
await copyFile(fileURLToPath(new URL("./session-copies.mjs", import.meta.url)),
  join(packageRoot, "dist", "detot-session-copies.mjs"));
const memoryCliPath = join(packageRoot, "dist", "cli.runtime-D93HQK_k.mjs");
await replaceExactly(memoryCliPath, "export { runMemoryForget,", "export { forgetMemoryEntries, runMemoryForget,", "Memory Core purge API");
await replaceExactly(memoryCliPath,
  "chunkIds.length > 0 && indexPlan.hasVectorTable ? expectDefined(indexPlan.extensionPath",
  "indexPlan.hasVectorTable ? expectDefined(indexPlan.extensionPath",
  "prepare native vector extension for orphan cleanup");
await replaceExactly(join(packageRoot, "dist", "manager-search.worker-CuiJOauz.mjs"),
  "if (result.value.hasVectorTable && result.value.chunks.length > 0) {",
  "if (result.value.hasVectorTable) {",
  "resolve native vector extension even when chunks were already removed");
await replaceExactly(join(packageRoot, "dist", "memory-entry-origins.worker-DZ4Gnu3_.mjs"),
  "\tdeleteMemoryIndexSources(db, input.sources);",
  "\tif (input.hasVectorTable) db.exec(\"DELETE FROM memory_index_chunks_vec WHERE id IN (SELECT vec.id FROM memory_index_chunks_vec AS vec LEFT JOIN memory_index_chunks AS chunk ON chunk.id = vec.id WHERE chunk.id IS NULL)\");\n\tdeleteMemoryIndexSources(db, input.sources);",
  "remove orphan vectors in the purge transaction");
const dreamingPath = join(packageRoot, "dist", "dreaming-state-D1mfF5St.mjs");
await replaceExactly(
  dreamingPath,
  "function openMemoryCoreStateStore(options) {\n\tconst store = dreamingState.getRuntime()(options);",
  "const detotStoreScope = new AsyncLocalStorage();\nexport function withDetotMemoryStore(openStore, run) { return detotStoreScope.run(openStore, run); }\nfunction openMemoryCoreStateStore(options) {\n\tconst store = (detotStoreScope.getStore() ?? dreamingState.getRuntime())(options);",
  "scoped Memory Core store",
);
const memoryPurgePath = join(packageRoot, "dist", "detot-memory-purge.mjs");
await writeFile(memoryPurgePath, [
  'import { DatabaseSync } from "node:sqlite";',
  'import { c as resolveAgentDb } from "./openclaw-agent-db.paths-BGqL4GOe.mjs";',
  'import { getLoadablePath } from "../../sqlite-vec/index.mjs";',
  'import { forgetMemoryEntries } from "./cli.runtime-D93HQK_k.mjs";',
  'import { i as createStore } from "./plugin-state-store-DY4rDlss.mjs";',
  'import { withDetotMemoryStore, S as withAuthority } from "./dreaming-state-D1mfF5St.mjs";',
  'export async function purgeSessionMemory({ cfg, agentId, sessionId, storePath, assertCurrent }) {',
  '  const report = await withDetotMemoryStore(options => createStore("memory-core", options, assertCurrent),',
  '    () => withAuthority(assertCurrent, () => forgetMemoryEntries({ cfg, agentId, sessionIds: [sessionId] })));',
  '  if (!report.sessionIds.includes(sessionId) || report.refusals.length || report.untargetableEntryKeys.length)',
  '    throw new Error("Memory Core purge could not verify complete session provenance.");',
  '  const db = new DatabaseSync(resolveAgentDb({ agentId }), { allowExtension: true });',
  '  try {',
  '    if (!db.prepare("SELECT 1 FROM sqlite_master WHERE name = ?").get("memory_index_chunks_vec")) return;',
  '    db.loadExtension(getLoadablePath());',
  '    assertCurrent();',
  '    db.exec("BEGIN IMMEDIATE");',
  '    try {',
  '      db.exec("DELETE FROM memory_index_chunks_vec WHERE id IN (SELECT vec.id FROM memory_index_chunks_vec AS vec LEFT JOIN memory_index_chunks AS chunk ON chunk.id = vec.id WHERE chunk.id IS NULL)");',
  '      assertCurrent();',
  '      db.exec("COMMIT");',
  '    } catch (error) { db.exec("ROLLBACK"); throw error; }',
  '  } finally { db.close(); }',
  '}',
].join("\n"), "utf8");
const erasurePath = join(packageRoot, "dist", "detot-session-artifact-erasure.mjs");
const erasureSource = `import { readdir, unlink } from "node:fs/promises";
import path from "node:path";

export async function eraseDeletedSessionFiles(params) {
\tconst sessionId = params.sessionId;
\tif (typeof sessionId !== "string" || !/^[a-zA-Z0-9-]{1,128}$/.test(sessionId)) throw new Error("Cannot safely resolve deleted session artifact names.");
\tconst sessionDir = path.resolve(path.dirname(params.storePath), "..", "sessions");
\tlet entries;
\ttry { entries = await readdir(sessionDir, { withFileTypes: true }); }
\tcatch (error) { if (error?.code === "ENOENT") return 0; throw error; }
\tconst prefixes = [sessionId + ".jsonl", sessionId + ".trajectory.jsonl", sessionId + ".trajectory-path.json"];
\tconst paths = entries.filter((entry) => (entry.isFile() || entry.isSymbolicLink()) && prefixes.some((prefix) =>
\t\tentry.name.startsWith(prefix + ".deleted.") || (!params.archiveOnly &&
\t\t\t(entry.name === prefix || prefix === sessionId + ".jsonl" && entry.name.startsWith(prefix + ".")))))
\t\t.map((entry) => path.join(sessionDir, entry.name));
\tfor (const file of paths) {
\t\ttry { await unlink(file); }
\t\tcatch (error) { if (error?.code !== "ENOENT") throw error; }
\t}
\treturn paths.length;
}
`;
let installedErasureSource;
try { installedErasureSource = await readFile(erasurePath, "utf8"); }
catch (error) { if (error?.code !== "ENOENT") throw error; }
if (installedErasureSource !== erasureSource) await writeFile(erasurePath, erasureSource, "utf8");

let deletionSource = await readFile(deletionPath, "utf8");
const regionMarker = "//#region src/gateway/server-methods/sessions-delete.ts";
const regionIndex = deletionSource.indexOf(regionMarker);
const importAnchor = 'import { isDeepStrictEqual } from "node:util";';
const importIndex = deletionSource.lastIndexOf(importAnchor, regionIndex);
assert(regionIndex > 0 && importIndex >= 0, "Cannot locate the sessions.delete module import boundary.");
const normalizedImports = `${importAnchor} import { revokeOpenResponsesForDeletedSession } from "./openresponses-http-DLg7g4Pa.mjs"; import { eraseDeletedSessionFiles } from "./detot-session-artifact-erasure.mjs"; import { purgeSessionMemory } from "./detot-memory-purge.mjs"; `;
deletionSource = deletionSource.slice(0, importIndex) + normalizedImports + deletionSource.slice(regionIndex);
await writeFile(deletionPath, deletionSource, "utf8");
await replaceExactly(
  deletionPath,
  "archiveTranscript: incognito ? false : deleteTranscript,",
  "archiveTranscript: false,",
  "session deletion archive suppression",
);
await replaceExactly(
  deletionPath,
  "deleteTranscriptWithoutArchive: incognito,",
  "deleteTranscriptWithoutArchive: true,",
  "session transcript purge",
);
await replaceExactly(
  deletionPath,
  "\t\t\t\t\t\tconst result = postCleanupEntry && pluginOwnerId && isModelSelectionLocked(postCleanupEntry) ? await rollbackPluginOwnedSessionEntryLifecycle({",
  "\t\t\t\t\t\tawait revokeOpenResponsesForDeletedSession({ agentId: target.agentId, sessionKeys: [key, target.canonicalKey, ...target.storeKeys], assertCurrent: commitGuard });\n\t\t\t\t\t\tconst result = postCleanupEntry && pluginOwnerId && isModelSelectionLocked(postCleanupEntry) ? await rollbackPluginOwnedSessionEntryLifecycle({",
  "sessions.delete continuation revocation",
);
deletionSource = await readFile(deletionPath, "utf8");
deletionSource = deletionSource.replace(/\n\t+if \(postCleanupEntry\?\.sessionId\) await runMemoryForget\(\{ agent: target\.agentId, session: \[postCleanupEntry\.sessionId\], json: true \}\);/g, "");
deletionSource = deletionSource.replace(/(\t+await revokeOpenResponsesForDeletedSession\(\{ agentId: target\.agentId, sessionKeys: \[key, target\.canonicalKey, \.\.\.target\.storeKeys\], assertCurrent: commitGuard \}\);\n)\1/g, "$1");
await writeFile(deletionPath, deletionSource, "utf8");
await replaceExactly(
  deletionPath,
  "if (result.deleted) {\n\t\t\t\t\t\t\tawait retirement.retire();",
  "if (result.deleted) {\n\t\t\t\t\t\t\tawait eraseDeletedSessionFiles({ storePath, sessionId: result.deletedSessionId });\n\t\t\t\t\t\t\tawait retirement.retire();",
  "sessions.delete retained transcript and trajectory purge",
);
const revocationAnchor = "\t\t\t\t\t\tawait revokeOpenResponsesForDeletedSession({";
const archiveAnchor = "\t\t\t\t\t\tif (postCleanupEntry?.sessionId) await eraseDeletedSessionFiles({ storePath, sessionId: postCleanupEntry.sessionId, archiveOnly: true });";
await replaceExactly(
  deletionPath,
  revocationAnchor,
  archiveAnchor + "\n" + revocationAnchor,
  "purge archived copies before deleting the session record",
);
const purgeLine = '\t\t\t\t\t\tif (postCleanupEntry?.sessionId && !incognito && cfg.plugins?.enabled !== false && (cfg.plugins?.slots?.memory ?? "memory-core") === "memory-core") await purgeSessionMemory({ cfg, agentId: target.agentId, sessionId: postCleanupEntry.sessionId, storePath, assertCurrent: commitGuard });\n';
const oldPurgeLine = purgeLine.replace(", storePath, assertCurrent", ", assertCurrent");
deletionSource = await readFile(deletionPath, "utf8");
if (deletionSource.includes(oldPurgeLine)) await replaceExactly(deletionPath, oldPurgeLine, purgeLine, "upgrade Memory Core purge");
await replaceExactly(deletionPath, archiveAnchor, purgeLine + archiveAnchor, "purge Memory Core before removing provenance");
await replaceExactly(deletionPath, purgeLine,
  '\t\t\t\t\t\tif (postCleanupEntry?.sessionId && process.env.DETOT_COPY_AUTHORITY_DIR) await (await import("./detot-session-copies.mjs")).sessionCopies(process.env.DETOT_COPY_AUTHORITY_DIR).revoke(postCleanupEntry.sessionId);\n' + purgeLine,
  "revoke backup keys before deleting session data");

console.log("Applied Detot retention patch to OpenClaw " + manifest.version + ".");
