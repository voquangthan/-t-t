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

## Disposition cho 22 cảnh báo moderate

Audit của ứng viên cho 22 finding moderate, 0 high và 0 critical. Đây là số finding trong cây cài đặt, không phải 22 lỗi độc lập ở mã Đệ Tốt. Báo cáo đã nhận diện hai advisory nguồn trực tiếp: `glob 13.0.6` trong npm đóng gói (GHSA-3g3m-2fjr-6pw4, sửa từ 14.0.2) và `minimatch 10.2.5/10.2.6` (GHSA-g3hv-75wg-m249, sửa từ 11.0.1). Các finding còn lại là đường phụ thuộc bị ảnh hưởng. Bản vá nằm ngoài các major đang khai báo; ép override đã có nguy cơ phá tương thích và chưa được kiểm thử.

| Quyết định | Biện pháp |
|---|---|
| Ngắn hạn trong lab | Chỉ chạy Gateway ở loopback, fixture giả, một tool allowlist; không mở shell/file/browser; request bị giới hạn kích thước và thời gian. Không dùng provider/môi trường nhận pattern glob tùy ý từ dữ liệu không tin cậy. Đây là giảm bề mặt tấn công, không phải bản vá lỗ hổng. |
| Trước dữ liệu người dùng | Lưu SBOM và audit JSON theo đúng lockfile ứng viên; xác minh cây thực tế và call path xem `glob`/`minimatch` có nhận đầu vào không tin cậy hay không; kiểm thử giới hạn CPU/thời gian; theo dõi bản vá tương thích upstream. |
| Khi có bản vá | Nâng đúng nhánh phụ thuộc, kiểm tra integrity/lockfile và chạy lại toàn bộ tương thích, retention, tool calling và DeepSeek trên chính artifact mới. Không dùng `npm audit fix --force`. |
| Chấp nhận rủi ro | Chỉ Product Owner/Security Owner mới có thể chấp nhận bằng văn bản nếu một phiên bản cần phát hành vẫn còn moderate. Hiện chưa có chấp nhận đó; không tuyên bố 22 finding đã được khắc phục. |

## Hướng sửa xóa và bằng chứng cần có

Một lần xóa phải thu hồi mapping cho **mọi** response ID thuộc session, ghi tombstone bền vững trước khi trả thành công, rồi xóa session row/transcript, FTS/vector index, memory có provenance, cache và derived artifact. Gateway khởi động lại phải nạp tombstone hoặc xác nhận mapping đã bị xóa; continuation bị từ chối trong khi session khác vẫn hoạt động.

Archive `.jsonl.deleted.*.zst` hiện là nội dung nén có thể giải nén, không phải mã hóa. Xóa bản rõ sau khi nén không phải cryptographic erasure. Thiết kế dài hạn cần mã hóa từng artifact session-owned bằng data-encryption key riêng, key bọc bằng key mã hóa do host giữ; xóa thì thu hồi key trước, sau đó dọn ciphertext và index theo vòng đời. Phải bảo đảm không có bản sao key trong backup. Archive cũ chưa được mã hóa phải giải nén, purge mọi bản sao/index có thể chỉnh sửa, checkpoint/vacuum các DB phù hợp và quét lại; nếu không thể chứng minh các bước đó, giữ lỗi xóa và không đưa dữ liệu thật vào hệ thống. Tombstone chống truy cập nhưng không chứng minh nội dung cũ đã bị xóa.

Regression test đã được mở rộng để lấy mọi continuation ID của cùng session, kiểm tra trước/sau restart, giữ session đối chứng, quét nội dung file (kể cả `.zst`/`.gz`) và quét các bảng SQLite của state. Phép thử retention hiện fail-fast nếu thiếu candidate. Trong checkout ngày 13/11/2026, `npm ci --ignore-scripts --no-audit --no-fund` không khôi phục được package vì DNS tới `registry.npmjs.org` trả `ENOTFOUND`; thư mục runtime candidate dưới `detot-lab/.local` không có. Node cục bộ là `24.21.0`, khác bản `24.22.3` đã ghi nhận trước đó. Vì vậy test retention dừng ở preflight, chưa thực thi trên Gateway; không có key DeepSeek trong môi trường này.

Trong lần cập nhật này, `node --check` và `git diff --check` đạt; `dotnet build detot-lab/Detot.Lab.csproj` đạt 0 warning/0 error sau restore. Chưa thể kiểm thử lại toàn bộ M1 trên candidate. Cần mở rộng regression với Memory Core/vector/cache và backup đã được seeded, truy xuất trước xóa rồi purge sau xóa. Không được coi việc không tìm thấy canary trong kho chưa được seeded là bằng chứng.

**Quyết định cổng hiện tại: FAIL / chưa nghiệm thu.** Regression test mới chưa chạy trên runtime candidate và chưa có bản vá runtime; lỗi xóa đã biết vẫn tồn tại. DeepSeek thật chưa được chạy trên candidate do thiếu key/runtime. 22 moderate đã có phương án giảm thiểu và theo dõi nhưng chưa được khắc phục hay chấp nhận rủi ro. Tiếp tục giữ dữ liệu giả; chưa chuyển Milestone 2. Chỉ mở cổng khi test xóa xuyên kho qua restart đạt trên đúng candidate, các kho memory/cache/backup được bao phủ thật, toàn bộ M1 được chạy lại, và DeepSeek candidate đạt khi có key.

## Chạy lại

Harness nhận `DETOT_TEST_OPENCLAW_ENTRY` để chọn runtime ứng viên. Các mode gồm chạy mặc định cho Gateway, Memory Core và mock continuation, `--dreaming` cho managed sweep, `--retention` cho TTL/incognito/xóa, và `--live` khi có key nhập cục bộ. Không chuyển runtime đang dùng sang ứng viên cho tới khi nghiệm thu.
