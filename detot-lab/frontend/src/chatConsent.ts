export interface ConsentRequirement {
  source: string;
  purpose: string;
  destination: string;
}

// The backend still checks every requirement before normal chat. Local
// Computer Use actions do not send chat history to an external provider.
export function preflightConsentRequirements<T extends ConsentRequirement>(requirements: readonly T[]): T[] {
  return requirements.filter(item => item.source !== "chat_history");
}
