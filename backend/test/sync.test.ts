import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { prisma } from "../src/lib/db.js";
import { fakeStorage, AgentSocket, bearer, connectAgent, createDevice, pairAgent, register, resetDb, startApp } from "./helpers.js";

let ctx: Awaited<ReturnType<typeof startApp>>;
const storage = fakeStorage();
beforeAll(async () => (ctx = await startApp(storage)));
afterAll(async () => {
  await ctx.app.close();
  await prisma.$disconnect();
});
beforeEach(resetDb);

async function setup() {
  const parent = await register(ctx.app);
  const d = await createDevice(ctx.app, parent);
  const token = (await pairAgent(ctx.app, d.pairing_code)).json().device_token as string;
  const agent = await connectAgent(ctx.wsBase, token);
  return { parent, d, token, agent };
}
const patch = (parent: string, id: string, payload: unknown) =>
  ctx.app.inject({ method: "PATCH", url: `/api/devices/${id}/policy`, headers: bearer(parent), payload: payload as object });

describe("policy sync", () => {
  it("sends full policy on connect", async () => {
    const { agent, d } = await setup();
    const m = await agent.next("policy:sync");
    expect(m.payload.device_id).toBe(d.id);
    expect(m.payload.version).toBe(1);
    expect(m.payload.protection_flags).toEqual({ web_filter: true, app_block: true, schedule_lock: false, screenshot: true });
    expect(m.payload.screenshot_interval_sec).toBe(60);
    await agent.close();
  });

  it("pushes updated policy live with bumped version", async () => {
    const { agent, parent, d } = await setup();
    await agent.next("policy:sync");
    const grid = Array.from({ length: 7 }, (_, day) => Array.from({ length: 24 }, (_, h) => h >= 8 && h < 21 && day < 5));
    const res = await patch(parent, d.id, {
      protection_flags: { schedule_lock: true },
      schedule_grid: grid,
      daily_limit_minutes: 120,
      blocked_categories: ["adult", "game"],
      custom_blocked_domains: ["Example.com"],
      blocked_apps: [{ name: "Roblox", process_name: "RobloxPlayerBeta.exe", mode: "always" }],
      screenshot_interval_sec: 30,
    });
    expect(res.statusCode).toBe(200);
    const m = await agent.next("policy:sync");
    expect(m.payload.version).toBe(2);
    expect(m.payload.schedule_grid).toEqual(grid);
    expect(m.payload.daily_limit_minutes).toBe(120);
    expect(m.payload.custom_blocked_domains).toEqual(["example.com"]);
    expect(m.payload.blocked_apps[0].process_name).toBe("RobloxPlayerBeta.exe");
    // partial flag merge keeps the others
    expect(m.payload.protection_flags).toEqual({ web_filter: true, app_block: true, schedule_lock: true, screenshot: true });
    await agent.close();
  });

  it("increments version on every edit and null clears the daily limit", async () => {
    const { agent, parent, d } = await setup();
    await agent.next("policy:sync");
    await patch(parent, d.id, { daily_limit_minutes: 60 });
    await patch(parent, d.id, { daily_limit_minutes: null });
    const a = await agent.next("policy:sync");
    const b = await agent.next("policy:sync");
    expect([a.payload.version, b.payload.version]).toEqual([2, 3]);
    expect(b.payload.daily_limit_minutes).toBeNull();
    await agent.close();
  });

  it("rejects invalid policy without bumping version or pushing", async () => {
    const { agent, parent, d } = await setup();
    await agent.next("policy:sync");
    for (const bad of [
      { schedule_grid: [[true]] },
      { screenshot_interval_sec: 5 },
      { screenshot_interval_sec: 301 },
      { blocked_categories: ["nope"] },
      { unknown_field: 1 },
    ]) expect((await patch(parent, d.id, bad)).statusCode).toBe(400);
    const row = await prisma.policy.findUniqueOrThrow({ where: { deviceId: d.id } });
    expect(row.version).toBe(1);
    await new Promise((r) => setTimeout(r, 150));
    expect(agent.messages.filter((m) => m.type === "policy:sync")).toHaveLength(0);
    await agent.close();
  });

  it("connect always states the manual-lock intent", async () => {
    const { agent, parent, d, token } = await setup();
    await agent.next("command:unlock");
    await agent.close();
    await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/lock`, headers: bearer(parent) });
    await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/unlock`, headers: bearer(parent) });
    const again = await connectAgent(ctx.wsBase, token);
    await again.next("command:unlock");
    await again.close();
  });

  it("offline agent gets the latest policy on reconnect", async () => {
    const { agent, parent, d, token } = await setup();
    await agent.next("policy:sync");
    await agent.close();
    await patch(parent, d.id, { daily_limit_minutes: 45 });
    const again = await connectAgent(ctx.wsBase, token);
    const m = await again.next("policy:sync");
    expect(m.payload.version).toBe(2);
    expect(m.payload.daily_limit_minutes).toBe(45);
    await again.close();
  });

  it("a new connection replaces the old one and only it receives updates", async () => {
    const { agent, parent, d, token } = await setup();
    await agent.next("policy:sync");
    const second = await connectAgent(ctx.wsBase, token);
    await agent.closed();
    await second.next("policy:sync");
    await patch(parent, d.id, { daily_limit_minutes: 10 });
    expect((await second.next("policy:sync")).payload.version).toBe(2);
    // replaced socket closing must not mark the device offline
    const dev = await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}`, headers: bearer(parent) });
    expect(dev.json().is_online).toBe(true);
    await second.close();
  });
});

describe("commands", () => {
  it("lock/unlock/grant reach the agent; lock persists across reconnect", async () => {
    const { agent, parent, d, token } = await setup();
    await agent.next("policy:sync");
    const h = bearer(parent);
    await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/lock`, headers: h });
    await agent.next("command:lock");
    await agent.close();

    const again = await connectAgent(ctx.wsBase, token);
    await again.next("policy:sync");
    await again.next("command:lock");

    await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/unlock`, headers: h });
    await again.next("command:unlock");

    const g = await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/grant-time`, headers: h, payload: { minutes: 15 } });
    const m = await again.next("command:grant_time");
    const until = new Date(m.payload.extra_time_until).getTime();
    expect(until - Date.now()).toBeGreaterThan(14 * 60_000);
    expect(new Date(g.json().extra_time_until).getTime()).toBe(until);
    // stacking
    await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/grant-time`, headers: h, payload: { minutes: 30 } });
    const m2 = await again.next("command:grant_time");
    expect(new Date(m2.payload.extra_time_until).getTime() - until).toBe(30 * 60_000);
    await again.close();
  });

  it("heartbeat updates device state", async () => {
    const { agent, parent, d } = await setup();
    agent.send("heartbeat", { active_minutes_today: 42, current_app: "chrome.exe", is_locked: true });
    await new Promise((r) => setTimeout(r, 300));
    const dev = (await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}`, headers: bearer(parent) })).json();
    expect(dev).toMatchObject({ active_minutes_today: 42, current_app: "chrome.exe", is_locked_now: true, is_online: true });
    await agent.close();
  });

  it("time request: create -> approve grants time once; reject grants none", async () => {
    const { agent, parent, d } = await setup();
    await agent.next("policy:sync");
    agent.send("time_request:create", { requested_minutes: 20, reason: "bài tập" });
    agent.send("time_request:create", { requested_minutes: 10, reason: "" });
    await new Promise((r) => setTimeout(r, 300));
    const list = (await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/time-requests?status=pending`, headers: bearer(parent) })).json().requests;
    expect(list).toHaveLength(2);
    const r20 = list.find((r: any) => r.requested_minutes === 20);
    const r10 = list.find((r: any) => r.requested_minutes === 10);

    const ok = await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/time-requests/${r20.id}/approve`, headers: bearer(parent), payload: {} });
    expect(ok.json().status).toBe("approved");
    const m = await agent.next("command:grant_time");
    expect(new Date(m.payload.extra_time_until).getTime() - Date.now()).toBeGreaterThan(19 * 60_000);

    const again = await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/time-requests/${r20.id}/approve`, headers: bearer(parent), payload: {} });
    expect(again.statusCode).toBe(409);

    const rej = await ctx.app.inject({ method: "POST", url: `/api/devices/${d.id}/time-requests/${r10.id}/reject`, headers: bearer(parent) });
    expect(rej.json().status).toBe("rejected");
    await new Promise((r) => setTimeout(r, 150));
    expect(agent.messages.filter((x) => x.type === "command:grant_time")).toHaveLength(0);
    await agent.close();
  });

  it("malformed agent messages get an error reply and don't kill the socket", async () => {
    const { agent } = await setup();
    await agent.next("policy:sync");
    agent.send("heartbeat", { active_minutes_today: "x" });
    await agent.next("error");
    agent.send("bogus");
    await agent.next("error");
    agent.send("heartbeat", { active_minutes_today: 1, is_locked: false });
    await agent.close();
  });
});

describe("screenshots & logs", () => {
  it("presign -> confirm -> parent lists with signed urls", async () => {
    const { token, parent, d } = await setup();
    const h = { authorization: `Bearer ${token}` };
    const ps = await ctx.app.inject({ method: "POST", url: "/api/agent/screenshots/presign", headers: h, payload: { content_type: "image/webp" } });
    const { storage_key, upload_url } = ps.json();
    expect(storage_key.startsWith(`${d.id}/`)).toBe(true);
    expect(storage_key.endsWith(".webp")).toBe(true);
    expect(upload_url).toContain(storage_key);

    const at = new Date().toISOString();
    // not uploaded yet
    expect((await ctx.app.inject({ method: "POST", url: "/api/agent/screenshots/confirm", headers: h, payload: { storage_key, captured_at: at } })).statusCode).toBe(409);
    // foreign key
    expect((await ctx.app.inject({ method: "POST", url: "/api/agent/screenshots/confirm", headers: h, payload: { storage_key: "other/x.webp", captured_at: at } })).statusCode).toBe(403);
    storage.existing.add(storage_key);
    expect((await ctx.app.inject({ method: "POST", url: "/api/agent/screenshots/confirm", headers: h, payload: { storage_key, captured_at: at } })).statusCode).toBe(201);

    const list = (await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/screenshots`, headers: bearer(parent) })).json().screenshots;
    expect(list).toHaveLength(1);
    expect(list[0].url).toContain(storage_key);
    const day = at.slice(0, 10);
    const byDay = (await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/screenshots?date=${day}`, headers: bearer(parent) })).json().screenshots;
    expect(byDay).toHaveLength(1);
    const other = (await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/screenshots?date=2000-01-01`, headers: bearer(parent) })).json().screenshots;
    expect(other).toHaveLength(0);
  });

  it("batch logs insert and list newest-first; requires device auth", async () => {
    const { token, parent, d } = await setup();
    const logs = [
      { log_type: "web", target: "youtube.com", title: "Video", duration_sec: 30, recorded_at: "2026-01-01T10:00:00.000Z" },
      { log_type: "app", target: "chrome.exe", title: "Tab", duration_sec: 60, recorded_at: "2026-01-01T11:00:00.000Z" },
    ];
    expect((await ctx.app.inject({ method: "POST", url: "/api/agent/logs/batch", payload: { logs } })).statusCode).toBe(401);
    const res = await ctx.app.inject({ method: "POST", url: "/api/agent/logs/batch", headers: { authorization: `Bearer ${token}` }, payload: { logs } });
    expect(res.json().inserted).toBe(2);
    const got = (await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/logs`, headers: bearer(parent) })).json().logs;
    expect(got.map((l: any) => l.target)).toEqual(["chrome.exe", "youtube.com"]);
    const web = (await ctx.app.inject({ method: "GET", url: `/api/devices/${d.id}/logs?type=web`, headers: bearer(parent) })).json().logs;
    expect(web).toHaveLength(1);
    const bad = await ctx.app.inject({ method: "POST", url: "/api/agent/logs/batch", headers: { authorization: `Bearer ${token}` }, payload: { logs: [{ log_type: "x" }] } });
    expect(bad.statusCode).toBe(400);
  });
});
