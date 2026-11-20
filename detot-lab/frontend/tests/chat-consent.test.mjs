import assert from "node:assert/strict";
import { test } from "node:test";
import { preflightConsentRequirements } from "../src/chatConsent.ts";

test("chat-history consent is enforced by the backend, not the external-consent preflight", () => {
  const requirements = [
    { source: "chat_history", purpose: "direct_request", destination: "DeepSeek" },
    { source: "calendar", purpose: "direct_request", destination: "DeepSeek" },
    { source: "uploaded_files", purpose: "direct_request", destination: "local_only" },
  ];

  assert.deepEqual(preflightConsentRequirements(requirements), requirements.slice(1));
  assert.equal(requirements.length, 3, "filtering does not mutate the request requirements");
});
