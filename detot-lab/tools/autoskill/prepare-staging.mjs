import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { DEFAULT_STAGING_ROOT } from "./pipeline.mjs";
import { applyUpstreamPatch } from "./apply-upstream-patch.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_ROOT = path.resolve(HERE, "../..");
const RESEARCH_REPO = path.join(LAB_ROOT, ".local", "research", "AutoSkill");
const ADAPTER_DIR = path.join(RESEARCH_REPO, "AutoSkill4OpenClaw", "adapter");
const CONFIG_PATH = path.join(DEFAULT_STAGING_ROOT, "gateway.staging-proposal-only.json");
const pluginId = "autoskill-openclaw-adapter";

const patchResult = applyUpstreamPatch();
const config = {
  gateway: { mode: "local", port: 19794, bind: "loopback", auth: { mode: "none" } },
  plugins: {
    enabled: true,
    allow: [pluginId],
    slots: { memory: "none" },
    load: { paths: [ADAPTER_DIR] },
    entries: {
      [pluginId]: {
        enabled: false,
        hooks: { allowConversationAccess: false },
        config: {
          userId: "detot-owner",
          runtimeMode: "embedded",
          reviewOnly: true,
          openclawSkillInstallMode: "openclaw_mirror",
          recallEnabled: false,
          skillRetrieval: { enabled: false },
          extractOnAgentEnd: false,
          successOnly: false,
          includeUserFeedback: false,
          logPayload: false,
          embedded: {
            sessionArchiveDir: path.join(DEFAULT_STAGING_ROOT, "state", "autoskill", "sessions"),
            reviewQueueDir: path.join(DEFAULT_STAGING_ROOT, "state", "autoskill", "proposals"),
            skillBankDir: path.join(DEFAULT_STAGING_ROOT, "state", "autoskill", "SkillBank"),
            openclawSkillsDir: path.join(DEFAULT_STAGING_ROOT, "state", "workspace", "skills"),
            sessionMaxTurns: 20,
            archiveRetentionDays: 7,
            liveExtractEveryTurns: 0,
            modelInvocation: { modes: ["openclaw-runtime"], timeoutMs: 20000, retries: 0 },
          },
        },
      },
    },
  },
};

fs.mkdirSync(DEFAULT_STAGING_ROOT, { recursive: true });
if (fs.existsSync(CONFIG_PATH)) {
  const existing = JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8"));
  if (JSON.stringify(existing) !== JSON.stringify(config)) throw new Error("staging_config_already_exists_and_differs; inspect it before changing");
} else {
  fs.writeFileSync(CONFIG_PATH, `${JSON.stringify(config, null, 2)}\n`, { flag: "wx" });
}
process.stdout.write(JSON.stringify({ ...patchResult, status: "staging_config_ready", configPath: CONFIG_PATH, pluginEnabled: false, conversationAccess: false, productionGatewayChanged: false }, null, 2) + "\n");
