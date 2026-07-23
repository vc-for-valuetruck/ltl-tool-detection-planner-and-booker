import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of, throwError } from 'rxjs';
import {
  DispatchAssembly,
  DispatchCandidate,
  DispatchRecommendationsResponse,
} from './dispatch-assist.models';
import { DispatchAssistService } from './dispatch-assist.service';
import { DispatchAssist } from './dispatch-assist';

function candidate(partial: Partial<DispatchCandidate> = {}): DispatchCandidate {
  return {
    driverId: 'D-1',
    driverName: 'Pat Driver',
    driverEmail: 'pat@fleet.test',
    driverPhone: null,
    driverHomeState: 'TX',
    dutyStatus: 'Active',
    truckId: 'T-1',
    truckNumber: '214',
    trailerId: null,
    trailerNumber: null,
    trailerEquipmentType: null,
    preferredDispatcherId: 'U-9',
    isPreferredPairing: true,
    referenceMilesFromOrigin: 60,
    score: 84,
    reasons: ['preferred pairing with TRK 214', 'home base TX matches origin state'],
    ...partial,
  };
}

function recommendations(
  partial: Partial<DispatchRecommendationsResponse> = {},
): DispatchRecommendationsResponse {
  return {
    target: {
      loadId: 'L-1',
      loadNumber: '100482',
      originCity: 'Laredo',
      originState: 'TX',
      destinationCity: 'Dallas',
      destinationState: 'TX',
      requiredEquipment: ['Dry Van'],
      source: 'Alvys load (read-only).',
    },
    candidates: partial.candidates ?? [candidate()],
    truncated: partial.truncated ?? false,
    alvysPosture: 'Read-only.',
    ...partial,
  };
}

function assembly(partial: Partial<DispatchAssembly> = {}): DispatchAssembly {
  return {
    id: 'asm-1',
    recordedAt: '2026-07-23T10:00:00Z',
    recordedBy: 'dispatcher@valuetruck.com',
    loadId: 'L-1',
    loadNumber: '100482',
    driverId: 'D-1',
    truckId: 'T-1',
    trailerId: null,
    score: 84,
    reasons: [],
    alvysWriteback: 'NotPerformed',
    notify: {
      sent: false,
      state: 'NotEnabled',
      overrideActive: true,
      overrideRecipient: 'joshua.davis@valuetruck.com',
      intendedRecipients: [{ role: 'driver', name: 'Pat Driver', address: 'pat@fleet.test' }],
      effectiveRecipients: [],
      detail: null,
    },
    ...partial,
  };
}

describe('DispatchAssist', () => {
  function build(stub: Partial<DispatchAssistService>, presetLoadId: string | null = null): DispatchAssist {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: DispatchAssistService, useValue: stub },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: () => presetLoadId } } },
        },
      ],
    });
    return TestBed.runInInjectionContext(() => new DispatchAssist());
  }

  it('does not search on init without a preset load id', () => {
    const rec = jasmine.createSpy('recommendations');
    const c = build({ recommendations: rec });
    c.ngOnInit();
    expect(rec).not.toHaveBeenCalled();
  });

  it('deep-links: a preset loadId prefills and runs the search', () => {
    const c = build({ recommendations: () => of(recommendations()) }, '100482');
    c.ngOnInit();
    expect(c['loadId']()).toBe('100482');
    expect(c['candidates']().length).toBe(1);
    expect(c['target']()?.loadNumber).toBe('100482');
  });

  it('requires at least a load id or an origin before searching', () => {
    const c = build({});
    expect(c['canSearch']()).toBeFalse();
    c['originState'].set('TX');
    expect(c['canSearch']()).toBeTrue();
  });

  it('populates candidates and target on a successful search', () => {
    const c = build({ recommendations: () => of(recommendations()) });
    c['loadId'].set('100482');
    c['search']();
    expect(c['loading']()).toBeFalse();
    expect(c['error']()).toBeNull();
    expect(c['candidates']().length).toBe(1);
  });

  it('shows an honest 404 message when a load cannot be resolved', () => {
    const c = build({ recommendations: () => throwError(() => ({ status: 404 })) });
    c['loadId'].set('nope');
    c['search']();
    expect(c['error']()).toContain('could not be resolved');
    expect(c['loading']()).toBeFalse();
  });

  it('records an assembly and exposes the notify + override banner state', () => {
    const c = build({
      recommendations: () => of(recommendations()),
      assemble: () => of(assembly()),
    });
    c['loadId'].set('100482');
    c['search']();
    c['assemble'](candidate());
    const a = c['lastAssembly']();
    expect(a?.id).toBe('asm-1');
    expect(a?.alvysWriteback).toBe('NotPerformed');
    expect(a?.notify.overrideActive).toBeTrue();
    expect(c['assemblingId']()).toBeNull();
  });

  it('surfaces an assemble failure without a fabricated success', () => {
    const c = build({
      recommendations: () => of(recommendations()),
      assemble: () => throwError(() => ({ status: 500 })),
    });
    c['loadId'].set('100482');
    c['search']();
    c['assemble'](candidate());
    expect(c['assembleError']()).toContain('Could not record');
    expect(c['lastAssembly']()).toBeNull();
  });

  it('bands scores and tones notify states for the UI', () => {
    const c = build({});
    expect(c['scoreBand'](90)).toBe('excellent');
    expect(c['scoreBand'](65)).toBe('good');
    expect(c['scoreBand'](45)).toBe('possible');
    expect(c['scoreBand'](20)).toBe('review');
    expect(c['notifyTone']('Sent')).toBe('ok');
    expect(c['notifyTone']('Failed')).toBe('bad');
    expect(c['notifyTone']('NotEnabled')).toBe('muted');
  });
});
