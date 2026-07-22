import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AiNarrativeComponent } from './ai-narrative.component';
import { AiNarrative } from './ai-narrative.service';
import { RUNTIME_CONFIG } from '../../../runtime-config';

const NARRATIVE_URL = '/api/ai/consolidation/narrative';

const BODY: AiNarrative = {
  whyReview: 'Two Laredo→Dallas loads share a receiver and lane.',
  whatToVerify: 'Confirm pallet counts and the 45,000 lb trailer limit at the dock.',
  nextAction: 'Generate the click card once weights are verified.',
  citations: ['Load L-1', 'Load L-2', 'Corridor LAREDO_TO_DALLAS'],
};

describe('AiNarrativeComponent', () => {
  let fixture: ComponentFixture<AiNarrativeComponent>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AiNarrativeComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: RUNTIME_CONFIG,
          useValue: { tenantId: '', clientId: '', apiScope: '', apiBaseUrl: '/api' },
        },
      ],
    });
    fixture = TestBed.createComponent(AiNarrativeComponent);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function start(planId = 'plan-1') {
    fixture.componentRef.setInput('planId', planId);
    fixture.detectChanges();
  }

  function expectRequest() {
    const req = http.expectOne((r) => r.url === NARRATIVE_URL);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('planId')).toBe('plan-1');
    return req;
  }

  function text(testid: string): string | null {
    const el = fixture.nativeElement.querySelector(`[data-testid="${testid}"]`);
    return el ? (el.textContent ?? '').trim() : null;
  }

  it('shows the loading skeleton before a response arrives', () => {
    start();
    expectRequest();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative-skeleton"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative"]')).toBeNull();
  });

  it('renders three labelled rows and citation chips on 200', () => {
    start();
    expectRequest().flush(BODY, {
      headers: { 'X-Ai-Source': 'llm', 'X-Ai-Cached': 'false' },
    });
    fixture.detectChanges();

    expect(text('ai-narrative-why-label')).toBe('Why review');
    expect(text('ai-narrative-verify-label')).toBe('What to verify');
    expect(text('ai-narrative-next-label')).toBe('Next action');

    expect(text('ai-narrative-why')).toBe(BODY.whyReview);
    expect(text('ai-narrative-verify')).toBe(BODY.whatToVerify);
    expect(text('ai-narrative-next')).toBe(BODY.nextAction);

    const chips = fixture.nativeElement.querySelectorAll(
      '[data-testid="ai-narrative-citations"] .ai-narrative-chip',
    );
    expect(chips.length).toBe(3);
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative-skeleton"]')).toBeNull();
  });

  it('surfaces the X-Ai-Source / X-Ai-Cached provenance on 200', () => {
    start();
    expectRequest().flush(BODY, {
      headers: { 'X-Ai-Source': 'cache', 'X-Ai-Cached': 'true' },
    });
    fixture.detectChanges();
    expect(text('ai-narrative-source')).toBe('cache · cached');
  });

  it('renders nothing on 404 disabled', () => {
    start();
    expectRequest().flush({ reason: 'disabled' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative-skeleton"]')).toBeNull();
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('renders nothing on 404 plan-not-found', () => {
    start();
    expectRequest().flush({ reason: 'plan-not-found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative"]')).toBeNull();
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('renders nothing on 503 ai-unavailable', () => {
    start();
    expectRequest().flush(
      { reason: 'ai-unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative"]')).toBeNull();
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('renders nothing on a network error', () => {
    start();
    expectRequest().error(new ProgressEvent('error'));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative"]')).toBeNull();
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('renders nothing when the request exceeds the 2s timeout', fakeAsync(() => {
    start();
    const req = http.expectOne((r) => r.url === NARRATIVE_URL);
    tick(2000);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-narrative"]')).toBeNull();
    expect(fixture.nativeElement.textContent.trim()).toBe('');
    // The RxJS timeout unsubscribes, so HttpClient cancels the outstanding request.
    expect(req.cancelled).toBeTrue();
  }));
});
