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
| Xóa xuyên kho trước khi vá | **FAIL.** RPC `sessions.delete` báo thành công. Continuation dùng response ID còn hạn của session đã xóa vẫn trả HTTP 200. Archive `.jsonl.deleted.*.zst` giữ canary giả sau giải nén. |

Bằng chứng baseline retention nằm tại `detot-lab/.local/m1-5AesMz/results.json`. Cơ chế lưu archive khi xóa session thường được mô tả trong tài liệu upstream. `memory forget` xử lý memory theo nguồn, trong khi transcript và bản sao khác có vòng đời riêng. Cần xác nhận cơ chế thu hồi continuation và xử lý archive/FTS/memory/backup bằng cùng canary trước khi dùng dữ liệu thật. [Sessions delete tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/cli/sessions.md) · [Memory forget tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/cli/memory.md)

## Kiểm thử lại trên ứng viên đã vá

| Bộ kiểm thử | Kết quả | Bằng chứng cục bộ |
|---|---|---|
| Gateway, bearer sai, continuation không hợp lệ, Memory Core/paraphrase, Dreaming preview và mock tool round trip | 5 PASS. Memory Core thực hiện 3 yêu cầu embedding giả lập; mock tool round trip trả HTTP 200 ở cả hai lượt. Đây chưa phải kiểm chứng xóa memory có provenance. | `detot-lab/.local/m1-VKegeU/results.json` |
| Dreaming managed sweep | 1 PASS. Job báo `succeeded`, `MEMORY.md` không đổi. | `detot-lab/.local/m1-HxDbMb/results.json` |
| Retention | 5 PASS. Incognito, TTL/restart và xóa đạt; xóa thu hồi 2 continuation ID, từ chối trước/sau restart, không còn canary trong file/archive nén hay các hàng SQLite có thể truy vấn, session đối chứng còn hoạt động. | `detot-lab/.local/m1-GMoSdW/results.json` |
| DeepSeek thật trên ứng viên | BLOCKED do tiến trình không có key. Không dùng PASS từ cài đặt gốc làm bằng chứng cho candidate. | Kết quả `BLOCKED` trong các run ứng viên |

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

Regression test đã được mở rộng để lấy mọi continuation ID của cùng session, kiểm tra trước/sau restart, giữ session đối chứng, thêm archive `.zst` có canary, quét nội dung file nén và quét các hàng/bảng SQLite. Harness tự tạo `.local` và fail-fast nếu thiếu candidate. Test hiện chưa seed một Memory Core entry có provenance tới session, cache/vector artifact hoặc bản backup; vì vậy kết quả không chứng minh purge các kho đó. SQLite được kiểm tra theo nội dung logic trong bảng, không theo byte thô của trang đã giải phóng; checkpoint/VACUUM offline và bản sao backup vẫn cần thiết kế/kiểm chứng riêng.

### Chạy lại candidate ngày 13/11/2026

Candidate được khôi phục tại chỗ từ lockfile hiện có (361 packages, OpenClaw `2026.11.3`, commit `847611d`); không clone hay dựng lại dự án. Ban đầu Node `24.22.3` thiếu, nên đã lấy đúng runtime vào npm cache (không thay Node hệ thống) và chạy lại M1 trên Node `24.22.3`. Kết quả mới nhất:

| Bộ kiểm thử | Kết quả | Bằng chứng |
|---|---|---|
| Gateway, bearer/continuation validation, Memory Core/paraphrase, mock tool continuation và Dreaming preview | 5 PASS | `detot-lab/.local/m1-waCI9v/results.json` |
| Managed Dreaming sweep | 1 PASS; `succeeded`, durable memory unchanged | `detot-lab/.local/m1-k1UhI5/results.json` |
| Retention | 5 PASS. Thu hồi 2 continuation ID, từ chối trước/sau restart, xóa archive fixture và không còn canary trong các hàng SQLite; session đối chứng sống sót. | `detot-lab/.local/m1-ACgQEg/results.json` |
| `npm audit --omit=dev` trên lockfile candidate | 22 moderate, 0 high/critical; 22 package findings thuộc 2 advisory nguồn trực tiếp (`glob`, `minimatch`) và các đường phụ thuộc. Tự động hạ OpenClaw xuống `2026.4.29` (major downgrade). | `detot-lab/.local/candidate-audit.json` |
| DeepSeek thật trên candidate | BLOCKED; chưa có key trong tiến trình thử | Kết quả `BLOCKED` trong mock suite |

Trong lần cập nhật này, bộ M1 thường, Dreaming và retention đều chạy trên candidate và Node ghim `24.22.3`, đạt các kiểm tra đã mô tả. `DEEPSEEK_API_KEY` không hiện diện trong tiến trình. Memory Core purge qua `sessions.delete`, vector/cache, cryptographic erasure cho archive lâu dài và backup chưa được kiểm chứng. Không coi việc không tìm thấy canary trong kho chưa seed là bằng chứng.

**Quyết định cổng hiện tại: FAIL / chưa nghiệm thu.** Candidate đã đạt regression xóa continuation, session row, transcript và archive fixture qua restart trên Node ghim. Chưa có test Memory Core entry gắn provenance với session, cache/vector hoặc backup; cũng chưa chứng minh cryptographic erasure cho archive hiện hữu. DeepSeek thật chưa chạy vì không có key. 22 moderate có biện pháp giảm thiểu/giám sát nhưng chưa được vá hay chấp nhận rủi ro. Tiếp tục chỉ dùng dữ liệu giả; không chuyển Milestone 2. Chỉ mở cổng sau khi các phần thiếu được kiểm thử trên candidate và DeepSeek thật đạt khi có key.

## Cập nhật DeepSeek trên candidate

Lượt live do người dùng chạy trên OpenClaw candidate `2026.11.3` đạt PASS. Cả lượt gọi tool và lượt continuation đều trả HTTP 200. Bằng chứng cục bộ nằm tại `detot-lab/.local/m1-hxrdQR/results.json`. Kết quả này thay thế các trạng thái BLOCKED của những lượt chạy trước. Cổng M1 vẫn mở các mục kiểm chứng xóa Memory Core, cache/vector, backup và archive.

## Chạy lại

Harness nhận `DETOT_TEST_OPENCLAW_ENTRY` để chọn runtime ứng viên. Các mode gồm chạy mặc định cho Gateway, Memory Core và mock continuation, `--dreaming` cho managed sweep, `--retention` cho TTL/incognito/xóa, và `--live` khi có key nhập cục bộ. Không chuyển runtime đang dùng sang ứng viên cho tới khi nghiệm thu.
