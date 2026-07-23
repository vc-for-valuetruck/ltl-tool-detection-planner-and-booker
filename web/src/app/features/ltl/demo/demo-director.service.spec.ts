import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { DemoDirectorService } from './demo-director.service';
import { DEMO_DIRECTOR_SCRIPT } from './demo-director.script';

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

describe('DemoDirectorService', () => {
  let service: DemoDirectorService;
  let router: RouterStub;

  beforeEach(() => {
    router = new RouterStub();
    TestBed.configureTestingModule({
      providers: [DemoDirectorService, { provide: Router, useValue: router }],
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
});
