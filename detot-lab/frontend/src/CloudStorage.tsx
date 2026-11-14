import { useCallback, useEffect, useMemo, useState } from "react";

type Status = { enabled: boolean; connected: boolean; email?: string | null; available: boolean; appFolderBytes?: number | null; message?: string | null };
type Artifact = { id: string; name: string; kind: string; category: string; version: string; pipeline: string; source: string; license: string; size: number; modifiedTime: string; sha256: string; integrity: string; webViewLink?: string | null };
type Source = { id: string; label: string; available: boolean; files: number; bytes: number };
type DriveHistory = { utc: string; action: string; target: string; result: string; sha256?: string | null };
type UploadSession = { id: string; size: number; receivedBytes: number; chunkSize: number };
type UploadProgress = UploadSession & { complete: boolean; duplicate: boolean; sha256?: string | null; artifact?: Artifact | null; message?: string | null };
const resumeKey = "detot.drive.pending-upload.v1";
const bytes = (value: number) => value < 1024 ** 2 ? `${(value / 1024).toFixed(0)} KiB` : value < 1024 ** 3 ? `${(value / 1024 ** 2).toFixed(1)} MiB` : `${(value / 1024 ** 3).toFixed(2)} GiB`;
const date = (value: string) => new Intl.DateTimeFormat("vi-VN", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));

async function driveApi<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`/api/drive/${path}`, options);
  if (response.status === 204) return undefined as T;
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.detail ?? data?.error ?? `Google Drive lỗi HTTP ${response.status}.`);
  return data as T;
}

