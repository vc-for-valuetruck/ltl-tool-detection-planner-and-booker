import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { LtlService } from '../ltl.service';
import { DemoDirectorService } from './demo-director.service';
import { DEMO_DIRECTOR_SCRIPT } from './demo-director.script';
import { DIRECTOR_NARRATION_KEY } from './demo-director.speech';

/** Minimal Router stub: records navigations and exposes a settable url. */
class RouterStub {
  url = '/ltl';
  navigated: string[] = [];
  navigateByUrl(url: string): Promise<boolean> {
    this.navigated.push(url);
    this.url = url;
    return Promise.resolve(true);
  }
}

/** Records the search queries the director issues and returns a configurable live-load result. */
class LtlServiceStub {
  queries: unknown[] = [];
  response: Observable<unknown> = of({ items: [], page: 0, pageSize: 0, total: 0, truncated: false });
  search(query: unknown): Observable<unknown> {
    this.queries.push(query);
    return this.response;
  }
}

describe('DemoDirectorService', () => {
  let service: DemoDirectorService;
  let router: RouterStub;
  let ltl: LtlServiceStub;

  beforeEach(() => {
    // Reset the persisted narration pref so the "defaults on" expectation is order-independent.
    try {
      localStorage.removeItem(DIRECTOR_NARRATION_KEY);
    } catch {
      /* storage may be unavailable in some CI sandboxes */
    }
    router = new RouterStub();
    ltl = new LtlServiceStub();
    TestBed.configureTestingModule({
      providers: [
        DemoDirectorService,
        { provide: Router, useValue: router },
        { provide: LtlService, useValue: ltl },
      ],
    });
    service = TestBed.inject(DemoDirectorService);
  });

  afterEach(() => {
    // Stops any in-flight poll loop kicked off by enter().
    service.exit();
  });

  it('starts inactive and paused on step 0', () => {
    expect(service.active()).toBeFalse();
    expect(service.playing()).toBeFalse();
    expect(service.index()).toBe(0);
    expect(service.total()).toBe(DEMO_DIRECTOR_SCRIPT.length);
  });

  it('start(true) activates and plays from step 0', () => {
    service.start(true);
    expect(service.active()).toBeTrue();
    expect(service.playing()).toBeTrue();
    expect(service.index()).toBe(0);
    expect(service.caption()).toBe(DEMO_DIRECTOR_SCRIPT[0].caption);
  });

  it('start(false) activates but stays paused for manual stepping', () => {
    service.start(false);
    expect(service.active()).toBeTrue();
    expect(service.playing()).toBeFalse();
  });

  it('next() advances the index and finish()es at the end', () => {
    service.start(false);
    service.next();
    expect(service.index()).toBe(1);
    // Jump to the last step, then next() finishes.
    for (let i = service.index(); i < service.total() - 1; i++) service.next();
    expect(service.index()).toBe(service.total() - 1);
    service.next();
    expect(service.finished()).toBeTrue();
    expect(service.playing()).toBeFalse();
    expect(service.status()).toBe('done');
  });

  it('prev() is clamped at step 0', () => {
    service.start(false);
    service.prev();
    expect(service.index()).toBe(0);
  });

  it('setSpeed clamps to the allowed set', () => {
    service.setSpeed(2);
    expect(service.speed()).toBe(2);
    service.setSpeed(999);
    expect(service.speed()).toBe(1);
  });

  it('start navigates to the first step route when not already there', () => {
    router.url = '/ltl/loads';
    service.start(false);
    expect(router.navigated).toContain(DEMO_DIRECTOR_SCRIPT[0].route!);
  });

  it('exit() tears down active + playing state', () => {
    service.start(true);
    service.exit();
    expect(service.active()).toBeFalse();
    expect(service.playing()).toBeFalse();
    expect(service.spotlight()).toBeNull();
  });

  it('replay() restarts from step 0 after finishing', () => {
    service.start(false);
    for (let i = 0; i < service.total(); i++) service.next();
    expect(service.finished()).toBeTrue();
    service.play(); // play() replays when finished
    expect(service.finished()).toBeFalse();
    expect(service.index()).toBe(0);
    expect(service.active()).toBeTrue();
  });

  it('counter renders a 1-based x / total string', () => {
    service.start(false);
    expect(service.counter()).toBe(`1 / ${service.total()}`);
    service.next();
    expect(service.counter()).toBe(`2 / ${service.total()}`);
  });

  it('narration defaults on and toggling flips + persists the preference', () => {
    expect(service.narrationEnabled()).toBeTrue();
    service.toggleNarration();
    expect(service.narrationEnabled()).toBeFalse();
    try {
      expect(localStorage.getItem(DIRECTOR_NARRATION_KEY)).toBe('false');
    } catch {
      /* storage unavailable — the signal state is still authoritative */
    }
    service.toggleNarration();
    expect(service.narrationEnabled()).toBeTrue();
  });

  it('a persisted "false" preference is honoured on construction', () => {
    try {
      localStorage.setItem(DIRECTOR_NARRATION_KEY, 'false');
    } catch {
      return; // storage unavailable — nothing to assert
    }
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        DemoDirectorService,
        { provide: Router, useValue: new RouterStub() },
        { provide: LtlService, useValue: new LtlServiceStub() },
      ],
    });
    const fresh = TestBed.inject(DemoDirectorService);
    expect(fresh.narrationEnabled()).toBeFalse();
    fresh.exit();
  });

  it('start() resolves a live load via the authenticated LtlService (LTL-only search)', () => {
    service.start(false);
    expect(ltl.queries.length).toBe(1);
    // The resolver asks for a small LTL-only page — the same authenticated read path as every page.
    expect(ltl.queries[0]).toEqual(jasmine.objectContaining({ ltlOnly: true }));
  });

  it('start() never throws when the live-load read fails — steps fall back to static demo values', () => {
    ltl.response = throwError(() => new Error('transient alvys read failure'));
    expect(() => service.start(false)).not.toThrow();
    expect(service.active()).toBeTrue();
    expect(service.index()).toBe(0);
  });

  it('replay() re-resolves the live load context', () => {
    service.start(false);
    for (let i = 0; i < service.total(); i++) service.next();
    expect(service.finished()).toBeTrue();
    ltl.queries = [];
    service.replay();
    expect(ltl.queries.length).toBe(1);
  });
});
