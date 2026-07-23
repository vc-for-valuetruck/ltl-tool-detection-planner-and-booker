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

/**
 * Placeholder GUID used by the E2E demo stack (see .github/workflows/ci.yml) so
 * MSAL's greedy option validation doesn't crash the API at build time. When we
 * see it on the client we treat the environment as unconfigured — otherwise the
 * branded /login screen + authGuard would trap the Demo-mode E2E flow behind a
 * sign-in card that will never resolve (no real Entra tenant behind it).
 */
const PLACEHOLDER_GUID = '00000000-0000-0000-0000-000000000000';

export function isAuthConfigured(config: RuntimeConfig): boolean {
  return (
    config.tenantId.length > 0 &&
    config.clientId.length > 0 &&
    config.tenantId !== PLACEHOLDER_GUID &&
    config.clientId !== PLACEHOLDER_GUID
  );
}
