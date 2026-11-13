import { verifyRestoreState } from "./restore-preflight.mjs";

try {
  const [state, authority] = process.argv.slice(2);
  if (!state || !authority) throw new Error("Restore state and authority are required.");
  await verifyRestoreState(state, authority);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
