import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { ReviewFeedbackListener, loadJsonConfig } from "./listener.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LAB_ROOT = path.resolve(HERE, "../..");
const DEFAULT_CONTROLLER_CONFIG = path.join(LAB_ROOT, ".local", "review-loop", "controller.json");
const DEFAULT_CONTROLLER_ROOT = path.join(LAB_ROOT, ".local", "review-loop", "controller");
const TASK_RE = /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,80}$/u;

function fail(code) { throw new Error(code); }

export function validateControllerConfig(config, taskId) {
  if (!TASK_RE.test(String(taskId || ""))) fail("invalid_task_id");
  if (config?.enabled !== true) fail("controller_disabled_in_local_config");
  if (!Array.isArray(config.approved_task_ids) || !config.approved_task_ids.includes(taskId)) fail("task_not_explicitly_approved");
  if (config.execute_advisory_revise === true) fail("execution_blocked_no_trusted_isolation_provider");
  if (!Array.isArray(config.allowed_paths) || config.allowed_paths.length === 0 || config.allowed_paths.some((p) => typeof p !== "string" || p.startsWith("/") || p.includes("..") || p.includes("\\"))) fail("invalid_allowed_paths");
  const pollSeconds = Math.min(120, Math.max(5, Number(config.poll_seconds || 20)));
  return { pollSeconds };
}

function taskPaths(root, taskId) {
  return {
    checkpoint: path.join(root, `${taskId}.json`),
    stop: path.join(root, `${taskId}.stop`),
  };
}

function saveCheckpoint(root, taskId, value) {
  fs.mkdirSync(root, { recursive: true });
  const file = taskPaths(root, taskId).checkpoint;
  const temp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(temp, `${JSON.stringify({ updated_at: new Date().toISOString(), task_id: taskId, ...value }, null, 2)}\n`, { flag: "wx", mode: 0o600 });
  fs.renameSync(temp, file);
}

