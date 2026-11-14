import { defineConfig } from "vite";

const lab = `http://localhost:${process.env.DETOT_WEB_PORT || "5183"}`;
const workspace = new URL("/workspace", process.env.DETOT_PUBLIC_ORIGIN || lab).href;
const devOrigins = new Set(["http://127.0.0.1:5173", "http://localhost:5173"]);

export default defineConfig({
  plugins: [{
    name: "detot-workspace-redirect",
    configureServer(server) {
      server.middlewares.use("/workspace", (_request, response) => {
        response.writeHead(302, { Location: workspace, "Cache-Control": "no-store" });
        response.end();
      });
    },
  }],
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
