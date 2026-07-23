import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { LtlService } from '../ltl.service';
import { DemoDirectorOverlay } from './demo-director-overlay';
import { DemoDirectorService } from './demo-director.service';

/** Stubs the one read the director makes at start — an empty live-load result is enough here. */
const ltlStub = {
  search: () => of({ items: [], page: 0, pageSize: 0, total: 0, truncated: false }),
};

class RouterStub {
  url = '/ltl';
  navigateByUrl(): Promise<boolean> {
    return Promise.resolve(true);
  }
}

describe('DemoDirectorOverlay', () => {
  let fixture: ComponentFixture<DemoDirectorOverlay>;
  let director: DemoDirectorService;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DemoDirectorOverlay],
      providers: [
        DemoDirectorService,
        { provide: Router, useValue: new RouterStub() },
        { provide: LtlService, useValue: ltlStub },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DemoDirectorOverlay);
    director = TestBed.inject(DemoDirectorService);
    fixture.detectChanges();
  });

  afterEach(() => {
    director.exit();
    fixture.destroy();
  });

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  it('renders nothing while the director is inactive', () => {
    expect(query('director-overlay')).toBeNull();
  });

  it('renders the caption bar with narration once active', () => {
    director.start(false);
    fixture.detectChanges();
    expect(query('director-overlay')).not.toBeNull();
    expect(query('director-caption')?.textContent?.trim()).toBe(director.caption());
    expect(query('director-step-counter')?.textContent?.trim()).toBe(director.counter());
  });

  it('shows the play control when paused and pause when playing', () => {
    director.start(false);
    fixture.detectChanges();
    expect(query('director-play')).not.toBeNull();
    expect(query('director-pause')).toBeNull();

    director.play();
    fixture.detectChanges();
    expect(query('director-pause')).not.toBeNull();
  });

  it('renders the posture note only for gated-write steps', () => {
    director.start(false);
    // Walk to the dock-combine step which carries a posture note.
    const combineIndex = director.steps.findIndex((s) => s.id === 'dock-combine');
    director.index.set(combineIndex);
    director.posture.set(director.steps[combineIndex].posture ?? null);
    fixture.detectChanges();
    expect(query('director-posture')).not.toBeNull();
  });

  it('shows the animated pointer once a target is measured and hides it otherwise', () => {
    // Give the spotlight a real, sized element to measure so a rect resolves.
    const target = document.createElement('div');
    target.id = 'director-test-target';
    target.style.cssText = 'position:fixed;top:40px;left:60px;width:120px;height:40px;';
    document.body.appendChild(target);
    try {
      director.start(false);
      director.spotlight.set('#director-test-target');
      fixture.detectChanges(); // effect measures the rect
      fixture.detectChanges(); // render the pointer from the measured rect
      expect(query('director-pointer')).not.toBeNull();

      director.spotlight.set(null);
      fixture.detectChanges();
      fixture.detectChanges();
      expect(query('director-pointer')).toBeNull();
    } finally {
      target.remove();
    }
  });

  it('narration toggle flips the label when speech synthesis is available', () => {
    if (!director.narrationAvailable) {
      pending('speechSynthesis unavailable in this environment');
      return;
    }
    director.start(false);
    fixture.detectChanges();
    const toggle = query('director-narration-toggle');
    expect(toggle).not.toBeNull();
    const before = director.narrationEnabled();
    toggle!.click();
    fixture.detectChanges();
    expect(director.narrationEnabled()).toBe(!before);
    expect(toggle!.getAttribute('aria-pressed')).toBe(String(!before));
  });
});
