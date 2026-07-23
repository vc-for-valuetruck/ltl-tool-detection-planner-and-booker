import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { DemoDirectorOverlay } from './demo-director-overlay';
import { DemoDirectorService } from './demo-director.service';

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
      providers: [DemoDirectorService, { provide: Router, useValue: new RouterStub() }],
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
});
