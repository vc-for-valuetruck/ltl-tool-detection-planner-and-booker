import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { YardWebhooksAdmin } from './yard-webhooks-admin';
import { YardWebhooksService } from './yard-webhooks.service';
import { YardWebhookAdminView } from './yard-webhooks.models';

describe('YardWebhooksService', () => {
  let service: YardWebhooksService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: RUNTIME_CONFIG,
          useValue: { tenantId: '', clientId: '', apiScope: '', apiBaseUrl: '/api' },
        },
      ],
    });
    service = TestBed.inject(YardWebhooksService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the webhook admin snapshot with a max', () => {
    service.webhookEvents(50).subscribe();
    const req = http.expectOne('/api/yard/webhooks/events?max=50');
    expect(req.request.method).toBe('GET');
    req.flush({
      events: [],
      totalReceived: 0,
      enabled: true,
      secretConfigured: true,
      toleranceSeconds: 300,
    });
  });
});

describe('YardWebhooksAdmin', () => {
  function view(overrides: Partial<YardWebhookAdminView> = {}): YardWebhookAdminView {
    return {
      events: [],
      totalReceived: 0,
      enabled: true,
      secretConfigured: true,
      toleranceSeconds: 300,
      ...overrides,
    };
  }

  function setup(snapshot: YardWebhookAdminView) {
    const service: Partial<YardWebhooksService> = {
      webhookEvents: () => of(snapshot),
    };
    TestBed.configureTestingModule({
      imports: [YardWebhooksAdmin],
      providers: [{ provide: YardWebhooksService, useValue: service }],
    });
    const fixture = TestBed.createComponent(YardWebhooksAdmin);
    fixture.detectChanges();
    return fixture;
  }

  it('shows the configured-receiver posture when enabled with a secret', () => {
    const fixture = setup(view({ enabled: true, secretConfigured: true }));
    const config = fixture.nativeElement.querySelector('[data-testid="yard-webhook-config"]');
    expect(config.textContent).toContain('Receiver enabled');
    expect(config.textContent).toContain('300s');
  });

  it('warns fail-closed when enabled without a signing secret', () => {
    const fixture = setup(view({ enabled: true, secretConfigured: false }));
    const config = fixture.nativeElement.querySelector('[data-testid="yard-webhook-config"]');
    expect(config.textContent).toContain('fails closed');
  });

  it('shows the dormant posture when the receiver is disabled', () => {
    const fixture = setup(view({ enabled: false, secretConfigured: false }));
    const config = fixture.nativeElement.querySelector('[data-testid="yard-webhook-config"]');
    expect(config.textContent).toContain('dormant');
  });

  it('renders event rows with honest — for missing equipment ids', () => {
    const fixture = setup(
      view({
        totalReceived: 1,
        events: [
          {
            eventId: 'evt-1',
            eventType: 'LtlDraftCreated',
            timestamp: 1_700_000_000,
            yardCode: null,
            tractorId: null,
            trailerId: null,
            driverId: null,
            processingState: 'Processed',
            receivedAt: '2026-07-22T10:00:00Z',
            rawBody: '{}',
          },
        ],
      }),
    );
    const table = fixture.nativeElement.querySelector('.yw-table');
    expect(table).toBeTruthy();
    expect(table.textContent).toContain('LtlDraftCreated');
    expect(table.textContent).toContain('—');
  });
});
