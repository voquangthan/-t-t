import { useCallback, useEffect, useState } from "react";

type Evidence = {
  id: string; text: string; category: string; status: string; source: string;
  conversationId?: string; createdAt?: string; version: number; history: string[];
};
type Suggestion = {
  id: string; kind: "merge" | "update" | "supersede" | "archive"; evidence: Evidence[];
  reason: string; proposedText?: string; confidence: number; createdAt: string; assessment?: string;
};
type WorkerStatus = { scanned: number; total: number; pending: boolean; retryCount: number; retryAt?: string };
const labels: Record<Suggestion["kind"], string> = {
  merge: "Gộp ký ức trùng", update: "Cập nhật thông tin", supersede: "Thông tin mới thay thế cũ", archive: "Lưu trữ ký ức",
};

export function MemoryReview() {
  const [items, setItems] = useState<Suggestion[]>([]);
  const [worker, setWorker] = useState<WorkerStatus>({ scanned: 0, total: 0, pending: false, retryCount: 0 });
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const reload = useCallback(async () => {
    const [response, statusResponse] = await Promise.all([fetch("/api/memory/review"), fetch("/api/memory/dreaming")]);
    if (!response.ok || !statusResponse.ok) throw new Error(response.status === 401 || statusResponse.status === 401 ? "Phiên đăng nhập đã hết hạn." : "Không tải được trạng thái Dreaming.");
    const [data, status]: [Suggestion[], WorkerStatus] = await Promise.all([response.json(), statusResponse.json()]);
    setItems(data);
    setWorker(status);
    setEdits(current => Object.fromEntries(data.map(item => [item.id, current[item.id] ?? item.proposedText ?? ""])));
  }, []);
  useEffect(() => { void reload().catch(cause => setError(cause instanceof Error ? cause.message : "Không tải được đề xuất.")); }, [reload]);
  async function decide(item: Suggestion, action: "approve" | "edit" | "reject") {
    setBusy(item.id); setError(""); setNotice("");
    try {
      const response = await fetch(`/api/memory/review/${item.id}`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action, text: action === "edit" ? edits[item.id] : undefined }),
      });
      if (response.status === 409) { await reload(); throw new Error("Ký ức nguồn đã đổi trong lúc chờ duyệt. Đề xuất cũ đã hết hạn; hãy xem đề xuất mới."); }
      if (!response.ok) throw new Error("Không thể cập nhật đề xuất.");
      setNotice(action === "reject" ? "Đã từ chối đề xuất." : "Đã ghi thay đổi và lịch sử ký ức.");
      await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không thể xử lý đề xuất."); }
    finally { setBusy(null); }
  }
  return <section className="transcript memory-review" aria-label="Đề xuất Dreaming">
    <div className="cloud-heading"><div><h1>Dreaming · rà soát ký ức</h1><p>Đề xuất do hệ thống tìm ứng viên bằng BGE-M3 và đánh giá bằng DeepSeek. Ký ức gốc chỉ đổi sau khi bạn duyệt.</p></div>
      <button disabled={busy !== null} onClick={() => void reload().catch(cause => setError(cause instanceof Error ? cause.message : "Không tải được đề xuất."))}>Làm mới</button></div>
    <div className="cloud-status" aria-live="polite"><strong>Quét nền: {worker.scanned}/{worker.total}</strong><p>{worker.pending ? "Đang chờ hoặc tiếp tục phân tích theo lô." : "Đã quét hết ký ức hiện tại."}{worker.retryCount ? ` Lượt thử lại: ${worker.retryCount}${worker.retryAt ? ` · tiếp theo ${new Date(worker.retryAt).toLocaleTimeString()}` : ""}.` : ""}</p></div>
    {error && <p className="cloud-error" role="alert">{error}</p>}{notice && <p className="cloud-notice" role="status">{notice}</p>}
    {items.length === 0 ? <div className="cloud-status"><strong>Chưa có đề xuất cần duyệt</strong><p>Khi có ký ức mới, Dreaming sẽ kiểm tra trong nền.</p></div> : items.map(item => <article className="cloud-item memory-suggestion" key={item.id}>
      <div className="cloud-item-title"><strong>{labels[item.kind]}</strong><span>{Math.round(item.confidence * 100)}% · {new Date(item.createdAt).toLocaleString()}</span></div>
      <p><strong>Gợi ý suy luận, chưa xác minh:</strong> {item.reason}</p>
      <div className="memory-evidence">{item.evidence.map(entry => <section key={entry.id}>
        <strong>{entry.category} · {entry.status} · v{entry.version}</strong>
        <p>{entry.text}</p><code>{entry.id} · {entry.source}{entry.conversationId ? ` · chat ${entry.conversationId}` : ""}</code>
        <details><summary>Lịch sử bằng chứng</summary><ul>{entry.history.map((change, index) => <li key={`${entry.id}-${index}`}>{change}</li>)}</ul></details>
      </section>)}</div>
      {(item.kind === "merge" || item.kind === "update") && <label className="memory-edit">Nội dung sau khi duyệt<textarea value={edits[item.id] ?? ""} maxLength={500} onChange={event => setEdits(current => ({ ...current, [item.id]: event.target.value }))}/></label>}
      <div className="cloud-actions"><button disabled={busy !== null} onClick={() => void decide(item, "approve")}>Duyệt đề xuất</button>
        {(item.kind === "merge" || item.kind === "update") && <button disabled={busy !== null || !(edits[item.id] ?? "").trim()} onClick={() => void decide(item, "edit")}>Sửa rồi duyệt</button>}
        <button disabled={busy !== null} onClick={() => void decide(item, "reject")}>Từ chối</button></div>
    </article>)}
  </section>;
}
