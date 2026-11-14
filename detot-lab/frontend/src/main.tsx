import React, { useEffect, useRef, useState } from "react";
import { Calendar } from "./Calendar";
import { suggestFromChat } from "./GoogleCalendar";
import { GoogleProfiles } from "./GoogleProfiles";
import { CloudStorage } from "./CloudStorage";
import { TrustedLearning } from "./TrustedLearning";
import { createRoot } from "react-dom/client";
import Markdown from "react-markdown";
import gfm from "remark-gfm";
import hljs from "highlight.js/lib/core";
import javascript from "highlight.js/lib/languages/javascript";
import typescript from "highlight.js/lib/languages/typescript";
import python from "highlight.js/lib/languages/python";
import json from "highlight.js/lib/languages/json";
import csharp from "highlight.js/lib/languages/csharp";
import { type Conversation, type Message, forkMessages, visibleConversations } from "./history";
import "./style.css";

const storageKey = "detot.conversations.v1";
const remoteWorkspace = location.protocol === "https:";
type HistoryEntry = { conversation: Conversation; revision: string };
Object.entries({ javascript, typescript, python, json, csharp }).forEach(([name, language]) => hljs.registerLanguage(name, language));
const uid = () => crypto.randomUUID();
const blank = (): Conversation => ({ id: uid(), title: "Cuộc trò chuyện mới", pinned: false, messages: [] });
const readHistory = (): Conversation[] => {
  if (remoteWorkspace) return [];
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(storageKey) ?? "[]");
    return Array.isArray(stored) ? stored.filter(item => item && typeof item.id === "string" &&
      typeof item.title === "string" && Array.isArray(item.messages) &&
      item.messages.every((message: Message) => message && typeof message.text === "string")) : [];
  } catch { return []; }
};

function Code({ className, children }: React.ComponentProps<"code">) {
  const language = className?.match(/^language-([\w-]+)$/)?.[1];
  const text = String(children ?? "");
  if (language && hljs.getLanguage(language)) {
    return <code className={className} dangerouslySetInnerHTML={{ __html: hljs.highlight(text, { language }).value }} />;
  }
  return <code className={className}>{children}</code>;
}

