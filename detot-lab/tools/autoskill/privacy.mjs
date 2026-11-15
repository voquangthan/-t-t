const PRIVATE_TOPIC_RE = /(?:\b(?:medical|health|diagnos(?:is|ed)|prescription|bank account|credit card|salary|income|tax return|passport|social security|national id|home address|my address|i live at|date of birth|private key|recovery code|one[- ]time password|otp)\b|\b(?:bệnh án|bệnh viện|chẩn đoán|đơn thuốc|sức khỏe cá nhân|tài khoản ngân hàng|số thẻ|lương|thu nhập|mã số thuế|hộ chiếu|căn cước|cccd|địa chỉ nhà|địa chỉ của tôi|tôi sống ở|ngày sinh|mã khôi phục|mật khẩu|khóa riêng|mã otp)\b)/iu;
const SECRET_RE = /(?:\b(?:authorization\s*:\s*bearer|bearer)\s+[A-Za-z0-9._~+/-]+=*|\b(?:api[_ -]?key|client[_ -]?secret|access[_ -]?token|refresh[_ -]?token|token|secret|password|passwd|mật khẩu|client secret|khóa bí mật)\s*[:=：]\s*[^\s,;]+|\b(?:sk-[A-Za-z0-9_-]{16,}|AIza[A-Za-z0-9_-]{20,}|ya29\.[A-Za-z0-9._-]{16,})\b)/iu;
const EMAIL_RE = /\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/giu;
const PHONE_RE = /\b(?:\+?\d[\d ()-]{7,}\d)\b/gu;
const LONG_NUMBER_RE = /\b(?:\d[ -]*?){13,19}\b/gu;
const EXECUTABLE_PATH_RE = /(?:^|\/)(?:scripts?|bin|hooks?)(?:\/|$)|\.(?:ps1|bat|cmd|sh|exe|dll|py|js|mjs|cjs)$/iu;

export function sanitizeExperienceText(value) {
  const text = String(value ?? "");
  if (!text.trim()) return "";
  // Fail closed on sensitive subject matter rather than trying to redact context.
  if (PRIVATE_TOPIC_RE.test(text)) return "[PRIVATE_SESSION_OMITTED]";
  return text
    .replace(SECRET_RE, "[REDACTED_CREDENTIAL]")
    .replace(EMAIL_RE, "[REDACTED_EMAIL]")
    .replace(PHONE_RE, "[REDACTED_PHONE]")
    .replace(LONG_NUMBER_RE, "[REDACTED_NUMBER]");
}

export function sanitizeSkillCandidate(candidate) {
  if (!candidate || typeof candidate !== "object" || Array.isArray(candidate)) {
    return { safe: false, reasons: ["invalid_candidate"], candidate: null };
  }
  const files = candidate.files && typeof candidate.files === "object" ? candidate.files : {};
  const unsafePaths = Object.keys(files).filter((filePath) => EXECUTABLE_PATH_RE.test(String(filePath)));
  if (unsafePaths.length) {
    return { safe: false, reasons: ["executable_or_hook_file"], candidate: null };
  }
  if (Object.keys(files).length) {
    return { safe: false, reasons: ["resource_files_require_separate_review"], candidate: null };
  }
  const serialized = JSON.stringify(candidate);
  if (PRIVATE_TOPIC_RE.test(serialized)) {
    return { safe: false, reasons: ["private_topic"], candidate: null };
  }
  if (SECRET_RE.test(serialized)) {
    return { safe: false, reasons: ["credential_pattern"], candidate: null };
  }
  const clone = JSON.parse(serialized);
  const redact = (value) => {
    if (typeof value === "string") return sanitizeExperienceText(value);
    if (Array.isArray(value)) return value.map(redact);
    if (value && typeof value === "object") {
      return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, redact(child)]));
    }
    return value;
  };
  return { safe: true, reasons: [], candidate: redact(clone) };
}

export function containsSensitiveContent(value) {
  const text = String(value ?? "");
  return PRIVATE_TOPIC_RE.test(text) || SECRET_RE.test(text);
}
