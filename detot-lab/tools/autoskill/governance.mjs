const TYPES = new Set(["independent", "conversation", "mixed", "unknown"]);
function scopeMatches(scope, target) {
  return Array.isArray(scope?.requirements) && scope.requirements.some((item) =>
    item?.source === target?.source && item?.purpose === target?.purpose && item?.destination === target?.destination);
}

function normalizedType(provenance) {
  return TYPES.has(provenance?.provenance_type) ? provenance.provenance_type : "unknown";
}

export function evaluateArtifactPolicy(artifact, revokedScope = null) {
  const provenance = artifact?.provenance || artifact;
  const type = normalizedType(provenance);
  const sourceIds = Array.isArray(provenance?.source_ids) ? provenance.source_ids.filter(Boolean) : [];
  const dependencies = Array.isArray(provenance?.dependency_ids) ? provenance.dependency_ids.filter(Boolean) : [];
  const retained = provenance?.retention_state === "retained";
  const verifiedIndependent = type === "independent" && provenance?.independence_evidence?.verified === true &&
    Array.isArray(provenance.independence_evidence.source_ids) && provenance.independence_evidence.source_ids.length > 0 &&
    sourceIds.length > 0 && sourceIds.length === provenance.independence_evidence.source_ids.length &&
    sourceIds.every((id) => provenance.independence_evidence.source_ids.includes(id));
  const revoked = Boolean(revokedScope && scopeMatches(provenance?.consent_scope, revokedScope));

  if (type === "unknown") return { decision: "UNKNOWN", action: "quarantine", reason: "provenance_missing_or_unknown" };
  if (provenance?.retention_state === "withdrawn") return { decision: "DERIVED", action: "invalidate_and_delete", reason: "revoked_scope" };
  if (type === "independent") return revoked
    ? { decision: "DERIVED", action: "invalidate_and_delete", reason: "revoked_scope" }
    : verifiedIndependent && retained
      ? { decision: "INDEPENDENT", action: "retain", reason: "verified_independent_source" }
      : { decision: "UNKNOWN", action: "quarantine", reason: "independence_not_verified" };
  const consentRecorded = Number.isSafeInteger(provenance?.consent_revision) && provenance.consent_revision >= 0 &&
    Array.isArray(provenance?.consent_scope?.requirements) && provenance.consent_scope.requirements.length > 0;
  if (type === "conversation" && revoked) return { decision: "DERIVED", action: "invalidate_and_delete", reason: "revoked_scope" };
  if (type === "conversation") return sourceIds.length > 0 && retained && consentRecorded
    ? { decision: "DERIVED", action: "retain_until_revoke", reason: "conversation_lineage_recorded" }
    : { decision: "UNKNOWN", action: "quarantine", reason: "incomplete_conversation_lineage" };
  if (type === "mixed") {
    const segments = Array.isArray(artifact?.content_segments) ? artifact.content_segments : [];
    const separable = segments.filter((segment) => segment?.provenance_type === "independent" &&
      segment?.independence_evidence?.verified === true && segment?.content_hash &&
      Array.isArray(segment?.independence_evidence?.source_ids) && segment.independence_evidence.source_ids.length &&
      Array.isArray(segment?.source_ids) && segment.source_ids.length === segment.independence_evidence.source_ids.length &&
      segment.source_ids.every((id) => segment.independence_evidence.source_ids.includes(id)));
    return separable.length
      ? { decision: "MIXED", action: "separate_verified_segments", reason: revoked ? "revoked_mixed_source_separate_verified_segments" : "verified_independent_segments_only", segment_ids: separable.map((item) => item.segment_id).filter(Boolean) }
      : { decision: "UNKNOWN", action: "quarantine", reason: "mixed_content_not_safely_separable" };
  }
  if (revoked) return { decision: "DERIVED", action: "invalidate_and_delete", reason: "revoked_scope" };
  return { decision: "UNKNOWN", action: "quarantine", reason: "provenance_missing_or_unknown" };
}

export function planConsentRevocation({ artifacts, revokedScope }) {
  if (!revokedScope?.source || !revokedScope?.purpose || !revokedScope?.destination)
    throw new Error("invalid_revoked_scope");
  const items = Array.isArray(artifacts) ? artifacts : [];
  const idFor = (item) => item?.provenance?.artifact_id || item?.artifact_id;
  const counts = new Map();
  for (const item of items) {
    const id = idFor(item);
    if (id) counts.set(id, (counts.get(id) || 0) + 1);
  }
  const duplicates = [...counts].filter(([, count]) => count > 1).map(([id]) => id).sort();
  if (duplicates.length) return {
    revoked_scope: { ...revokedScope },
    actions: items.map((item, index) => ({ artifact_id: idFor(item) || `unidentified:${index}`, action: "quarantine", reason: "duplicate_artifact_id" })),
    impacted_artifact_ids: [],
    requires_review: true,
    blocked: true,
  };
  const unidentified = items.map((item, index) => ({ item, index })).filter(({ item }) => !idFor(item));
  const byId = new Map(items.map((item) => [idFor(item), item]).filter(([id]) => Boolean(id)));
  const impacted = new Set();
  for (const [id, item] of byId) {
    const provenance = item?.provenance || item;
    if (scopeMatches(provenance?.consent_scope, revokedScope)) impacted.add(id);
  }
  let changed = true;
  while (changed) {
    changed = false;
    for (const [id, item] of byId) {
      if (impacted.has(id)) continue;
      const provenance = item?.provenance || item;
      const dependencies = [...(provenance?.dependency_ids || []), ...(provenance?.source_ids || [])];
      if (dependencies.some((dependency) => impacted.has(dependency))) { impacted.add(id); changed = true; }
    }
  }
  const actions = unidentified.map(({ index }) => ({ artifact_id: `unidentified:${index}`, action: "quarantine", reason: "artifact_id_missing" }));
  for (const [id, item] of byId) {
    if (!impacted.has(id)) {
      const policy = evaluateArtifactPolicy(item);
      actions.push({ artifact_id: id, action: policy.action, reason: policy.reason });
      continue;
    }
    const policy = evaluateArtifactPolicy(item, revokedScope);
    if (policy.action === "separate_verified_segments") actions.push({ artifact_id: id, action: policy.action, reason: policy.reason, segment_ids: policy.segment_ids });
    else if (policy.decision === "UNKNOWN") actions.push({ artifact_id: id, action: "quarantine", reason: "revoked_dependency_or_unknown_lineage" });
    else actions.push({ artifact_id: id, action: "invalidate_and_delete", reason: policy.reason === "revoked_scope" ? policy.reason : "derived_from_revoked_artifact" });
  }
  return { revoked_scope: { ...revokedScope }, actions, impacted_artifact_ids: [...impacted].sort(), requires_review: actions.some((item) => item.action === "quarantine" || item.action === "separate_verified_segments"), blocked: false };
}

export function assertArtifactUsable(artifact) {
  const policy = evaluateArtifactPolicy(artifact);
  if (["UNKNOWN"].includes(policy.decision) || policy.action === "quarantine" ||
      artifact?.provenance?.activation_status === "blocked" || artifact?.provenance?.retention_state !== "retained") {
    throw new Error(`artifact_not_usable:${policy.reason}`);
  }
  return policy;
}
