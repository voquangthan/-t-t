import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_ROOT = path.resolve(HERE, "../..");
const RESEARCH_REPO = path.join(LAB_ROOT, ".local", "research", "AutoSkill");
const PINNED_COMMIT = "94c47ca488d4ba4117d20272e66d49b9877e68cf";
const PATCH_FILE = path.join(HERE, "adapter-review-only.patch");
const PRIVACY_SOURCE = path.join(HERE, "privacy.mjs");
const PROVENANCE_SOURCE = path.join(HERE, "provenance.mjs");
const ADAPTER_DIR = path.join(RESEARCH_REPO, "AutoSkill4OpenClaw", "adapter");
const ADAPTER_RUNTIME = path.join(ADAPTER_DIR, "embedded_runtime.js");
const PRIVACY_DEST = path.join(ADAPTER_DIR, "detot-privacy.mjs");
const PROVENANCE_DEST = path.join(ADAPTER_DIR, "detot-provenance.mjs");

function runGit(args, options = {}) {
  return execFileSync("git", ["-C", RESEARCH_REPO, ...args], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], ...options }).trim();
}

export function applyUpstreamPatch() {
  if (!fs.existsSync(path.join(RESEARCH_REPO, ".git"))) throw new Error("AutoSkill research clone missing; clone it to detot-lab/.local/research/AutoSkill first.");
  if (runGit(["rev-parse", "HEAD"]) !== PINNED_COMMIT) throw new Error("AutoSkill upstream revision does not match the tested pin; inspect and rebase the patch before applying.");
  if (!fs.existsSync(PATCH_FILE) || !fs.existsSync(PRIVACY_SOURCE) || !fs.existsSync(PROVENANCE_SOURCE))
    throw new Error("tracked patch or governance module missing");

  const runtime = fs.readFileSync(ADAPTER_RUNTIME, "utf8");
  const plugin = fs.readFileSync(path.join(ADAPTER_DIR, "index.js"), "utf8");
  const alreadyPatched = runtime.includes('from "./detot-privacy.mjs"') &&
    runtime.includes("cfg.reviewOnly === true") && runtime.includes("autoskill_consent_denied") &&
    runtime.includes('from "./detot-provenance.mjs"') &&
    plugin.includes('consentAllows(authorizeConsent, "capture")');
  if (!alreadyPatched) {
    const status = runGit(["status", "--porcelain", "--", "AutoSkill4OpenClaw/adapter"]);
    if (status) throw new Error("adapter has local modifications; refusing to overwrite them. Review or back them up first.");
    runGit(["apply", "--check", PATCH_FILE]);
    runGit(["apply", PATCH_FILE]);
  }

  for (const [source, destination, label] of [
    [PRIVACY_SOURCE, PRIVACY_DEST, "detot-privacy.mjs"],
    [PROVENANCE_SOURCE, PROVENANCE_DEST, "detot-provenance.mjs"],
  ]) {
    const bytes = fs.readFileSync(source);
    if (fs.existsSync(destination) && !fs.readFileSync(destination).equals(bytes)) {
      throw new Error(`${label} differs from the tracked version; refusing to overwrite local edits.`);
    }
    if (!fs.existsSync(destination)) fs.writeFileSync(destination, bytes, { flag: "wx" });
  }
  return { status: alreadyPatched ? "already_patched" : "patched", upstreamCommit: PINNED_COMMIT, adapterPath: ADAPTER_DIR };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.stdout.write(`${JSON.stringify(applyUpstreamPatch(), null, 2)}\n`);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
