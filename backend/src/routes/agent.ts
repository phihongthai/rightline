import type { FastifyPluginAsync } from "fastify";
import { randomUUID } from "node:crypto";
import { z } from "zod";
import { hashToken, newDeviceToken } from "../lib/tokens.js";

const Pair = z.object({
  pairing_code: z.string().trim().toUpperCase().length(6),
  machine_name: z.string().trim().min(1).max(100),
  os_version: z.string().max(100).optional(),
  agent_version: z.string().max(50).optional(),
});

const Presign = z.object({ content_type: z.enum(["image/webp", "image/jpeg"]).default("image/webp") });
const Confirm = z.object({ storage_key: z.string().min(1).max(300), captured_at: z.string().datetime() });
const LogBatch = z.object({
  logs: z
    .array(
      z.object({
        log_type: z.enum(["web", "app", "system"]),
        target: z.string().max(500),
        title: z.string().max(500).default(""),
        duration_sec: z.number().int().min(0).max(86400).default(0),
        recorded_at: z.string().datetime(),
      }),
    )
    .min(1)
    .max(500),
});

export const agentRoutes: FastifyPluginAsync = async (app) => {
  const { db, storage } = app.deps;

  app.post(
    "/pair",
    { config: { rateLimit: { max: app.deps.config.pairRateLimitPerMin, timeWindow: "1 minute" } } },
    async (req, reply) => {
      const b = Pair.parse(req.body);
      const token = newDeviceToken();
      // Single atomic claim: code valid, unexpired. Re-pair replaces any old token.
      const claimed = await db.device.updateMany({
        where: { pairingCode: b.pairing_code, pairingExpiresAt: { gt: new Date() } },
        data: {
          pairingCode: null,
          pairingExpiresAt: null,
          deviceTokenHash: hashToken(token),
          osVersion: b.os_version,
          agentVersion: b.agent_version,
          isOnline: false,
        },
      });
      if (claimed.count === 0) return reply.code(400).send({ error: "invalid_or_expired_code" });
      // pairing_code was unique and is now null; find by token hash.
      const device = await db.device.findUniqueOrThrow({ where: { deviceTokenHash: hashToken(token) } });
      return { device_id: device.id, device_token: token };
    },
  );

  const auth = { onRequest: app.authAgent };

  app.post("/screenshots/presign", auth, async (req) => {
    const { content_type } = Presign.parse(req.body ?? {});
    const dev = req.agentDevice!;
    const ext = content_type === "image/webp" ? "webp" : "jpg";
    const day = new Date().toISOString().slice(0, 10);
    const key = `${dev.id}/${day}/${randomUUID()}.${ext}`;
    return { storage_key: key, upload_url: await storage.presignPut(key, content_type), method: "PUT" };
  });

  app.post("/screenshots/confirm", auth, async (req, reply) => {
    const b = Confirm.parse(req.body);
    const dev = req.agentDevice!;
    if (!b.storage_key.startsWith(`${dev.id}/`)) return reply.code(403).send({ error: "forbidden_key" });
    if (!(await storage.exists(b.storage_key))) return reply.code(409).send({ error: "object_missing" });
    const s = await db.screenshot.create({
      data: { deviceId: dev.id, storageKey: b.storage_key, capturedAt: new Date(b.captured_at) },
    });
    return reply.code(201).send({ id: s.id });
  });

  app.post("/logs/batch", auth, async (req, reply) => {
    const b = LogBatch.parse(req.body);
    const dev = req.agentDevice!;
    const r = await db.activityLog.createMany({
      data: b.logs.map((l) => ({
        deviceId: dev.id,
        logType: l.log_type,
        target: l.target,
        title: l.title,
        durationSec: l.duration_sec,
        recordedAt: new Date(l.recorded_at),
      })),
    });
    return reply.code(201).send({ inserted: r.count });
  });
};
