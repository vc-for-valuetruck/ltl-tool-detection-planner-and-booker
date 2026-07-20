import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { LtlService } from './ltl.service';
import { RUNTIME_CONFIG } from '../../runtime-config';
import { AccessorialReviewContext, CapacitySnapshot, LaneRateContext } from './ltl.models';
import { LaredoArrivalsBoard } from './arrivals.models';

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

describe('LtlService — rating & capacity context', () => {
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

  it('calls GET /api/ltl/capacity/today and maps the snapshot', () => {
    let result: CapacitySnapshot | undefined;

    service.capacityToday().subscribe((snapshot) => (result = snapshot));

    const req = http.expectOne('/api/ltl/capacity/today');
    expect(req.request.method).toBe('GET');

    const fakeResponse: CapacitySnapshot = {
      generatedAt: '2026-07-20T00:00:00Z',
      activeTrucks: 3,
      totalTrucks: 5,
      inTransitTrips: 2,
      totalTrailers: 4,
      trailersByType: [{ equipmentType: 'Dry Van', count: 4 }],
      truncated: false,
      source: 'Live Alvys',
    };
    req.flush(fakeResponse);

    expect(result).toBeDefined();
    expect(result!.activeTrucks).toBe(3);
    expect(result!.trailersByType[0].equipmentType).toBe('Dry Van');
  });

  it('calls GET /api/ltl/arrivals (no date) and maps the board', () => {
    let result: LaredoArrivalsBoard | undefined;

    service.arrivals().subscribe((b) => (result = b));

    const req = http.expectOne('/api/ltl/arrivals');
    expect(req.request.method).toBe('GET');

    const fakeResponse: LaredoArrivalsBoard = {
      generatedAt: '2026-07-20T00:00:00Z',
      date: '2026-07-20',
      yard: 'LAREDO',
      arrivals: [],
      truncated: false,
      source: 'Live Alvys trips.',
    };
    req.flush(fakeResponse);

    expect(result).toBeDefined();
    expect(result!.yard).toBe('LAREDO');
  });

  it('calls GET /api/ltl/arrivals with the date query param when supplied', () => {
    service.arrivals('2026-07-21').subscribe();

    const req = http.expectOne('/api/ltl/arrivals?date=2026-07-21');
    expect(req.request.method).toBe('GET');
    req.flush({
      generatedAt: '2026-07-21T00:00:00Z',
      date: '2026-07-21',
      yard: 'LAREDO',
      arrivals: [],
      truncated: false,
      source: 'Live Alvys trips.',
    } as LaredoArrivalsBoard);
  });

  it('calls GET /api/ltl/lane-rate with the origin/destination states as query params', () => {
    let result: LaneRateContext | undefined;

    service.laneRate('TX', 'IL').subscribe((ctx) => (result = ctx));

    const req = http.expectOne('/api/ltl/lane-rate?originState=TX&destinationState=IL');
    expect(req.request.method).toBe('GET');

    const fakeResponse: LaneRateContext = {
      originState: 'TX',
      destinationState: 'IL',
      sampleSize: 3,
      medianRpm: 5,
      minRpm: 4,
      maxRpm: 6,
      basis: 'Recent tenant history, not a market rate.',
      generatedAt: '2026-07-20T00:00:00Z',
    };
    req.flush(fakeResponse);

    expect(result).toBeDefined();
    expect(result!.medianRpm).toBe(5);
    expect(result!.sampleSize).toBe(3);
  });
});
