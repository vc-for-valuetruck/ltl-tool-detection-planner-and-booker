import { expect, Locator, Page } from '@playwright/test';

export type LtlHomeFlavor = 'legacy' | 'consolidations';

export async function isVisible(locator: Locator, timeout = 3_000): Promise<boolean> {
  return locator.isVisible({ timeout }).catch(() => false);
}

export async function detectHomeFlavor(page: Page): Promise<LtlHomeFlavor> {
  const consolidationsHeading = page.getByRole('heading', { name: "Today's consolidations" });
  if (await isVisible(consolidationsHeading)) {
    return 'consolidations';
  }
  const legacyHeading = page.getByRole('heading', { name: 'LTL Operating Console' });
  if (await isVisible(legacyHeading)) {
    return 'legacy';
  }
  await expect(consolidationsHeading).toBeVisible({
    timeout: 1,
    message:
      "Unknown /ltl landing UI: neither \"Today's consolidations\" nor \"LTL Operating Console\" heading is visible.",
  });
  return 'consolidations';
}
