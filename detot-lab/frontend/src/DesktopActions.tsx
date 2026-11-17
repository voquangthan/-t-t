import { useCallback, useEffect, useState } from "react";

type WorkspaceStatus = {
  available: boolean;
  repository?: string | null;
  branch?: string | null;
  hasTrackedChanges?: boolean | null;
  message?: string | null;
};

type ComputerUsePermissions = {
  observe: string[];
  navigate: string[];
  write: string[];
  codeExecution: boolean;
};

type DesktopObservation = {
  error?: string;
  authenticated?: boolean;
  login?: string | null;
  needsUserLogin?: boolean;
  repository?: string;
  applications?: string[];
  pullRequests?: Array<{
    number: number;
    title: string;
    state: string;
    merged: boolean;
    headSha: string;
    headBranch: string | null;
    baseBranch: string | null;
    checks: Array<{ name: string; status: string; conclusion: string | null }>;
  }>;
};

const permissionLabels: Record<string, string> = {
  "approved-applications": "ứng dụng được duyệt",
  "github-account": "tài khoản GitHub trong Edge riêng",
  "github-pull-requests": "PR và CI công khai",
  "open-workspace": "mở workspace",
  "open-github": "mở trang PR GitHub",
  "open-edge": "mở Edge",
  "open-vscode": "mở VS Code",
};

const displayPermissions = (values: string[] | undefined) =>
  values?.length ? values.map(value => permissionLabels[value] ?? value).join(", ") : "không có quyền";

