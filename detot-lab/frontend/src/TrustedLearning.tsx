import { useCallback, useEffect, useState } from "react";

type Source = {
  id: string; type: string; name: string; scope: string; accessPermission: string; license: string; licenseUrl: string;
  status: string; checkedUtc?: string | null; approvedUtc?: string | null; lastSyncUtc?: string | null;
  fileCount: number; lastAccepted: number; lastReview: number; lastDuplicate: number;
  checkSummary?: string | null; lastError?: string | null; licenseEvidenceUrl?: string | null; licenseDetectionMethod?: string | null;
  quarantineReason?: string | null; paused: boolean; workerState: string; retryCount: number; nextAttemptUtc?: string | null;
  lastAttemptUtc?: string | null; lastSyncStatus: string; lastQuarantined: number; quarantinedDocuments: number;
  lastDurationMilliseconds: number;
};
type DriveScope = { id: string; name: string; files: number };
type DiscoveryStatus = { configured: boolean; provider: string };
type SearchCandidate = { title: string; url: string; domain: string; description: string; documentType: string; licenseStatus: string; officialSource: boolean; version?: string | null };
type DiscoveryResponse = { provider: string; candidates: SearchCandidate[] };
type Knowledge = { text: string; source: string; citation: string; license: string; sha256: string; relevance: number };
type ReviewItem = { hash: string; sourceName: string; title: string; text: string; citation: string; license: string };
type Dashboard = { sourceCount: number; approvedCount: number; pausedCount: number; indexedChunks: number; duplicateChunks: number; quarantinedItems: number; errorCount: number; lastAttemptUtc?: string | null; lastSyncUtc?: string | null; cloudMirrorPending: boolean };
type Quarantine = { type: string; sourceId: string; source: string; reason: string; citation: string; hash?: string | null; updatedUtc?: string | null };

async function api<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`/api/learning/${path}`, options);
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error ?? body?.detail ?? `Learning API lỗi HTTP ${response.status}.`);
  return body as T;
}

const date = (value?: string | null) => value ? new Intl.DateTimeFormat("vi-VN", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)) : "chưa có";

