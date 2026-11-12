export const config = {
  databaseUrl: process.env.DATABASE_URL ?? "postgresql://postgres:postgres@localhost:5432/kidsafe",
  redisUrl: process.env.REDIS_URL || undefined,
  jwtSecret: process.env.JWT_SECRET ?? "dev-secret-change-me",
  port: Number(process.env.PORT ?? 3000),
  corsOrigin: process.env.CORS_ORIGIN ?? "http://localhost:3001",
  s3: {
    endpoint: process.env.S3_ENDPOINT ?? "http://localhost:9000",
    region: process.env.S3_REGION ?? "auto",
    bucket: process.env.S3_BUCKET ?? "screenshots",
    accessKey: process.env.S3_ACCESS_KEY ?? "minioadmin",
    secretKey: process.env.S3_SECRET_KEY ?? "minioadmin",
  },
  pairingTtlMs: 15 * 60 * 1000,
  trialDays: 30,
  offlineAfterMs: 90 * 1000,
  pairRateLimitPerMin: 10,
  authRateLimitPerMin: 20,
};
export type Config = typeof config;
