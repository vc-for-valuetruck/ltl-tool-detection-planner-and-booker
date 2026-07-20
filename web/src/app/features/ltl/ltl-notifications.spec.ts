import { LtlNotifications } from './ltl-notifications';
import { LtlService } from './ltl.service';
import { NotificationEvent, NotificationFeedResponse } from './notifications.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function event(partial: Partial<NotificationEvent> = {}): NotificationEvent {
  return {
    id: partial.id ?? 'e1',
    idempotencyKey: 'k1',
    stage: 'ConsolidationPlanCreated',
    title: 'Consolidation plan recorded · 100234',
    summary: 'A plan was recorded.',
    occurredAt: '2026-07-20T09:30:00Z',
    firedAt: '2026-07-20T09:30:05Z',
    deliveries: [
      { channel: 'InApp', state: 'Delivered', recipients: ['dispatcher'] },
    ],
    ...partial,
  };
}

function feed(partial: Partial<NotificationFeedResponse> = {}): NotificationFeedResponse {
  return {
    total: partial.total ?? 1,
    items: partial.items ?? [event()],
    channels: partial.channels ?? [
      { channel: 'InApp', configured: true },
      { channel: 'Teams', configured: false },
      { channel: 'Email', configured: false },
    ],
  };
}

describe('LtlNotifications', () => {
  function build(stub: Partial<LtlService>): LtlNotifications {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: LtlService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlNotifications());
  }

  it('loads the feed on init and exposes items, total and channels', () => {
    const c = build({ notifications: () => of(feed()) });
    c.ngOnInit();
    expect(c['items']().length).toBe(1);
    expect(c['total']()).toBe(1);
    expect(c['channels']().length).toBe(3);
    expect(c['loading']()).toBeFalse();
    expect(c['hasItems']()).toBeTrue();
  });

  it('shows an empty feed honestly', () => {
    const c = build({ notifications: () => of(feed({ items: [], total: 0 })) });
    c.ngOnInit();
    expect(c['hasItems']()).toBeFalse();
  });

  it('surfaces a feed error and clears loading', () => {
    const c = build({ notifications: () => throwError(() => ({ message: 'boom' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
    expect(c['hasItems']()).toBeFalse();
  });

  it('maps delivery states to honest pill classes', () => {
    const c = build({ notifications: () => of(feed()) });
    expect(c['deliveryClass']('Delivered')).toContain('pill-ok');
    expect(c['deliveryClass']('Pending')).toContain('pill-warn');
    expect(c['deliveryClass']('Failed')).toContain('pill-danger');
    expect(c['deliveryClass']('NotConfigured')).toContain('pill-muted');
    expect(c['deliveryLabel']('NotConfigured')).toBe('Not configured');
  });

  it('labels workflow stages for the badge', () => {
    const c = build({ notifications: () => of(feed()) });
    expect(c['stageLabel']('ConsolidationPlanCreated')).toBe('Consolidation plan');
    expect(c['stageLabel']('ExceptionRaised')).toBe('Exception');
  });
});