export function TrustedLearning({ initialRequest }: { initialRequest?: { topic: string; domains: string[]; exactUrl?: string } }) {
  const [sources, setSources] = useState<Source[]>([]);
  const [driveScopes, setDriveScopes] = useState<DriveScope[]>([]);
  const [type, setType] = useState("website");
  const [name, setName] = useState("");
  const [scope, setScope] = useState("");
  const [discoveryStatus, setDiscoveryStatus] = useState<DiscoveryStatus>({ configured: false, provider: "Chưa cấu hình" });
  const [topic, setTopic] = useState("");
  const [domainFilter, setDomainFilter] = useState("");
  const [documentType, setDocumentType] = useState("all");
  const [candidates, setCandidates] = useState<SearchCandidate[]>([]);
  const [knowledgeQuery, setKnowledgeQuery] = useState("");
  const [knowledge, setKnowledge] = useState<Knowledge[]>([]);
  const [review, setReview] = useState<ReviewItem[]>([]);
  const [dashboard, setDashboard] = useState<Dashboard>();
  const [quarantine, setQuarantine] = useState<Quarantine[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const reload = useCallback(async () => {
    try {
      const [items, scopes, reviewItems, discovery, stats, quarantineItems] = await Promise.all([
        api<Source[]>("sources"), api<DriveScope[]>("drive-scopes").catch(() => []), api<ReviewItem[]>("review"),
        api<DiscoveryStatus>("discovery/status").catch(() => ({ configured: false, provider: "Chưa cấu hình" })),
        api<Dashboard>("dashboard"), api<Quarantine[]>("quarantine"),
      ]);
      setSources(items); setDriveScopes(scopes); setReview(reviewItems); setDiscoveryStatus(discovery); setDashboard(stats); setQuarantine(quarantineItems);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không tải được danh sách nguồn."); }
  }, []);
  useEffect(() => { void reload(); }, [reload]);

  const search = async () => {
    setBusy(true); setError("");
    try { setKnowledge(await api<Knowledge[]>(`knowledge?q=${encodeURIComponent(knowledgeQuery)}`)); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "Không truy vấn được kho kiến thức."); }
    finally { setBusy(false); }
  };

  const runDiscovery = async (searchTopic: string, domains: string[]) => {
    setBusy(true); setError(""); setMessage(""); setCandidates([]);
    try {
      const result = await api<DiscoveryResponse>("discovery/search", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ topic: searchTopic, domains, documentType }) });
      setCandidates(result.candidates);
      setMessage(`Search qua ${result.provider}: tìm thấy ${result.candidates.length} nguồn ứng viên. Chưa nguồn nào được thêm hoặc lập chỉ mục.`);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không tìm được nguồn ứng viên."); }
    finally { setBusy(false); }
  };

  const discover = async (event: React.FormEvent) => {
    event.preventDefault();
    void runDiscovery(topic, domainFilter.split(/[\s,;]+/).map(value => value.trim()).filter(Boolean));
  };

  useEffect(() => {
    if (!initialRequest) return;
    setTopic(initialRequest.topic);
    setDomainFilter(initialRequest.domains.join(", "));
    if (initialRequest.exactUrl) {
      const url = new URL(initialRequest.exactUrl);
      const github = url.hostname.toLowerCase() === "github.com";
      setCandidates([{ title: initialRequest.topic === "Tài liệu chính thức" ? url.pathname : initialRequest.topic,
        url: initialRequest.exactUrl, domain: url.hostname, description: "URL đã được nêu trực tiếp trong yêu cầu chat.",
        documentType: github ? "GitHub README" : "Web", licenseStatus: "Chưa kiểm tra", officialSource: false }]);
      setMessage("Đã giữ nguyên URL được nêu. Kiểm tra license trước; nguồn chỉ được thu thập sau khi bạn phê duyệt.");
    }
    else if (discoveryStatus.configured) void runDiscovery(initialRequest.topic, initialRequest.domains);
    else setMessage("Đã mở Search Discovery từ yêu cầu chat. Backend chưa cấu hình Search Provider; yêu cầu vẫn cần phê duyệt nguồn trước khi học.");
  }, [initialRequest, discoveryStatus.configured]);

  const checkCandidate = async (candidate: SearchCandidate) => {
    setBusy(true); setError(""); setMessage("");
    try {
      const type = candidate.documentType === "GitHub README" ? "github" : "website";
      const source = await api<Source>("sources", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type, name: candidate.title, scope: candidate.url, accessPermission: "read", license: "", licenseUrl: "" }) });
      const checked = await api<Source>(`sources/${source.id}/check`, { method: "POST" });
      setMessage(checked.checkSummary ?? "Đã kiểm tra nguồn. Phê duyệt riêng để bắt đầu đồng bộ.");
      setCandidates(items => items.filter(item => item.url !== candidate.url)); await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không kiểm tra được nguồn ứng viên."); await reload(); }
    finally { setBusy(false); }
  };

  const add = async (event: React.FormEvent) => {
    event.preventDefault(); setBusy(true); setError(""); setMessage("");
    try {
      const source = await api<Source>("sources", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type, name, scope: type === "drive-folder" ? scope : scope.trim(), accessPermission: "read" }) });
      const checked = await api<Source>(`sources/${source.id}/check`, { method: "POST" });
      setMessage(checked.checkSummary ?? "Đã kiểm tra nguồn. Đọc kết quả và phê duyệt nếu phù hợp.");
      setName(""); setScope(""); await reload();
    } catch (cause) { setError(cause instanceof Error ? cause.message : "Không thể thêm hoặc kiểm tra nguồn."); await reload(); }
    finally { setBusy(false); }
  };

  const mutate = async (id: string, action: "check" | "approve" | "sync" | "delete" | "pause" | "resume" | "retry") => {
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
    <div className="cloud-heading"><div><h1>Nguồn học</h1><p>Backend tự phát hiện giấy phép từ metadata có bằng chứng. Nguồn chưa rõ license hoặc chưa được phép sẽ bị cách ly.</p></div><button disabled={busy} onClick={() => void reload()}>Làm mới</button></div>
    {dashboard && <div className="cloud-footnote"><strong>Kho kiến thức:</strong> {dashboard.sourceCount} nguồn · {dashboard.approvedCount} đang học · {dashboard.pausedCount} tạm dừng · {dashboard.indexedChunks} đoạn đã lập chỉ mục · {dashboard.duplicateChunks} trùng · {dashboard.quarantinedItems} cách ly · {dashboard.errorCount} lỗi · Drive mirror {dashboard.cloudMirrorPending ? "đang chờ đồng bộ" : "đã cập nhật"}. Lần chạy: {date(dashboard.lastAttemptUtc)} · lần đồng bộ: {date(dashboard.lastSyncUtc)}.</div>}
    {message && <p role="status" className="cloud-notice">{message}</p>}{error && <p role="alert" className="cloud-error">{error}</p>}
    <div className="cloud-footnote"><strong>Giới hạn hiện tại:</strong> Connector nguồn chỉ đọc: website đúng một URL, GitHub README của một repository, hoặc dataset trong thư mục DETOT-AI đã chọn. Universal Reader quyết định định dạng nào trích xuất được; nguồn lớn, thiếu license hoặc ngoài phạm vi bị bỏ qua/cách ly. Manifest Drive không đổi sẽ không tải lại artifact. Bản chỉ mục có citation được đồng bộ vào Drive; worker kiểm tra mỗi 15 phút, đồng bộ thay đổi và làm mới tối đa sau 6 giờ.</div>
    <form className="cloud-upload" onSubmit={event => void discover(event)}>
      <h2>Search Discovery</h2>
      <p>Tìm nguồn ứng viên để học lâu dài. Kết quả chỉ là gợi ý; Đệ Tốt không tự thêm nguồn, phê duyệt hay lập chỉ mục.</p>
      <div className="cloud-form-grid">
        <label>Chủ đề<input required minLength={2} maxLength={200} value={topic} onChange={event => setTopic(event.target.value)} placeholder="Ví dụ: ASP.NET Core Authentication" /></label>
        <label>Lọc domain · tối đa 5<input value={domainFilter} onChange={event => setDomainFilter(event.target.value)} placeholder="learn.microsoft.com, github.com" /></label>
        <label>Loại tài liệu<select value={documentType} onChange={event => setDocumentType(event.target.value)}><option value="all">Tất cả</option><option value="web">Trang web</option><option value="markdown">Markdown</option><option value="github">GitHub repository</option></select></label>
      </div>
      <p>{discoveryStatus.configured ? `Search Provider: ${discoveryStatus.provider}` : "Search Provider chưa cấu hình. Backend cần DETOT_SEARCH_PROVIDER=microsoft-web và DETOT_SEARCH_API_KEY."}</p>
      <button className="primary" disabled={busy || !discoveryStatus.configured || topic.trim().length < 2}>{busy ? "Đang tìm…" : "Tìm nguồn ứng viên"}</button>
      {candidates.map(candidate => <article className="cloud-item" key={candidate.url}>
        <div className="cloud-item-title"><strong>{candidate.title}</strong><span>{candidate.domain} · {candidate.documentType} · License: {candidate.licenseStatus}</span></div>
        <p><a href={candidate.url} target="_blank" rel="noreferrer">{candidate.url}</a></p>
        {candidate.description && <p>{candidate.description}</p>}{candidate.officialSource && <small>Nguồn chính thức</small>}{candidate.version && <small> · {candidate.version}</small>}
        <button type="button" disabled={busy} onClick={() => void checkCandidate(candidate)}>Kiểm tra nguồn</button>
      </article>)}
    </form>
    <form className="cloud-upload" onSubmit={event => void add(event)}>
      <h2>Nguồn mới</h2>
      <div className="cloud-form-grid">
        <label>Loại nguồn<select value={type} onChange={event => { setType(event.target.value); setScope(""); }}><option value="website">Website · một URL</option><option value="github">GitHub · một repository</option><option value="drive-folder">Google Drive · thư mục dataset</option></select></label>
        <label>Tên hiển thị<input maxLength={120} value={name} onChange={event => setName(event.target.value)} placeholder="Ví dụ: Microsoft Learn" /></label>
        {type === "drive-folder" ? <label>Phạm vi Drive<select required value={scope} onChange={event => setScope(event.target.value)}><option value="">Chọn thư mục dataset</option>{driveScopes.map(item => <option key={item.id} value={item.id}>{item.name} · {item.files} tệp</option>)}</select></label> : <label>{type === "github" ? "URL repository" : "URL trang cụ thể"}<input required type="url" value={scope} onChange={event => setScope(event.target.value)} placeholder={type === "github" ? "https://github.com/owner/repo" : "https://docs.example.org/topic"} /></label>}
        <label>Quyền connector<input value="Chỉ đọc" readOnly /></label>
      </div>
      <p>Quyền truy cập mô tả thao tác được cấp cho connector. Giấy phép là metadata bản quyền riêng; backend tự xác minh, không nhận license do người dùng nhập làm bằng chứng.</p>
      <button className="primary" disabled={busy || (type === "drive-folder" && driveScopes.length === 0)}>{busy ? "Đang kiểm tra…" : "Kiểm tra nguồn"}</button>
    </form>
    <div className="cloud-list"><h2>Nguồn học</h2>{sources.length === 0 ? <p>Chưa có nguồn. Nguồn mới luôn ở trạng thái chưa được phê duyệt.</p> : sources.map(source => <article className="cloud-item" key={source.id}>
      <div className="cloud-item-title"><strong>{source.name}</strong><span>{source.type} · {source.status} · Chỉ đọc · License: {source.license || "chưa xác định"}</span></div>
      <p><a href={source.scope.startsWith("https://") ? source.scope : undefined} target="_blank" rel="noreferrer">{source.scope}</a></p>
      <p>{source.checkSummary || "Chưa kiểm tra."}{source.lastError && ` · Lỗi gần nhất: ${source.lastError}`}</p>
      {source.licenseEvidenceUrl && <p>Giấy phép {source.license} · bằng chứng: <a href={source.licenseEvidenceUrl} target="_blank" rel="noreferrer">{source.licenseDetectionMethod || source.licenseEvidenceUrl}</a></p>}
      {source.quarantineReason && <p role="alert">Lý do cách ly: {source.quarantineReason}</p>}
      <p>Kiểm tra: {date(source.checkedUtc)} · Duyệt: {date(source.approvedUtc)} · Đồng bộ: {date(source.lastSyncUtc)}</p>
      <p>{source.lastAccepted} đoạn được lập chỉ mục · {source.lastReview} cần xem lại · {source.lastDuplicate} trùng · {source.lastQuarantined + source.quarantinedDocuments} cách ly · {source.lastSyncStatus} · worker {source.workerState} · retry {source.retryCount} · lần chạy gần nhất {source.lastDurationMilliseconds} ms{source.nextAttemptUtc ? ` (tiếp theo ${date(source.nextAttemptUtc)})` : ""}</p>
      <div className="cloud-actions">{source.status !== "approved" && <button disabled={busy} onClick={() => void mutate(source.id, "check")}>Kiểm tra lại</button>}{source.status === "checked" && <button disabled={busy} onClick={() => void mutate(source.id, "approve")}>Phê duyệt</button>}{source.status === "approved" && <><button disabled={busy} onClick={() => void mutate(source.id, "sync")}>Đồng bộ ngay</button><button disabled={busy} onClick={() => void mutate(source.id, source.paused ? "resume" : "pause")}>{source.paused ? "Tiếp tục" : "Tạm dừng"}</button></>}{source.lastError && <button disabled={busy} onClick={() => void mutate(source.id, "retry")}>Thử lại</button>}<button disabled={busy} onClick={() => void mutate(source.id, "delete")}>Thu hồi và xóa chỉ mục</button></div>
    </article>)}</div>
    <div className="cloud-list"><h2>Ứng viên cần xem lại · {review.length}</h2>{review.length === 0 ? <p>Chưa có trích đoạn bị đánh dấu mâu thuẫn tiềm năng.</p> : review.map(item => <article className="cloud-item" key={item.hash}><strong>{item.sourceName} · {item.license}</strong><p>{item.text}</p><a href={item.citation.startsWith("https://") ? item.citation : undefined} target="_blank" rel="noreferrer">Mở nguồn gốc</a><button disabled={busy} onClick={() => void acceptReview(item.hash)}>Duyệt trích đoạn</button></article>)}</div>
    <div className="cloud-list"><h2>Cách ly · {quarantine.length}</h2>{quarantine.length === 0 ? <p>Không có nguồn hoặc nội dung bị cách ly.</p> : quarantine.map((item, index) => <article className="cloud-item" key={`${item.sourceId}-${item.hash ?? index}`}><strong>{item.source} · {item.type}</strong><p>{item.reason}</p><a href={item.citation.startsWith("https://") ? item.citation : undefined} target="_blank" rel="noreferrer">Bằng chứng / citation</a><small>{date(item.updatedUtc)}</small></article>)}</div>
    <div className="cloud-filter"><label>Tìm kiến thức đã lập chỉ mục<input value={knowledgeQuery} onChange={event => setKnowledgeQuery(event.target.value)} placeholder="Nhập câu hỏi hoặc từ khóa" /></label><button disabled={busy || knowledgeQuery.trim().length < 2} onClick={() => void search()}>Tra cứu</button></div>
    <div className="cloud-list">{knowledge.map((item, index) => <article className="cloud-item" key={`${item.sha256}-${index}`}><strong>{item.source} · {item.license}</strong><p>{item.text}</p><a href={item.citation.startsWith("https://") ? item.citation : undefined} target="_blank" rel="noreferrer">Nguồn trích dẫn</a><code>SHA-256 {item.sha256} · độ liên quan {item.relevance}</code></article>)}</div>
  </section>;
}
