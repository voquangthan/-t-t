import React, { useEffect, useRef, useState } from "react";
import { Calendar } from "./Calendar";
import { suggestFromChat } from "./GoogleCalendar";
import { GoogleProfiles } from "./GoogleProfiles";
import { CloudStorage } from "./CloudStorage";
import { TrustedLearning } from "./TrustedLearning";
import { MemoryReview } from "./MemoryReview";
import { AgenticTasks } from "./AgenticTasks";
import { PersonalNotes } from "./PersonalNotes";
import { ConsentSettings } from "./ConsentSettings";
import { WeatherIntelligence } from "./WeatherIntelligence";
import { Icon, Overview } from "./Overview";
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
type DailyTask = { id: string; text: string; status: string; calendarReminderAt?: string | null };
type DailyNote = { id: string; text: string; status?: string; createdAt?: string | null };
type DailyEvent = { id: string; summary?: string; title?: string; start?: { dateTime?: string; date?: string } | string; end?: { dateTime?: string; date?: string } | string };
type DailyCalendarAccount = { id: string; email?: string };
type ConsentRequirement = { source: string; purpose: string; destination: string };

async function dailyRead<T>(path: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json() as Promise<T>;
}

function dailyDateKey(value: string | Date) {
  const parts = new Intl.DateTimeFormat("en", { timeZone: "Asia/Bangkok", year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(new Date(value));
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(item => item.type === type)?.value ?? "";
  return `${part("year")}-${part("month")}-${part("day")}`;
}

function dailyEventStart(event: DailyEvent) {
  if (typeof event.start === "string") return event.start;
  return event.start?.dateTime ?? (event.start?.date ? `${event.start.date}T00:00:00+07:00` : "");
}

function dailyEventEnd(event: DailyEvent) {
  if (typeof event.end === "string") return event.end;
  return event.end?.dateTime ?? (event.end?.date ? `${event.end.date}T23:59:59+07:00` : "");
}
type LearningIntent = { topic: string; domains: string[]; exactUrl?: string; collect?: boolean };
function parseLearningIntent(text: string): LearningIntent | null {
  const match = text.trim().match(/^(?:đệ tốt[, ]*)?(tìm và học|học kiến thức từ|học|cập nhật kiến thức|tìm tài liệu)\s+(.+)$/i);
  if (!match) return null;
  const collect = match[1].toLowerCase() !== "tìm tài liệu";
  let topic = match[2].trim().replace(/[.!?]+$/, "");
  const foundUrl = topic.match(/https:\/\/[^\s]+/i)?.[0];
  const url = foundUrl?.replace(/[),.;!?]+$/, "");
  if (url) {
    let domain = "";
    try { domain = new URL(url).hostname; } catch { return null; }
    topic = topic.replace(foundUrl!, "").replace(/\b(?:từ|trong|tại)\b/gi, "").trim() || "Tài liệu chính thức";
    return { topic, domains: [domain], exactUrl: url, collect };
  }
  const source = topic.match(/\s+(?:từ|trên)\s+(microsoft learn|python(?:\.org)?|mdn|github|tài liệu chính thức)$/i);
  if (source) {
    const domains: Record<string, string> = { "microsoft learn": "learn.microsoft.com", "python": "docs.python.org", "python.org": "docs.python.org", "mdn": "developer.mozilla.org", "github": "github.com" };
    topic = topic.slice(0, source.index).trim();
    if (source[1].toLowerCase() !== "tài liệu chính thức") return { topic, domains: [domains[source[1].toLowerCase()]], collect };
  }
  const domain = topic.match(/\s+(?:từ|trên)\s+([a-z0-9-]+(?:\.[a-z0-9-]+)+)$/i);
  if (domain) return { topic: topic.slice(0, domain.index).trim(), domains: [domain[1]], collect };
  return topic.length >= 2 ? { topic, domains: [], collect } : null;
}
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
  type AppView = "overview" | "chat" | "tasks" | "calendar" | "notes" | "weather" | "privacy" | "cloud" | "learning" | "dreaming" | "practice" | "skills";
  const [view, setView] = useState<AppView>(() => {
    const params = new URLSearchParams(location.search);
    return params.has("calendar") ? "calendar" : params.has("drive") ? "cloud" : "overview";
  });
  const [toolsOpen, setToolsOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem("detot.sidebar-collapsed") === "true");
  const [historyOpen, setHistoryOpen] = useState(true);
  const [mobileHistoryOpen, setMobileHistoryOpen] = useState(false);
  const [learningIntent, setLearningIntent] = useState<LearningIntent>();
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
  const [dailyLoading, setDailyLoading] = useState(false);
  const [practiceNotes, setPracticeNotes] = useState<string>();
  const [practiceLearning, setPracticeLearning] = useState("");
  const [learningMap, setLearningMap] = useState<{ completed: number; nextStep: string; topics: Array<{
    id: string; title: string; completed: number; correctDiagnoses: number; wrongDiagnoses: number;
    goodHandoffs: number; earlyEnds: number; mastery: number; confidence: string; signal: string;
  }> }>({ completed: 0, nextStep: "", topics: [] });
  const [chatLearning, setChatLearning] = useState<{ summary: string; skills: Array<{
    topic: string; skill: string; observations: number; progressSignals: number; practiceSignals: number;
    confidence: string; pattern: string; lastSeen: string;
  }> }>({ summary: "", skills: [] });
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
  const navigate = (next: AppView) => { setView(next); setPracticeNotes(undefined); setSide(false); setMobileHistoryOpen(false); setError(""); };
  const select = (id?: string) => { setView("chat"); setActive(id); setDraft(""); setAttachments([]); setSide(false); setMobileHistoryOpen(false); setError(""); retry.current = null; };

  const addAttachments = (incoming: File[]) => {
    const combined = [...attachments, ...incoming];
    if (combined.length > 5) { setError("Mỗi lượt gửi tối đa 5 tệp."); return; }
    if (combined.some(file => file.size > 8 * 1024 * 1024)) { setError("Mỗi tệp tối đa 8 MiB."); return; }
    setError("");
    setAttachments(combined);
  };

  async function send(text: string, base = conversation ?? blank(), prefix = base.messages, requestId = uid(), context?: string | null,
    startPractice = false, files = attachments, startMiniPractice = false, extraContext?: string,
    extraConsents?: ConsentRequirement[]) {
    if ((!text.trim() && files.length === 0) || request.current) return;
    const previousMessage = prefix.at(-1);
    const pendingCalendarContext = context === null ? undefined : context ?? (previousMessage?.role === "assistant" ? previousMessage.calendarContext : undefined);
    const learning = files.length === 0 && !pendingCalendarContext && !startPractice && !base.practice ? parseLearningIntent(text) : null;
    if (learning) {
      setView("learning"); setLearningIntent(learning);
      setPracticeNotes(undefined); setSide(false); setDraft(""); setError("");
      return;
    }
    setView("chat");
    const prompt = [text.trim() || "Đọc và phân tích các tệp đính kèm.",
      files.length ? `Tệp đính kèm: ${files.map(file => file.name).join(", ")}` : ""].filter(Boolean).join("\n").slice(0, 1500);
    const displayText = (files.length ? `${prompt}\n📎 ${files.map(file => file.name).join(", ")}` : prompt).slice(0, 450);
    const user: Message = { id: requestId, role: "user", text: displayText };
    const target = { ...base, practice: startPractice || base.practice,
      miniPractice: startMiniPractice || base.miniPractice, gatewayLinked: true,
      title: prefix.length ? base.title : prompt.slice(0, 52), messages: [...prefix, user] };
    save(target);
    setActive(target.id);
    setDraft("");
    setAttachments([]);
    setError("");
    setBusy(true);
    const controller = new AbortController();
    request.current = controller;
    retry.current = () => void send(text, base, prefix, requestId, context === null ? null : pendingCalendarContext,
      startPractice, files, startMiniPractice, extraContext, extraConsents);
    try {
      const requestConsents = [...(extraConsents ?? [])];
      if (files.length) requestConsents.push(
        { source: "uploaded_files", purpose: "direct_request", destination: "local_only" },
        { source: "uploaded_files", purpose: "direct_request", destination: "DeepSeek" },
      );
      if (prefix.length && !startPractice && !startMiniPractice && !base.practice && !base.miniPractice)
        requestConsents.push({ source: "chat_history", purpose: "direct_request", destination: "DeepSeek" });
      if (remoteWorkspace && revisions.current.has(target.id))
        requestConsents.push({ source: "chat_history", purpose: "direct_request", destination: "local_only" });
      if (pendingCalendarContext) requestConsents.push(
        { source: "calendar", purpose: "direct_request", destination: "DeepSeek" },
      );
      if (requestConsents.length) {
        const permission = await fetch("/api/consent/check", { method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ requirements: requestConsents, requestId }) });
        if (permission.status === 403) throw new Error("Yêu cầu cần quyền cho nguồn dữ liệu tương ứng trong mục Quyền riêng tư.");
        if (!permission.ok) throw new Error("Không kiểm tra được quyền riêng tư trước khi truy xuất dữ liệu.");
      }
      let documentContext: string | undefined = extraContext;
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
      const suggestion = pendingCalendarContext || target.practice || target.miniPractice || extraContext ? undefined : await suggestFromChat(user.text, controller.signal);
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
        body: JSON.stringify({ message: prompt, requestId, calendarContext: pendingCalendarContext, conversationId: target.id, startPractice,
          miniPractice: target.miniPractice, startMiniPractice, documentContext, visionImages, consentRequirements: requestConsents,
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
      let eventUrl: string | undefined;
      if (typeof data.eventUrl === "string") {
        try {
          const candidate = new URL(data.eventUrl);
          if (candidate.protocol === "https:" &&
            (candidate.hostname === "calendar.google.com" && candidate.pathname.startsWith("/calendar/event") ||
              candidate.hostname === "www.google.com" && candidate.pathname === "/calendar/event" && candidate.searchParams.has("eid")))
            eventUrl = candidate.toString();
        } catch { /* Ignore malformed URLs returned by the provider. */ }
      }
      const assistant: Message = { id: uid(), role: "assistant", text: data.reply + (eventUrl ? `\n\n[Mở sự kiện trong Google Calendar](${eventUrl})` : ""),
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

  async function askDailyOverview() {
    if (dailyLoading || busy || conversation?.practice || conversation?.miniPractice) return;
    setDailyLoading(true);
    setError("");
    const controller = new AbortController();
    try {
      const required: ConsentRequirement[] = [
        { source: "task_memory", purpose: "direct_request", destination: "DeepSeek" },
        { source: "task_memory", purpose: "direct_request", destination: "local_only" },
        { source: "calendar", purpose: "direct_request", destination: "DeepSeek" },
        { source: "calendar", purpose: "direct_request", destination: "local_only" },
        { source: "personal_notes", purpose: "direct_request", destination: "DeepSeek" },
        { source: "personal_notes", purpose: "direct_request", destination: "local_only" },
      ];
      const permission = await fetch("/api/consent/check", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ requirements: required, requestId: crypto.randomUUID() }) });
      if (permission.status === 403) throw new Error("Tổng quan cần quyền đọc cục bộ và dùng trong Chat cho Công việc, Google Calendar và Ghi chú trong mục Quyền riêng tư.");
      if (!permission.ok) throw new Error("Không kiểm tra được quyền riêng tư trước khi đọc dữ liệu.");
      const [tasksResult, notesResult, calendarResult] = await Promise.allSettled([
        dailyRead<DailyTask[]>("/api/memory/agentic/tasks", controller.signal),
        dailyRead<DailyNote[]>("/api/memory?category=note", controller.signal),
        (async () => {
          const accounts = await dailyRead<DailyCalendarAccount[]>("/api/google/calendar/accounts", controller.signal);
          if (accounts.length) {
            return dailyRead<{ events: DailyEvent[] }>(`/api/google/calendar/${encodeURIComponent(accounts[0].id)}/events`, controller.signal);
          }
          return dailyRead<{ events: DailyEvent[]; enabled?: boolean }>("/api/calendar", controller.signal);
        })(),
      ]);
      const today = dailyDateKey(new Date());
      const dateLabel = new Intl.DateTimeFormat("vi-VN", { weekday: "long", day: "numeric", month: "long", year: "numeric", timeZone: "Asia/Bangkok" }).format(new Date());
      const contextLines = [`TỔNG QUAN NGÀY ${dateLabel.toLocaleUpperCase("vi-VN")} (múi giờ Asia/Bangkok).`,
        "Đây là dữ liệu cá nhân đã được người dùng yêu cầu xem. Chỉ tóm tắt dữ liệu dưới đây; phân biệt rõ mục nào không có dữ liệu. Không tự tạo sự kiện, nhắc lịch hoặc đánh dấu công việc hoàn thành."];

      if (tasksResult.status === "fulfilled") {
        const openTasks = tasksResult.value.filter(task => task.status === "open");
        const todayTasks = openTasks.filter(task => task.calendarReminderAt && dailyDateKey(task.calendarReminderAt) === today);
        const unscheduledTasks = openTasks.filter(task => !task.calendarReminderAt);
        contextLines.push("\nCÔNG VIỆC ĐANG MỞ HẸN HÔM NAY:", ...(todayTasks.length ? todayTasks.slice(0, 20).map(task => `- ${task.text} (nhắc ${new Date(task.calendarReminderAt!).toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit", timeZone: "Asia/Bangkok" })})`) : ["- Không có việc nào được hẹn hôm nay."]),
          "\nCÔNG VIỆC ĐANG MỞ CHƯA CÓ GIỜ HẸN:", ...(unscheduledTasks.length ? unscheduledTasks.slice(0, 10).map(task => `- ${task.text}`) : ["- Không có."]));
      } else contextLines.push("\nCÔNG VIỆC: Không tải được danh sách công việc.");

      if (calendarResult.status === "fulfilled") {
        const todayEvents = calendarResult.value.events.filter(event => {
          const start = dailyEventStart(event);
          const end = dailyEventEnd(event) || start;
          const allDay = typeof event.start === "object" && !!event.start?.date && !event.start?.dateTime;
          return start && dailyDateKey(start) <= today && (allDay ? dailyDateKey(end) > today : dailyDateKey(end) >= today);
        }).sort((a, b) => Date.parse(dailyEventStart(a)) - Date.parse(dailyEventStart(b))).slice(0, 30);
        contextLines.push("\nSỰ KIỆN TRONG LỊCH HÔM NAY:", ...(todayEvents.length ? todayEvents.map(event => {
          const start = dailyEventStart(event);
          const allDay = typeof event.start === "object" && !!event.start?.date && !event.start?.dateTime;
          const time = allDay ? "cả ngày" : new Date(start).toLocaleTimeString("vi-VN", { hour: "2-digit", minute: "2-digit", timeZone: "Asia/Bangkok" });
          return `- ${time}: ${event.summary ?? event.title ?? "Sự kiện"}`;
        }) : ["- Không có sự kiện hôm nay."]));
      } else contextLines.push("\nLỊCH: Không tải được lịch.");

      if (notesResult.status === "fulfilled") {
        const recentNotes = notesResult.value.filter(note => note.status !== "deleted" && note.status !== "archived")
          .sort((a, b) => Date.parse(b.createdAt ?? "") - Date.parse(a.createdAt ?? ""))
          .slice(0, 5);
        contextLines.push("\nGHI CHÚ CÁ NHÂN GẦN ĐÂY:", ...(recentNotes.length ? recentNotes.map(note => `- ${note.text.slice(0, 500)}`) : ["- Chưa có ghi chú."]));
      } else contextLines.push("\nGHI CHÚ: Không tải được ghi chú cá nhân.");

      const base = conversation ?? blank();
      void send("Dựa trên tổng quan cá nhân hôm nay, hãy giúp mình nắm lịch trình, việc cần chú ý và một bước tiếp theo phù hợp. Trả lời ngắn gọn bằng tiếng Việt.",
        base, base.messages, uid(), null, false, [], false, contextLines.join("\n").slice(0, 120_000), required);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Không tải được tổng quan hôm nay.");
    } finally {
      setDailyLoading(false);
    }
  }

  function beginMiniPractice() {
    const session: Conversation = { ...blank(), title: "Luyện nhanh 5 phút", miniPractice: true };
    setView("chat"); setPracticeNotes(undefined); setSide(false);
    void send("Bắt đầu một thử thách code 5 phút phù hợp với mình.", session, [], uid(), undefined, false, [], true);
  }

  async function endMiniPractice() {
    if (!conversation?.miniPractice) return;
    await updateConversation({ ...conversation, miniPractice: false });
  }

  const last = conversation?.messages.at(-1);
  const titles: Record<AppView, string> = { overview:"Tổng quan", chat:conversation?.title ?? "Trò chuyện", tasks:"Công việc", calendar:"Lịch", notes:"Ghi chú", weather:"Thời tiết", privacy:"Quyền riêng tư", cloud:"Cloud Storage", learning:"Nguồn học", dreaming:"Dreaming", practice:"Ca trực giả lập", skills:"Bản đồ học tập" };
  const navItems: Array<{id:AppView;label:string;icon:string}> = [
    {id:"overview",label:"Tổng quan",icon:"overview"},{id:"chat",label:"Trò chuyện",icon:"chat"},{id:"tasks",label:"Công việc",icon:"tasks"},{id:"calendar",label:"Lịch",icon:"calendar"},{id:"notes",label:"Ghi chú",icon:"notes"},{id:"weather",label:"Thời tiết",icon:"weather"},{id:"privacy",label:"Quyền riêng tư",icon:"privacy"}
  ];
  const toolItems: Array<{id:AppView;label:string;icon:string}> = [
    {id:"cloud",label:"Cloud Storage",icon:"cloud"},{id:"learning",label:"Nguồn học",icon:"book"},{id:"dreaming",label:"Dreaming",icon:"dream"},{id:"practice",label:"Ca trực giả lập",icon:"practice"},{id:"skills",label:"Bản đồ học tập",icon:"skills"}
  ];
  const startChat = (text:string) => {setView("chat");void send(text,blank(),[],uid());};
  async function openSkills(){
    setBusy(true);
    try{const response=await fetch("/api/practice/notes");if(!response.ok)throw new Error("Không tải được ghi chú học tập.");
      const data:{notes:string;learning?:string;map?:typeof learningMap;chatLearning?:typeof chatLearning}=await response.json();
      setPracticeNotes(data.notes);setPracticeLearning(data.learning??"");setLearningMap(data.map??{completed:0,nextStep:"",topics:[]});setChatLearning(data.chatLearning??{summary:"",skills:[]});navigate("skills");
    }catch(cause){setError((cause as Error).message);}finally{setBusy(false);}
  }
  const historyPanel = <>
    <button className="history-new" disabled={busy} onClick={()=>{select();input.current?.focus();}}>＋ Cuộc trò chuyện mới</button>
    <label className="history-search"><Icon name="search"/><input aria-label="Tìm cuộc trò chuyện" placeholder="Tìm cuộc trò chuyện…" value={query} onChange={event=>setQuery(event.target.value)}/></label>
    <div className="history-label">GẦN ĐÂY</div><nav className="history-list" aria-label="Lịch sử trò chuyện">
      {query.trim()&&visibleItems.length===0&&<p role="status">Không tìm thấy cuộc trò chuyện.</p>}
      {visibleItems.map(item=><div className={"history-row "+(item.id===active?"selected":"")} key={item.id}>
        <button className="history-title" aria-current={item.id===active?"page":undefined} disabled={busy} onClick={()=>select(item.id)}>{item.pinned?"◆ ":""}{item.title}</button>
        <details><summary aria-label={"Thao tác với "+item.title}>⋯</summary><div className="history-menu">
          <button disabled={busy} onClick={()=>void updateConversation({...item,pinned:!item.pinned})}>{item.pinned?"Bỏ ghim":"Ghim"}</button>
          <button disabled={busy} onClick={()=>{const name=window.prompt("Tên cuộc trò chuyện",item.title)?.trim();if(name)void updateConversation({...item,title:name.slice(0,100)});}}>Đổi tên</button>
          <button disabled={busy} onClick={async()=>{if(!window.confirm("Xóa cuộc trò chuyện khỏi lịch sử?"))return;setBusy(true);try{if(remoteWorkspace||item.gatewayLinked){const response=await fetch("/api/chat/"+encodeURIComponent(item.id),{method:"DELETE"});if(!response.ok)throw new Error("Gateway chưa xác nhận xóa.");}setItems(old=>old.filter(value=>value.id!==item.id));if(active===item.id)select();}catch(cause){setError((cause as Error).message);}finally{setBusy(false);}}}>{item.gatewayLinked?"Xóa cuộc trò chuyện":"Xóa bản lưu cũ"}</button>
        </div></details>
      </div>)}
    </nav>
  </>;
  return <div className={"app "+(collapsed?"nav-collapsed ":"")+(view==="chat"&&historyOpen?"with-chat-history":"")}>
    {side&&<button className="nav-scrim" aria-label="Đóng điều hướng" onClick={()=>setSide(false)}/>}
    <aside className={"app-sidebar "+(side?"open ":"")+(collapsed?"collapsed":"")}>
      <div className="brand"><span className="mark">✦</span><div className="brand-copy">Đệ Tốt<small>Không gian cá nhân</small></div></div>
      <nav className="primary-nav" aria-label="Điều hướng chính">
        {navItems.map(item=><button key={item.id} className={"nav-item "+(view===item.id?"active":"")} aria-current={view===item.id?"page":undefined} title={collapsed?item.label:undefined} onClick={()=>item.id==="chat"?select():navigate(item.id)}><Icon name={item.icon}/><span>{item.label}</span></button>)}
        <button className="nav-item tools-toggle" aria-expanded={toolsOpen} title={collapsed?"Công cụ":undefined} onClick={()=>setToolsOpen(value=>!value)}><Icon name="tools"/><span>Công cụ</span><Icon name="chevron"/></button>
        {toolsOpen&&<div className="tools-list">{toolItems.map(item=><button key={item.id} className={"nav-item tool-item "+(view===item.id?"active":"")} title={collapsed?item.label:undefined} onClick={()=>{if(item.id==="practice"){select();void send("Bắt đầu một ca trực giả lập.",blank(),[],uid(),undefined,true);}else if(item.id==="skills")void openSkills();else{if(item.id==="learning")setLearningIntent(undefined);navigate(item.id);}}}><Icon name={item.icon}/><span>{item.label}</span></button>)}</div>}
      </nav>
      <div className="sidebar-spacer"/>
      <details className="account-menu"><summary title={collapsed?"Tài khoản và trạng thái":undefined}><span className="account-avatar">ĐT</span><span className="account-copy"><strong>Tài khoản</strong><small>{remoteWorkspace?"Đã đăng nhập":"Máy này"}</small></span><Icon name="chevron"/></summary>
        <div className="account-popover"><div className="account-status"><span className="status-dot"/>Lab · dữ liệu thử</div><div className="google-profile-slot"><GoogleProfiles/></div>{remoteWorkspace&&<form method="post" action="/logout"><button className="account-action">Đăng xuất</button></form>}<button className="account-action" onClick={()=>setDark(!dark)}>{dark?"☼":"☾"} Giao diện {dark?"sáng":"tối"}</button></div>
      </details>
      <button className="collapse-toggle" aria-label={collapsed?"Mở rộng thanh điều hướng":"Thu gọn thanh điều hướng"} onClick={()=>setCollapsed(value=>{localStorage.setItem("detot.sidebar-collapsed",String(!value));return !value;})}><Icon name={collapsed?"expand":"collapse"}/><span>Thu gọn</span></button>
    </aside>
    {view==="chat"&&historyOpen&&<aside className={"chat-history "+(mobileHistoryOpen?"mobile-history-open":"")} aria-label="Lịch sử cuộc trò chuyện">{historyPanel}</aside>}
    {mobileHistoryOpen&&<button className="history-scrim" aria-label="Đóng lịch sử" onClick={()=>setMobileHistoryOpen(false)}/>}
    <main className="main-content">
      <header className="topbar"><button className="mobile-nav-button" aria-label="Mở điều hướng" onClick={()=>setSide(true)}><Icon name="menu"/></button>
        {view==="chat"&&<button className="history-toggle" aria-label="Lịch sử trò chuyện" onClick={()=>{if(window.matchMedia("(max-width: 760px)").matches)setMobileHistoryOpen(true);else setHistoryOpen(open=>!open);}}><Icon name="history"/></button>}
        <strong>{titles[view]}</strong><span className="topbar-spacer"/>
        {conversation?.practice&&<button className="topbar-action" disabled={busy} onClick={()=>{select();input.current?.focus();}}>Chuyển sang chat thường</button>}
        {conversation?.miniPractice&&<><span className="mini-practice-badge">Luyện nhanh · có thể dừng bất cứ lúc nào</span><button className="topbar-action" disabled={busy} onClick={()=>void endMiniPractice()}>Kết thúc lượt luyện</button></>}
        {view==="chat"&&!conversation?.practice&&!conversation?.miniPractice&&<button className="topbar-action daily-overview-action" disabled={busy||dailyLoading} title="Đọc công việc, sự kiện hôm nay và ghi chú gần đây để soạn tổng quan trong Chat" onClick={()=>void askDailyOverview()}>{dailyLoading?"Đang tải tổng quan…":"Tóm tắt hôm nay"}</button>}
        {view==="chat"&&<button className="topbar-action" disabled={busy} onClick={()=>{select();input.current?.focus();}}>＋ Chat mới</button>}
      </header>
      {error&&<div className="global-error" role="alert">{error} {retry.current&&<button onClick={()=>retry.current?.()}>Thử lại</button>}</div>}
      <div className={"view-content "+(view==="chat"?"chat-content":"")}>
        {view==="overview"&&<Overview onNavigate={navigate} onChat={startChat}/>}
        {view==="tasks"&&<AgenticTasks/>}{view==="calendar"&&<Calendar/>}{view==="notes"&&<PersonalNotes/>}
        {view==="weather"&&<WeatherIntelligence onNavigate={navigate}/>}
        {view==="privacy"&&<ConsentSettings/>}
        {view==="cloud"&&<CloudStorage/>}{view==="learning"&&<TrustedLearning initialRequest={learningIntent}/>}
        {view==="dreaming"&&<MemoryReview/>}
        {view==="skills"&&<section className="learning-dashboard" aria-label="Bản đồ học tập">
          <section className="learning-overview"><h2>Bản đồ học tập</h2><p><strong>Tiến độ:</strong> {practiceLearning}</p><p>{learningMap.nextStep}</p>
            <button disabled={busy} onClick={()=>{select();void send("Bắt đầu một ca trực giả lập.",blank(),[],uid(),undefined,true);}}>Luyện ca được đề xuất</button>{" "}<button disabled={busy} onClick={beginMiniPractice}>Luyện nhanh 5 phút</button>
            {learningMap.completed>0&&<div className="learning-map">{learningMap.topics.map(topic=><article className="learning-topic" key={topic.id}><div className="learning-topic-heading"><strong>{topic.title}</strong><span>{topic.mastery}% tín hiệu tiến bộ</span></div><progress max={100} value={topic.mastery}/><p>{topic.confidence} · {topic.completed} ca · chẩn đoán đúng {topic.correctDiagnoses}, sai {topic.wrongDiagnoses} · bàn giao tốt {topic.goodHandoffs}</p><p>{topic.signal}</p></article>)}</div>}
            <p className="muted">Điểm tiến bộ được làm mượt khi còn ít lượt. Bản đồ chỉ lưu số liệu tổng hợp.</p></section>
          <section className="chat-learning"><div className="chat-learning-heading"><div><h3>Hiểu cách bạn học qua trò chuyện</h3><p>{chatLearning.summary||"Chưa có tín hiệu học tập."}</p></div>{chatLearning.skills.length>0&&<button disabled={busy} onClick={async()=>{if(!window.confirm("Xóa toàn bộ tín hiệu cá nhân hóa rút ra từ các buổi ôn code?"))return;setBusy(true);try{const response=await fetch("/api/practice/chat-learning",{method:"DELETE"});if(!response.ok)throw new Error("Không xóa được tín hiệu học tập.");setChatLearning({summary:"Tín hiệu đã xóa.",skills:[]});}catch(cause){setError((cause as Error).message);}finally{setBusy(false);}}}>Xóa tín hiệu</button>}</div>
            {chatLearning.skills.map(item=><article className="learning-topic" key={item.topic+item.skill}><strong>{item.topic} · {item.skill}</strong><p>{item.pattern}</p><p>{item.observations} lượt · tự áp dụng {item.progressSignals} · cần gợi ý {item.practiceSignals}</p></article>)}</section>
          <section className="learning-notes"><h3>Ghi chú học tập</h3><textarea value={practiceNotes??""} maxLength={2000} onChange={event=>setPracticeNotes(event.target.value)}/><button disabled={busy} onClick={async()=>{setBusy(true);try{const response=await fetch("/api/practice/notes",{method:"PUT",headers:{"Content-Type":"application/json"},body:JSON.stringify({notes:practiceNotes??""})});if(!response.ok)throw new Error("Không lưu được ghi chú học tập.");}catch(cause){setError((cause as Error).message);}finally{setBusy(false);}}}>Lưu ghi chú</button></section>
        </section>}
        {view==="chat"&&<div className="chat-page">
          <section className="transcript" aria-label="Nội dung trò chuyện" aria-busy={busy}>
            {!conversation?.messages.length&&<div className="welcome"><div className="mark">✦</div><h1>Hôm nay bạn muốn làm gì?</h1><p>Trò chuyện với Đệ Tốt hoặc tạo một sự kiện trong lịch thử.</p></div>}
            {conversation?.messages.map((message,index)=><article key={message.id} className={message.role}><div className="author">{message.role==="user"?"Bạn":"Đệ Tốt"}</div>{message.tool&&<div className="tool">✓ {message.tool}</div>}
              {message.role==="assistant"?<Markdown remarkPlugins={[gfm]} components={{code:Code,img:({alt})=><span>{alt||"[Hình ảnh]"}</span>,a:({href,children})=><a href={href} target="_blank" rel="noopener noreferrer">{children}</a>}}>{message.text}</Markdown>:<div className="user-text">{message.text}</div>}
              {message.role==="user"&&<button className="action" disabled={busy} onClick={()=>{const text=window.prompt("Sửa tin nhắn và tạo nhánh mới",message.text)?.trim();if(text&&conversation)void send(text,{...conversation,id:uid(),pinned:false},forkMessages(conversation.messages,index));}}>Sửa và tạo nhánh</button>}
              {message.role==="assistant"&&index===conversation.messages.length-1&&!message.tool&&!message.calendarContext&&<button className="action" disabled={busy} onClick={()=>{const previous=conversation.messages[index-1];if(previous?.role==="user")void send(previous.text,conversation,forkMessages(conversation.messages,index-1));}}>Tạo lại câu trả lời</button>}
            </article>)}
            {busy&&<p className="progress" role="status">Đệ Tốt đang xử lý…</p>}<div ref={end}/>
          </section>
          <footer><form className={attachments.length?"composer has-attachments":"composer"} onDragOver={event=>event.preventDefault()} onDrop={event=>{event.preventDefault();addAttachments(Array.from(event.dataTransfer.files));}} onSubmit={event=>{event.preventDefault();void send(draft);}}>
            <input ref={fileInput} className="file-input" type="file" multiple aria-label="Chọn tệp để phân tích" accept=".txt,.md,.markdown,.json,.jsonl,.ndjson,.xml,.csv,.tsv,.cs,.py,.js,.jsx,.ts,.tsx,.sql,.html,.css,.yml,.yaml,.toml,.ini,.sh,.ps1,.java,.go,.rs,.php,.rb,.c,.h,.cpp,.hpp,.docx,.xlsx,.pptx,.zip,.pdf,.doc,.xls,.ppt,.parquet,.sqlite,.sqlite3,.db,.png,.jpg,.jpeg,.webp,.gif,.tif,.tiff,.bmp" onChange={event=>{addAttachments(Array.from(event.target.files??[]));event.currentTarget.value="";}}/>
            {attachments.length>0&&<div className="attachment-list" aria-label="Tệp đính kèm">{attachments.map((file,index)=><AttachmentChip key={file.name+index} file={file} onRemove={()=>setAttachments(old=>old.filter((_,item)=>item!==index))}/>)}</div>}
            <textarea ref={input} aria-label="Tin nhắn" rows={2} maxLength={1500} placeholder="Nhắn cho Đệ Tốt…" value={draft} onChange={event=>setDraft(event.target.value)} onKeyDown={event=>{if(event.key==="Enter"&&!event.shiftKey&&!event.nativeEvent.isComposing){event.preventDefault();void send(draft);}}}/>
            <div className="controls">{last?.calendarContext&&<button type="button" disabled={busy||!draft.trim()} onClick={()=>void send(draft,conversation,conversation?.messages,uid(),last.calendarContext)}>Bổ sung cho lịch</button>}
              <button type="button" disabled={busy||attachments.length>=5||!!conversation?.miniPractice} onClick={()=>fileInput.current?.click()}>＋ Tệp</button>{busy?<button type="button" onClick={()=>request.current?.abort()}>Dừng</button>:<button className="primary" disabled={!draft.trim()&&attachments.length===0}>Gửi ↑</button>}</div>
          </form><p>{remoteWorkspace?"Lịch sử tự lưu trên laptop.":"Lịch sử lưu trên trình duyệt."} Tệp được trích xuất trong lab; phần văn bản gửi tới nhà cung cấp LLM. PDF/ảnh cần OCR hoặc adapter tương ứng.</p></footer>
        </div>}
      </div>
    </main>
  </div>;
}

createRoot(document.getElementById("root")!).render(<App />);
