# 12 — Milestone 1 · Dependency và retention

**Phạm vi** OpenClaw `2026.11.3`, dữ liệu giả, state và cổng loopback riêng. Lab đang dùng vẫn giữ nguyên cài đặt. Cấu hình công cụ chỉ cung cấp fixture, không mở shell, ghi file hay Windows control cho LLM.

## Dependency

Đã thử override `openclaw → zod 4.6.7` và `openclaw → npm 12.2.2` trong bản sao cài đặt riêng, giữ OpenClaw `2026.11.3`. Đã đối chiếu integrity của hai tarball với npm registry và xác nhận npm mới đóng gói `undici 6.29.1` cùng `brace-expansion 5.0.12`. Manifest và lockfile của ứng viên nằm trong `detot-lab/tests/runtime-candidate/`. Lockfile SHA-256 `d782ade2f672896b3d7fefb0217dd6e22c0aa4c2b4eee0047fd56a1b08ec4b6c`. [Zod](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5) · [Undici](https://github.com/advisories/GHSA-2m5v-pch6-74hx) · [Brace-expansion](https://github.com/advisories/GHSA-qhr7-859c-m2p7)

| Phạm vi | High | Moderate | Critical | Tổng |
|---|---:|---:|---:|---:|
| Cài đặt Lab trước sửa | 4 | 23 | 0 | 27 |
| Ứng viên đã cài, audit lại | 0 | 22 | 0 | 22 |

Hai advisory trực tiếp còn lại trong kết quả audit ứng viên là `glob` và `minimatch`. `glob 13.0.6` nằm trong npm đóng gói. `minimatch 10.2.5` nằm trong npm đóng gói và `10.2.6` nằm ở root. Các mục audit còn lại là các package bị ảnh hưởng qua hai đường phụ thuộc. Dải vá vượt major đang dùng. Đề xuất sửa tự động của npm là hạ OpenClaw xuống `2026.4.29`, ngoài mốc tương thích đã duyệt. Giữ cảnh báo mở. Không dùng `npm audit fix --force`. [Glob advisory](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4) · [Minimatch advisory](https://github.com/advisories/GHSA-g3hv-75wg-m249)

## Kết quả đã xác nhận trên bản ghim

| Ca | Kết quả thực tế |
|---|---|
| DeepSeek thật | **PASS.** Artifact chạy cục bộ `.local/m1-QbR9lC/results.json` có hai lượt HTTP 200. Log Gateway có `stopReason=tool_calls` rồi `stopReason=stop`. Lượt thử dùng fixture có mã tổng hợp, không lưu API key trong báo cáo. |
| Incognito | **PASS trong phạm vi hợp đồng.** Tạo session incognito qua RPC, tiếp tục bằng session key được, không tạo response mapping và gửi lại response ID trả 400. Sau khi dừng, ba bảng transcript/FTS kiểm tra chỉ đọc không có canary incognito dạng rõ. Chưa bao phủ mọi bản sao log, backup hoặc blob mã hóa/nén. |
| TTL và restart | **PASS.** Lưu mapping hợp lệ. Dừng Gateway của fixture, đặt duy nhất `expires_at` của mapping thử về quá khứ, rồi khởi động lại cùng state. Mapping hết hạn trả HTTP 400. Mapping đối chứng chưa hết hạn trả HTTP 200. Phép thử xác minh kiểm tra hạn runtime và lưu mapping qua restart. |
| Xóa xuyên kho | **FAIL.** RPC `sessions.delete` báo thành công. Continuation dùng response ID còn hạn của session đã xóa vẫn trả HTTP 200. Archive `.jsonl.deleted.*.zst` giữ canary giả sau giải nén. Chưa đạt yêu cầu xóa/revoke xuyên transcript, mapping, memory index và backup. |

Bằng chứng baseline retention nằm tại `detot-lab/.local/m1-5AesMz/results.json`. Cơ chế lưu archive khi xóa session thường được mô tả trong tài liệu upstream. `memory forget` xử lý memory theo nguồn, trong khi transcript và bản sao khác có vòng đời riêng. Cần xác nhận cơ chế thu hồi continuation và xử lý archive/FTS/memory/backup bằng cùng canary trước khi dùng dữ liệu thật. [Sessions delete tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/cli/sessions.md) · [Memory forget tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/cli/memory.md)

## Kiểm thử lại trên ứng viên đã vá

| Bộ kiểm thử | Kết quả | Bằng chứng cục bộ |
|---|---|---|
| Gateway, bearer sai, continuation không hợp lệ, Memory Core, Dreaming preview và mock tool round trip | 5 PASS. Memory Core thực hiện 3 yêu cầu embedding giả lập. Mock tool round trip trả HTTP 200 ở cả hai lượt. | `detot-lab/.local/m1-XvrnP4/results.json` |
| Dreaming managed sweep | 1 PASS. Job báo `succeeded`, `MEMORY.md` không đổi. | `detot-lab/.local/m1-2n5S0r/results.json` |
| Retention | 4 PASS gồm readiness, Incognito, tạo mapping và TTL qua restart. 1 FAIL ở xóa. Archive nén còn canary và continuation trả HTTP 200. | `detot-lab/.local/m1-quj3ej/results.json` |
| DeepSeek thật trên ứng viên | BLOCKED do không có key trong tiến trình thử. Kết quả PASS của lượt DeepSeek trên cài đặt gốc vẫn được ghi ở trên. | Kết quả `BLOCKED` trong các run ứng viên |

**Quyết định cổng** Milestone 1 còn FAIL ở xóa và còn 22 mục moderate chưa xử lý. Giữ dữ liệu giả, giữ runtime Lab đang dùng và chưa bắt đầu Milestone 2. Xử lý thu hồi continuation cùng vòng đời archive là điều kiện kỹ thuật cần chốt. Việc đổi topology hoặc thay bản Gateway dùng thật cần xem xét riêng theo kiến trúc đã duyệt.

## Chạy lại

Harness nhận `DETOT_TEST_OPENCLAW_ENTRY` để chọn runtime ứng viên. Các mode gồm chạy mặc định cho Gateway, Memory Core và mock continuation, `--dreaming` cho managed sweep, `--retention` cho TTL/incognito/xóa, và `--live` khi có key nhập cục bộ. Không chuyển runtime đang dùng sang ứng viên cho tới khi nghiệm thu.
