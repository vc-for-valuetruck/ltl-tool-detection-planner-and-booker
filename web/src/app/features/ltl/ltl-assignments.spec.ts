import { LtlAssignments } from './ltl-assignments';
import { LtlService } from './ltl.service';
import { AssignmentAudit, AssignmentAuditQuery, AssignmentReasonType } from './ltl.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function audit(partial: Partial<AssignmentAudit>): AssignmentAudit {
  return {
    id: 'a1',
    loadId: 'L1',
    driverId: null,
    truckId: null,
    trailerId: null,
    matchScore: null,
    matchLabel: null,
    notes: null,
    reasonType: 'Unspecified',
    overrideReason: null,
    warnings: [],
    recordedBy: 'dispatcher@valuetruck.com',
    recordedAt: '2026-07-21T10:00:00Z',
    alvysWriteback: 'NotPerformed',
    ...partial,
  };
}

describe('LtlAssignments', () => {
  function build(stub: Partial<LtlService>): LtlAssignments {
    TestBed.configureTestingModule({
      providers: [{ provide: LtlService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlAssignments());
  }

  it('loads history on init and exposes the rows', () => {
    const c = build({
      assignmentHistory: () => of([audit({ id: 'a1' }), audit({ id: 'a2', loadId: 'L2' })]),
    });
    c.ngOnInit();
    expect(c['rows']().length).toBe(2);
    expect(c['loading']()).toBeFalse();
    expect(c['hasRows']()).toBeTrue();
  });

  it('passes the user/day/reason filters to the API', () => {
    const seen: AssignmentAuditQuery[] = [];
    const c = build({
      assignmentHistory: (query) => {
        seen.push(query ?? {});
        return of([]);
      },
    });
    c['user'].set('bob@valuetruck.com');
    c['day'].set('2026-07-21');
    c['reasonType'].set('CustomerRequest');
    c['load']();
    expect(seen.at(-1)).toEqual({
      user: 'bob@valuetruck.com',
      day: '2026-07-21',
      reasonType: 'CustomerRequest',
    });
  });

  it('omits blank filters so the query stays undefined, not empty strings', () => {
    const seen: AssignmentAuditQuery[] = [];
    const c = build({
      assignmentHistory: (query) => {
        seen.push(query ?? {});
        return of([]);
      },
    });
    c['load']();
    expect(seen.at(-1)).toEqual({ user: undefined, day: undefined, reasonType: undefined });
  });

  it('clears filters and refetches', () => {
    let calls = 0;
    const c = build({
      assignmentHistory: () => {
        calls++;
        return of([]);
      },
    });
    c['user'].set('someone');
    c['reasonType'].set('Other');
    c['clearFilters']();
    expect(c['user']()).toBe('');
    expect(c['reasonType']()).toBe('');
    expect(calls).toBe(1);
  });

  it('surfaces an error and clears loading', () => {
    const c = build({ assignmentHistory: () => throwError(() => ({ message: 'down' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('down');
    expect(c['loading']()).toBeFalse();
  });

  it('maps typed reasons to human labels and falls back to the raw value', () => {
    const c = build({ assignmentHistory: () => of([]) });
    expect(c['reasonLabel']('ServiceRecovery')).toBe('Service recovery');
    expect(c['reasonLabel']('Unspecified')).toBe('Unspecified');
    expect(c['reasonLabel']('Mystery' as AssignmentReasonType)).toBe('Mystery');
  });

  it('renders missing equipment as an em dash, never invented', () => {
    const c = build({ assignmentHistory: () => of([]) });
    expect(c['equipment'](audit({}))).toBe('—');
    expect(c['equipment'](audit({ driverId: 'DR1', trailerId: 'TR1' }))).toBe('DR1 · TR1');
  });
});
