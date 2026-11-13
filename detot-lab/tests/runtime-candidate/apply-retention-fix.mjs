import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";

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
\tconst paths = entries.filter((entry) => entry.isFile() && prefixes.some((prefix) => entry.name === prefix || entry.name.startsWith(prefix + ".deleted.") || prefix === sessionId + ".jsonl" && entry.name.startsWith(prefix + "."))).map((entry) => path.join(sessionDir, entry.name));
\tfor (const file of paths) await unlink(file);
\treturn paths.length;
}
`;
let installedErasureSource;
try { installedErasureSource = await readFile(erasurePath, "utf8"); }
catch { await writeFile(erasurePath, erasureSource, "utf8"); }
if (installedErasureSource !== undefined) assert.equal(installedErasureSource, erasureSource, "Unexpected candidate artifact-erasure helper content.");

let deletionSource = await readFile(deletionPath, "utf8");
const regionMarker = "//#region src/gateway/server-methods/sessions-delete.ts";
const regionIndex = deletionSource.indexOf(regionMarker);
const importAnchor = 'import { isDeepStrictEqual } from "node:util";';
const importIndex = deletionSource.lastIndexOf(importAnchor, regionIndex);
assert(regionIndex > 0 && importIndex >= 0, "Cannot locate the sessions.delete module import boundary.");
const normalizedImports = `${importAnchor} import { revokeOpenResponsesForDeletedSession } from "./openresponses-http-DLg7g4Pa.mjs"; import { eraseDeletedSessionFiles } from "./detot-session-artifact-erasure.mjs"; `;
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

console.log("Applied Detot retention patch to OpenClaw " + manifest.version + ".");
