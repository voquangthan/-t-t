const githubHttpError = /^github_http_(\d{3})$/u;

export function summarizeCheckRuns(response, limit = 30) {
  if (response && typeof response === "object" && typeof response.error === "string") {
    const match = githubHttpError.exec(response.error);
    const statusCode = match ? Number(match[1]) : null;
    return {
      checksStatus: "unavailable",
      checksError: response.rateLimited ? "rate_limited" : statusCode ? "http_error" : "invalid_response",
      checksHttpStatus: statusCode,
      checksTotalCount: null,
      checksTruncated: false,
      checks: [],
    };
  }

  if (!response || typeof response !== "object" || !Array.isArray(response.check_runs)) {
    return {
      checksStatus: "unavailable",
      checksError: "invalid_response",
      checksHttpStatus: null,
      checksTotalCount: null,
      checksTruncated: false,
      checks: [],
    };
  }

  const totalCount = Number.isSafeInteger(response.total_count) && response.total_count >= 0
    ? response.total_count
    : response.check_runs.length;
  const checks = response.check_runs.slice(0, limit).map(run => ({
    name: typeof run?.name === "string" ? run.name.slice(0, 120) : "check",
    status: typeof run?.status === "string" ? run.status : "unknown",
    conclusion: typeof run?.conclusion === "string" ? run.conclusion : null,
  }));
  return {
    checksStatus: totalCount === 0 ? "empty" : "available",
    checksError: null,
    checksHttpStatus: null,
    checksTotalCount: totalCount,
    checksTruncated: totalCount > checks.length,
    checks,
  };
}
