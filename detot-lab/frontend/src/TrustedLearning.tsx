import { useCallback, useEffect, useState } from "react";

type Source = {
  id: string; type: string; name: string; scope: string; license: string; licenseUrl: string;
  status: string; checkedUtc?: string | null; approvedUtc?: string | null; lastSyncUtc?: string | null;
  fileCount: number; lastAccepted: number; lastReview: number; lastDuplicate: number;
  checkSummary?: string | null; lastError?: string | null;
};
type DriveScope = { id: string; name: string; files: number };
type Knowledge = { text: string; source: string; citation: string; license: string; sha256: string; relevance: number };
type ReviewItem = { hash: string; sourceName: string; title: string; text: string; citation: string; license: string };

async function api<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`/api/learning/${path}`, options);
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error ?? body?.detail ?? `Learning API lỗi HTTP ${response.status}.`);
  return body as T;
}

const licenses = ["CC-BY-4.0", "CC-BY-SA-4.0", "CC0-1.0", "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "PSF-2.0", "Python-2.0", "MPL-2.0", "GPL-3.0-only", "GPL-3.0-or-later", "Public-Domain", "Proprietary-Owner-Provided"];
const date = (value?: string | null) => value ? new Intl.DateTimeFormat("vi-VN", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)) : "chưa có";

