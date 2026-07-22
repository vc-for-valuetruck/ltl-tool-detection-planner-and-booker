/**
 * TypeScript mirrors of the Yard webhook admin contracts
 * (see src/LtlTool.Api/Features/Integrations/Yard/Webhooks/YardWebhookApiModels.cs). Read-only
 * projections for the `/admin/yard/webhooks` panel — the raw body carries the business payload only,
 * never any signing secret or auth material. Every nullable field renders "—", never fabricated.
 */

/** Read-only admin projection of one received Yard webhook delivery and its processing state. */
export interface YardWebhookEventView {
  eventId: string;
  eventType: string;
  timestamp: number;
  yardCode?: string | null;
  tractorId?: string | null;
  trailerId?: string | null;
  driverId?: string | null;
  processingState: string;
  processingError?: string | null;
  receivedAt: string;
  processedAt?: string | null;
  rawBody: string;
}

/**
 * The webhook admin snapshot: recent events (newest first), the lifetime total, and an honest
 * snapshot of receiver configuration. No secret value is ever included — only whether one exists.
 */
export interface YardWebhookAdminView {
  events: YardWebhookEventView[];
  totalReceived: number;
  enabled: boolean;
  secretConfigured: boolean;
  toleranceSeconds: number;
}
