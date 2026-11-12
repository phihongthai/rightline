import Fastify, { type FastifyInstance, type FastifyReply, type FastifyRequest } from "fastify";
import cors from "@fastify/cors";
import jwt from "@fastify/jwt";
import rateLimit from "@fastify/rate-limit";
import websocket from "@fastify/websocket";
import { ZodError } from "zod";
import { config as defaultConfig, type Config } from "./config.js";
import { prisma as defaultPrisma, type Db } from "./lib/db.js";
import { Hub } from "./lib/hub.js";
import { createS3Storage, type Storage } from "./lib/storage.js";
import { authRoutes } from "./routes/auth.js";
import { parentRoutes } from "./routes/parent.js";
import { agentRoutes } from "./routes/agent.js";
import { agentWs } from "./ws/agent.js";
import { hashToken } from "./lib/tokens.js";
import type { Device } from "@prisma/client";

export interface Deps {
  db: Db;
  hub: Hub;
  storage: Storage;
  config: Config;
}

declare module "fastify" {
  interface FastifyInstance {
    deps: Deps;
    authParent: (req: FastifyRequest, reply: FastifyReply) => Promise<void>;
    authAgent: (req: FastifyRequest, reply: FastifyReply) => Promise<void>;
  }
  interface FastifyRequest {
    agentDevice?: Device;
  }
}
declare module "@fastify/jwt" {
  interface FastifyJWT {
    payload: { sub: string };
    user: { sub: string };
  }
}

export async function buildApp(overrides: Partial<Deps> = {}): Promise<FastifyInstance> {
  const config = overrides.config ?? defaultConfig;
  const deps: Deps = {
    db: overrides.db ?? defaultPrisma,
    hub: overrides.hub ?? new Hub(),
    storage: overrides.storage ?? createS3Storage(config.s3),
    config,
  };
  if (!overrides.hub) await deps.hub.start(config.redisUrl);

  const app = Fastify({ logger: process.env.NODE_ENV === "test" ? false : true });
  app.decorate("deps", deps);

  await app.register(cors, { origin: config.corsOrigin.split(","), credentials: true, methods: ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"] });
  await app.register(jwt, { secret: config.jwtSecret, sign: { expiresIn: "30d" } });
  await app.register(rateLimit, { global: false });
  await app.register(websocket);

  app.setErrorHandler((err, _req, reply) => {
    if (err instanceof ZodError) {
      return reply.code(400).send({ error: "validation_error", issues: err.issues });
    }
    const e = err as { statusCode?: number; message: string };
    if (e.statusCode && e.statusCode < 500) return reply.code(e.statusCode).send({ error: e.message });
    app.log.error(err);
    return reply.code(500).send({ error: "internal_error" });
  });

  app.decorate("authParent", async (req, reply) => {
    try {
      await req.jwtVerify();
    } catch {
      reply.code(401).send({ error: "unauthorized" });
    }
  });

  app.decorate("authAgent", async (req, reply) => {
    const m = /^Bearer (.+)$/.exec(req.headers.authorization ?? "");
    if (!m) return reply.code(401).send({ error: "unauthorized" });
    const device = await deps.db.device.findUnique({ where: { deviceTokenHash: hashToken(m[1]) } });
    if (!device) return reply.code(401).send({ error: "unauthorized" });
    req.agentDevice = device;
  });

  app.get("/health", async () => ({ ok: true }));
  await app.register(authRoutes, { prefix: "/api/auth" });
  await app.register(parentRoutes, { prefix: "/api" });
  await app.register(agentRoutes, { prefix: "/api/agent" });
  await app.register(agentWs);

  app.addHook("onClose", async () => {
    if (!overrides.hub) await deps.hub.stop();
  });
  return app;
}

/** Marks devices offline whose heartbeat went silent. */
export async function sweepOffline(deps: Deps) {
  const cutoff = new Date(Date.now() - deps.config.offlineAfterMs);
  return deps.db.device.updateMany({
    where: { isOnline: true, lastSeenAt: { lt: cutoff } },
    data: { isOnline: false },
  });
}
