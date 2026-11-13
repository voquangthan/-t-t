import { defineConfig } from "vite";

const lab = "http://127.0.0.1:5183";
const dev = "http://127.0.0.1:5173";

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
            if (incoming.headers.origin === dev) outgoing.setHeader("Origin", lab);
          });
        },
      },
    },
  },
});
