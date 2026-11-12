# Windows Agent (Phase 3)

| Project | Target | Role |
| :-- | :-- | :-- |
| `Agent.Core` | net8.0 | Platform-neutral logic: policy engine, encrypted offline state, backend WebSocket client, named-pipe IPC, `AgentController`. |
| `Agent.Service` | net8.0-windows | Windows Service (`LocalSystem`): hosts `AgentController`, launches the Helper in the user session (WTS + `CreateProcessAsUser`), DPAPI (LocalMachine) + ACL-protected state, ACL'd pipe that only accepts the real Helper binary. |
| `Agent.Helper` | net8.0-windows (WPF) | User-session app: pairing dialog, full-screen TopMost lock window with "Xin thêm giờ", keyboard hook (Win / Alt+Tab / Alt+Esc / Alt+F4 / Ctrl+Esc), idle + foreground-app reporting. |
| `Agent.Tests` | net8.0 | Unit tests + optional integration test against a live backend. |

## Build (Windows)
```powershell
dotnet build KidSafeAgent.sln -c Release
dotnet publish Agent.Service -c Release -r win-x64 --self-contained false -o publish
dotnet publish Agent.Helper  -c Release -r win-x64 --self-contained false -o publish   # same folder
sc.exe create KidSafeAgent binPath= "C:\Program Files\KidSafeClone\Agent.Service.exe" start= auto
```
Set `Agent:BackendUrl` in `appsettings.json` next to `Agent.Service.exe`. Installer, service recovery, install-dir ACL and uninstall PIN are Phase 4.

## Tests (any OS)
```bash
dotnet test Agent.Tests
# with the real backend running on :3000 (Postgres + Redis up):
KIDSAFE_BACKEND_URL=http://localhost:3000 dotnet test Agent.Tests
```

## Decisions
* `schedule_grid[day][hour]`, day 0 = Sunday (`DayOfWeek`), hour = local time.
* Both the weekly schedule and `daily_limit_minutes` are governed by the `schedule_lock` switch ("Lịch dùng máy").
* Precedence: manual lock > extra time (`extra_time_until`) > schedule > daily limit.
* Usage minutes only count while the Helper reports `idle < 120s` (report must be < 15s old) and the machine is unlocked.
* Everything needed to enforce (policy, token, counter, manual lock, extra time) is persisted, so enforcement continues with no network.
