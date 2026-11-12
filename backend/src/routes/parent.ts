import type { FastifyPluginAsync, RouteHandlerMethod } from "fastify";
import type { Device } from "@prisma/client";
import { z } from "zod";
import { PolicyPatch, defaultPolicyData, patchToData, serializePolicy } from "../lib/policy.js";
import { generatePairingCode, uninstallPin } from "../lib/tokens.js";

const IdParam = z.object({ id: z.string().uuid() });
const uuid = z.string().uuid();

function serializeDevice(d: Device) {
  return {
    id: d.id,
    name: d.name,
    os_version: d.osVersion,
    agent_version: d.agentVersion,
    paired: d.deviceTokenHash !== null,
    pairing_code: d.pairingCode && d.pairingExpiresAt && d.pairingExpiresAt > new Date() ? d.pairingCode : null,
    pairing_expires_at: d.pairingCode ? d.pairingExpiresAt : null,
    is_online: d.isOnline,
    last_seen_at: d.lastSeenAt,
    is_locked_now: d.isLockedNow,
    manual_locked: d.manualLocked,
    extra_time_until: d.extraTimeUntil,
    active_minutes_today: d.activeMinutesToday,
    current_app: d.currentApp,
    license_expires_at: d.licenseExpiresAt,
  };
}

