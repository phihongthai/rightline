# REQUIREMENTS: Hệ thống Quản lý & Bảo vệ Máy tính Windows (MVP Tối ưu)

> Tài liệu đặc tả kỹ thuật dành cho **Claude Code**. Mục tiêu: xây dựng phiên bản MVP chạy ổn định, tập trung 100% vào **Windows Child Agent** và **Parent Web App (Mobile-first PWA)**, cắt bỏ toàn bộ phần dư thừa (Android/iOS app, cổng thanh toán) để ra sản phẩm nhanh và ít lỗi vận hành.

---

## 1. Phạm vi & Nguyên tắc tối giản (Scope & Non-Goals)

### Trong phạm vi (In-Scope)
1. **Backend API & Realtime Sync:** Quản lý tài khoản phụ huynh, ghép nối thiết bị bằng mã 6 ký tự, đồng bộ chính sách (policy) thời gian thực qua WebSocket, cấp Presigned URL upload ảnh chụp màn hình lên S3/Cloudflare R2.
2. **Parent Web App (Mobile-first PWA):** Phụ huynh thao tác trên trình duyệt điện thoại/PC để lập lịch dùng máy theo lưới giờ, khóa/mở khẩn cấp, duyệt xin thêm giờ, bật/tắt lớp bảo vệ, xem ảnh chụp màn hình và lịch sử hoạt động.
3. **Windows Child Agent (2 tiến trình):**
   - `Agent.Service` (Windows Service quyền `LocalSystem`): Giữ kết nối WebSocket, lưu policy offline, chặn app/game, lọc web (Local DNS Proxy + chặn DoH), chống tắt dịch vụ.
   - `Agent.Helper` (User-session app): Chụp màn hình định kỳ (chỉ khi có tương tác), hiện màn hình khóa toàn màn hình + nút xin thêm giờ, thu thập tiêu đề cửa sổ / lịch sử trình duyệt.

### Ngoài phạm vi (Non-Goals — KHÔNG làm ở MVP)
- **KHÔNG** làm app con trên Android/iOS.
- **KHÔNG** làm native mobile app (Flutter/React Native) cho phụ huynh — dùng Next.js PWA responsive trên mobile.
- **KHÔNG** làm MITM HTTPS Proxy (cài Root CA) ở v1 để tránh lỗi SSL pinning trên game/app ngân hàng.
- **KHÔNG** tích hợp cổng thanh toán tự động ở v1 (dùng trường `license_expires_at` và mã kích hoạt đơn giản trong DB).

---

## 2. Kiến trúc & Tech Stack

| Module | Thư mục | Stack đề xuất | Ghi chú triển khai |
| :--- | :--- | :--- | :--- |
| **Backend** | `backend/` | Node.js + TypeScript + Fastify + Prisma + PostgreSQL + Redis | Dùng `ws` hoặc `@fastify/websocket` cho kênh Agent. Dùng `@aws-sdk/client-s3` tương thích Cloudflare R2. |
| **Parent Web** | `parent-web/` | Next.js (App Router) + TypeScript + Tailwind CSS | Tối ưu giao diện cảm ứng cho điện thoại (mobile-first), có manifest PWA. |
| **Windows Agent** | `windows-agent/` | C# (.NET 8) — `Worker Service` + `WPF` | Tách 2 project: `Agent.Service` và `Agent.Helper`, đóng gói chung 1 bộ cài (Inno Setup / WiX). |
| **Infra local** | `docker-compose.yml` | PostgreSQL 16 + Redis 7 + MinIO (giả lập R2 local) | Phục vụ dev/test end-to-end ngay trên máy local. |

---

## 3. Thiết kế Database (PostgreSQL)

1. **`users`**: `id` (uuid), `email` (unique), `password_hash`, `name`, `created_at`.
2. **`devices`**:
   - `id` (uuid), `user_id` (fk), `name` (vd: "Máy bàn của Bo"), `os_version`, `agent_version`.
   - `pairing_code` (varchar(6), nullable, hết hạn sau 15 phút), `device_token_hash` (dùng để Agent auth sau khi ghép nối).
   - `is_online` (bool), `last_seen_at` (timestamp), `is_locked_now` (bool), `extra_time_until` (timestamp, nullable).
   - `license_expires_at` (timestamp).
