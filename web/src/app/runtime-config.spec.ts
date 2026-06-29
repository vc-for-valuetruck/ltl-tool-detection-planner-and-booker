import { normalizeRuntimeConfig, isAuthConfigured } from './runtime-config';

describe('runtime-config', () => {
  it('applies defaults for missing values', () => {
    const config = normalizeRuntimeConfig(null);
    expect(config.tenantId).toBe('');
    expect(config.clientId).toBe('');
    expect(config.apiScope).toBe('');
    expect(config.apiBaseUrl).toBe('/api');
  });

  it('keeps provided values and falls back apiBaseUrl when blank', () => {
    const config = normalizeRuntimeConfig({ tenantId: 't', clientId: 'c', apiScope: 's', apiBaseUrl: '' });
    expect(config.tenantId).toBe('t');
    expect(config.apiBaseUrl).toBe('/api');
  });

  it('treats auth as configured only when tenant and client are present', () => {
    expect(isAuthConfigured(normalizeRuntimeConfig({ tenantId: 't', clientId: 'c' }))).toBeTrue();
    expect(isAuthConfigured(normalizeRuntimeConfig({ tenantId: 't' }))).toBeFalse();
    expect(isAuthConfigured(normalizeRuntimeConfig(null))).toBeFalse();
  });
});
