# Windows Agent (Phases 3 + 4)

| Project | Target | Role |
| :-- | :-- | :-- |
| `Agent.Core` | net8.0 | Platform-neutral logic: policy engine, encrypted offline state, backend WebSocket client, named-pipe IPC, `AgentController`. |
| `Agent.Service` | net8.0-windows | Windows Service (`LocalSystem`): hosts `AgentController`, launches the Helper in the user session (WTS + `CreateProcessAsUser`), DPAPI (LocalMachine) + ACL-protected state, ACL'd pipe that only accepts the real Helper binary. |
| `Agent.Helper` | net8.0-windows (WPF) | User-session app: pairing dialog, full-screen TopMost lock window with "Xin thêm giờ", keyboard hook (Win / Alt+Tab / Alt+Esc / Alt+F4 / Ctrl+Esc), idle + foreground-app reporting. |
| `Agent.Tests` | net8.0 | Unit tests + optional integration test against a live backend. |

## Phase 4 protections
| Layer | Where | How |
| :-- | :-- | :-- |
| Web filter | `Agent.Core` `DnsProxy` | Local DNS proxy 127.0.0.1:53 (UDP+TCP). Blocked → `0.0.0.0` / `::` / NXDOMAIN; allowed → forwarded to `8.8.8.8` (or `1.1.1.3` family DNS when the *adult* category is on). Precedence: allowed domains > blocked domains > categories. Category lists are a built-in **seed** (`Filtering/categories.json`), not a complete blocklist. |
| Adapter DNS | `DnsGuard` + `WmiDnsConfigurator` | Active adapters are pointed at 127.0.0.1, originals remembered in the encrypted state and restored when the filter is turned off or on uninstall (`cleanup`). Re-enforced every 30 s; if a *previously redirected* adapter was changed back, a `dns_changed` tamper alert is sent. If the proxy cannot bind, DNS is **not** redirected (never cut the internet). |
| DoH bypass | `DohPolicy` + `RegistryPolicyWriter` | `HKLM\SOFTWARE\Policies\{Google\Chrome,Microsoft\Edge,BraveSoftware\Brave}\DnsOverHttpsMode = "off"`. |
| App/game blocker | `ProcessBlocker` | WMI `Win32_ProcessStartTrace` (event-driven) kills matches immediately (+ sweep on policy change and every 30 s). Never touches explorer/svchost/the agent itself. `outside_schedule` mode blocks only in hours the grid marks as locked. |
| Screenshots | `Helper/ScreenCapture`, `ScreenshotUploader` | Whole virtual desktop → max 1280 px → JPEG q60 (steps down to q30 until < 80 KB). Only when unlocked, `idle < 60 s`, `screenshot` switch on. Durable queue (max 50, oldest dropped), presign → PUT → confirm, file deleted after success. |
| Activity | `ActivityLog` | Foreground app + window title with seconds, DNS-derived web domains (blocked ones flagged), blocked-app kills; batched every 60 s to `/api/agent/logs/batch`, re-queued while offline. |
| Anti-tamper | `TamperProtection`, `SecurePaths` | Service recovery (restart after 1 s ×3), service ACL (non-admins cannot stop/start/reconfigure), install dir `SYSTEM`/Admins only, `%ProgramData%` state ACL + DPAPI, pipe accepts only the real `Agent.Helper.exe`. Alerts: `dns_changed`, `unclean_shutdown` (killed / power loss), `impostor_pipe_client`; queued across restarts. |
| Uninstall gate | `UninstallAuth`, `installer/KidSafe.iss` | Parent's daily 8-digit PIN (HMAC of the device token + UTC date — works offline; shown in the parent app) **or** the parent's account password (checked via API). Silent uninstall is refused. |

**Limits you should know:** the protections assume the child uses a *standard* (non-admin) Windows account — a local administrator can stop the service, edit ACLs and change DNS. Ctrl+Alt+Del cannot be intercepted. IPv6 DNS servers and apps with their own hard-coded resolvers are not covered. Stopping the service leaves DNS on 127.0.0.1 (fail-closed) until it restarts; the uninstaller restores it.

## Build (Windows)
```powershell
dotnet build KidSafeAgent.sln -c Release
dotnet publish Agent.Service -c Release -r win-x64 --self-contained false -o publish
dotnet publish Agent.Helper  -c Release -r win-x64 --self-contained false -o publish   # same folder
sc.exe create KidSafeAgent binPath= "C:\Program Files\KidSafeClone\Agent.Service.exe" start= auto
```
Set `Agent:BackendUrl` in `appsettings.json` next to `Agent.Service.exe`.

Installer (needs Inno Setup 6): `./build-installer.ps1` → `dist/KidSafeSetup.exe` (`/BACKEND=https://api.example.com` selects the server). CLI verbs: `Agent.Service.exe harden | verify-uninstall <pin-or-password> | cleanup`.

## Tests (any OS)
```bash
dotnet test Agent.Tests
# with the real backend running on :3000 (Postgres + Redis up) and S3/MinIO on :9000
# (without Docker: `node ../backend/test/fake-s3.mjs 9000`):
KIDSAFE_BACKEND_URL=http://localhost:3000 dotnet test Agent.Tests
```

## Decisions
* `schedule_grid[day][hour]`, day 0 = Sunday (`DayOfWeek`), hour = local time.
* Both the weekly schedule and `daily_limit_minutes` are governed by the `schedule_lock` switch ("Lịch dùng máy").
* Precedence: manual lock > extra time (`extra_time_until`) > schedule > daily limit.
* Usage minutes only count while the Helper reports `idle < 120s` (report must be < 15s old) and the machine is unlocked.
* Everything needed to enforce (policy, token, counter, manual lock, extra time) is persisted, so enforcement continues with no network.