3. **`policies`** (1-1 với `devices`):
   - `device_id` (pk, fk), `version` (int, tăng mỗi lần sửa).
   - `protection_flags` (jsonb): `{ web_filter: bool, app_block: bool, schedule_lock: bool, screenshot: bool }`.
   - `schedule_grid` (jsonb): Mảng `7 x 24` boolean (`true` = được phép dùng máy, `false` = khóa).
   - `daily_limit_minutes` (int, nullable — `null` nếu không giới hạn tổng phút/ngày).
   - `blocked_categories` (text[]): vd `["adult", "gambling", "social", "game"]`.
   - `custom_blocked_domains` (text[]), `custom_allowed_domains` (text[]).
   - `blocked_apps` (jsonb): danh sách `{ name, process_name, mode: "always" | "outside_schedule" }`.
   - `screenshot_interval_sec` (int, mặc định `60`, cho phép `15` đến `300`).
4. **`time_requests`**: `id`, `device_id`, `requested_minutes` (int), `reason` (text), `status` (`pending` | `approved` | `rejected`), `created_at`, `resolved_at`.
5. **`screenshots`**: `id`, `device_id`, `storage_key`, `captured_at` (index theo `(device_id, captured_at DESC)`).
6. **`activity_logs`**: `id`, `device_id`, `log_type` (`web` | `app` | `system`), `target` (domain/process/event), `title` (tiêu đề cửa sổ/trang), `duration_sec` (int), `recorded_at`.

---

## 4. Giao thức Backend <-> Windows Agent

### Luồng ghép nối (Pairing)
1. Phụ huynh bấm "Thêm máy Windows" trên Web -> Backend sinh `pairing_code` 6 ký tự (TTL 15 phút).
2. Trên máy con, khi cài xong Agent lần đầu, hiện hộp thoại nhập mã 6 ký tự -> gọi `POST /api/agent/pair` kèm `{ pairing_code, machine_name, os_version }`.
3. Backend trả về `{ device_id, device_token }`. Agent lưu `device_token` vào vùng bảo vệ (`LocalSystem` ACL / DPAPI `LocalMachine`) và kết nối WebSocket `/ws/agent` với header `Authorization: Bearer <device_token>`.

### Sự kiện WebSocket
- **Server -> Agent:**
  - `policy:sync`: Gửi toàn bộ `policy` mới nhất khi phụ huynh thay đổi cấu hình hoặc khi Agent mới kết nối lại.
  - `command:lock` / `command:unlock`: Khóa hoặc mở khóa máy ngay lập tức.
  - `command:grant_time`: `{ extra_time_until: ISO8601 }` — cộng thêm giờ ngay lập tức.
- **Agent -> Server:**
  - `heartbeat`: Mỗi 30 giây gửi `{ active_minutes_today, current_app, is_locked }`.
  - `time_request:create`: Khi con bấm "Xin thêm giờ" trên màn hình khóa.
  - `alert:tamper`: Khi phát hiện nỗ lực tắt Service hoặc chỉnh sửa cấu hình trái phép.

### Upload ảnh màn hình & Log (REST API)
- `POST /api/agent/screenshots/presign`: Agent xin Presigned PUT URL -> upload trực tiếp file `.webp`/`.jpg` lên MinIO/R2 -> gọi `POST /api/agent/screenshots/confirm` để lưu metadata.
- `POST /api/agent/logs/batch`: Agent gom log web/app gửi theo lô mỗi 60 giây để giảm tải DB.

---

## 5. Yêu cầu kỹ thuật bắt buộc cho Windows Agent (`windows-agent/`)

Claude Code **phải** tuân thủ các ràng buộc hệ điều hành Windows dưới đây (không viết kiểu đơn giản dễ bị lách):