export const parentRoutes: FastifyPluginAsync = async (app) => {
  const { db, hub, storage, config } = app.deps;
  const auth = { onRequest: app.authParent };

  async function owned(userId: string, id: string) {
    const d = await db.device.findFirst({ where: { id, userId } });
    if (!d) throw Object.assign(new Error("not_found"), { statusCode: 404 });
    return d;
  }

  async function newCode() {
    for (let i = 0; i < 10; i++) {
      const code = generatePairingCode();
      if (!(await db.device.findUnique({ where: { pairingCode: code } }))) return code;
    }
    throw new Error("could not allocate pairing code");
  }

  async function pushPolicy(deviceId: string) {
    const p = await db.policy.findUniqueOrThrow({ where: { deviceId } });
    await hub.send(deviceId, { type: "policy:sync", payload: serializePolicy(p) });
    return p;
  }

  // ---- devices ----
  app.get("/devices", auth, async (req) => {
    const list = await db.device.findMany({ where: { userId: req.user.sub }, orderBy: { createdAt: "asc" } });
    return { devices: list.map(serializeDevice) };
  });

  app.post("/devices", auth, async (req, reply) => {
    const { name } = z.object({ name: z.string().trim().min(1).max(100) }).parse(req.body);
    const d = await db.device.create({
      data: {
        userId: req.user.sub,
        name,
        pairingCode: await newCode(),
        pairingExpiresAt: new Date(Date.now() + config.pairingTtlMs),
        licenseExpiresAt: new Date(Date.now() + config.trialDays * 86400_000),
        policy: { create: defaultPolicyData() },
      },
    });
    return reply.code(201).send(serializeDevice(d));
  });

  app.get("/devices/:id", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    return serializeDevice(await owned(req.user.sub, id));
  });

  app.patch("/devices/:id", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    const { name } = z.object({ name: z.string().trim().min(1).max(100) }).parse(req.body);
    await owned(req.user.sub, id);
    return serializeDevice(await db.device.update({ where: { id }, data: { name } }));
  });

  app.delete("/devices/:id", auth, async (req, reply) => {
    const { id } = IdParam.parse(req.params);
    await owned(req.user.sub, id);
    await db.device.delete({ where: { id } });
    return reply.code(204).send();
  });

  /** Issue a fresh pairing code (re-pair; invalidates existing token). */
  app.post("/devices/:id/pairing-code", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    await owned(req.user.sub, id);
    const d = await db.device.update({
      where: { id },
      data: {
        pairingCode: await newCode(),
        pairingExpiresAt: new Date(Date.now() + config.pairingTtlMs),
      },
    });
    return serializeDevice(d);
  });

  /** PIN the parent reads out to allow uninstalling the agent today (UTC date). */
  app.get("/devices/:id/uninstall-pin", auth, async (req, reply) => {
    const { id } = IdParam.parse(req.params);
    const d = await owned(req.user.sub, id);
    if (!d.deviceTokenHash) return reply.code(409).send({ error: "not_paired" });
    const date = new Date().toISOString().slice(0, 10);
    return { pin: uninstallPin(d.deviceTokenHash, date), valid_for_date_utc: date };
  });

  // ---- commands ----
  app.post("/devices/:id/lock", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    await owned(req.user.sub, id);
    const d = await db.device.update({ where: { id }, data: { manualLocked: true } });
    await hub.send(id, { type: "command:lock" });
    return serializeDevice(d);
  });

  app.post("/devices/:id/unlock", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    await owned(req.user.sub, id);
    const d = await db.device.update({ where: { id }, data: { manualLocked: false } });
    await hub.send(id, { type: "command:unlock" });
    return serializeDevice(d);
  });

  app.post("/devices/:id/grant-time", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    const { minutes } = z.object({ minutes: z.number().int().min(1).max(480) }).parse(req.body);
    const dev = await owned(req.user.sub, id);
    const d = await grant(dev, minutes);
    return serializeDevice(d);
  });

  async function grant(dev: Device, minutes: number) {
    const base = dev.extraTimeUntil && dev.extraTimeUntil > new Date() ? dev.extraTimeUntil : new Date();
    const until = new Date(base.getTime() + minutes * 60_000);
    const d = await db.device.update({ where: { id: dev.id }, data: { extraTimeUntil: until } });
    await hub.send(dev.id, { type: "command:grant_time", payload: { extra_time_until: until.toISOString() } });
    return d;
  }

  // ---- policy ----
  app.get("/devices/:id/policy", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    await owned(req.user.sub, id);
    return serializePolicy(await db.policy.findUniqueOrThrow({ where: { deviceId: id } }));
  });

  const updatePolicy: RouteHandlerMethod = async (req) => {
    const { id } = IdParam.parse(req.params);
    const patch = PolicyPatch.parse(req.body);
    await owned(req.user.sub, id);
    const cur = await db.policy.findUniqueOrThrow({ where: { deviceId: id } });
    await db.policy.update({
      where: { deviceId: id },
      data: {
        ...patchToData(patch, cur.protectionFlags as Record<string, boolean>),
        version: { increment: 1 },
      },
    });
    return serializePolicy(await pushPolicy(id));
  };
  app.patch("/devices/:id/policy", auth, updatePolicy);
  app.put("/devices/:id/policy", auth, updatePolicy);

  // ---- time requests ----
  app.get("/devices/:id/time-requests", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    const { status } = z
      .object({ status: z.enum(["pending", "approved", "rejected"]).optional() })
      .parse(req.query);
    await owned(req.user.sub, id);
    const rows = await db.timeRequest.findMany({
      where: { deviceId: id, ...(status && { status }) },
      orderBy: { createdAt: "desc" },
      take: 100,
    });
    return {
      requests: rows.map((r) => ({
        id: r.id,
        requested_minutes: r.requestedMinutes,
        reason: r.reason,
        status: r.status,
        created_at: r.createdAt,
        resolved_at: r.resolvedAt,
      })),
    };
  });

  const resolve = (status: "approved" | "rejected"): RouteHandlerMethod =>
    async (req, reply) => {
      const p = z.object({ id: uuid, reqId: uuid }).parse(req.params);
      const body = z.object({ minutes: z.number().int().min(1).max(480).optional() }).parse(req.body ?? {});
      const dev = await owned(req.user.sub, p.id);
      // Atomic pending -> resolved transition prevents double-grant.
      const r = await db.timeRequest.updateMany({
        where: { id: p.reqId, deviceId: p.id, status: "pending" },
        data: { status, resolvedAt: new Date() },
      });
      if (r.count === 0) return reply.code(409).send({ error: "not_pending" });
      const tr = await db.timeRequest.findUniqueOrThrow({ where: { id: p.reqId } });
      if (status === "approved") await grant(dev, body.minutes ?? tr.requestedMinutes);
      return { id: tr.id, status: tr.status, resolved_at: tr.resolvedAt };
    };
  app.post("/devices/:id/time-requests/:reqId/approve", auth, resolve("approved"));
  app.post("/devices/:id/time-requests/:reqId/reject", auth, resolve("rejected"));

  // ---- screenshots & logs ----
  app.get("/devices/:id/screenshots", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    const q = z
      .object({
        date: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional(),
        limit: z.coerce.number().int().min(1).max(200).default(60),
        before: z.string().datetime().optional(),
      })
      .parse(req.query);
    await owned(req.user.sub, id);
    const range = q.date
      ? { gte: new Date(`${q.date}T00:00:00.000Z`), lt: new Date(new Date(`${q.date}T00:00:00.000Z`).getTime() + 86400_000) }
      : undefined;
    const rows = await db.screenshot.findMany({
      where: {
        deviceId: id,
        ...(range && { capturedAt: range }),
        ...(q.before && { capturedAt: { ...(range ?? {}), lt: new Date(q.before) } }),
      },
      orderBy: { capturedAt: "desc" },
      take: q.limit,
    });
    return {
      screenshots: await Promise.all(
        rows.map(async (s) => ({ id: s.id, captured_at: s.capturedAt, url: await storage.presignGet(s.storageKey) })),
      ),
    };
  });

  app.get("/devices/:id/logs", auth, async (req) => {
    const { id } = IdParam.parse(req.params);
    const q = z
      .object({
        type: z.enum(["web", "app", "system"]).optional(),
        limit: z.coerce.number().int().min(1).max(500).default(100),
        before: z.string().datetime().optional(),
      })
      .parse(req.query);
    await owned(req.user.sub, id);
    const rows = await db.activityLog.findMany({
      where: {
        deviceId: id,
        ...(q.type && { logType: q.type }),
        ...(q.before && { recordedAt: { lt: new Date(q.before) } }),
      },
      orderBy: { recordedAt: "desc" },
      take: q.limit,
    });
    return {
      logs: rows.map((l) => ({
        id: l.id,
        log_type: l.logType,
        target: l.target,
        title: l.title,
        duration_sec: l.durationSec,
        recorded_at: l.recordedAt,
      })),
    };
  });
};
