import { defineConfig } from "vite";

const lab = "http://127.0.0.1:5183";
const devOrigins = new Set(["http://127.0.0.1:5173", "http://localhost:5173"]);

export default defineConfig({
  server: {
    host: "127.0.0.1",
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": {
        target: lab,
        changeOrigin: true,
        configure(proxy) {
          proxy.on("proxyReq", (outgoing, incoming) => {
            const origin = incoming.headers.origin;
            if (origin && devOrigins.has(origin)) outgoing.setHeader("Origin", lab);
          });
        },
      },
    },
  },
});
