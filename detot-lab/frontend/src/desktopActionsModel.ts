export type GithubAccountStatus = "verified" | "signed_out" | "not_verified" | "failed";

export type DesktopCheck = { name: string; status: string | null; conclusion: string | null };

export type DesktopPullRequest = {
  number: number;
  title: string;
  state: string;
  merged?: boolean;
  headSha: string | null;
  headBranch?: string | null;
  baseBranch?: string | null;
  checks: DesktopCheck[];
  checksStatus?: "available" | "empty" | "unavailable";
  checksError?: "rate_limited" | "http_error" | "invalid_response" | null;
  checksHttpStatus?: number | null;
  totalChecks?: number | null;
  displayedChecks?: number | null;
  checksTruncated?: boolean;
  failedChecks?: number;
  failedCheckNames?: string[];
};

export type DesktopObservation = {
  error?: string;
  status?: GithubAccountStatus;
  username?: string | null;
  authenticated?: boolean;
  login?: string | null;
  needsUserLogin?: boolean;
  repository?: string;
  applications?: string[];
  observationStatus?: "available" | "empty" | "partial" | "unavailable";
  pullRequests?: DesktopPullRequest[];
};

export function githubAccountPresentation(result: DesktopObservation) {
  let status: GithubAccountStatus = result.status ??
    (result.authenticated && result.login ? "verified" : result.needsUserLogin ? "signed_out" : "not_verified");
  const username = status === "verified" ? result.username ?? result.login ?? null : null;
  if (status === "verified" && !username) status = "not_verified";
  switch (status) {
    case "verified":
      return { status, label: "Đã đăng nhập", title: "Profile Edge đang đăng nhập", detail: username ?? "Không có username xác minh được.", notice: username ? `Profile Edge đang đăng nhập với tài khoản ${username}.` : "Không xác minh được username trong profile Edge." };
    case "signed_out":
      return { status, label: "Đang đăng xuất", title: "Profile Edge riêng chưa đăng nhập GitHub", detail: "Không tìm thấy phiên đăng nhập trong profile riêng.", notice: "Profile Edge riêng của Đệ Tốt đang đăng xuất khỏi GitHub." };
    case "failed":
      return { status, label: "Kiểm tra thất bại", title: "Không thể kiểm tra tài khoản", detail: "Edge hoặc GitHub không phản hồi để xác minh.", notice: "Không thể kiểm tra tài khoản GitHub trong profile Edge riêng." };
    default:
      return { status: "not_verified" as const, label: "Chưa xác minh", title: "Chưa xác định được tài khoản", detail: "Giao diện chưa cung cấp bằng chứng rõ ràng về trạng thái đăng nhập.", notice: "Profile Edge chưa xác định được tài khoản GitHub." };
  }
}

export function pullRequestMetadata(pr: DesktopPullRequest): string {
  return `${pr.state}${pr.merged ? " · đã merge" : ""} · ${pr.headBranch ?? "?"} → ${pr.baseBranch ?? "?"} · ${(pr.headSha ?? "").slice(0, 8)}`;
}

export function pullRequestChecksSummary(pr: DesktopPullRequest): string | null {
  const total = pr.totalChecks ?? pr.checks.length;
  const displayed = pr.displayedChecks ?? pr.checks.length;
  const failures = pr.failedChecks ?? 0;
  const failureNames = pr.failedCheckNames?.slice(0, 10) ?? [];
  if (!pr.checksTruncated && failures === 0) return null;
  const failureSummary = failures > 0
    ? ` · ${failures} check lỗi${failureNames.length ? `: ${failureNames.join(", ")}` : ""}`
    : "";
  const truncatedSummary = pr.checksTruncated ? " · danh sách bị cắt; kết quả dựa trên dữ liệu đã lấy" : "";
  return `Đang hiện ${displayed}/${total} checks${failureSummary}${truncatedSummary}.`;
}
