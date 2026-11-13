# 03 — Kế hoạch tích hợp

Chạy OpenClaw như dịch vụ riêng trong WSL2, với một instance dành cho chủ sở hữu Đệ Tốt. React gọi ASP.NET Core cùng origin. ASP.NET Core giữ phiên người dùng, điều phối phê duyệt, audit, registry và kết nối Gateway phía máy chủ. Gateway giữ session, agent turn, memory và cron. Windows Hub chạy theo người dùng thường trong Windows. Các module riêng giao tiếp bằng API có schema và danh tính dịch vụ riêng. [Windows Hub và WSL2](https://docs.openclaw.ai/platforms/windows) · [Gateway protocol](https://docs.openclaw.ai/gateway/protocol)

| GitHub project | Cách tận dụng |
|---|---|
| [openclaw/openclaw](https://github.com/openclaw/openclaw) | Dùng bản phát hành ghim phiên bản. Gọi WebSocket RPC để nhận streaming và điều khiển session/cron. Đánh giá HTTP Responses nếu hợp đồng chat đơn giản đáp ứng yêu cầu. |
| [openclaw/openclaw-windows-node](https://github.com/openclaw/openclaw-windows-node) | Dùng companion qua Gateway node pairing. MCP loopback là phương án kiểm thử riêng cho công cụ Windows đã giới hạn. Ghim release Hub độc lập. |
| [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) · [react/react](https://github.com/react/react) | API .NET giữ secrets và quyền. React nhận stream từ API Đệ Tốt và hiển thị trạng thái phê duyệt. |
| [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk) | Ứng viên cho MCP server .NET với tool có schema, ví dụ tra Places, Routes và Weather. Kiểm tra transport được tag hỗ trợ. |

## Hợp đồng kết nối

- **Gateway.** Dùng client WebSocket phía .NET và test schema với phiên bản đã ghim. Xử lý handshake, xác thực, role/scope, session key, event đến muộn, reconnect và hủy. Chỉ cấp RPC cần thiết. Tạo request ID riêng và dùng idempotency khi gửi lại. Đọc trạng thái trước khi thử lại thao tác ghi bị mất phản hồi. [Protocol tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/gateway/protocol.md)
- **Provider.** OpenRouter hỗ trợ onboarding `openrouter-api-key`, biến `OPENROUTER_API_KEY` và model `openrouter/<provider>/<model>`. Cấp key từ secret store vào tiến trình Gateway. Ghim model/provider hỗ trợ schema tool, streaming và reasoning đã kiểm tra. Kiểm tra giới hạn chi phí, timeout, lỗi 401/429 và quyền xử lý dữ liệu của provider trước khi dùng dữ liệu thật. [OpenRouter](https://docs.openclaw.ai/providers/openrouter)
- **Tool nghiệp vụ.** Ưu tiên MCP ngoài tiến trình cho .NET. Khi cần hook vòng đời của Gateway, dùng Plugin SDK TypeScript qua API công khai. Skill chỉ dùng cho hướng dẫn tác vụ đã duyệt. Đặt xác thực và kiểm tra quyền ở endpoint thực thi, kể cả khi có tool policy Gateway. [MCP tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/tools/mcp.md) · [Plugin SDK](https://docs.openclaw.ai/plugins/sdk-overview/infrastructure)
- **Google.** Tách key Maps/Weather theo API và môi trường, giới hạn quota, giữ key ở backend. Calendar chỉ xin scope đọc sự kiện phù hợp sau khi chủ tài khoản đồng ý. Tách quyền ghi, lưu token mã hóa, hỗ trợ thu hồi và xác nhận mỗi thay đổi. Skill `gog` là tài liệu đối chiếu cho CLI/OAuth. [Weather API](https://developers.google.com/maps/documentation/weather/get-api-key) · [Calendar scopes](https://developers.google.com/workspace/calendar/api/auth) · [gog tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/skills/gog/SKILL.md)
- **Dữ liệu.** Gateway sở hữu state nội bộ. ASP.NET Core giữ consent, approval, audit và registry trong kho riêng. Daily Learning nhận dữ liệu tối thiểu từ các nguồn đã cấp quyền, ghi checkpoint theo nguồn và giữ liên kết bằng chứng để sửa hoặc thu hồi. Cron của Gateway là nguồn lịch duy nhất cho các job agent.

## Xung đột cần xác minh

| Xung đột | Quyết định và phép thử |
|---|---|
| Node/TypeScript và .NET; Lit và React | Tách tiến trình và hợp đồng. Test phiên bản protocol, event streaming, restart và reconnect. |
| WSL/Windows networking và filesystem | Test loopback giữa hai môi trường, phiên đăng nhập, boot sau restart, khác biệt chữ hoa/thường và chuẩn hóa path. Giới hạn mount và ACL, giữ dữ liệu runtime trong môi trường sở hữu. |
| Quyền Gateway, MCP và quyền người dùng | Xác nhận user/session từ backend tin cậy. Chỉ ánh xạ tool đã duyệt. Test scope thấp và token hết hạn trên từng đường gọi. |
| Job trùng, đồng hồ và DST | Một scheduler; khóa chạy theo job và kỳ lịch. Test retry, mất kết nối, trùng event, giờ chuyển DST và máy ngủ. |
| Tách phiên bản Hub, Gateway và SDK | Ghim từng bản và giữ ma trận tương thích; kiểm chứng lại trước khi nâng cấp hoặc rollback. |