export function DesktopActions() {
  const [status, setStatus] = useState<WorkspaceStatus>();
  const [permissions, setPermissions] = useState<ComputerUsePermissions>();
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [observation, setObservation] = useState<DesktopObservation>();
  const [observing, setObserving] = useState("");

  const reload = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const [response, permissionsResponse] = await Promise.all([
        fetch("/api/desktop/status", { cache: "no-store" }),
        fetch("/api/desktop/permissions", { cache: "no-store" }),
      ]);
      if (!response.ok) throw new Error(response.status === 404
        ? "Thao tác desktop chỉ khả dụng khi Đệ Tốt chạy local trên Windows."
        : `Không đọc được workspace (HTTP ${response.status}).`);
      if (!permissionsResponse.ok) throw new Error(`Không đọc được quyền Computer Use (HTTP ${permissionsResponse.status}).`);
      setStatus(await response.json() as WorkspaceStatus);
      setPermissions(await permissionsResponse.json() as ComputerUsePermissions);
    } catch (cause) {
      setStatus(undefined);
      setPermissions(undefined);
      setError(cause instanceof Error ? cause.message : "Không đọc được trạng thái workspace.");
    } finally { setLoading(false); }
  }, []);

  useEffect(() => { void reload(); }, [reload]);

  async function execute(action: "open-workspace" | "open-github" | "open-edge" | "open-vscode") {
    setBusy(action); setError(""); setNotice("");
    try {
      const response = await fetch("/api/desktop/actions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action }),
      });
      const result = await response.json() as { message?: string; detail?: string; title?: string };
      if (!response.ok) throw new Error(result.detail ?? result.message ?? `Không thực hiện được hành động (HTTP ${response.status}).`);
      setNotice(result.message ?? "Đã gửi yêu cầu mở.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Không thực hiện được hành động.");
    } finally { setBusy(""); }
  }

  async function observe(target: "github-account" | "github-pull-requests") {
    setObserving(target); setError(""); setNotice("");
    try {
      const response = await fetch("/api/desktop/observe", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ target }),
      });
      const result = await response.json() as DesktopObservation;
      if (!response.ok || result.error) throw new Error(result.error ?? `Không đọc được GitHub (HTTP ${response.status}).`);
      setObservation(result);
      if (target === "github-account" && result.needsUserLogin)
        setNotice("Profile Edge riêng của Đệ Tốt chưa đăng nhập GitHub. Hãy đăng nhập trực tiếp trong cửa sổ Edge nếu muốn dùng profile này.");
      else if (target === "github-account")
        setNotice(result.login ? `Profile Edge đang đăng nhập với tài khoản ${result.login}.` : "Profile Edge chưa xác định được tài khoản.");
      else setNotice("Đã đọc danh sách PR và trạng thái CI công khai; không đọc nội dung review hoặc file trong repository.");
    } catch (cause) {
      setObservation(undefined);
      setError(cause instanceof Error ? cause.message : "Không đọc được GitHub.");
    } finally { setObserving(""); }
  }

  async function observeApplications() {
    setObserving("approved-applications"); setError(""); setNotice("");
    try {
      const response = await fetch("/api/desktop/observe", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ target: "approved-applications" }),
      });
      const result = await response.json() as DesktopObservation;
      if (!response.ok || result.error) throw new Error(result.error ?? `Không đọc được ứng dụng đang mở (HTTP ${response.status}).`);
      setObservation(result);
      setNotice("Chỉ đọc tên ứng dụng thuộc danh sách cho phép; không đọc tiêu đề hay nội dung cửa sổ.");
    } catch (cause) {
      setObservation(undefined);
      setError(cause instanceof Error ? cause.message : "Không đọc được ứng dụng đang mở.");
    } finally { setObserving(""); }
  }

  return <section className="desktop-actions-page">
    <div className="cloud-heading"><div><span className="eyebrow">THAO TÁC CỤC BỘ</span><h1>Trợ lý máy tính</h1>
      <p>Mở workspace, mở repository GitHub và kiểm tra nhánh cùng trạng thái Git.</p></div>
      <button type="button" disabled={loading || !!busy} onClick={() => void reload()}>Kiểm tra lại</button>
    </div>
    <div className="desktop-safety-note" role="note">
      <strong>Quyền hiện tại:</strong> Quan sát: {permissions ? displayPermissions(permissions.observe) : "đang tải"}. Điều hướng: {permissions ? displayPermissions(permissions.navigate) : "đang tải"}. Ghi: {permissions ? displayPermissions(permissions.write) : "đang tải"}. Chạy mã: {permissions?.codeExecution ? "được bật" : permissions ? "đang bị khóa" : "đang tải"}. Đăng nhập/chọn tài khoản không được tự động hóa.
    </div>
    {loading && <p role="status">Đang kiểm tra workspace…</p>}
    {error && <p className="desktop-message desktop-error" role="alert">{error}</p>}
    {notice && <p className="desktop-message desktop-notice" role="status">{notice}</p>}
    {!loading && status && <section className="desktop-workspace-card" aria-label="Trạng thái workspace">
      <div className="overview-card-head"><div><h2>Repository đang mở</h2></div><span className={status.available ? "desktop-state-ok" : "desktop-state-error"}>{status.available ? "Local" : "Không khả dụng"}</span></div>
      <dl className="desktop-status-grid">
        <div><dt>Nhánh</dt><dd>{status.branch ?? "Chưa xác định"}</dd></div>
        <div><dt>Thay đổi đã theo dõi</dt><dd>{status.hasTrackedChanges === null || status.hasTrackedChanges === undefined ? "Chưa đọc được" : status.hasTrackedChanges ? "Có thay đổi" : "Sạch"}</dd></div>
        <div><dt>Remote GitHub</dt><dd>{status.repository ? <a href={status.repository} target="_blank" rel="noreferrer">{status.repository.replace("https://github.com/", "")}</a> : "Không có remote GitHub hợp lệ"}</dd></div>
      </dl>
      {status.message && <p className="desktop-muted">{status.message}</p>}
      <div className="desktop-actions-row">
        <button type="button" disabled={!!busy || !status.available} onClick={() => void execute("open-workspace")}>{busy === "open-workspace" ? "Đang mở…" : "Mở workspace"}</button>
        <button type="button" disabled={!!busy} onClick={() => void execute("open-vscode")}>{busy === "open-vscode" ? "Đang mở…" : "Mở VS Code"}</button>
        <button type="button" disabled={!!busy} onClick={() => void execute("open-edge")}>{busy === "open-edge" ? "Đang mở…" : "Mở Edge"}</button>
        <button className="primary" type="button" disabled={!!busy || !status.repository} onClick={() => void execute("open-github")}>{busy === "open-github" ? "Đang mở…" : "Mở repository GitHub"}</button>
      </div>
      <div className="desktop-actions-row">
        <button type="button" disabled={!!observing} onClick={() => void observeApplications()}>{observing === "approved-applications" ? "Đang quan sát…" : "Ứng dụng đang mở"}</button>
        <button type="button" disabled={!!observing} onClick={() => void observe("github-account")}>{observing === "github-account" ? "Đang kiểm tra…" : "Kiểm tra tài khoản trong Edge riêng"}</button>
        <button type="button" disabled={!!observing || !status.repository} onClick={() => void observe("github-pull-requests")}>{observing === "github-pull-requests" ? "Đang đọc…" : "Đọc PR và CI"}</button>
      </div>
    </section>}
    {observation?.applications && <section className="desktop-workspace-card" aria-label="Ứng dụng đang mở">
      <div className="overview-card-head"><h2>Ứng dụng đang mở</h2><span className="desktop-muted">Chỉ tên ứng dụng</span></div>
      {observation.applications.length === 0 ? <p className="desktop-muted">Không thấy cửa sổ của ứng dụng trong danh sách được hỗ trợ.</p> :
        <ul>{observation.applications.map(application => <li key={application}>{application}</li>)}</ul>}
      <p className="desktop-muted">Không đọc tiêu đề, văn bản, tab hoặc nội dung bên trong cửa sổ.</p>
    </section>}
    {observation?.authenticated && observation.login && <section className="desktop-workspace-card" aria-label="Tài khoản GitHub">
      <div className="overview-card-head"><h2>Tài khoản trong profile Edge riêng</h2><span className="desktop-state-ok">Đã đăng nhập</span></div>
      <p className="desktop-muted">{observation.login}. Đây là profile riêng do adapter quản lý; không đọc tài khoản trong cửa sổ Edge cá nhân khác.</p>
    </section>}
    {observation?.pullRequests && <section className="desktop-workspace-card" aria-label="Pull Request và CI">
      <div className="overview-card-head"><h2>Pull Request gần đây</h2><span className="desktop-muted">{observation.repository}</span></div>
      {observation.pullRequests.length === 0 ? <p className="desktop-muted">Không có Pull Request công khai gần đây.</p> : <ul className="desktop-pr-list">
        {observation.pullRequests.map(pr => <li key={`${pr.number}-${pr.headSha}`}>
          <div><a href={`https://github.com/${observation.repository}/pull/${pr.number}`} target="_blank" rel="noreferrer">#{pr.number} {pr.title}</a>
            <small>{pr.state}{pr.merged ? " · đã merge" : ""} · {pr.headBranch ?? "?"} → {pr.baseBranch ?? "?"} · {pr.headSha.slice(0, 8)}</small></div>
          <div className="desktop-check-list">{pr.checks.length ? pr.checks.map((check, index) => <span key={`${check.name}-${index}`} className={check.conclusion === "success" ? "desktop-check-ok" : check.conclusion === "failure" ? "desktop-check-error" : ""}>{check.name}: {check.conclusion ?? check.status}</span>) : <span>Chưa có check run</span>}</div>
        </li>)}
      </ul>}
    </section>}
  </section>;
}
