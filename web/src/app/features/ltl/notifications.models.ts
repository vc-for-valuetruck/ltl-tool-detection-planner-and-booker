/**
 * Client-side types for the LTL workflow notification feed (Phase 6). Mirrors the read-only
 * `GET /api/ltl/notifications` payload. Enums are serialized as strings by the API. Nothing here
 * writes to Alvys — the feed reflects fired workflow events exactly as the engine recorded them.
 */

export type NotificationStage =
  | 'ConsolidationPlanCreated'
  | 'ClickCardGenerated'
  | 'AssignmentConfirmed'
  | 'PickupEvent'
  | 'DeliveryEvent'
  | 'BillingReady'
  | 'Invoiced'
  | 'ExceptionRaised';

export type NotificationChannelKind = 'InApp' | 'Teams' | 'Email';

export type NotificationDeliveryState = 'Delivered' | 'Pending' | 'NotConfigured' | 'Failed';

export interface NotificationDelivery {
  channel: NotificationChannelKind;
  state: NotificationDeliveryState;
  recipients: string[];
  detail?: string | null;
}

export interface NotificationEvent {
  id: string;
  idempotencyKey: string;
  stage: NotificationStage;
  title: string;
  summary: string;
  loadId?: string | null;
  loadNumber?: string | null;
  planId?: string | null;
  linkPath?: string | null;
  occurredAt: string;
  firedAt: string;
  deliveries: NotificationDelivery[];
}

export interface NotificationChannelStatus {
  channel: NotificationChannelKind;
  configured: boolean;
}

export interface NotificationFeedResponse {
  total: number;
  items: NotificationEvent[];
  channels: NotificationChannelStatus[];
}
