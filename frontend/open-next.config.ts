import { defineCloudflareConfig } from "@opennextjs/cloudflare";

// Start without an incremental cache binding. Configure R2 caching only if the
// application later needs Next.js ISR or shared server-render cache state.
export default defineCloudflareConfig();
