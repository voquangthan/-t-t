import { createArtifactProvenance, createContentSegments, stableArtifactId } from "./provenance.mjs";

function markSegmentsUnknown(segments, artifactId) {
  return (Array.isArray(segments) ? segments : []).map((segment, index) => ({
    ...segment,
    segment_id: String(segment?.segment_id || stableArtifactId("legacy_segment", artifactId, index)),
    artifact_id: artifactId,
    source_ids: [],
    dependency_ids: [],
    provenance_type: "unknown",
    consent_scope: null,
    validation_status: "unknown",
    activation_status: "blocked",
    retention_state: "quarantined",
  }));
}

/** Classifies an old AutoSkill record without inferring provenance or changing its content. */
export function migrateLegacyArtifact({ artifact, ownerId, artifactType, artifactKey }) {
  if (!artifact || typeof artifact !== "object" || Array.isArray(artifact)) throw new Error("invalid_legacy_artifact");
  if (!String(ownerId || "").trim() || !String(artifactType || "").trim() || !String(artifactKey || "").trim())
    throw new Error("legacy_artifact_identity_required");
  if (artifact.provenance && typeof artifact.provenance === "object") {
    return { changed: false, artifact, reason: "provenance_already_present" };
  }
  const id = stableArtifactId("legacy_artifact", ownerId, artifactType, artifactKey);
  const rawSegments = Array.isArray(artifact.content_segments) && artifact.content_segments.length
    ? artifact.content_segments
    : createContentSegments({ artifactId: id, values: { candidate: artifact.candidate, skill_markdown: artifact.skill_markdown },
      sourceIds: [], dependencyIds: [], provenanceType: "unknown", consent: null });
  const migrated = {
    ...artifact,
    provenance: createArtifactProvenance({
      artifactId: id,
      artifactType,
      sourceIds: [],
      dependencyIds: [],
      provenanceType: "unknown",
      validationStatus: "unknown",
      activationStatus: "blocked",
      retentionState: "quarantined",
    }),
    content_segments: markSegmentsUnknown(rawSegments, id),
  };
  return { changed: true, artifact: migrated, reason: "legacy_provenance_quarantined" };
}
