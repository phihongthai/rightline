-- CreateTable
CREATE TABLE "users" (
    "id" UUID NOT NULL,
    "email" TEXT NOT NULL,
    "password_hash" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT "users_pkey" PRIMARY KEY ("id")
);

-- CreateTable
CREATE TABLE "devices" (
    "id" UUID NOT NULL,
    "user_id" UUID NOT NULL,
    "name" TEXT NOT NULL,
    "os_version" TEXT,
    "agent_version" TEXT,
    "pairing_code" VARCHAR(6),
    "pairing_expires_at" TIMESTAMP(3),
    "device_token_hash" TEXT,
    "is_online" BOOLEAN NOT NULL DEFAULT false,
    "last_seen_at" TIMESTAMP(3),
    "is_locked_now" BOOLEAN NOT NULL DEFAULT false,
    "extra_time_until" TIMESTAMP(3),
    "active_minutes_today" INTEGER NOT NULL DEFAULT 0,
    "current_app" TEXT,
    "license_expires_at" TIMESTAMP(3) NOT NULL,
    "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT "devices_pkey" PRIMARY KEY ("id")
);

-- CreateTable
CREATE TABLE "policies" (
    "device_id" UUID NOT NULL,
    "version" INTEGER NOT NULL DEFAULT 1,
    "protection_flags" JSONB NOT NULL,
    "schedule_grid" JSONB NOT NULL,
    "daily_limit_minutes" INTEGER,
    "blocked_categories" TEXT[],
    "custom_blocked_domains" TEXT[],
    "custom_allowed_domains" TEXT[],
    "blocked_apps" JSONB NOT NULL,
    "screenshot_interval_sec" INTEGER NOT NULL DEFAULT 60,

    CONSTRAINT "policies_pkey" PRIMARY KEY ("device_id")
);

-- CreateTable
CREATE TABLE "time_requests" (
    "id" UUID NOT NULL,
    "device_id" UUID NOT NULL,
    "requested_minutes" INTEGER NOT NULL,
    "reason" TEXT NOT NULL DEFAULT '',
    "status" TEXT NOT NULL DEFAULT 'pending',
    "created_at" TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "resolved_at" TIMESTAMP(3),

    CONSTRAINT "time_requests_pkey" PRIMARY KEY ("id")
);

-- CreateTable
CREATE TABLE "screenshots" (
    "id" UUID NOT NULL,
    "device_id" UUID NOT NULL,
    "storage_key" TEXT NOT NULL,
    "captured_at" TIMESTAMP(3) NOT NULL,

    CONSTRAINT "screenshots_pkey" PRIMARY KEY ("id")
);

-- CreateTable
CREATE TABLE "activity_logs" (
    "id" UUID NOT NULL,
    "device_id" UUID NOT NULL,
    "log_type" TEXT NOT NULL,
    "target" TEXT NOT NULL,
    "title" TEXT NOT NULL DEFAULT '',
    "duration_sec" INTEGER NOT NULL DEFAULT 0,
    "recorded_at" TIMESTAMP(3) NOT NULL,

    CONSTRAINT "activity_logs_pkey" PRIMARY KEY ("id")
);

-- CreateIndex
CREATE UNIQUE INDEX "users_email_key" ON "users"("email");

-- CreateIndex
CREATE UNIQUE INDEX "devices_pairing_code_key" ON "devices"("pairing_code");

-- CreateIndex
CREATE UNIQUE INDEX "devices_device_token_hash_key" ON "devices"("device_token_hash");

-- CreateIndex
CREATE INDEX "devices_user_id_idx" ON "devices"("user_id");

-- CreateIndex
CREATE INDEX "time_requests_device_id_status_idx" ON "time_requests"("device_id", "status");

-- CreateIndex
CREATE INDEX "screenshots_device_id_captured_at_idx" ON "screenshots"("device_id", "captured_at" DESC);

-- CreateIndex
CREATE INDEX "activity_logs_device_id_recorded_at_idx" ON "activity_logs"("device_id", "recorded_at" DESC);

-- AddForeignKey
ALTER TABLE "devices" ADD CONSTRAINT "devices_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "users"("id") ON DELETE CASCADE ON UPDATE CASCADE;

-- AddForeignKey
ALTER TABLE "policies" ADD CONSTRAINT "policies_device_id_fkey" FOREIGN KEY ("device_id") REFERENCES "devices"("id") ON DELETE CASCADE ON UPDATE CASCADE;

-- AddForeignKey
ALTER TABLE "time_requests" ADD CONSTRAINT "time_requests_device_id_fkey" FOREIGN KEY ("device_id") REFERENCES "devices"("id") ON DELETE CASCADE ON UPDATE CASCADE;

-- AddForeignKey
ALTER TABLE "screenshots" ADD CONSTRAINT "screenshots_device_id_fkey" FOREIGN KEY ("device_id") REFERENCES "devices"("id") ON DELETE CASCADE ON UPDATE CASCADE;

-- AddForeignKey
ALTER TABLE "activity_logs" ADD CONSTRAINT "activity_logs_device_id_fkey" FOREIGN KEY ("device_id") REFERENCES "devices"("id") ON DELETE CASCADE ON UPDATE CASCADE;
