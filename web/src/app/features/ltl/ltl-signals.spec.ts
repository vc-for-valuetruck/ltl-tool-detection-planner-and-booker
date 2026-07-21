import { LtlSignals } from './ltl-signals';
import { LtlService } from './ltl.service';
import { SignalIngestResponse, SignalView } from './signals.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function view(partial: Partial<SignalView> = {}): SignalView {
  return {
    id: partial.id ?? 's1',
    sourceType: 'email',
    sourceId: 'msg-1',
    signalType: 'AccessorialEvidence',
    confidence: 1,
    evidenceQuote: 'Driver was detained 3 hours.',
    suggestedSurface: 'BillingWorklistBadge',
    summary: 'detention evidence',
    loadNumber: '100234',
    status: 'Pending',
    ingestedBy: 'dispatcher@vt.com',
    createdAt: '2026-07-21T09:30:00Z',
    decidedAt: null,
    decidedBy: null,
    ...partial,
  };
}

describe('LtlSignals', () => {
  function build(stub: Partial<LtlService>): LtlSignals {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: LtlService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlSignals());
  }

  const baseStub = (over: Partial<LtlService> = {}): Partial<LtlService> => ({
    signalExtractor: () => of({ name: 'deterministic-keyword' }),
    signals: () => of([view()]),
    ...over,
  });

  it('loads the queue and the active extractor on init', () => {
    const c = build(baseStub());
    c.ngOnInit();
    expect(c['items']().length).toBe(1);
    expect(c['extractorName']()).toBe('deterministic-keyword');
    expect(c['loading']()).toBeFalse();
    expect(c['hasItems']()).toBeTrue();
  });

  it('shows an empty queue honestly', () => {
    const c = build(baseStub({ signals: () => of([]) }));
    c.ngOnInit();
    expect(c['hasItems']()).toBeFalse();
  });

  it('surfaces a queue load error and clears loading', () => {
    const c = build(baseStub({ signals: () => throwError(() => ({ message: 'boom' })) }));
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
  });

  it('blocks ingest until text and sourceId are present', () => {
    const c = build(baseStub());
    c.ngOnInit();
    expect(c['canIngest']()).toBeFalse();
    c['sourceId'].set('msg-1');
    c['text'].set('Driver was detained.');
    expect(c['canIngest']()).toBeTrue();
  });

  it('records the recorded count on a successful ingest and reloads', () => {
    const ingested: SignalIngestResponse = { count: 2, signals: [view(), view({ id: 's2' })] };
    let listCalls = 0;
    const c = build(
      baseStub({
        ingestSignals: () => of(ingested),
        signals: () => {
          listCalls++;
          return of([view()]);
        },
      }),
    );
    c.ngOnInit();
    c['sourceId'].set('msg-1');
    c['text'].set('Driver was detained.');
    c['ingest']();
    expect(c['lastIngestCount']()).toBe(2);
    expect(c['ingestError']()).toBeNull();
    // one load on init + one after ingest
    expect(listCalls).toBe(2);
  });

  it('surfaces the fail-closed error verbatim and records nothing', () => {
    const c = build(
      baseStub({
        ingestSignals: () =>
          throwError(() => ({ error: { error: 'Extraction failed. Nothing was recorded.' } })),
      }),
    );
    c.ngOnInit();
    c['sourceId'].set('msg-1');
    c['text'].set('Driver was detained.');
    c['ingest']();
    expect(c['ingestError']()).toBe('Extraction failed. Nothing was recorded.');
    expect(c['lastIngestCount']()).toBeNull();
  });

  it('accepting a signal replaces it in place without a full reload', () => {
    const accepted = view({ status: 'Accepted', decidedBy: 'dispatcher@vt.com' });
    const c = build(baseStub({ acceptSignal: () => of(accepted) }));
    c.ngOnInit();
    c['accept'](view());
    expect(c['items']()[0].status).toBe('Accepted');
  });

  it('rejecting a signal replaces it in place', () => {
    const rejected = view({ status: 'Rejected' });
    const c = build(baseStub({ rejectSignal: () => of(rejected) }));
    c.ngOnInit();
    c['reject'](view());
    expect(c['items']()[0].status).toBe('Rejected');
  });

  it('maps types, surfaces and status to honest labels/classes', () => {
    const c = build(baseStub());
    expect(c['typeLabel']('AccessorialEvidence')).toBe('Accessorial evidence');
    expect(c['surfaceLabel']('BillingWorklistBadge')).toBe('Billing badge');
    expect(c['statusClass']('Accepted')).toContain('pill-ok');
    expect(c['statusClass']('Pending')).toContain('pill-warn');
    expect(c['statusClass']('Rejected')).toContain('pill-muted');
  });
});
