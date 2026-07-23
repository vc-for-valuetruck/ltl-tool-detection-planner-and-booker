import { DemoContext } from './demo-director.models';
import { DEMO_DIRECTOR_SCRIPT } from './demo-director.script';

/**
 * Guards the walkthrough script's shape + honesty invariants (CLAUDE.md safety principles). The
 * runtime behaviour is covered by demo-director.service.spec.ts; this locks the *content* so a
 * future edit can't quietly drop the gated-write posture or the graceful-degrade flags.
 */
describe('DEMO_DIRECTOR_SCRIPT', () => {
  it('has unique step ids and non-empty captions', () => {
    const ids = new Set<string>();
    for (const step of DEMO_DIRECTOR_SCRIPT) {
      expect(step.id.length).toBeGreaterThan(0);
      expect(step.caption.trim().length).toBeGreaterThan(0);
      expect(ids.has(step.id)).withContext(`duplicate id ${step.id}`).toBeFalse();
      ids.add(step.id);
    }
  });

  it('covers all five acts in order', () => {
    const order: string[] = [];
    for (const step of DEMO_DIRECTOR_SCRIPT) {
      if (order[order.length - 1] !== step.act) order.push(step.act);
    }
    expect(order).toEqual(['Dock', 'Consolidate', 'Loads & Dispatch', 'Back Office', 'Lifecycle']);
  });

  it('every action step names a target selector', () => {
    for (const step of DEMO_DIRECTOR_SCRIPT) {
      if (!step.action) continue;
      if (step.action === 'fillMany') {
        expect(step.fields && step.fields.length > 0)
          .withContext(`${step.id} fillMany needs fields`)
          .toBeTrue();
      } else {
        expect((step.actionSelector ?? step.target ?? '').length)
          .withContext(`${step.id} needs an action selector`)
          .toBeGreaterThan(0);
      }
    }
  });

  it('gated-write steps carry an honest posture note', () => {
    const combine = DEMO_DIRECTOR_SCRIPT.find((s) => s.id === 'dock-combine');
    const assemble = DEMO_DIRECTOR_SCRIPT.find((s) => s.id === 'dispatch-assemble');
    expect(combine?.posture).toMatch(/NotPerformed|gated/i);
    expect(assemble?.posture).toMatch(/NotPerformed|gated|override/i);
  });

  it('steps whose live data may be empty are optional so the tour degrades gracefully', () => {
    const mayBeEmpty = ['dock-pick-yard', 'consolidate-candidates', 'dispatch-candidates', 'billing'];
    for (const id of mayBeEmpty) {
      const step = DEMO_DIRECTOR_SCRIPT.find((s) => s.id === id);
      expect(step?.optional).withContext(`${id} should be optional`).toBeTrue();
    }
  });

  const liveCtx = (over: Partial<DemoContext> = {}): DemoContext => ({
    loadNumber: 'LIVE-9001',
    loadId: 'id-9001',
    originCity: 'El Paso',
    originState: 'TX',
    destinationCity: 'Phoenix',
    destinationState: 'AZ',
    laneLabel: 'El Paso, TX → Phoenix, AZ',
    laneOpenCount: 7,
    totalOpen: 432,
    topLanes: [],
    customerName: null,
    ...over,
  });

  it('dispatch lane drives a real live load lane, falling back to the static demo lane', () => {
    const step = DEMO_DIRECTOR_SCRIPT.find((s) => s.id === 'dispatch-lane')!;
    // A complete live lane overrides the hardcoded Laredo→Dallas fields.
    const live = step.resolveFields!(liveCtx());
    expect(live).toEqual([
      { selector: '#da-ocity', value: 'El Paso' },
      { selector: '#da-ostate', value: 'TX' },
      { selector: '#da-dcity', value: 'Phoenix' },
      { selector: '#da-dstate', value: 'AZ' },
    ]);
    // An incomplete lane falls back (null) to the static demo fields the step still declares.
    expect(step.resolveFields!(liveCtx({ destinationState: null }))).toBeNull();
    expect(step.fields && step.fields.length).toBe(4);
  });

  it('injects the live busiest-lane roll-up into the Consolidate narration', () => {
    const step = DEMO_DIRECTOR_SCRIPT.find((s) => s.id === 'consolidate-open')!;
    const spoken = step.resolveCaption!(
      liveCtx({
        totalOpen: 432,
        topLanes: [
          {
            label: 'Tijuana, BCN → Johnstown, OH',
            originCity: 'Tijuana',
            originState: 'BCN',
            destinationCity: 'Johnstown',
            destinationState: 'OH',
            openLoadCount: 10,
          },
        ],
      }),
    );
    expect(spoken).toContain('Tijuana, BCN → Johnstown, OH');
    expect(spoken).toContain('10');
    // With no live lanes the step keeps its static caption (returns null).
    expect(step.resolveCaption!(liveCtx({ topLanes: [] }))).toBeNull();
  });
});
