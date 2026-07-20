import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { LtlService } from './ltl.service';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { AccessorialReviewContext } from './ltl.models';

describe('LtlService — accessorial signals', () => {
  let service: LtlService;
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
    service = TestBed.inject(LtlService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('calls GET /api/ltl/loads/{id}/accessorial-signals and maps the response', () => {
    let result: AccessorialReviewContext | undefined;

    service.getAccessorialSignals('LTL-100').subscribe((ctx) => (result = ctx));

    const req = http.expectOne('/api/ltl/loads/LTL-100/accessorial-signals');
    expect(req.request.method).toBe('GET');

    const fakeResponse: AccessorialReviewContext = {
      evaluated: true,
      signals: [
        {
          type: 'Detention',
          evidenceQuote: '…driver waited 3 hours at the dock…',
          sourceId: 'N1',
          sourceType: 'Note',
          confidence: 1.0,
        },
      ],
    };
    req.flush(fakeResponse);

    expect(result).toBeDefined();
    expect(result!.evaluated).toBeTrue();
    expect(result!.signals.length).toBe(1);
    expect(result!.signals[0].type).toBe('Detention');
    expect(result!.signals[0].confidence).toBe(1.0);
  });

  it('URL-encodes the load id/number', () => {
    service.getAccessorialSignals('100/A').subscribe();

    const req = http.expectOne('/api/ltl/loads/100%2FA/accessorial-signals');
    expect(req.request.method).toBe('GET');
    req.flush({ evaluated: false, signals: [] });
  });

  it('maps not-evaluated context (evaluated=false, empty signals)', () => {
    let result: AccessorialReviewContext | undefined;

    service.getAccessorialSignals('LTL-200').subscribe((ctx) => (result = ctx));

    const req = http.expectOne('/api/ltl/loads/LTL-200/accessorial-signals');
    req.flush({ evaluated: false, signals: [] } as AccessorialReviewContext);

    expect(result).toBeDefined();
    expect(result!.evaluated).toBeFalse();
    expect(result!.signals).toEqual([]);
  });
});
