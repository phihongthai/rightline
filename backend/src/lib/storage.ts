import { S3Client, PutObjectCommand, GetObjectCommand, HeadObjectCommand } from "@aws-sdk/client-s3";
import { getSignedUrl } from "@aws-sdk/s3-request-presigner";
import type { Config } from "../config.js";

export interface Storage {
  presignPut(key: string, contentType: string): Promise<string>;
  presignGet(key: string): Promise<string>;
  exists(key: string): Promise<boolean>;
}

export function createS3Storage(cfg: Config["s3"]): Storage {
  const client = new S3Client({
    region: cfg.region,
    endpoint: cfg.endpoint,
    forcePathStyle: true,
    credentials: { accessKeyId: cfg.accessKey, secretAccessKey: cfg.secretKey },
  });
  return {
    presignPut: (key, contentType) =>
      getSignedUrl(client, new PutObjectCommand({ Bucket: cfg.bucket, Key: key, ContentType: contentType }), {
        expiresIn: 300,
      }),
    presignGet: (key) =>
      getSignedUrl(client, new GetObjectCommand({ Bucket: cfg.bucket, Key: key }), { expiresIn: 600 }),
    async exists(key) {
      try {
        await client.send(new HeadObjectCommand({ Bucket: cfg.bucket, Key: key }));
        return true;
      } catch {
        return false;
      }
    },
  };
}
