# Consent and privacy audit

## Scope and current storage map

The implementation does not label persisted data as “M1–M4”. The concrete stores found in this repository are:

| Data | Current store / reader | Processing and destination | Consent gate |
|---|---|---|---|
| Owner Memory entries (tasks, notes, devices, people, preferences, events) | `memory-{owner-hash}.bin`; `Practice.ReadOwnerMemories`, query/search APIs | Local query; direct Memory answer can call DeepSeek; Dreaming pairs can be embedded locally and sent to DeepSeek | `memory_core / direct_request / local_only`; separate `DeepSeek`; Dreaming requires both `local_only` and `DeepSeek` |
| Legacy/current work and structured memory | `current-work.bin`, `structured-memory.bin`, `memory-owners.bin`; legacy migration and backup readers | Local migration; selected encrypted backup may be uploaded to Drive | Migration requires local Memory grant; backup/restore requires `memory_core / direct_request / external_service` |
| Memory embeddings and indexes | owner-scoped versioned index files; `SearchMemoryAsync` | Local embedding provider when configured, otherwise lexical fallback; index remains local | Memory local retrieval grant is checked before search |
| Dreaming proposals, progress and retries | owner proposal files and shared progress checkpoint | Worker reads Memory, proposes merges, then stores local proposals; never runs unless both Dreaming grants are active | Owner lease checked before reads, model calls and writes; revocation cancels the lease and clears pending proposals/checkpoints |
| Learning profile notes and skills | `notes.bin`, `learning.bin`, owner-scoped chat-learning files | Chat may add notes/profile to DeepSeek context; skill learner sends one direct Chat message to DeepSeek and stores a summary locally | Separate `learning_notes`, `learning_profile`, `chat_history` purpose/destination grants |
| Chat and AutoSkill | Chat is handled by Gateway; AutoSkill staging config | Direct Chat content goes to the configured Gateway after the user presses send. AutoSkill capture/archive/extraction is disabled in lab staging | Direct send is audited as an explicit action. AutoSkill requires a separate future backend lease before it can be enabled |
| Gmail | Google OAuth account token; search and message preview endpoints | Google returns subject/from/snippet to the local UI; current code does not send Gmail content to DeepSeek | OAuth is not AI consent; search/preview require `gmail / direct_request / local_only` |
| Google Calendar | Google OAuth account token and event APIs | Event list is returned to the local UI; direct chat context requires a separate DeepSeek grant; event writes go to Google | Read requires `calendar / direct_request / local_only`; Chat context requires `DeepSeek`; create/edit/delete requires `external_service` |
| Google Drive and trusted learning | OAuth Drive storage, local encrypted source index/snapshot | User-directed Drive UI operations stay in Drive/local UI. Approved learning collection and sync use external sources; selected Memory backups upload encrypted archives | Drive scopes require `google_drive / direct_request / local_only`; background learning requires `trusted_learning / skill_learning / external_service`; Memory backup has its own external grant |
| Search and Reader | `SearchDiscoveryService`, `UniversalUrlReader` | Search query goes to the selected search provider; fetched public URL text can be included in a direct DeepSeek request | Search request requires `public_web / direct_request / external_service`; Reader text requires `public_web / direct_request / DeepSeek` |
| Weather and device status | No collection worker or connector exists | Not read or transmitted | Catalog entries are explicitly marked inactive; granting them does not activate collection |

## Policy semantics

Consent is keyed by the authenticated owner ID and the exact `(source, purpose, destination)` tuple. A missing, unreadable, corrupt, or unsupported policy entry is denied. OAuth credentials only authorize access to the provider; they never create an AI consent grant. The policy file is owner-hashed and protected with the app's persistent Data Protection keys. Audit rows contain source, purpose, destination, decision, timestamp, actor, request ID and revision only.

Background purposes (`dreaming`, `skill_learning`, `proactive_analysis`) default to deny. No proactive collector is implemented. Dreaming and chat skill learning start only after backend policy checks; the AutoSkill Gateway hook is disabled in staging. Direct Chat send remains an explicit user action and is logged; optional data sources and derived context each need separate grants.

## Revocation behavior

Any policy revision cancels the owner's active consent epoch. Workers use linked cancellation tokens and validate the same lease again before committing results. Revoking Dreaming also clears pending suggestions and retry/progress state. Revoking Chat learning clears the owner's learned profile and removes pending reflection drafts. Revoking trusted learning clears the local index/snapshot and prevents another cloud refresh. Pausing a source remains available after revocation. Local revocation cannot retract content already sent to DeepSeek, Google, or another external provider.

## Known boundaries

- The codebase has no `M1`, `M2`, `M3`, or `M4` identifiers; the table maps the actual stores and routes discovered here instead of assigning new labels.
- Existing local chat transcripts may contain replies derived from data previously sent to a model. Revocation prevents future processing and removes worker-owned derived profiles/proposals; it cannot retract provider-side data or rewrite already delivered transcripts.
- Direct Chat sends send the text the user submits to the configured Gateway. This direct action does not silently authorize additional local sources, files, calendar context, Memory, Reader content, or learned profiles.
- Gmail-to-model and Drive-to-model catalog options describe future paths only; no current route sends those provider contents to DeepSeek.