function sleep(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

function createLock(root, taskId) {
  const lock = path.join(root, "controller.lock");
  fs.mkdirSync(root, { recursive: true });
  let fd;
  try { fd = fs.openSync(lock, "wx", 0o600); }
  catch (error) { if (error?.code === "EEXIST") fail("controller_already_running_or_stale_lock_requires_manual_check"); throw error; }
  fs.writeFileSync(fd, JSON.stringify({ pid: process.pid, task_id: taskId, started_at: new Date().toISOString() }));
  return () => { fs.closeSync(fd); fs.rmSync(lock, { force: true }); };
}

export async function runController({ taskId, controllerConfig, controllerConfigProvider, listenerConfig, once = false, execute = false, onState = () => {}, listenerFactory, controllerRoot = DEFAULT_CONTROLLER_ROOT }) {
  const readConfig = controllerConfigProvider || (() => controllerConfig);
  const initialConfig = readConfig();
  validateControllerConfig(initialConfig, taskId);
  if (execute) fail("execution_blocked_no_trusted_isolation_provider");

  const releaseLock = createLock(controllerRoot, taskId);
  const paths = taskPaths(controllerRoot, taskId);
  const listener = listenerFactory
    ? listenerFactory()
    : new ReviewFeedbackListener({ repoRoot: LAB_ROOT, config: listenerConfig });
  let stopping = false;
  const stop = () => { stopping = true; };
  process.once("SIGINT", stop);
  process.once("SIGTERM", stop);
  try {
    for (;;) {
      if (stopping || fs.existsSync(paths.stop)) {
        fs.rmSync(paths.stop, { force: true });
        saveCheckpoint(controllerRoot, taskId, { status: "STOPPED", stage_advance_allowed: false, reason: "operator_signal_before_poll" });
        return { status: "STOPPED", stageAdvanceAllowed: false };
      }

      const state = await listener.pollOnce(taskId);
      onState({ status: state.review_status, sha: state.commit_sha, eventId: state.last_processed_review_id });

      // Reload approval after each remote poll. Revocation is fail-closed; polling performs no code execution or Git writes.
      let currentConfig;
      try {
        currentConfig = readConfig();
        validateControllerConfig(currentConfig, taskId);
      } catch {
        saveCheckpoint(controllerRoot, taskId, { status: "BLOCKED", commit_sha: state.commit_sha, stage_advance_allowed: false, reason: "task_approval_revoked_or_config_invalid" });
        return { status: "BLOCKED", stageAdvanceAllowed: false };
      }

      if (stopping || fs.existsSync(paths.stop)) {
        fs.rmSync(paths.stop, { force: true });
        saveCheckpoint(controllerRoot, taskId, { status: "STOPPED", commit_sha: state.commit_sha, review_status: state.review_status, stage_advance_allowed: false, reason: "operator_signal_after_poll" });
        return { status: "STOPPED", stageAdvanceAllowed: false };
      }

      if (state.review_status === "REVISE") {
        saveCheckpoint(controllerRoot, taskId, { status: "ADVISORY_REVISE_PENDING", commit_sha: state.commit_sha, event_id: state.last_feedback?.review_id || null, stage_advance_allowed: false, reason: "execution_blocked_until_trusted_isolation_exists" });
        return { status: "ADVISORY_REVISE_PENDING", stageAdvanceAllowed: false };
      }
      if (["PASS", "BLOCKED", "TIMEOUT", "REVIEW_CHANNEL_UNAVAILABLE", "REVISE_LIMIT"].includes(state.review_status)) {
        const status = state.review_status === "PASS" ? "PASS_AWAITING_HUMAN_CONFIRMATION" : state.review_status;
        saveCheckpoint(controllerRoot, taskId, { status, commit_sha: state.commit_sha, stage_advance_allowed: false, reason: "no_automatic_stage_advance" });
        return { status, stageAdvanceAllowed: false };
      }
      if (once) {
        saveCheckpoint(controllerRoot, taskId, { status: "WAITING", commit_sha: state.commit_sha, review_status: state.review_status, stage_advance_allowed: false, reason: "no_actionable_review_event" });
        return { status: "WAITING", stageAdvanceAllowed: false };
      }

      const waitUntil = Date.now() + validateControllerConfig(currentConfig, taskId).pollSeconds * 1000;
      while (!stopping && !fs.existsSync(paths.stop) && Date.now() < waitUntil) await sleep(200);
    }
  } catch (error) {
    const reason = String(error?.message || "controller_failed").replace(/[^a-zA-Z0-9_.:-]/gu, "_").slice(0, 100);
    try { saveCheckpoint(controllerRoot, taskId, { status: "BLOCKED", stage_advance_allowed: false, reason }); } catch { /* keep the original failure */ }
    throw error;
  } finally {
    process.removeListener("SIGINT", stop);
    process.removeListener("SIGTERM", stop);
    releaseLock();
  }
}

function localConfigPath() {
  return process.env.DETOT_REVIEW_CONTROLLER_CONFIG || DEFAULT_CONTROLLER_CONFIG;
}

function parseArgs(argv) {
  const [command, ...rest] = argv;
  if (command === "--help" || command === "-h") return { command: "help" };
  const options = {};
  for (let i = 0; i < rest.length; i += 1) {
    if (!rest[i].startsWith("--")) fail("invalid_cli_arguments");
    const key = rest[i].slice(2);
    if (key === "once" || key === "execute") options[key] = true;
    else {
      if (!rest[i + 1] || rest[i + 1].startsWith("--")) fail("invalid_cli_arguments");
      options[key] = rest[++i];
    }
  }
  return { command, options };
}

async function main(argv) {
  const { command, options = {} } = parseArgs(argv);
  if (command === "help") return process.stdout.write("controller start --task ID [--once]\ncontroller stop --task ID\nCodex execution is currently hard-blocked until a trusted OS isolation provider is available.\n");
  const configPath = options.config || localConfigPath();
  const root = path.resolve(options.root || DEFAULT_CONTROLLER_ROOT);
  if (command === "stop") {
    if (!TASK_RE.test(String(options.task || ""))) fail("invalid_task_id");
    const lock = path.join(root, "controller.lock");
    if (!fs.existsSync(lock)) fail("controller_not_running");
    let holder;
    try { holder = JSON.parse(fs.readFileSync(lock, "utf8")); } catch { fail("controller_lock_invalid_manual_recovery_required"); }
    if (holder.task_id !== options.task) fail("controller_task_mismatch");
    fs.writeFileSync(taskPaths(root, options.task).stop, "stop\n", { flag: "w", mode: 0o600 });
    return process.stdout.write("stop_requested\n");
  }
  if (command !== "start") fail("unknown_command");
  const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
  const result = await runController({ taskId: options.task, controllerConfig: config, controllerConfigProvider: () => JSON.parse(fs.readFileSync(configPath, "utf8")), listenerConfig: loadJsonConfig(options["listener-config"]), once: options.once === true, execute: options.execute === true, controllerRoot: root, onState: (state) => process.stdout.write(`${JSON.stringify(state)}\n`) });
  process.stdout.write(`${JSON.stringify(result)}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).catch((error) => {
    process.stderr.write(`${error?.message?.replace(/\b(?:ghp|github_pat)_[A-Za-z0-9_]+/gu, "[REDACTED]") || "review_controller_failed"}\n`);
    process.exitCode = 1;
  });
}