### 5.1. Kiến trúc 2 tiến trình (Session 0 Isolation)
- Windows Service chạy ở Session 0 không thể chụp màn hình hay hiện cửa sổ lên màn hình người dùng.
- `Agent.Service` phải phát hiện phiên đăng nhập đang active (`WTSGetActiveConsoleSessionId`) và khởi chạy `Agent.Helper.exe` trong user session bằng `WTSQueryUserToken` + `CreateProcessAsUser`.
- Thiết lập giao tiếp IPC nội bộ (Named Pipe có kiểm tra ACL) giữa `Agent.Service` và `Agent.Helper`.

### 5.2. Hoạt động Offline-First
- `Agent.Service` phải lưu bản sao `policy` và bộ đếm `active_minutes_today` xuống file cục bộ được mã hóa/bảo vệ ACL.
- Nếu mất mạng (hoặc trẻ rút dây mạng để né), Agent vẫn phải tiếp tục thực thi lịch khóa máy, giới hạn phút/ngày và chặn app bình thường.

### 5.3. Lọc Web & Chống lách DoH (Local DNS Proxy)
- `Agent.Service` chạy một **Local DNS Proxy** trên `127.0.0.1:53` (UDP/TCP), forward các domain hợp lệ lên upstream DNS sạch (vd `1.1.1.3` / `8.8.8.8`), trả về `0.0.0.0` / `NXDOMAIN` cho domain nằm trong `blocked_categories` hoặc `custom_blocked_domains` (ưu tiên `custom_allowed_domains`).
- Tự động trỏ DNS của các network adapter đang hoạt động về `127.0.0.1` (khôi phục khi tắt lớp bảo vệ hoặc gỡ app).
- **Chống lách DoH (DNS-over-HTTPS):** Ghi Registry Policy tắt DoH trên Chrome (`HKLM\SOFTWARE\Policies\Google\Chrome\DnsOverHttpsMode = "off"`) và Edge (`HKLM\SOFTWARE\Policies\Microsoft\Edge\DnsOverHttpsMode = "off"`).

### 5.4. Chặn App/Game & Đếm thời gian thực
- Dùng **ETW (`Microsoft.Diagnostics.Tracing.TraceEvent`)** hoặc WMI `Win32_ProcessStartTrace` để bắt sự kiện mở tiến trình mới tức thời (tránh polling gây tốn CPU) và kill ngay nếu khớp `blocked_apps`.
- Chỉ tính thời gian sử dụng máy khi người dùng thực sự tương tác (`GetLastInputInfo` idle < 120 giây).

### 5.5. Chụp ảnh màn hình thông minh
- Chỉ chụp khi máy **không bị khóa** và user đang có tương tác (`idle < 60s`).
- Nén ảnh ở độ phân giải hợp lý (chiều ngang tối đa `1280px`, định dạng WebP/JPEG quality `60` — dung lượng mục tiêu **< 80KB/ảnh**).
- Nếu mất mạng, lưu hàng đợi tối đa 50 ảnh cục bộ và gửi bù khi có mạng (xóa file local ngay sau khi upload thành công).

### 5.6. Màn hình khóa & Chống tắt/gỡ (Anti-Tamper)
- Khi đến giờ khóa hoặc hết `daily_limit_minutes`: `Agent.Helper` hiện cửa sổ `TopMost` che toàn màn hình (không có nút đóng `Alt+F4`, chặn phím `Win` / `Alt+Tab` qua low-level keyboard hook ở user session), hiển thị lý do khóa + nút **"Xin thêm giờ"** (nhập số phút & lời nhắn gửi về phụ huynh).
- **Anti-Tamper tối thiểu cho MVP:**
  - Thiết lập Service Recovery (tự khởi động lại sau 1 giây nếu bị crash/kill).
  - Bảo vệ thư mục cài đặt (`C:\Program Files\KidSafeClone`) bằng NTFS ACL: chỉ `SYSTEM` và `Administrators` có quyền ghi/xóa.
  - Vô hiệu hóa quyền `Stop` service từ `services.msc` / `sc stop` đối với user thường.
  - Gỡ cài đặt yêu cầu nhập mật khẩu phụ huynh (xác thực qua API hoặc mã PIN offline).

