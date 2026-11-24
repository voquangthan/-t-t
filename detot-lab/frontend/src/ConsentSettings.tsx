import { useCallback, useEffect, useState } from "react";

type Option = {
  source: string; sourceLabel: string; purpose: string; purposeLabel: string;
  destination: string; destinationLabel: string; description: string;
};
type Grant = { source: string; purpose: string; destination: string; allowed: boolean; changedAt?: string | null };
type Audit = { source: string; purpose: string; destination: string; decision: string; at: string; actor: string; requestId?: string | null; policyRevision: number };
type Snapshot = { options: Option[]; grants: Grant[]; audit: Audit[] };

const decisions: Record<string, string> = {
  granted: "Đã cấp quyền", revoked: "Đã thu hồi", allowed: "Được phép", denied: "Bị từ chối",
};

export function ConsentSettings() {
  const [snapshot, setSnapshot] = useState<Snapshot>({ options: [], grants: [], audit: [] });
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const reload = useCallback(async () => {
    const response = await fetch("/api/consent", { cache: "no-store" });
    if (!response.ok) throw new Error(response.status === 401 ? "Phiên đăng nhập đã hết hạn." : "Không tải được quyền riêng tư.");
    setSnapshot(await response.json());
  }, []);
  useEffect(() => { void reload().catch(cause => setError(cause instanceof Error ? cause.message : "Không tải được quyền.")); }, [reload]);

  async function setAllowed(option: Option, allowed: boolean) {
    const key = `${option.source}:${option.purpose}:${option.destination}`;
    setBusy(key); setError(""); setNotice("");
    try {
      const response = await fetch("/api/consent", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...option, allowed, requestId: crypto.randomUUID() }) });
      const data = await response.json().catch(() => null);
      if (!response.ok) throw new Error(data?.error ?? "Không lưu được thay đổi quyền.");
      setNotice(allowed ? "Đã cấp quyền cho đúng nguồn, mục đích và nơi xử lý này." :
        "Đã thu hồi quyền. Luồng nền mới sẽ dừng; dữ liệu đã gửi tới nhà cung cấp bên ngoài không thể được thu hồi bằng thao tác tại đây.");
      await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không thể cập nhật quyền."); }
    finally { setBusy(""); }
  }

  const grantFor = (option: Option) => snapshot.grants.find(grant => grant.source === option.source &&
    grant.purpose === option.purpose && grant.destination === option.destination);

  return <section className="transcript consent-settings" aria-label="Quyền riêng tư và đồng ý xử lý dữ liệu">
    <div className="cloud-heading"><div><h1>Quyền riêng tư</h1><p>Mọi quyền dưới đây mặc định tắt. Computer Use vẫn có thể mở ứng dụng được duyệt cục bộ; bật riêng “Chế độ thông minh” mới cho phép gửi cấu trúc UI và nhãn đã lọc qua Gateway. Các quyền OAuth chỉ kết nối dịch vụ, không tự cấp quyền gửi nội dung cho model hoặc xử lý nền.</p></div>
      <button disabled={!!busy} onClick={() => void reload().catch(cause => setError(cause instanceof Error ? cause.message : "Không tải được quyền."))}>Làm mới</button></div>
    {error && <p className="cloud-error" role="alert">{error}</p>}{notice && <p className="cloud-notice" role="status">{notice}</p>}
    <div className="consent-list">{snapshot.options.map(option => {
      const grant = grantFor(option);
      const key = `${option.source}:${option.purpose}:${option.destination}`;
      return <article className="consent-option" key={key}>
        <div><strong>{option.sourceLabel}</strong><span>{option.purposeLabel} → {option.destinationLabel}</span><p>{option.description}</p>
          <small>{grant?.changedAt ? `Cập nhật ${new Date(grant.changedAt).toLocaleString()}` : "Chưa từng cấp quyền"}</small></div>
        <label className="consent-toggle"><span>{grant?.allowed ? "Đang cho phép" : "Đang chặn"}</span>
          <input type="checkbox" checked={grant?.allowed ?? false} disabled={!!busy} aria-label={`${grant?.allowed ? "Thu hồi" : "Cấp"} quyền ${option.sourceLabel}: ${option.purposeLabel}, ${option.destinationLabel}`}
            onChange={event => void setAllowed(option, event.currentTarget.checked)} />
        </label>
      </article>;
    })}</div>
    <section className="consent-audit"><h2>Lịch sử quyền gần đây</h2><p>Chỉ lưu metadata: nguồn, mục đích, nơi xử lý, quyết định và thời điểm. Không ghi nội dung Memory, Chat, thư hay sự kiện.</p>
      {snapshot.audit.length ? <div className="consent-audit-list">{snapshot.audit.map((item, index) => <article key={`${item.policyRevision}-${item.at}-${index}`}>
        <strong>{decisions[item.decision] ?? item.decision}</strong><span>{item.source} · {item.purpose} → {item.destination}</span>
        <small>{new Date(item.at).toLocaleString()} · {item.actor}{item.requestId ? ` · ${item.requestId}` : ""}</small>
      </article>)}</div> : <p>Chưa có quyết định nào được ghi nhận.</p>}
    </section>
    <p className="consent-retention">Thu hồi quyền dừng xử lý mới, hủy lượt nền đang chạy và xóa gợi ý/hồ sơ phái sinh liên quan khi có thể. Lịch sử Chat do bạn lưu vẫn cần được xóa riêng. Dữ liệu đã gửi cho nhà cung cấp bên ngoài không thể tự thu hồi từ đây.</p>
  </section>;
}
