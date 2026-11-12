import "./globals.css";
import type { Metadata, Viewport } from "next";
import { PwaRegister } from "@/components/PwaRegister";

export const metadata: Metadata = {
  title: "KidSafe - Phụ huynh",
  description: "Quản lý & bảo vệ máy tính Windows của con",
  manifest: "/manifest.webmanifest",
  icons: { icon: "/icon.svg", apple: "/icon.svg" },
};
export const viewport: Viewport = { width: "device-width", initialScale: 1, themeColor: "#2563eb" };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="vi">
      <body className="mx-auto min-h-screen max-w-xl text-slate-900 antialiased">
        <PwaRegister />
        {children}
      </body>
    </html>
  );
}
