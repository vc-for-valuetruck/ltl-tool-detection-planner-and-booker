import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { buildAppConfig } from './app/app.config';
import { normalizeRuntimeConfig, RuntimeConfig } from './app/runtime-config';

async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  try {
    const response = await fetch('runtime-config.json', { cache: 'no-store' });
    if (!response.ok) {
      return normalizeRuntimeConfig(null);
    }
    return normalizeRuntimeConfig(await response.json());
  } catch {
    return normalizeRuntimeConfig(null);
  }
}

loadRuntimeConfig()
  .then((config) => bootstrapApplication(App, buildAppConfig(config)))
  .catch((err) => console.error(err));
