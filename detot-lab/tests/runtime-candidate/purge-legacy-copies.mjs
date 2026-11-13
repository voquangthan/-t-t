import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { purgeConfiguredCopies, purgeRegisteredPlaintextCopies } from "./restore-preflight.mjs";

const [mode, root, authority, manifest] = process.argv.slice(2);
if (["--scan-config", "--apply-config"].includes(mode)) {
  if (!root || authority || manifest) throw new Error("Provide exactly one JSON configuration file.");
  const config = JSON.parse(await readFile(root, "utf8"));
  await purgeConfiguredCopies(config.roots, resolve(config.authority), {
    apply: mode === "--apply-config",
    log: event => console.log(JSON.stringify(event)),
  });
  process.exit(0);
}
if (mode !== "--apply" || !root || !authority || !manifest || process.argv.length !== 6)
  throw new Error("Usage: node purge-legacy-copies.mjs --apply COPY_DIRECTORY AUTHORITY_DIRECTORY INVENTORY_JSON");
const inventory = JSON.parse(await readFile(manifest, "utf8"));
if (!Array.isArray(inventory)) throw new Error("Expected a JSON array of copy inventory entries.");
const count = await purgeRegisteredPlaintextCopies(resolve(root), resolve(authority), inventory);
console.log(JSON.stringify({ filesUnlinked: count }));
