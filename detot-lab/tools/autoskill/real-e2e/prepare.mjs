import fs from "node:fs";
import crypto from "node:crypto";
import path from "node:path";
import { isDeepStrictEqual } from "node:util";
import { fileURLToPath } from "node:url";
import { applyUpstreamPatch } from "../apply-upstream-patch.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_ROOT = path.resolve(HERE, "../../..");
const ROOT = path.join(LAB_ROOT, ".local", "autoskill-staging");
const STATE = path.join(ROOT, "real-e2e-state");
const PROFILE = path.join(ROOT, "real-e2e-profile");
const CONFIG = path.join(ROOT, "gateway.real-e2e.json");
const TOOL_DIR = path.join(ROOT, "plugins", "detot-synthetic-case");
const ADAPTER_DIR = path.join(LAB_ROOT, ".local", "research", "AutoSkill", "AutoSkill4OpenClaw", "adapter");
const OPENCLAW = path.join(LAB_ROOT, "tests", "runtime-candidate", "node_modules", "openclaw");

function diffPaths(left, right, prefix = "$") {
  if (left && right && typeof left === "object" && typeof right === "object" && !Array.isArray(left) && !Array.isArray(right)) {
    const paths = [];
    for (const key of new Set([...Object.keys(left), ...Object.keys(right)])) paths.push(...diffPaths(left[key], right[key], `${prefix}.${key}`));
    return paths;
  }
  return isDeepStrictEqual(left, right) ? [] : [prefix];
}

applyUpstreamPatch();
const source = JSON.parse(fs.readFileSync(path.join(LAB_ROOT, "gateway.lab.json"), "utf8"));
const deepseekKey = source.models?.providers?.deepseek?.apiKey;
if (deepseekKey?.source !== "env" || deepseekKey?.id !== "DEEPSEEK_API_KEY") {
  throw new Error("refusing_to_copy_non_environment_deepseek_credential");
}
const existing = fs.existsSync(CONFIG) ? JSON.parse(fs.readFileSync(CONFIG, "utf8")) : null;
const config = structuredClone(source);
config.gateway.mode = "local";
config.gateway.bind = "loopback";
config.gateway.port = 19794;
config.gateway.auth = {
  mode: "token",
  token: existing?.gateway?.auth?.token || crypto.randomBytes(32).toString("hex"),
};
config.gateway.reload = { mode: "off" };
config.plugins.enabled = true;
config.plugins.allow = ["autoskill-openclaw-adapter", "detot-synthetic-case"];
config.plugins.slots = { memory: "none" };
config.plugins.load = { paths: [ADAPTER_DIR, TOOL_DIR] };
config.plugins.entries = {
  "autoskill-openclaw-adapter": {
    enabled: true,
    hooks: { allowConversationAccess: true },
    config: {
      userId: "detot-synthetic-owner",
      runtimeMode: "embedded",
      reviewOnly: true,
      openclawSkillInstallMode: "openclaw_mirror",
      recallEnabled: false,
      skillRetrieval: { enabled: false },
      extractOnAgentEnd: true,
      successOnly: false,
      includeUserFeedback: false,
      logPayload: false,
      embedded: {
        sessionArchiveDir: path.join(STATE, "autoskill", "sessions"),
        reviewQueueDir: path.join(STATE, "autoskill", "proposals"),
        skillBankDir: path.join(STATE, "autoskill", "SkillBank"),
        openclawSkillsDir: path.join(STATE, "workspace", "skills"),
        sessionMaxTurns: 20,
        archiveRetentionDays: 7,
        liveExtractEveryTurns: 0,
        modelInvocation: { modes: ["openclaw-runtime"], timeoutMs: 30000, retries: 0 },
      },
    },
  },
  "detot-synthetic-case": { enabled: true, config: {} },
};
config.tools.profile = "minimal";
config.tools.allow = ["detot_synthetic_case_check"];
config.tools.deny = [
  "exec", "process", "read", "write", "edit", "apply_patch", "browser", "nodes",
  "web_search", "web_fetch", "gateway", "cron", "sessions_spawn", "group:runtime",
  "group:fs", "group:nodes", "group:web", "group:openclaw", "group:plugins", "canvas",
];
config.agents.defaults.workspace = path.join(STATE, "workspace");
config.skills = { ...config.skills, allowBundled: [] };

