import { LtlLoadDetail } from './ltl-load-detail';
import { LtlService } from './ltl.service';
import { LtlLoadSummary, LtlPlace, MatchFactor, MatchResult } from './ltl.models';
import { YardArtifactView } from './yard-artifacts.models';
import { ActivatedRoute } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function place(partial: Partial<LtlPlace>): LtlPlace {
  return { name: null, city: null, state: null, zip: null, label: null, ...partial };
}

function load(partial: Partial<LtlLoadSummary>): LtlLoadSummary {
  return { id: 'X', loadNumber: 'L-1', ...partial } as LtlLoadSummary;
}

describe('LtlLoadDetail', () => {
  function build(loadNumber: string | null, stub: Partial<LtlService>): LtlLoadDetail {
    // Artifacts + matches are supplementary; default to empty feeds unless a test overrides them.
    const withArtifacts: Partial<LtlService> = {
      yardArtifacts: () => of([]),
      getMatches: () => of([]),
      ...stub,
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: LtlService, useValue: withArtifacts },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['loadNumber', loadNumber]]) } } },
      ],
    });
    return TestBed.runInInjectionContext(() => new LtlLoadDetail());
  }

  function artifact(overrides: Partial<YardArtifactView>): YardArtifactView {
    return {
      id: 'a1',
      yard: 'LAREDO',
      truckUnit: 'T1',
      trailerUnit: null,
      loadNumber: 'L-1',
      submittedBy: 'dock@valuetruck.com',
      capturedAt: '2026-07-20T00:00:00Z',
      createdAt: '2026-07-20T00:00:00Z',
      status: 'Passed',
      passedItems: 3,
      failedItems: 0,
      naItems: 0,
      verifiedPallets: null,
      files: [],
      ...overrides,
    };
  }

  it('fetches the load by the route param on init', () => {
    let asked: string | undefined;
    const c = build('L-100234', {
      getLoad: (ref) => {
        asked = ref;
        return of(load({ loadNumber: 'L-100234' }));
      },
    });
    c.ngOnInit();
    expect(asked).toBe('L-100234');
    expect(c['hasLoad']()).toBeTrue();
    expect(c['loading']()).toBeFalse();
  });

  it('errors without calling Alvys when no load number is in the URL', () => {
    let called = false;
    const c = build(null, {
      getLoad: () => {
        called = true;
        return of(load({}));
      },
    });
    c.ngOnInit();
    expect(called).toBeFalse();
    expect(c['error']()).toBe('No load number in the URL.');
    expect(c['loading']()).toBeFalse();
  });

  it('surfaces an Alvys error and clears loading', () => {
    const c = build('L-1', { getLoad: () => throwError(() => ({ message: 'boom' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
    expect(c['hasLoad']()).toBeFalse();
  });

  it('formats a place from label, then city/state/zip, then em dash', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['place'](null)).toBe('—');
    expect(c['place'](place({ label: 'Dallas, TX' }))).toBe('Dallas, TX');
    expect(c['place'](place({ city: 'Irving', state: 'TX' }))).toBe('Irving, TX');
    expect(c['place'](place({}))).toBe('—');
  });

  it('renders unknown weight as "Unknown", never zero', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['weightLabel'](load({ weightLbs: null }))).toBe('Unknown');
    expect(c['weightLabel'](load({ weightLbs: 42360 }))).toBe('42,360 lb');
  });

  it('renders missing money as an em dash, never zero', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['formatCurrency'](null)).toBe('—');
    expect(c['formatCurrency'](1200)).toBe('$1,200');
  });

  it('fetches yard artifacts for the load number and exposes them', () => {
    let askedLoad: string | undefined;
    const c = build('L-100234', {
      getLoad: () => of(load({ loadNumber: 'L-100234' })),
      yardArtifacts: (q) => {
        askedLoad = q.loadNumber;
        return of([artifact({ loadNumber: 'L-100234', status: 'Flagged' })]);
      },
    });
    c.ngOnInit();
    expect(askedLoad).toBe('L-100234');
    expect(c['artifacts']().length).toBe(1);
    expect(c['artifacts']()[0].status).toBe('Flagged');
  });

  it('keeps the load visible when the artifacts fetch fails', () => {
    const c = build('L-1', {
      getLoad: () => of(load({ loadNumber: 'L-1' })),
      yardArtifacts: () => throwError(() => ({ message: 'boom' })),
    });
    c.ngOnInit();
    expect(c['hasLoad']()).toBeTrue();
    expect(c['artifacts']()).toEqual([]);
  });

  function factor(overrides: Partial<MatchFactor>): MatchFactor {
    return {
      name: 'Equipment match',
      status: 'Strong',
      detail: 'Trailer is Reefer, matching required equipment.',
      points: 30,
      maxPoints: 30,
      rawValue: 'trailer Reefer vs required Reefer',
      weight: 30,
      ...overrides,
    };
  }

  function match(overrides: Partial<MatchResult>): MatchResult {
    return {
      driverId: 'd1',
      driverName: 'Jane Doe',
      truckId: 't1',
      truckNumber: 'TR-1',
      trailerId: 'r1',
      trailerNumber: 'RL-1',
      label: 'Excellent',
      labelText: 'Excellent Match',
      score: 92,
      summary: 'Excellent Match: strong on Equipment match.',
      factors: [factor({})],
      disqualifiers: [],
      warnings: [],
      predictionBasis: 'AlvysPredictionUnavailable',
      ...overrides,
    } as MatchResult;
  }

  it('fetches and exposes ranked matches for the load', () => {
    let asked: string | undefined;
    const c = build('L-100234', {
      getLoad: () => of(load({ loadNumber: 'L-100234', id: 'ID-1' })),
      getMatches: (ref) => {
        asked = ref;
        return of([match({})]);
      },
    });
    c.ngOnInit();
    expect(asked).toBe('L-100234');
    expect(c['matches']().length).toBe(1);
    expect(c['matchesLoading']()).toBeFalse();
    expect(c['matchesError']()).toBeNull();
  });

  it('keeps the load visible when the matches fetch fails', () => {
    const c = build('L-1', {
      getLoad: () => of(load({ loadNumber: 'L-1' })),
      getMatches: () => throwError(() => ({ message: 'boom' })),
    });
    c.ngOnInit();
    expect(c['hasLoad']()).toBeTrue();
    expect(c['matches']()).toEqual([]);
    expect(c['matchesError']()).toBe('boom');
  });

  it('reports an Unavailable factor as "not scored", never a zero contribution', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['factorContribution'](factor({ status: 'Unavailable', points: 0, maxPoints: 0 }))).toBe('not scored');
    expect(c['factorContribution'](factor({ status: 'Strong', points: 30, maxPoints: 30 }))).toBe('30 / 30 pts');
  });

  it('builds an audit-friendly clipboard breakdown including factors, warnings and basis', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    const text = c['matchClipboardText'](
      match({
        warnings: ['Co-driver John is terminated — verify the team pairing.'],
        disqualifiers: [],
        factors: [factor({}), factor({ name: 'Window feasibility', status: 'Unavailable', points: 0, maxPoints: 0, rawValue: null })],
      }),
    );
    expect(text).toContain('Excellent Match (92/100) — Jane Doe');
    expect(text).toContain('- Equipment match (Strong, 30 / 30 pts)');
    expect(text).toContain('- Window feasibility (Unavailable, not scored)');
    expect(text).toContain('Co-driver John is terminated');
    expect(text).toContain('Ranking basis: AlvysPredictionUnavailable');
  });

  it('maps match labels to chip classes', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['matchLabelClass']('Excellent')).toBe('chip chip-good');
    expect(c['matchLabelClass']('Possible')).toBe('chip chip-neutral');
    expect(c['matchLabelClass']('Risky')).toBe('chip chip-warn');
    expect(c['matchLabelClass']('NotRecommended')).toBe('chip chip-danger');
  });

  it('classifies artifact status chips and formats verified dims', () => {
    const c = build('L-1', { getLoad: () => of(load({})) });
    expect(c['artifactChipClass']('Passed')).toBe('chip chip-good');
    expect(c['artifactChipClass']('Flagged')).toBe('chip chip-danger');
    expect(c['artifactChipClass']('Submitted')).toBe('chip chip-neutral');

    expect(
      c['verifiedDims'](
        artifact({
          verifiedPallets: {
            palletCount: 12,
            lengthInches: 48,
            widthInches: 40,
            heightInches: 60,
            source: 'yard verification',
          },
        }),
      ),
    ).toBe('48×40×60 in');
    expect(c['verifiedDims'](artifact({ verifiedPallets: null }))).toBeNull();
  });
});
