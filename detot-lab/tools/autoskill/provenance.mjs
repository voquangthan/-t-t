import crypto from "node:crypto";

export const CONSENT_REQUIREMENTS = Object.freeze({
  capture: Object.freeze([
    Object.freeze({ source: "chat_history", purpose: "skill_learning", destination: "local_only" }),
  ]),
  analysis: Object.freeze([
    Object.freeze({ source: "chat_history", purpose: "skill_learning", destination: "local_only" }),
    Object.freeze({ source: "chat_history", purpose: "skill_learning", destination: "DeepSeek" }),
  ]),
});

const PROVENANCE_TYPES = new Set(["independent", "conversation", "mixed", "unknown"]);
const VALIDATION_STATUSES = new Set(["captured", "pending_review", "replay_passed", "validated", "unknown"]);
const ACTIVATION_STATUSES = new Set(["inactive", "active", "not_applicable", "blocked", "unknown"]);
const RETENTION_STATES = new Set(["retained", "quarantined", "withdrawn", "unknown"]);

function normalizeConsentScope(consent) {
  if (!consent || !Number.isSafeInteger(consent.revision) || consent.revision < 0 || !Array.isArray(consent.requirements)) return null;
  const same = (left, right) => left?.source === right.source && left?.purpose === right.purpose && left?.destination === right.destination;
  const valid = Object.values(CONSENT_REQUIREMENTS).find((expected) => expected.length === consent.requirements.length &&
    expected.every((item, index) => same(item, consent.requirements[index])));
  return valid ? { revision: consent.revision, requirements: valid.map((item) => ({ ...item })) } : null;
}

export function normalizeConsentGrant(value, stage) {
  const expected = CONSENT_REQUIREMENTS[stage];
  if (!expected || !value || typeof value !== "object" || value.allowed !== true ||
      !Number.isSafeInteger(value.revision) || value.revision < 0) return null;
  if (value.stage !== undefined && value.stage !== stage) return null;
  const supplied = value.requirements;
  if (!Array.isArray(supplied) || supplied.length !== expected.length) return null;
  const same = (left, right) => left?.source === right.source && left?.purpose === right.purpose &&
    left?.destination === right.destination;
  if (!expected.every((requirement, index) => same(requirement, supplied[index]))) return null;
  return { stage, revision: value.revision, requirements: expected.map((item) => ({ ...item })) };
}

export function stableArtifactId(type, ...parts) {
  const name = String(type || "unknown").toLowerCase();
  const digest = crypto.createHash("sha256").update([name, ...parts.map((part) => String(part ?? ""))].join("\0")).digest("hex");
  return `${name}:${digest.slice(0, 32)}`;
}

export function hashContent(value) {
  return crypto.createHash("sha256").update(typeof value === "string" ? value : JSON.stringify(value)).digest("hex");
}

export function createArtifactProvenance({
  artifactId,
  artifactType,
  sourceIds = [],
  dependencyIds = [],
  provenanceType = "unknown",
  consent,
  validationStatus = "unknown",
  activationStatus = "unknown",
  retentionState = "unknown",
}) {
  const normalizedConsent = normalizeConsentScope(consent);
  const validProvenance = PROVENANCE_TYPES.has(provenanceType) ? provenanceType : "unknown";
  return {
    artifact_id: String(artifactId || ""),
    artifact_type: String(artifactType || "unknown"),
    source_ids: [...new Set(sourceIds.map(String).filter(Boolean))],
    dependency_ids: [...new Set(dependencyIds.map(String).filter(Boolean))],
    provenance_type: validProvenance,
    consent_scope: normalizedConsent,
    consent_revision: normalizedConsent?.revision ?? null,
    validation_status: VALIDATION_STATUSES.has(validationStatus) ? validationStatus : "unknown",
    activation_status: ACTIVATION_STATUSES.has(activationStatus) ? activationStatus : "unknown",
    retention_state: RETENTION_STATES.has(retentionState) ? retentionState : "unknown",
  };
}

export function createContentSegments({ artifactId, values, sourceIds, dependencyIds, provenanceType, consent }) {
  const segments = [];
  const normalizedConsent = normalizeConsentScope(consent);
  const visit = (value, fieldPath) => {
    if (typeof value === "string") {
      if (!value.trim()) return;
      const contentHash = hashContent(value);
      segments.push({
        segment_id: stableArtifactId("segment", artifactId, fieldPath, contentHash),
        artifact_id: artifactId,
        field_path: fieldPath,
        content_hash: contentHash,
        source_ids: [...new Set(sourceIds.map(String).filter(Boolean))],
        dependency_ids: [...new Set(dependencyIds.map(String).filter(Boolean))],
        provenance_type: PROVENANCE_TYPES.has(provenanceType) ? provenanceType : "unknown",
        consent_scope: normalizedConsent,
        validation_status: "pending_review",
        activation_status: "inactive",
        retention_state: "retained",
      });
      return;
    }
    if (Array.isArray(value)) {
      value.forEach((item, index) => visit(item, `${fieldPath}[${index}]`));
      return;
    }
    if (value && typeof value === "object") {
      for (const [key, item] of Object.entries(value)) visit(item, fieldPath ? `${fieldPath}.${key}` : key);
    }
  };
  visit(values, "");
  return segments;
}