---

## 6. Yêu cầu giao diện Parent Web (`parent-web/`)

Thiết kế **Mobile-first** (hiển thị đẹp trên màn hình dọc điện thoại 375px–430px, không cần app riêng):
1. **Trang Đăng nhập / Đăng ký** + **Danh sách thiết bị** (hiển thị trạng thái Online/Offline, đang khóa/mở, thời gian đã dùng hôm nay).
2. **Màn hình Chi tiết thiết bị (Dashboard điều khiển):**
   - Nút lớn: **Khóa máy ngay / Mở khóa**, **Cộng nhanh +15p / +30p**.
   - Banner nổi bật khi con gửi yêu cầu **Xin thêm giờ** (nút Duyệt / Từ chối).
   - 4 công tắc bật/tắt nhanh từng lớp bảo vệ: `Chặn Web`, `Chặn App/Game`, `Lịch dùng máy`, `Chụp màn hình`.
3. **Tab Lịch dùng máy & Giới hạn:**
   - Lưới 7 ngày × 24 giờ trực quan (chạm/kéo để tô ô xanh = được phép, ô xám = khóa) + ô nhập tổng phút tối đa/ngày.
4. **Tab Chặn Web & App:**
   - Chọn nhóm nội dung chặn (Web đen, Cờ bạc, Mạng xã hội, Game).
   - Thêm/xóa ngoại lệ domain (Chặn thêm / Luôn cho phép).
   - Thêm/xóa tên tiến trình game/app cần chặn (kèm danh sách gợi ý phổ biến: `RobloxPlayerBeta.exe`, `Valorant.exe`, `LeagueClient.exe`, `Steam.exe`...).
5. **Tab Ảnh chụp màn hình & Lịch sử:**
   - Lưới ảnh theo mốc giờ, bấm xem phóng to, lọc theo ngày.
   - Danh sách lịch sử web/app theo thời gian.

---

## 7. Lộ trình thực hiện theo từng Phase (Để giao cho Claude Code)

Hãy triển khai tuần tự từng Phase, kiểm thử chạy được trước khi sang Phase tiếp theo:

- **Phase 1 — Nền tảng & Backend (`docker-compose.yml` + `backend/`):**
  - Dựng Postgres, Redis, MinIO.
  - Tạo Prisma schema, Auth (JWT), CRUD `devices` + `pairing_code`, CRUD `policies`, WebSocket `/ws/agent`, endpoint presigned upload MinIO/R2, endpoint batch logs.
  - Viết unit/integration test cho luồng pairing và sync policy.
- **Phase 2 — Parent Web (`parent-web/`):**
  - Dựng Next.js + Tailwind, kết nối Backend API.
  - Hoàn thiện màn hình danh sách máy, pairing bằng mã 6 ký tự, công tắc bảo vệ, lưới lịch 7×24, nút khóa/mở, duyệt xin thêm giờ, xem ảnh & log.
- **Phase 3 — Windows Agent Core (`windows-agent/`):**
  - Tạo solution .NET 8 gồm `Agent.Service` và `Agent.Helper`.
  - Hoàn thiện luồng cài đặt/nhập mã pairing, Session 0 -> User Session launcher, Named Pipe IPC, WebSocket sync + offline cache.
  - Hoàn thiện màn hình khóa `TopMost` + nút xin thêm giờ.
- **Phase 4 — Các lớp bảo vệ Windows:**
  - Chụp màn hình nén WebP/JPEG upload MinIO/R2.
  - ETW process blocker + idle time counter.
  - Local DNS Proxy + tắt DoH Registry Policy + thu thập lịch sử tiêu đề cửa sổ/trình duyệt.
  - Anti-tamper (ACL, Service recovery, gỡ cài đặt cần PIN) + script đóng gói installer (Inno Setup).