function AttachmentChip({ file, onRemove }: { file: File; onRemove: () => void }) {
  const [preview, setPreview] = useState<string>();
  useEffect(() => {
    if (!file.type.startsWith("image/")) { setPreview(undefined); return; }
    const url = URL.createObjectURL(file);
    setPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [file]);
  return <span className="attachment-chip" title={`${file.name} · ${(file.size / 1024 / 1024).toFixed(2)} MiB`}>
    {preview ? <img className="attachment-thumb" src={preview} alt={`Xem trước ${file.name}`} /> : "📎"} {file.name}
    <button type="button" aria-label={`Bỏ ${file.name}`} onClick={onRemove}>×</button>
  </span>;
}

function App() {
  const [calendar, setCalendar] = useState(() => new URLSearchParams(location.search).has("calendar"));
  const [cloudStorage, setCloudStorage] = useState(() => new URLSearchParams(location.search).has("drive"));
  const [trustedLearning, setTrustedLearning] = useState(false);
  const [items, setItems] = useState(readHistory);
  const [active, setActive] = useState<string | undefined>(() => {
    if (remoteWorkspace) return undefined;
    try { return sessionStorage.getItem("detot.active-conversation") ?? undefined; }
    catch { return undefined; }
  });
  const [query, setQuery] = useState("");
  const [draft, setDraft] = useState("");
  const [attachments, setAttachments] = useState<File[]>([]);
  const [side, setSide] = useState(false);
  const [dark, setDark] = useState(() => remoteWorkspace || localStorage.getItem("detot.theme") !== "light");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [practiceNotes, setPracticeNotes] = useState<string>();
  const [practiceLearning, setPracticeLearning] = useState("");
  const revisions = useRef(new Map<string, string>());
  const request = useRef<AbortController | null>(null);
  const retry = useRef<(() => void) | null>(null);
  const end = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);
  const fileInput = useRef<HTMLInputElement>(null);
  const conversation = items.find(item => item.id === active);
  const visibleItems = visibleConversations(items, query);
  useEffect(() => {
    if (!remoteWorkspace) return;
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch("/api/chat/history", { signal: controller.signal });
        if (response.status === 401) { location.replace("/login"); return; }
        if (!response.ok) throw new Error("Không tải được lịch sử từ laptop.");
        const entries: HistoryEntry[] = await response.json();
        if (controller.signal.aborted) return;
        const incoming = entries.filter(entry => !revisions.current.has(entry.conversation.id));
        for (const entry of incoming) revisions.current.set(entry.conversation.id, entry.revision);
        setItems(old => [...old, ...incoming.map(entry => entry.conversation)
          .filter(item => !old.some(existing => existing.id === item.id))]);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Không tải được lịch sử từ laptop.");
      }
    })();
    return () => controller.abort();
  }, []);
  useEffect(() => {
    if (remoteWorkspace) return;
    try {
      if (conversation) sessionStorage.setItem("detot.active-conversation", conversation.id);
      else sessionStorage.removeItem("detot.active-conversation");
    } catch { /* The current conversation remains usable without browser storage. */ }
  }, [conversation?.id]);
  useEffect(() => {
    if (remoteWorkspace) return;
    try { localStorage.setItem(storageKey, JSON.stringify(items)); }
    catch { setError("Không thể lưu lịch sử trên trình duyệt này."); }
  }, [items]);
  useEffect(() => {
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    if (!remoteWorkspace) localStorage.setItem("detot.theme", dark ? "dark" : "light");
  }, [dark]);
  useEffect(() => { end.current?.scrollIntoView({ block: "end" }); }, [active, conversation?.messages.length, busy]);

  const save = (value: Conversation) => setItems(old => [value, ...old.filter(item => item.id !== value.id)]);
  async function sync(value: Conversation) {
    if (!remoteWorkspace) return;
    const response = await fetch(`/api/chat/history/${value.id}`, {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ conversation: value, revision: revisions.current.get(value.id) }),
    });
    if (response.status === 401) {
      location.replace("/login");
      throw new Error("Phiên đăng nhập đã hết hạn.");
    }
    if (response.status === 409) throw new Error("Lịch sử đã thay đổi trên máy khác. Tải lại trang để cập nhật.");
    if (!response.ok) throw new Error("Không đồng bộ được lịch sử lên laptop.");
    const entry: HistoryEntry = await response.json();
    revisions.current.set(value.id, entry.revision);
  }

  async function updateConversation(value: Conversation) {
    setBusy(true);
    setError("");
    try {
      await sync(value);
      save(value);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Không cập nhật được cuộc trò chuyện.");
    } finally {
      setBusy(false);
    }
  }
  const select = (id?: string) => { setCalendar(false); setCloudStorage(false); setTrustedLearning(false); setActive(id); setDraft(""); setAttachments([]); setSide(false); setError(""); retry.current = null; };

  const addAttachments = (incoming: File[]) => {
    const combined = [...attachments, ...incoming];
    if (combined.length > 5) { setError("Mỗi lượt gửi tối đa 5 tệp."); return; }
    if (combined.some(file => file.size > 8 * 1024 * 1024)) { setError("Mỗi tệp tối đa 8 MiB."); return; }
    setError("");
    setAttachments(combined);
  };

  async function send(text: string, base = conversation ?? blank(), prefix = base.messages, requestId = uid(), context?: string, startPractice = false, files = attachments) {
    if ((!text.trim() && files.length === 0) || request.current) return;
    const prompt = [text.trim() || "Đọc và phân tích các tệp đính kèm.",
      files.length ? `Tệp đính kèm: ${files.map(file => file.name).join(", ")}` : ""].filter(Boolean).join("\n").slice(0, 1500);
    const displayText = (files.length ? `${prompt}\n📎 ${files.map(file => file.name).join(", ")}` : prompt).slice(0, 450);
    const user: Message = { id: requestId, role: "user", text: displayText };
    const target = { ...base, practice: startPractice || base.practice, gatewayLinked: true,
      title: prefix.length ? base.title : prompt.slice(0, 52), messages: [...prefix, user] };
    save(target);
    setActive(target.id);
    setDraft("");
    setAttachments([]);
    setError("");
    setBusy(true);
    const controller = new AbortController();
    request.current = controller;
    retry.current = () => void send(text, base, prefix, requestId, context, startPractice, files);
    try {
      let documentContext: string | undefined;
      let visionImages: string[] = [];
      if (files.length) {
        if (files.reduce((total, file) => total + file.size, 0) > 9 * 1024 * 1024)
          throw new Error("Tổng dung lượng tệp tối đa 9 MiB mỗi lượt.");
        const form = new FormData();
        files.forEach(file => form.append("files", file, file.name));
        const read = await fetch("/api/chat/read-files", { method: "POST", body: form, signal: controller.signal });
        const parsed = await read.json().catch(() => ({}));
        if (!read.ok || typeof parsed.context !== "string") throw new Error(parsed.detail || parsed.error || `Không đọc được tệp (HTTP ${read.status}).`);
        documentContext = parsed.context;
        visionImages = Array.isArray(parsed.images) ? parsed.images.filter((item: unknown): item is string => typeof item === "string") : [];
      }
      await sync(target);
      const suggestion = context || target.practice ? undefined : await suggestFromChat(user.text, controller.signal);
      if (suggestion !== undefined) {
        const assistant: Message = { id: uid(), role: "assistant", text: suggestion };
        const completed = { ...target, messages: [...target.messages, assistant] };
        await sync(completed);
        save(completed);
        retry.current = null;
        return;
      }
      const revision = revisions.current.get(target.id);
      const response = await fetch("/api/chat", {
        method: "POST", headers: revision
          ? { "Content-Type": "application/json", "X-Detot-History-Revision": revision }
          : { "Content-Type": "application/json" },
        body: JSON.stringify({ message: prompt, requestId, calendarContext: context, conversationId: target.id, startPractice, documentContext, visionImages,
          history: prefix.slice(-8).map(message => ({ role: message.role, text: message.text.slice(0, 450) })) }),
        signal: controller.signal,
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok || typeof data.reply !== "string") {
        const fallback = response.status === 429
          ? "Đệ Tốt đang xử lý một yêu cầu. Hãy thử lại."
          : response.status >= 500
            ? "Không kết nối được với backend lab. Chạy Start-Lab.ps1, đợi Gateway sẵn sàng rồi thử lại."
            : `API chat không trả về phản hồi hợp lệ (HTTP ${response.status}).`;
        throw new Error(data.detail || data.error || fallback);
      }
      const assistant: Message = { id: uid(), role: "assistant", text: data.reply,
        tool: data.googleCalendarCreated ? "Google Calendar" : data.calendarCreated ? "Đã tạo sự kiện trong lịch thử" : data.toolUsed ? "Công cụ lab đã hoàn tất" : undefined,
        calendarContext: data.calendarContext };
      const completed = { ...target, messages: [...target.messages, assistant] };
      save(completed);
      retry.current = null;
      const savedRevision = response.headers.get("X-Detot-History-Revision");
      if (savedRevision) revisions.current.set(target.id, savedRevision);
      else if (remoteWorkspace) throw new Error("Đã nhận câu trả lời nhưng chưa lưu được lên laptop. Tải lại để xem lịch sử đã lưu.");
    } catch (cause) {
      setError(controller.signal.aborted ? "Đã dừng yêu cầu." : cause instanceof Error ? cause.message : "Lỗi kết nối.");
    } finally {
      request.current = null;
      setBusy(false);
    }
  }

  const last = conversation?.messages.at(-1);
  return <div className="app">
    {side && <button className="scrim" aria-label="Đóng sidebar" onClick={() => setSide(false)} />}
    <aside className={side ? "open" : ""}>
      <div className="brand"><span className="mark">✦</span><div>Đệ Tốt<small>Không gian cá nhân</small></div></div>
      <button className="new" disabled={busy} onClick={() => { select(); input.current?.focus(); }}>＋ Cuộc trò chuyện mới</button>
      <input aria-label="Tìm cuộc trò chuyện" placeholder="Tìm cuộc trò chuyện…" value={query} onChange={event => setQuery(event.target.value)} />
      <nav aria-label="Lịch sử">
        {query.trim() && visibleItems.length === 0 && <p role="status">Không tìm thấy cuộc trò chuyện.</p>}
        {visibleItems.map(item => <div className={item.id === active ? "row selected" : "row"} key={item.id}>
          <button className="title" aria-current={!calendar && item.id === active ? "true" : undefined} disabled={busy} onClick={() => select(item.id)}>{item.pinned ? "◆ " : ""}{item.title}</button>
          <details><summary aria-label={`Thao tác với ${item.title}`}>⋯</summary><div className="menu">
            <button disabled={busy} onClick={() => void updateConversation({ ...item, pinned: !item.pinned })}>{item.pinned ? "Bỏ ghim" : "Ghim"}</button>
            <button disabled={busy} onClick={() => {
              const title = window.prompt("Tên cuộc trò chuyện", item.title)?.trim();
              if (title) void updateConversation({ ...item, title: title.slice(0, 100) });
            }}>Đổi tên</button>
            <button disabled={busy} onClick={async () => {
              if (!window.confirm(remoteWorkspace ? "Xóa cuộc trò chuyện khỏi lịch sử trên laptop?" :
                item.gatewayLinked ? "Xóa cuộc trò chuyện khỏi Gateway và trình duyệt?" : "Xóa bản lưu cũ trên trình duyệt?")) return;
              setBusy(true);
              try {
                if (remoteWorkspace || item.gatewayLinked) {
                  const response = await fetch(`/api/chat/${encodeURIComponent(item.id)}`, { method: "DELETE" });
                  if (!response.ok) throw new Error("Gateway chưa xác nhận xóa. Hãy thử lại.");
                }
                setItems(old => old.filter(value => value.id !== item.id));
                if (active === item.id) select();
              } catch (cause) {
                setError(cause instanceof Error ? cause.message : "Không xóa được cuộc trò chuyện.");
              } finally { setBusy(false); }
            }}>{item.gatewayLinked ? "Xóa cuộc trò chuyện" : "Xóa bản lưu cũ"}</button>
          </div></details>
        </div>)}
      </nav>
      <div className="sidebar-footer">
        <button disabled={busy} onClick={() => { setCalendar(false); setCloudStorage(true); setTrustedLearning(false); setPracticeNotes(undefined); setSide(false); }}>Cloud Storage</button>{" "}
        <button disabled={busy} onClick={() => { setCalendar(false); setCloudStorage(false); setTrustedLearning(true); setPracticeNotes(undefined); setSide(false); }}>Nguồn học</button>{" "}
        <button disabled={busy} onClick={() => { select(); void send("Bắt đầu một ca trực giả lập.", blank(), [], uid(), undefined, true); }}>Ca trực giả lập</button>{" "}
        <button disabled={busy} onClick={async () => {
          setBusy(true);
          try {
            const response = await fetch("/api/practice/notes");
            if (!response.ok) throw new Error("Không tải được ghi chú học tập.");
            const data: { notes: string; learning?: string } = await response.json();
            setPracticeNotes(data.notes);
            setPracticeLearning(data.learning ?? "");
            setCalendar(false);
            setCloudStorage(false);
            setTrustedLearning(false);
            setSide(false);
          } catch (cause) { setCalendar(false); setCloudStorage(false); setError((cause as Error).message); }
          finally { setBusy(false); }
        }}>Ghi chú học tập</button>{" "}
        <button aria-pressed={calendar} onClick={() => { setCalendar(!calendar); setCloudStorage(false); setTrustedLearning(false); setSide(false); }}>Lịch</button>{" "}
        <button onClick={() => setDark(!dark)}>{dark ? "☼ Giao diện sáng" : "☾ Giao diện tối"}</button>
      </div>
    </aside>
    <main>
      <header><button className="mobile" aria-label="Mở lịch sử" onClick={() => setSide(true)}>☰</button><strong>{cloudStorage ? "Cloud Storage" : trustedLearning ? "Nguồn học" : calendar ? "Lịch" : conversation?.title ?? "Cuộc trò chuyện mới"}</strong><span className="lab">Lab · dữ liệu thử</span><GoogleProfiles />{remoteWorkspace && <form method="post" action="/logout"><button>Đăng xuất</button></form>}</header>
      {practiceNotes !== undefined && <section aria-label="Ghi chú học tập" className="transcript">
        <p><strong>Tiến độ tự học:</strong> {practiceLearning}</p>
        <form onSubmit={async event => {
          event.preventDefault();
          setBusy(true);
          setError("");
          try {
            const response = await fetch("/api/practice/notes", {
              method: "PUT", headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ notes: practiceNotes }),
            });
            if (!response.ok) throw new Error("Không lưu được ghi chú học tập.");
            setPracticeNotes(undefined);
          } catch (cause) { setError((cause as Error).message); }
          finally { setBusy(false); }
        }}>
          <label>Ghi chú học tập<textarea value={practiceNotes} maxLength={2000}
            onChange={event => setPracticeNotes(event.target.value)} /></label>
          <button disabled={busy}>Lưu</button>{" "}
          <button type="button" disabled={busy} onClick={() => setPracticeNotes("")}>Xóa nội dung</button>{" "}
          <button type="button" disabled={busy} onClick={() => setPracticeNotes(undefined)}>Đóng</button>
        </form>
      </section>}
      {cloudStorage ? <CloudStorage /> : trustedLearning ? <TrustedLearning /> : calendar ? <Calendar /> : <>
      <section className="transcript" aria-label="Nội dung trò chuyện" aria-busy={busy}>
        {!conversation?.messages.length && <div className="welcome"><div className="mark">✦</div><h1>Hôm nay bạn muốn làm gì?</h1><p>Trò chuyện với Đệ Tốt hoặc tạo một sự kiện trong lịch thử.</p></div>}
        {conversation?.messages.map((message, index) => <article key={message.id} className={message.role}>
          <div className="author">{message.role === "user" ? "Bạn" : "Đệ Tốt"}</div>
          {message.tool && <div className="tool">✓ {message.tool}</div>}
          {message.role === "assistant" ? <Markdown remarkPlugins={[gfm]} components={{
            code: Code, img: ({ alt }) => <span>{alt || "[Hình ảnh]"}</span>,
            a: ({ href, children }) => <a href={href} target="_blank" rel="noopener noreferrer">{children}</a>,
          }}>{message.text}</Markdown> : <div className="user-text">{message.text}</div>}
          {message.role === "user" && <button className="action" disabled={busy} onClick={() => {
            const text = window.prompt("Sửa tin nhắn và tạo nhánh mới", message.text)?.trim();
            if (text && conversation) void send(text, { ...conversation, id: uid(), pinned: false }, forkMessages(conversation.messages, index));
          }}>Sửa và tạo nhánh</button>}
          {message.role === "assistant" && index === conversation.messages.length - 1 && !message.tool && !message.calendarContext && <button className="action" disabled={busy} onClick={() => {
            const previous = conversation.messages[index - 1];
            if (previous?.role === "user") void send(previous.text, conversation, forkMessages(conversation.messages, index - 1));
          }}>Tạo lại câu trả lời</button>}
        </article>)}
        {busy && <p className="progress" role="status">Đệ Tốt đang xử lý…</p>}
        <div ref={end} />
      </section>
      <footer>
        {error && <div className="error" role="alert">{error} {retry.current && <button onClick={() => retry.current?.()}>Thử lại</button>}</div>}
        <form className={attachments.length ? "composer has-attachments" : "composer"} onDragOver={event => event.preventDefault()}
          onDrop={event => { event.preventDefault(); addAttachments(Array.from(event.dataTransfer.files)); }}
          onSubmit={event => { event.preventDefault(); void send(draft); }}>
          <input ref={fileInput} className="file-input" type="file" multiple aria-label="Chọn tệp để phân tích"
            accept=".txt,.md,.markdown,.json,.jsonl,.ndjson,.xml,.csv,.tsv,.cs,.py,.js,.jsx,.ts,.tsx,.sql,.html,.css,.yml,.yaml,.toml,.ini,.sh,.ps1,.java,.go,.rs,.php,.rb,.c,.h,.cpp,.hpp,.docx,.xlsx,.pptx,.zip,.pdf,.doc,.xls,.ppt,.parquet,.sqlite,.sqlite3,.db,.png,.jpg,.jpeg,.webp,.gif,.tif,.tiff,.bmp"
            onChange={event => { addAttachments(Array.from(event.target.files ?? [])); event.currentTarget.value = ""; }} />
          {attachments.length > 0 && <div className="attachment-list" aria-label="Tệp đính kèm">
            {attachments.map((file, index) => <AttachmentChip key={`${file.name}-${index}`} file={file}
              onRemove={() => setAttachments(old => old.filter((_, item) => item !== index))} />)}
          </div>}
          <textarea ref={input} aria-label="Tin nhắn" rows={2} maxLength={1500} placeholder="Nhắn cho Đệ Tốt…" value={draft} onChange={event => setDraft(event.target.value)}
            onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); void send(draft); } }} />
          <div className="controls">
            {last?.calendarContext && <button type="button" disabled={busy || !draft.trim()} onClick={() => void send(draft, conversation, conversation?.messages, uid(), last.calendarContext)}>Bổ sung cho lịch</button>}
            <button type="button" disabled={busy || attachments.length >= 5} onClick={() => fileInput.current?.click()}>＋ Tệp</button>
            {busy ? <button type="button" onClick={() => request.current?.abort()}>Dừng</button> : <button className="primary" disabled={!draft.trim() && attachments.length === 0}>Gửi ↑</button>}
          </div>
        </form>
        <p>{remoteWorkspace ? "Lịch sử tự lưu trên laptop." : "Lịch sử lưu trên trình duyệt."} Tệp được trích xuất trong lab; phần văn bản gửi tới nhà cung cấp LLM. PDF/ảnh cần OCR hoặc adapter tương ứng.</p>
      </footer>
      </>}
    </main>
  </div>;
}

createRoot(document.getElementById("root")!).render(<App />);
