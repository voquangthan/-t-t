# TASK 002 — Xác minh source, dependency, Gateway và bảo mật

**Ngày khảo sát** 12/11/2026. **Mốc** OpenClaw `v2026.11.3`, commit `847611dcaa3bf0e2783335f961dd1fd0580e4d9f`.

**Đề xuất quyết định** Tiếp tục dùng OpenClaw làm ứng viên Gateway cho MVP. Giữ `v2026.11.3` làm mốc khảo sát. Trước khi dùng với dữ liệu thật, chọn bản phân phối đã xử lý các advisory có liên quan và kiểm thử đường gọi, cấu hình quyền và khôi phục trong môi trường cô lập.

## 1. Xác minh nguồn

- GitHub liên kết tag với commit trên và đánh dấu chữ ký Verified. Đã tải source archive từ GitHub Codeload theo SHA và trích các thư mục Gateway, protocol, agent, infra, plugin, browser, OpenRouter, UI và các manifest để đọc cục bộ. Máy thiếu Git, nên bằng chứng cục bộ là archive theo commit. Chữ ký Git được đối chiếu trên GitHub. [Release](https://github.com/openclaw/openclaw/releases/tag/v2026.11.3#release-verification) · [Commit](https://github.com/openclaw/openclaw/commit/847611dcaa3bf0e2783335f961dd1fd0580e4d9f)
- SHA-256 archive nguồn ghi nhận cục bộ là `3bd54d51c9c417f4ebb870d58274847b733f31ef642929ecb23570521d237e6`. SHA-256 `pnpm-lock.yaml` là `cac4e3810242167d941d14b6befd63959ca76bd10289437c33caa5366eeb383c`. Hai giá trị dùng để nhận diện các tệp đã kiểm tra.
- SHA-256 ZIP dependency evidence khớp digest upstream `344d295eab1bcead5d4dbbb5bec47a11b6d78a4dcba23a13feee5c23fd4bb585`. Manifest trong ZIP ghi cùng tag, SHA và phiên bản, được tạo ở workflow preflight. [ZIP dependency evidence](https://github.com/openclaw/openclaw/releases/download/v2026.11.3/openclaw-2026.11.3-dependency-evidence.zip) · [Workflow](https://github.com/openclaw/openclaw/actions/runs/42805216493)

## 2. Kiểm tra dependency và license

Manifest khai báo MIT, Node `>=24.16.0 <25 || >=26.1.0` và pnpm 12.15.0. Lockfile v9 có phần bootstrap package manager và phần workspace. Các phụ thuộc trực tiếp đáng chú ý gồm `ws 8.22.0`, `express 5.2.1`, `playwright-core 1.63.0`, `openai 7.28.0`, `pg 8.23.1` và `sqlite-vec 0.1.9` tùy chọn. Workspace có cooldown phát hành 10.080 phút với ngoại lệ và danh sách quyền build. Rà lại lifecycle scripts, binary theo nền tảng và thông báo license của artifact được chọn trước khi phân phối. [Manifest](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/package.json) · [Lockfile](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/pnpm-lock.yaml) · [Notices](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/THIRD_PARTY_NOTICES.md)

Đọc JSON của cổng kiểm tra upstream và nhóm lại theo lockfile, graph và mức độ. **56 phát hiện** gồm **51 trong production graph của workspace** (1 critical, 14 high, 28 moderate, 8 low), 2 mục trong nhóm `all` của workspace và 3 trong lockfile tooling Vercel. Cổng chỉ chặn malware và báo 0 mục thuộc loại đó. Nguồn advisory repo công khai chỉ phủ một phần (547/1.065 repo đọc đầy đủ, 868 vấn đề về độ phủ). Workspace graph có 1.884 phiên bản đã phân giải, trong đó 1.479 ở production graph. Phạm vi gói thực sự triển khai cho MVP cần đối chiếu từ artifact và các plugin bật. [Bằng chứng upstream](https://github.com/openclaw/openclaw/releases/download/v2026.11.3/openclaw-2026.11.3-dependency-evidence.zip)

| Ưu tiên | Đối chiếu cục bộ và xử lý |
|---|---|
| DOMPurify | UI khai báo và lockfile ghim `3.4.16`, trong dải critical của advisory. Link Reader dùng RETURN_DOM_FRAGMENT. Lệnh sanitize tại đó chưa cho thấy SAFE_FOR_TEMPLATES, nên quan sát này chỉ xác nhận một phần điều kiện của advisory. [Link Reader](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/ui/src/components/link-reader-markdown.tsx#L193) Giữ Control UI ngoài phạm vi MVP và đối chiếu bản từ `3.4.17` trở lên với các advisory còn lại trước khi dùng UI. [GHSA-cg2g-xh64-v4q7](https://github.com/advisories/GHSA-cg2g-xh64-v4q7) |
| Zod | Root ghim `4.6.5`; lockfile có nhiều phiên bản, bao gồm `4.6.7`. Advisory liệt kê bản vá `4.6.6`. Kiểm kê các bản được đóng gói và giới hạn kích thước đầu vào tại ingress trước validation. [GHSA-mrhx-c7xc-m3w5](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5) |
| Undici | Root ghim `8.12.2`; lockfile vẫn có `mailauth@7.1.1 → undici@8.11.2`. Đối chiếu đường phụ thuộc mail và artifact MVP trước khi kết luận về phơi nhiễm. [GHSA-xjj6-f33q-929c](https://github.com/advisories/GHSA-xjj6-f33q-929c) |
| OpenClaw và plugin | Advisory node exec từ TASK 001 ảnh hưởng OpenClaw trước `2026.6.5`, ngoài dải của mốc hiện tại. Advisory Prometheus áp dụng cho package plugin riêng trước `2026.9.3`. Ghim plugin theo bản vá nếu đưa vào phạm vi sau này. [Node approvals](https://github.com/openclaw/openclaw/security/advisories/GHSA-8f46-3xx3-8c9m) · [Prometheus](https://github.com/openclaw/openclaw/security/advisories/GHSA-rx8p-qcpv-c7vr) |

Phiên kiểm tra này đối chiếu lockfile và báo cáo release, chưa chạy scanner độc lập, build, cài runtime hay rà license bắc cầu của artifact triển khai.

## 3. Gateway và hợp đồng ASP.NET Core

Bổ sung cho TASK 001. Control UI dùng Vite và có cả Lit lẫn Solid. React của Đệ Tốt vẫn là client riêng. [UI manifest](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/ui/package.json)

1. **Kết nối.** WebSocket nhận frame `connect` đã validate. Luồng xác thực xử lý danh tính, bằng chứng thiết bị khi cần và thẩm quyền của phiên. Ghim schema trong `packages/gateway-protocol`; giữ token và danh tính thiết bị ở backend. [WS message handler](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/server/ws-connection/message-handler.ts) · [Connect auth](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/server/ws-connection/connect-auth.ts)
2. **RPC.** Dispatcher kiểm tra thẩm quyền trước khi gọi handler, với scope theo method và kiểm tra truy cập session khi phù hợp. `chat.send` có `idempotencyKey` và ACK tách khỏi quá trình thực thi. Backend phải nối ACK với run ID, đối chiếu trạng thái khi mất kết nối, và kiểm tra bộ lọc event trước khi phát về React. [Dispatcher](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/server-methods.ts) · [Chat dispatch](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/server-methods/chat-send-input-dispatch.ts) · [Event scopes](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/server-broadcast-scopes.ts)
3. **LLM và tool.** Source xác nhận plugin OpenRouter dùng `OPENROUTER_API_KEY`, plugin browser đăng ký tool và Gateway có điểm thực thi tool qua HTTP. MVP chỉ cung cấp tool giả lập chỉ đọc. Cần khóa quyền ở executor và Gateway vì một lượt chat có thể yêu cầu tool theo policy đã cấp. [OpenRouter](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/extensions/openrouter/index.ts) · [Browser registration](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/extensions/browser/plugin-registration.ts)

| Bề mặt | Scope được khai báo | Phạm vi MVP |
|---|---|---|
| `health`, `chat.history`, `cron.get`, `cron.runs` | `operator.read` | Backend chỉ trả dữ liệu thuộc phiên đã xác thực. |
| `chat.send`, `chat.abort` | `operator.write` | Gọi từ backend với allowlist RPC. Chat tạo trạng thái hội thoại. Tool nghiệp vụ giới hạn chỉ đọc. |
| `cron.add/update/remove/run`, `config.patch` | `operator.admin` | Tách quản trị khỏi danh tính runtime. Job thử nghiệm do người vận hành cấu hình. |
| `exec.approval.resolve`, `node.invoke` | `operator.approvals`; node dùng scope động theo command | Để ngoài bề mặt MVP. |

[Core descriptors](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/methods/core-descriptors.ts) · [Scope động](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/method-scopes.ts)

## 4. Quyết định bảo mật và tiêu chí trước triển khai

- **HTTP trust.** `POST /tools/invoke` dùng shared-secret HTTP theo mô hình full operator khi xác thực bằng bearer token/password. Giữ Gateway private, không chuyển tiếp route này ra React hoặc reverse proxy công khai. Giữ HTTP Responses/Chat Completions tắt cho MVP WebSocket. Xác nhận luồng WebSocket dùng scope tối thiểu và ứng dụng kiểm tra quyền theo phiên. [Tools Invoke](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/tools-invoke-http.ts) · [HTTP routes](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/gateway/server-http-request.ts)
- **Thực thi và dữ liệu.** Chặn exec/process, elevated, browser và node trong MVP. Mở các tool đã duyệt theo nhu cầu. Tách workspace thử và không đưa secrets vào prompt, UI hay log. Kiểm tra quyền ở điểm thực thi, kể cả luồng gọi từ cron. Audit upstream nêu rõ rằng tắt tool ghi file vẫn để lại đường ghi nếu exec có quyền file system. [Security audit source](https://github.com/openclaw/openclaw/blob/847611dcaa3bf0e2783335f961dd1fd0580e4d9f/src/security/audit.ts)
- **Điều kiện nghiệm thu.** Chốt artifact và plugin cụ thể. Quét lại advisory và license trên dependency được phân phối, xử lý critical/high áp dụng. Trong môi trường được duyệt, xác nhận tài khoản thiếu scope bị chặn, session khác không nhận event, retry không chạy trùng, hủy và reconnect nhất quán, browser/exec/node bị chặn theo cấu hình, và khôi phục từ backup vào workspace tách biệt. Các điều kiện này đang chờ kiểm thử.
- **Phạm vi TASK 002.** Dừng sau khảo sát và ghi bằng chứng. Chưa chạy Gateway, chưa kết nối provider, Google hoặc Windows Hub. Kiểm tra phiên bản binary Hub, ma trận Gateway–Hub và thử nghiệm dữ liệu giả là điều kiện cho phase tiếp theo.
