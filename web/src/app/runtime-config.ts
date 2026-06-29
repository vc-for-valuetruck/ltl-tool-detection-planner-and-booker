import { InjectionToken } from '@angular/core';

/**
 * Runtime configuration loaded from `runtime-config.json` at startup. In Docker,
 * the web container regenerates this file from RUNTIME_* environment variables
 * (see web/Dockerfile + docker-entrypoint.sh) so the same image works across
 * environments without a rebuild.
 */
export interface RuntimeConfig {
  tenantId: string;
  clientId: string;
  apiScope: string;
  apiBaseUrl: string;
}

export const RUNTIME_CONFIG = new InjectionToken<RuntimeConfig>('RUNTIME_CONFIG');

export function normalizeRuntimeConfig(raw: Partial<RuntimeConfig> | null | undefined): RuntimeConfig {
  return {
    tenantId: raw?.tenantId ?? '',
    clientId: raw?.clientId ?? '',
    apiScope: raw?.apiScope ?? '',
    apiBaseUrl: raw?.apiBaseUrl || '/api',
  };
}

export function isAuthConfigured(config: RuntimeConfig): boolean {
  return config.tenantId.length > 0 && config.clientId.length > 0;
}