if (existing) {
  const expected = structuredClone(config);
  const actual = structuredClone(existing);
  expected.gateway.auth.token = "";
  actual.gateway.auth ??= {};
  actual.gateway.auth.token = "";
  if (actual.skills?.allowBundled === false) actual.skills.allowBundled = [];
  if (!isDeepStrictEqual(actual, expected)) {
    throw new Error(`real_e2e_config_exists_and_differs_at:${diffPaths(expected, actual).join(",")}; inspect it before modifying`);
  }
}
fs.mkdirSync(STATE, { recursive: true });
fs.mkdirSync(path.join(PROFILE, "AppData", "Roaming"), { recursive: true });
fs.mkdirSync(path.join(PROFILE, "AppData", "Local"), { recursive: true });
fs.mkdirSync(path.join(STATE, "workspace", "skills"), { recursive: true });
fs.mkdirSync(path.join(STATE, "autoskill", "sessions"), { recursive: true });
fs.mkdirSync(path.join(STATE, "autoskill", "proposals"), { recursive: true });
fs.mkdirSync(path.join(STATE, "autoskill", "SkillBank"), { recursive: true });
fs.mkdirSync(path.join(STATE, "tmp"), { recursive: true });
fs.mkdirSync(TOOL_DIR, { recursive: true });
fs.mkdirSync(path.join(ROOT, "logs"), { recursive: true });

const packageJson = {
  name: "detot-synthetic-case",
  version: "0.1.0",
  private: true,
  type: "module",
  openclaw: { extensions: ["./index.mjs"] },
};
const manifest = {
  id: "detot-synthetic-case",
  name: "Đệ Tốt Synthetic Case Tool",
  description: "Checks synthetic ticket ordering tasks without accessing the host filesystem.",
  categories: ["other"],
  contracts: { tools: ["detot_synthetic_case_check"] },
  activation: { onStartup: true },
  configSchema: { type: "object", additionalProperties: false },
};
const entry = `import path from "node:path";
import { pathToFileURL } from "node:url";
const typeboxPath = process.env.DETOT_TYPEBOX_MODULE;
if (!typeboxPath) throw new Error("staging_typebox_module_missing");
const { Type } = await import(pathToFileURL(path.resolve(typeboxPath)).href);
const cases = {
  train: { expected: ["INC-12@2", "INC-10@1", "INC-12@1", "INC-13@1"], reason: "keep the highest revision per ticket id, then sort P1 before P2 before P3, oldest first, stable for ties" },
  eval1: { expected: ["A@3", "B@1", "A@2", "C@1"], reason: "deduplicate by highest revision before sorting; priority rank then timestamp; stable ties" },
  eval2: { expected: ["K@2", "J@1", "L@1", "M@1"], reason: "choose latest ticket revision, then P1/P2/P3 and oldest timestamp" },
  eval3: { expected: ["R@1", "Q@3", "P@1", "S@1"], reason: "latest revision wins; sort priority rank, timestamp, preserve input order on ties" },
  eval4: { expected: ["X@2", "Y@1", "Z@1", "X@1"], reason: "dedupe ticket id using greatest revision, then priority and creation time" },
  eval5: { expected: ["U@1", "V@2", "W@1", "T@1"], reason: "retain max revision per id, sort P1 then P2 then P3, timestamp ascending and stable ties" },
};
export default {
  id: "detot-synthetic-case",
  name: "Đệ Tốt Synthetic Case Tool",
  description: "Checks ticket reconciliation on fixed synthetic cases only.",
  register(api) {
    api.registerTool({
      name: "detot_synthetic_case_check",
      description: "Check a proposed ordered list of ticketId@revision values for one synthetic case. Returns PASS or precise deterministic feedback. No host data is accessed.",
      parameters: Type.Object({
        caseId: Type.Union([Type.Literal("train"), Type.Literal("eval1"), Type.Literal("eval2"), Type.Literal("eval3"), Type.Literal("eval4"), Type.Literal("eval5")]),
        order: Type.Array(Type.String()),
      }, { additionalProperties: false }),
      outputSchema: Type.Object({ status: Type.String(), expected: Type.Array(Type.String()), guidance: Type.String() }, { additionalProperties: false }),
      async execute(_toolCallId, params) {
        const testCase = cases[params.caseId];
        if (!testCase) return { content: [{ type: "text", text: "BLOCKED: unknown synthetic case" }], details: { status: "BLOCKED", expected: [], guidance: "unknown_case" } };
        const pass = JSON.stringify(params.order) === JSON.stringify(testCase.expected);
        const details = { status: pass ? "PASS" : "FAIL", expected: testCase.expected, guidance: pass ? "" : testCase.reason };
        return { content: [{ type: "text", text: JSON.stringify(details) }], details };
      },
    });
  },
};
`;
fs.writeFileSync(path.join(TOOL_DIR, "package.json"), `${JSON.stringify(packageJson, null, 2)}\n`);
fs.writeFileSync(path.join(TOOL_DIR, "openclaw.plugin.json"), `${JSON.stringify(manifest, null, 2)}\n`);
fs.writeFileSync(path.join(TOOL_DIR, "index.mjs"), entry);
if (!existing || !existing.gateway?.auth?.token || existing.skills?.allowBundled === false) {
  fs.writeFileSync(CONFIG, `${JSON.stringify(config, null, 2)}\n`, { flag: existing ? "w" : "wx" });
}
process.stdout.write(`${JSON.stringify({ status: existing ? "already_ready" : "prepared", config: CONFIG, state: STATE, port: 19794, primaryGatewayChanged: false, credentialCopied: false }, null, 2)}\n`);
