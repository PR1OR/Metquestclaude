// Cloudflare Workers entry point. Secrets are set with:
//   wrangler secret put ANTHROPIC_API_KEY
//   wrangler secret put APP_AUTH_TOKEN
import { handleRequest, createRateLimiter } from "./handler.js";

// Per-isolate rate limiter — best-effort, sufficient for a personal deployment.
const rateLimiter = createRateLimiter();

export default {
  async fetch(request, env) {
    return handleRequest(request, env, { rateLimiter });
  },
};
