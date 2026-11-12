// E2E smoke: needs backend on :3000 and `next start` on :3001. Run: node e2e/smoke.mjs
import { chromium } from "playwright";
import WebSocket from "ws";
import assert from "node:assert/strict";

const WEB = process.env.WEB_URL ?? "http://localhost:3001";
const API = process.env.API_URL ?? "http://localhost:3000";
const email = `e2e${Date.now()}@example.com`;

const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
const page = await (await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true })).newPage();
page.setDefaultTimeout(8000);
const step = (m) => console.log("✓", m);

// register
await page.goto(`${WEB}/login`);
await page.getByText("Chưa có tài khoản? Đăng ký").click();
await page.getByPlaceholder("Tên của bạn").fill("Mẹ Bo");
await page.getByPlaceholder("Email").fill(email);
await page.getByPlaceholder("Mật khẩu").fill("password123");
await page.getByRole("button", { name: "Đăng ký", exact: true }).click();
await page.getByText("Máy của con").waitFor();
step("register + redirect to list");

// add device -> pairing code
await page.getByText("+ Thêm máy Windows").click();
await page.getByPlaceholder(/Tên máy/).fill("Máy bàn của Bo");
await page.getByRole("button", { name: "Tạo mã ghép nối" }).click();
const code = (await page.getByTestId("pairing-code").textContent()).trim();
assert.match(code, /^[A-Z2-9]{6}$/);
step(`pairing code ${code}`);

// agent pairs + connects
const pair = await (await fetch(`${API}/api/agent/pair`, {
  method: "POST", headers: { "content-type": "application/json" },
  body: JSON.stringify({ pairing_code: code, machine_name: "BO-PC", os_version: "Win11" }),
})).json();
const ws = new WebSocket(`${API.replace("http", "ws")}/ws/agent`, { headers: { authorization: `Bearer ${pair.device_token}` } });
const inbox = [];
ws.on("message", (d) => inbox.push(JSON.parse(d)));
await new Promise((r) => ws.once("open", r));
const waitMsg = async (type, pred = () => true) => {
  for (let i = 0; i < 60; i++) {
    const k = inbox.findIndex((m) => m.type === type && pred(m));
    if (k >= 0) return inbox.splice(k, 1)[0];
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error("timeout " + type);
};
await waitMsg("policy:sync");
ws.send(JSON.stringify({ type: "heartbeat", payload: { active_minutes_today: 75, current_app: "chrome.exe", is_locked: false } }));
await page.getByText("Online").first().waitFor({ timeout: 10000 });
await page.getByText("1g 15p").waitFor();
step("device shows online + usage");

// toggle
await page.getByRole("switch", { name: "Chụp màn hình" }).click();
let m = await waitMsg("policy:sync", (m) => m.payload.protection_flags.screenshot === false);
step("toggle -> policy:sync v" + m.payload.version);

// lock / unlock / +15
await page.getByRole("button", { name: "Khóa máy ngay" }).click();
await waitMsg("command:lock");
await page.getByRole("button", { name: "Mở khóa" }).click();
await waitMsg("command:unlock");
await page.getByRole("button", { name: "+15 phút" }).click();
await waitMsg("command:grant_time");
step("lock / unlock / +15p");

// time request banner -> approve
ws.send(JSON.stringify({ type: "time_request:create", payload: { requested_minutes: 20, reason: "làm bài tập" } }));
await page.getByText("Con xin thêm 20 phút").waitFor({ timeout: 10000 });
await page.getByRole("button", { name: "Duyệt" }).click();
await waitMsg("command:grant_time");
step("time request approved");

// schedule: drag-paint then save
await page.getByRole("tab", { name: "Lịch & Giới hạn" }).click();
await page.getByRole("button", { name: "Khóa hết" }).click();
const grid = page.getByTestId("grid");
const cell = (d, h) => page.locator(`[data-d="${d}"][data-h="${h}"]`);
const a = await cell(1, 8).boundingBox(), b = await cell(1, 12).boundingBox();
await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
await page.mouse.down();
await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 8 });
await page.mouse.up();
assert.equal(await cell(1, 10).getAttribute("data-on"), "true");
assert.equal(await cell(1, 13).getAttribute("data-on"), "false");
await page.getByPlaceholder("Không giới hạn").fill("90");
await page.getByRole("button", { name: "Lưu thay đổi" }).click();
m = await waitMsg("policy:sync", (m) => m.payload.daily_limit_minutes === 90);
assert.deepEqual(m.payload.schedule_grid[1].map(Number).join(""), "000000001111100000000000");
assert.equal(m.payload.schedule_grid[2].some(Boolean), false);
step("grid drag + limit saved & synced");

// block tab
await page.getByRole("tab", { name: "Chặn Web & App" }).click();
await page.getByLabel("Chặn thêm domain").fill("https://Bad.example.com/x");
await page.getByLabel("Chặn thêm domain").press("Enter");
await page.getByRole("button", { name: "+ Steam.exe" }).click();
await page.getByText("Game").locator("..").getByRole("checkbox").click();
m = await waitMsg("policy:sync", (m) => m.payload.blocked_categories.includes("game"));
assert.ok(m.payload.custom_blocked_domains.includes("bad.example.com"));
assert.ok(m.payload.blocked_apps.some((x) => x.process_name === "Steam.exe"));
step("block lists synced");

// activity
await fetch(`${API}/api/agent/logs/batch`, {
  method: "POST", headers: { "content-type": "application/json", authorization: `Bearer ${pair.device_token}` },
  body: JSON.stringify({ logs: [{ log_type: "web", target: "youtube.com", title: "Video hay", duration_sec: 120, recorded_at: new Date().toISOString() }] }),
});
await page.getByRole("tab", { name: "Ảnh & Lịch sử" }).click();
await page.getByText("youtube.com").waitFor();
step("activity log visible");

// manifest
assert.equal((await fetch(`${WEB}/manifest.webmanifest`)).status, 200);
const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
assert.equal(overflow, false, "no horizontal overflow at 390px");
step("manifest ok, no horizontal overflow");

ws.close();
await browser.close();
console.log("E2E PASS");