export function CloudStorage() {
  const [status, setStatus] = useState<Status>();
  const [items, setItems] = useState<Artifact[]>([]);
  const [history, setHistory] = useState<DriveHistory[]>([]);
  const [sources, setSources] = useState<Source[]>([]);
  const [kind, setKind] = useState("all");
  const [pipeline, setPipeline] = useState("");
  const [tab, setTab] = useState<"artifacts" | "backup" | "history">("artifacts");
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState("");
  const [uploadId, setUploadId] = useState("");
  const [file, setFile] = useState<File>();
  const [metadata, setMetadata] = useState({ kind: "dataset", category: "vietnamese", version: "1.0.0", pipeline: "", source: "", license: "" });
  const [selectedSources, setSelectedSources] = useState<string[]>([]);
  const [passphrase, setPassphrase] = useState("");
  const [restorePassphrase, setRestorePassphrase] = useState("");
  const [restoreUrl, setRestoreUrl] = useState("");
  const [keep, setKeep] = useState(5);

  const reload = useCallback(async () => {
    setError("");
    try {
      const connected = await driveApi<Status>("status");
      setStatus(connected);
      if (!connected.connected || !connected.available) { setItems([]); return; }
      const query = new URLSearchParams();
      if (kind !== "all") query.set("kind", kind);
      if (pipeline.trim()) query.set("pipeline", pipeline.trim());
      const [artifacts, events] = await Promise.all([
        driveApi<Artifact[]>(`items${query.size ? `?${query}` : ""}`),
        driveApi<DriveHistory[]>("history"),
      ]);
      setItems(artifacts); setHistory(events);
      const backupSources = await driveApi<Source[]>("backups/sources");
      setSources(backupSources);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không tải được trạng thái Drive."); }
  }, [kind, pipeline]);

  useEffect(() => {
    const controller = new AbortController();
    const url = new URL(location.href);
    const outcome = url.searchParams.get("drive");
    if (outcome) {
      url.searchParams.delete("drive"); window.history.replaceState(window.history.state, "", url);
      setNotice(outcome === "connected" ? "Đã kết nối Drive. Thư mục DETOT-AI đã sẵn sàng." : `Google Drive chưa kết nối (${outcome}).`);
    }
    void reload();
    return () => controller.abort();
  }, [reload]);

  const usage = useMemo(() => status?.appFolderBytes == null ? "Drive chưa cung cấp mức dùng thư mục ứng dụng." : `${bytes(status.appFolderBytes)} trong thư mục DETOT-AI`, [status]);
  const connect = async () => {
    setBusy(true); setError("");
    try { const { url } = await driveApi<{ url: string }>("connect", { method: "POST" }); location.assign(url); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Không thể bắt đầu OAuth Google Drive."); setBusy(false); }
  };

  const upload = async () => {
    if (!file) return;
    setBusy(true); setError(""); setProgress("Đang tạo phiên tải lên…");
    try {
      let session: UploadSession;
      const saved = localStorage.getItem(resumeKey);
      if (saved) {
        const pending = JSON.parse(saved) as { id: string; fileName: string; size: number };
        if (pending.fileName === file.name && pending.size === file.size) {
          session = await driveApi<UploadSession>(`uploads/${pending.id}`);
        } else {
          throw new Error(`Có phiên chưa xong cho ${pending.fileName}. Chọn lại đúng tệp đó để tiếp tục, hoặc hoàn tất phiên trước.`);
        }
      } else {
        session = await driveApi<UploadSession>("uploads", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...metadata, fileName: file.name, size: file.size }) });
        localStorage.setItem(resumeKey, JSON.stringify({ id: session.id, fileName: file.name, size: file.size }));
      }
      setUploadId(session.id);
      let offset = session.receivedBytes;
      while (offset < file.size) {
        const end = Math.min(offset + session.chunkSize, file.size);
        const response = await fetch(`/api/drive/uploads/${session.id}`, { method: "PUT", headers: { "Content-Range": `bytes ${offset}-${end - 1}/${file.size}`, "Content-Type": "application/octet-stream" }, body: file.slice(offset, end) });
        const result = await response.json().catch(() => null) as UploadProgress | null;
        if (!response.ok) throw new Error(result && "message" in result && result.message || `Tải lên lỗi HTTP ${response.status}.`);
        if (!result) throw new Error("Máy chủ không trả tiến độ tải lên.");
        if (result.complete) {
          if (!result.duplicate && !result.artifact) throw new Error("Drive chưa xác nhận artifact.");
          localStorage.removeItem(resumeKey); setUploadId(""); setProgress(`Hoàn tất · SHA-256 ${result.sha256 ?? "đã xác minh"}`); setFile(undefined); await reload(); return;
        }
        if (result.receivedBytes <= offset) throw new Error(result.message || "Tải lên tạm dừng; có thể tiếp tục bằng cách chọn lại cùng tệp.");
        offset = result.receivedBytes;
        setProgress(`Đã tải ${bytes(offset)} / ${bytes(file.size)} (${Math.floor(offset * 100 / file.size)}%)`);
      }
      const final = await driveApi<UploadProgress>(`uploads/${session.id}`);
      if (final.complete) { localStorage.removeItem(resumeKey); setUploadId(""); setProgress(`Hoàn tất · SHA-256 ${final.sha256 ?? "đã xác minh"}`); setFile(undefined); await reload(); }
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Tải lên thất bại."); }
    finally { setBusy(false); }
  };

  const download = async (artifact: Artifact) => {
    setBusy(true); setError("");
    try {
      const response = await fetch(`/api/drive/items/${encodeURIComponent(artifact.id)}/download`);
      if (!response.ok) { const data = await response.json().catch(() => null); throw new Error(data?.detail ?? `Tải xuống lỗi HTTP ${response.status}.`); }
      const blob = await response.blob();
      const url = URL.createObjectURL(blob); const anchor = document.createElement("a"); anchor.href = url; anchor.download = artifact.name; anchor.click(); URL.revokeObjectURL(url);
      setNotice(`Đã tải và xác minh SHA-256: ${response.headers.get("X-Detot-SHA256") ?? artifact.sha256}`);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Tải xuống thất bại."); }
    finally { setBusy(false); }
  };

  const verify = async (artifact: Artifact) => {
    setBusy(true); setError("");
    try { const result = await driveApi<{ result: string }>(`items/${encodeURIComponent(artifact.id)}/verify`, { method: "POST" }); setNotice(`${artifact.name}: ${result.result} SHA-256`); await reload(); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Kiểm tra toàn vẹn thất bại."); }
    finally { setBusy(false); }
  };

  const remove = async (artifact: Artifact) => {
    if (!confirm(`Chuyển “${artifact.name}” trong thư mục DETOT-AI vào Thùng rác Google Drive?`)) return;
    setBusy(true); setError("");
    try { await driveApi(`items/${encodeURIComponent(artifact.id)}`, { method: "DELETE" }); setNotice("Đã chuyển artifact trên Drive vào Thùng rác."); await reload(); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Không xóa được artifact trên Drive."); }
    finally { setBusy(false); }
  };

  const createBackup = async () => {
    if (passphrase.length < 16) { setError("Cụm mật khẩu backup cần ít nhất 16 ký tự."); return; }
    if (!selectedSources.length || !confirm("Tạo bản sao lưu từ các nguồn đã chọn và tải bản backup đã mã hóa lên Google Drive?")) return;
    setBusy(true); setError(""); setProgress("Đang tạo manifest, kiểm tra dữ liệu và mã hóa backup…");
    try {
      const result = await driveApi<{ name: string; files: number; bytes: number; sha256: string; message: string }>("backups", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sources: selectedSources, passphrase }) });
      setPassphrase(""); setProgress(""); setNotice(`${result.message} ${result.files} tệp · ${bytes(result.bytes)} · SHA-256 ${result.sha256}`); await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Backup thất bại."); }
    finally { setBusy(false); }
  };

  const restore = async (artifact: Artifact) => {
    if (restorePassphrase.length < 16 || !confirm(`Xác minh và giải nén “${artifact.name}” vào vùng staging tạm? Không ghi đè Memory Core/Dreaming.`)) return;
    setBusy(true); setError(""); setRestoreUrl("");
    try {
      const result = await driveApi<{ files: number; bytes: number; message: string; downloadUrl: string }>(`backups/${encodeURIComponent(artifact.id)}/restore`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ passphrase: restorePassphrase }) });
      setRestorePassphrase(""); setRestoreUrl(result.downloadUrl); setNotice(`${result.message} ${result.files} tệp · ${bytes(result.bytes)}.`);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Khôi phục thất bại."); }
    finally { setBusy(false); }
  };

  return <section className="transcript cloud-storage">
    <div className="cloud-heading"><div><h1>Cloud Storage</h1><p>Kho riêng <strong>DETOT-AI</strong> trên Google Drive. Dataset và artifact đồng bộ giữa máy được cấp quyền.</p></div><button disabled={busy || !status?.connected} onClick={async () => { setBusy(true); setError(""); try { await driveApi("refresh", { method: "POST" }); setNotice("Token Drive đã làm mới."); await reload(); } catch (cause) { setError(cause instanceof Error ? cause.message : "Làm mới token thất bại."); } finally { setBusy(false); } }}>Làm mới token</button></div>
    {notice && <p role="status" className="cloud-notice">{notice}</p>}{error && <p role="alert" className="cloud-error">{error}</p>}
    <div className="cloud-status"><div><strong>{status?.connected ? "Đã kết nối Google Drive" : status?.enabled ? "Chưa kết nối" : "OAuth chưa cấu hình"}</strong><span>{status?.email ?? status?.message ?? "Đang đọc trạng thái…"}</span></div><span>{usage}</span>{status?.connected ? <button disabled={busy} onClick={async () => { if (!confirm("Xóa token Drive lưu cục bộ khỏi Đệ Tốt? File Drive được giữ nguyên; Gmail và Calendar không bị ngắt.")) return; setBusy(true); try { await driveApi("connect", { method: "DELETE" }); setNotice("Đã xóa token Drive cục bộ; dữ liệu Drive được giữ nguyên."); await reload(); } catch (cause) { setError(cause instanceof Error ? cause.message : "Ngắt kết nối thất bại."); } finally { setBusy(false); } }}>Ngắt kết nối</button> : <button className="primary" disabled={busy || !status?.enabled} onClick={() => void connect()}>Kết nối tài khoản voquangthan1234</button>}</div>
    <p className="cloud-footnote">Chỉ thao tác trong thư mục do ứng dụng đánh dấu. File Drive cũ được giữ riêng. Dung lượng hiển thị là của DETOT-AI khi API trả được.</p>
    {status?.connected && <>
      <div className="cloud-tabs"><button aria-pressed={tab === "artifacts"} onClick={() => setTab("artifacts")}>Dữ liệu</button><button aria-pressed={tab === "backup"} onClick={() => setTab("backup")}>Memory backup</button><button aria-pressed={tab === "history"} onClick={() => setTab("history")}>Lịch sử đồng bộ</button><button disabled={busy} onClick={() => void reload()}>Làm mới danh sách</button></div>
      {tab === "artifacts" && <>
        <form className="cloud-upload" onSubmit={event => { event.preventDefault(); void upload(); }}>
          <h2>Tải dữ liệu lên</h2><div className="cloud-form-grid"><label>Loại<select value={metadata.kind} onChange={event => setMetadata({ ...metadata, kind: event.target.value, category: event.target.value === "dataset" ? metadata.category : "" })}><option value="dataset">Dataset</option><option value="model">Model</option><option value="training-result">Kết quả huấn luyện</option><option value="document">Tài liệu</option></select></label>
          {metadata.kind === "dataset" && <label>Nhóm<select value={metadata.category} onChange={event => setMetadata({ ...metadata, category: event.target.value })}><option value="vietnamese">Vietnamese</option><option value="programming">Programming</option><option value="it-support">IT Support</option><option value="agent-training">Agent Training</option></select></label>}
          <label>Phiên bản<input required maxLength={40} value={metadata.version} onChange={event => setMetadata({ ...metadata, version: event.target.value })}/></label><label>Pipeline<input maxLength={80} value={metadata.pipeline} onChange={event => setMetadata({ ...metadata, pipeline: event.target.value })}/></label><label>Nguồn dữ liệu<input required maxLength={1000} value={metadata.source} onChange={event => setMetadata({ ...metadata, source: event.target.value })}/></label><label>Giấy phép<input required maxLength={300} placeholder="Ví dụ: CC BY 4.0" value={metadata.license} onChange={event => setMetadata({ ...metadata, license: event.target.value })}/></label><label className="cloud-file">Tệp<input type="file" required onChange={event => setFile(event.target.files?.[0])}/></label></div>
          <button className="primary" disabled={busy || !file}>{busy ? "Đang xử lý…" : "Tải lên và xác minh SHA-256"}</button>{progress && <span className="progress">{progress}</span>}
        </form>
        <div className="cloud-filter"><label>Loại xem<select value={kind} onChange={event => setKind(event.target.value)}><option value="all">Tất cả</option><option value="dataset">Dataset</option><option value="model">Model</option><option value="backup">Backup</option><option value="training-result">Kết quả huấn luyện</option><option value="document">Tài liệu</option></select></label><label>Pipeline<input value={pipeline} onChange={event => setPipeline(event.target.value)} placeholder="Lọc theo pipeline"/></label></div>
        <div className="cloud-list">{items.length === 0 ? <p>Chưa có artifact phù hợp trong DETOT-AI.</p> : items.map(item => <article className="cloud-item" key={item.id}><div className="cloud-item-title"><strong>{item.name}</strong><span>{item.kind} · v{item.version} · {bytes(item.size)} · {date(item.modifiedTime)}</span></div><p>Nguồn: {item.source || "—"} · Giấy phép: {item.license || "—"}{item.pipeline && ` · Pipeline: ${item.pipeline}`}</p><code>SHA-256 {item.sha256 || "chưa có"} · {item.integrity}</code><div className="cloud-actions"><button disabled={busy} onClick={() => void download(item)}>Tải xuống</button><button disabled={busy} onClick={() => void verify(item)}>Kiểm tra toàn vẹn</button>{item.kind === "backup" && <button disabled={busy} onClick={() => void restore(item)}>Khôi phục an toàn</button>}{item.webViewLink && <a href={item.webViewLink} target="_blank" rel="noreferrer">Mở trên Drive</a>}<button disabled={busy} onClick={() => void remove(item)}>Chuyển vào Thùng rác</button></div>{item.kind === "backup" && <label>Cụm mật khẩu khôi phục<input type="password" autoComplete="new-password" value={restorePassphrase} onChange={event => setRestorePassphrase(event.target.value)} /></label>}</article>)}</div>
        {restoreUrl && <p role="status"><a href={restoreUrl}>Tải gói restore đã kiểm tra (chỉ dùng một lần)</a></p>}
      </>}
      {tab === "backup" && <div className="cloud-upload"><h2>Sao lưu Memory Core và Dreaming</h2><p>Chỉ các nguồn được cấu hình rõ ràng trên máy chủ mới xuất hiện. Backup được mã hóa trước khi tải lên; passphrase không được lưu. Restore kiểm tra manifest, SHA-256 và danh sách thu hồi session ngoài snapshot.</p>{sources.length === 0 && <p>Chưa cấu hình nguồn backup (DETOT_MEMORY_CORE_PATH, DETOT_DREAMING_PATH, DETOT_MEMORY_RELATED_PATH).</p>}{sources.map(source => <label className="cloud-source" key={source.id}><input type="checkbox" disabled={!source.available} checked={selectedSources.includes(source.id)} onChange={event => setSelectedSources(event.target.checked ? [...selectedSources, source.id] : selectedSources.filter(id => id !== source.id))}/><span>{source.label} · {source.available ? `${source.files} tệp, ${bytes(source.bytes)}` : "không khả dụng"}</span></label>)}<label>Cụm mật khẩu riêng (tối thiểu 16 ký tự)<input type="password" autoComplete="new-password" value={passphrase} onChange={event => setPassphrase(event.target.value)}/></label><button className="primary" disabled={busy || !selectedSources.length} onClick={() => void createBackup()}>Tạo backup mã hóa</button>{progress && <span className="progress">{progress}</span>}<div className="cloud-retention"><label>Giữ lại <input type="number" min={1} max={100} value={keep} onChange={event => setKeep(Number(event.target.value))}/> bản gần nhất</label><button disabled={busy} onClick={async () => { if (!confirm(`Chuyển các backup vượt quá ${keep} bản vào Thùng rác Drive?`)) return; setBusy(true); try { const result = await driveApi<{ retained: number; movedToTrash: number }>("retention", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ keep }) }); setNotice(`Giữ ${result.retained}; chuyển ${result.movedToTrash} backup cũ vào Thùng rác Drive.`); await reload(); } catch (cause) { setError(cause instanceof Error ? cause.message : "Áp dụng retention thất bại."); } finally { setBusy(false); } }}>Áp dụng retention</button></div></div>}
      {tab === "history" && <div className="cloud-list">{history.length === 0 ? <p>Chưa có lịch sử thao tác Drive.</p> : history.map((entry, index) => <article className="cloud-item" key={`${entry.utc}-${index}`}><strong>{entry.action}: {entry.target}</strong><span>{date(entry.utc)} · {entry.result}</span>{entry.sha256 && <code>SHA-256 {entry.sha256}</code>}</article>)}</div>}
    </>}
  </section>;
}
