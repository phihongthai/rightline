import type { FastifyPluginAsync } from "fastify";
import bcrypt from "bcryptjs";
import { z } from "zod";

const Creds = z.object({
  email: z.string().email().transform((s) => s.toLowerCase()),
  password: z.string().min(8).max(200),
});
const Register = Creds.extend({ name: z.string().trim().min(1).max(100) });

export const authRoutes: FastifyPluginAsync = async (app) => {
  const { db } = app.deps;
  const limit = { config: { rateLimit: { max: app.deps.config.authRateLimitPerMin, timeWindow: "1 minute" } } };

  app.post("/register", limit, async (req, reply) => {
    const b = Register.parse(req.body);
    if (await db.user.findUnique({ where: { email: b.email } }))
      return reply.code(409).send({ error: "email_taken" });
    const user = await db.user.create({
      data: { email: b.email, name: b.name, passwordHash: await bcrypt.hash(b.password, 10) },
    });
    const token = app.jwt.sign({ sub: user.id });
    return reply.code(201).send({ token, user: { id: user.id, email: user.email, name: user.name } });
  });

  app.post("/login", limit, async (req, reply) => {
    const b = Creds.parse(req.body);
    const user = await db.user.findUnique({ where: { email: b.email } });
    if (!user || !(await bcrypt.compare(b.password, user.passwordHash)))
      return reply.code(401).send({ error: "invalid_credentials" });
    return { token: app.jwt.sign({ sub: user.id }), user: { id: user.id, email: user.email, name: user.name } };
  });

  app.get("/me", { onRequest: app.authParent }, async (req) => {
    const u = await db.user.findUniqueOrThrow({ where: { id: req.user.sub } });
    return { id: u.id, email: u.email, name: u.name };
  });
};