export function TrustedLearning() {
  const [sources, setSources] = useState<Source[]>([]);
  const [driveScopes, setDriveScopes] = useState<DriveScope[]>([]);
  const [type, setType] = useState("website");
  const [name, setName] = useState("");
  const [scope, setScope] = useState("");
  const [license, setLicense] = useState("");
  const [licenseUrl, setLicenseUrl] = useState("");
  const [knowledgeQuery, setKnowledgeQuery] = useState("");
  const [knowledge, setKnowledge] = useState<Knowledge[]>([]);
  const [review, setReview] = useState<ReviewItem[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const reload = useCallback(async () => {
    try {
      const [items, scopes, reviewItems] = await Promise.all([
        api<Source[]>("sources"), api<DriveScope[]>("drive-scopes").catch(() => []), api<ReviewItem[]>("review"),
      ]);
      setSources(items); setDriveScopes(scopes); setReview(reviewItems);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không tải được danh sách nguồn."); }
  }, []);
  useEffect(() => { void reload(); }, [reload]);

  const search = async () => {
    setBusy(true); setError("");
    try { setKnowledge(await api<Knowledge[]>(`knowledge?q=${encodeURIComponent(knowledgeQuery)}`)); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Không truy vấn được kho kiến thức."); }
    finally { setBusy(false); }
  };

  const add = async (event: React.FormEvent) => {
    event.preventDefault(); setBusy(true); setError(""); setMessage("");
    try {
      const source = await api<Source>("sources", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type, name, scope: type === "drive-folder" ? scope : scope.trim(), license: type === "drive-folder" ? "Per-artifact-metadata" : license, licenseUrl: licenseUrl.trim() }) });
      const checked = await api<Source>(`sources/${source.id}/check`, { method: "POST" });
      setMessage(checked.checkSummary ?? "Đã kiểm tra nguồn. Đọc kết quả và phê duyệt nếu phù hợp.");
      setName(""); setScope(""); setLicenseUrl(""); await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không thể thêm hoặc kiểm tra nguồn."); await reload(); }
    finally { setBusy(false); }
  };

  const mutate = async (id: string, action: "check" | "approve" | "sync" | "delete") => {
    if (action === "delete" && !confirm("Xóa nguồn và các trích đoạn kiến thức của nó khỏi chỉ mục? File gốc trên Drive không bị xóa.")) return;
    setBusy(true); setError(""); setMessage("");
    try {
      if (action === "delete") { await api(`sources/${id}`, { method: "DELETE" }); setMessage("Đã xóa nguồn khỏi danh sách và chỉ mục kiến thức."); }
      else {
        const result = await api<Source | { message?: string }>(`sources/${id}/${action}`, { method: "POST" });
        setMessage((result as Source).checkSummary ?? (result as { message?: string }).message ?? "Thao tác với nguồn đã xong.");
      }
      await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Thao tác với nguồn thất bại."); }
    finally { setBusy(false); }
  };

  const acceptReview = async (hash: string) => {
    setBusy(true); setError("");
    try { await api(`review/${hash}/accept`, { method: "POST" }); setMessage("Đã duyệt trích đoạn và đưa vào chỉ mục RAG."); await reload(); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Không duyệt được trích đoạn."); }
    finally { setBusy(false); }
  };

  return <section className="transcript trusted-learning">
    <div className="cloud-heading"><div><h1>Nguồn học được phê duyệt</h1><p>Đệ Tốt chỉ đọc phạm vi được chọn. Thêm nguồn, kiểm tra giấy phép, rồi phê duyệt trước khi worker đồng bộ.</p></div><button disabled={busy} onClick={() => void reload()}>Làm mới</button></div>
    {message && <p role="status" className="cloud-notice">{message}</p>}{error && <p role="alert" className="cloud-error">{error}</p>}
    <div className="cloud-footnote"><strong>Giới hạn hiện tại:</strong> Website chỉ đọc đúng một URL, không lần theo liên kết; GitHub chỉ đọc README của một repository; Drive chỉ đọc dataset trong thư mục DETOT-AI đã chọn. Chỉ JSON, JSONL, CSV, TXT và Markdown được trích xuất; PDF và Parquet chưa được lập chỉ mục. Bản chỉ mục có citation được đồng bộ vào Drive để dùng giữa các máy. Worker kiểm tra nguồn mỗi 15 phút và đồng bộ lại tối đa 6 giờ một lần; có thể chạy thủ công.</div>
    <form className="cloud-upload" onSubmit={event => void add(event)}>
      <h2>Nguồn mới</h2>
      <div className="cloud-form-grid">
        <label>Loại nguồn<select value={type} onChange={event => { setType(event.target.value); setScope(""); }}><option value="website">Website · một URL</option><option value="github">GitHub · một repository</option><option value="drive-folder">Google Drive · thư mục dataset</option></select></label>
        <label>Tên hiển thị<input maxLength={120} value={name} onChange={event => setName(event.target.value)} placeholder="Ví dụ: Microsoft Learn" /></label>
        {type === "drive-folder" ? <label>Phạm vi Drive<select required value={scope} onChange={event => setScope(event.target.value)}><option value="">Chọn thư mục dataset</option>{driveScopes.map(item => <option key={item.id} value={item.id}>{item.name} · {item.files} tệp</option>)}</select></label> : <label>{type === "github" ? "URL repository" : "URL trang cụ thể"}<input required type="url" value={scope} onChange={event => setScope(event.target.value)} placeholder={type === "github" ? "https://github.com/owner/repo" : "https://docs.example.org/topic"} /></label>}
        {type !== "drive-folder" && <label>Giấy phép<select required value={license} onChange={event => setLicense(event.target.value)}><option value="">Chọn giấy phép</option>{licenses.map(value => <option key={value}>{value}</option>)}</select></label>}
        {type === "website" && <label>URL bằng chứng giấy phép<input required type="url" value={licenseUrl} onChange={event => setLicenseUrl(event.target.value)} placeholder="https://example.org/license" /></label>}
      </div>
      <p>Chọn giấy phép theo quyền sử dụng thực tế của nguồn. Khai báo giấy phép là xác nhận của người thêm nguồn; hệ thống kiểm tra URL/metadata nhưng không thể thay bạn đưa ra kết luận pháp lý.</p>
      <button className="primary" disabled={busy || (type === "drive-folder" && driveScopes.length === 0)}>{busy ? "Đang kiểm tra…" : "Kiểm tra nguồn"}</button>
    </form>
    <div className="cloud-list"><h2>Nguồn học</h2>{sources.length === 0 ? <p>Chưa có nguồn. Nguồn mới luôn ở trạng thái chưa được phê duyệt.</p> : sources.map(source => <article className="cloud-item" key={source.id}>
      <div className="cloud-item-title"><strong>{source.name}</strong><span>{source.type} · {source.status} · {source.license}</span></div>
      <p><a href={source.scope.startsWith("https://") ? source.scope : undefined} target="_blank" rel="noreferrer">{source.scope}</a></p>
      <p>{source.checkSummary || "Chưa kiểm tra."}{source.lastError && ` · Lỗi gần nhất: ${source.lastError}`}</p>
      <p>Kiểm tra: {date(source.checkedUtc)} · Duyệt: {date(source.approvedUtc)} · Đồng bộ: {date(source.lastSyncUtc)}</p>
      <p>{source.lastAccepted} đoạn được lập chỉ mục · {source.lastReview} cần xem lại · {source.lastDuplicate} trùng lặp</p>
      <div className="cloud-actions">{source.status !== "approved" && <button disabled={busy} onClick={() => void mutate(source.id, "check")}>Kiểm tra lại</button>}{source.status === "checked" && <button disabled={busy} onClick={() => void mutate(source.id, "approve")}>Phê duyệt</button>}{source.status === "approved" && <button disabled={busy} onClick={() => void mutate(source.id, "sync")}>Đồng bộ ngay</button>}<button disabled={busy} onClick={() => void mutate(source.id, "delete")}>Thu hồi và xóa chỉ mục</button></div>
    </article>)}</div>
    <div className="cloud-list"><h2>Ứng viên cần xem lại · {review.length}</h2>{review.length === 0 ? <p>Chưa có trích đoạn bị đánh dấu mâu thuẫn tiềm năng.</p> : review.map(item => <article className="cloud-item" key={item.hash}><strong>{item.sourceName} · {item.license}</strong><p>{item.text}</p><a href={item.citation.startsWith("https://") ? item.citation : undefined} target="_blank" rel="noreferrer">Mở nguồn gốc</a><button disabled={busy} onClick={() => void acceptReview(item.hash)}>Duyệt trích đoạn</button></article>)}</div>
    <div className="cloud-filter"><label>Tìm kiến thức đã lập chỉ mục<input value={knowledgeQuery} onChange={event => setKnowledgeQuery(event.target.value)} placeholder="Nhập câu hỏi hoặc từ khóa" /></label><button disabled={busy || knowledgeQuery.trim().length < 2} onClick={() => void search()}>Tra cứu</button></div>
    <div className="cloud-list">{knowledge.map((item, index) => <article className="cloud-item" key={`${item.sha256}-${index}`}><strong>{item.source} · {item.license}</strong><p>{item.text}</p><a href={item.citation.startsWith("https://") ? item.citation : undefined} target="_blank" rel="noreferrer">Nguồn trích dẫn</a><code>SHA-256 {item.sha256} · độ liên quan {item.relevance}</code></article>)}</div>
  </section>;
}
