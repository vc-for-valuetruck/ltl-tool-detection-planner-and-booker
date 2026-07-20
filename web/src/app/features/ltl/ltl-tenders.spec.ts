import { LtlTenders } from './ltl-tenders';
import { AlvysTendersService } from './alvys-tenders.service';
import { AlvysTender, AlvysTendersResponse } from './alvys-tenders.models';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

function tender(partial: Partial<AlvysTender>): AlvysTender {
  return { Id: 'T', Status: 'New', ...partial } as AlvysTender;
}

function response(items: AlvysTender[]): AlvysTendersResponse {
  return { Page: 0, PageSize: 100, Total: items.length, Items: items };
}

describe('LtlTenders', () => {
  function build(stub: Partial<AlvysTendersService>): LtlTenders {
    TestBed.configureTestingModule({
      providers: [{ provide: AlvysTendersService, useValue: stub }],
    });
    return TestBed.runInInjectionContext(() => new LtlTenders());
  }

  const inMinutes = (m: number) => new Date(Date.now() + m * 60000).toISOString();

  it('loads tenders on init and exposes the rows', () => {
    const c = build({ search: () => of(response([tender({ Id: 'A' })])) });
    c.ngOnInit();
    expect(c['rows']().length).toBe(1);
    expect(c['loading']()).toBeFalse();
    expect(c['hasRows']()).toBeTrue();
  });

  it('orders rows by expiration soonest-first, unknown-expiry last', () => {
    const c = build({
      search: () =>
        of(
          response([
            tender({ Id: 'late', ExpirationDate: { DateTime: inMinutes(300) } }),
            tender({ Id: 'none' }),
            tender({ Id: 'soon', ExpirationDate: { DateTime: inMinutes(30) } }),
          ]),
        ),
    });
    c.ngOnInit();
    expect(c['rows']().map((r) => r.tender.Id)).toEqual(['soon', 'late', 'none']);
  });

  it('flags tenders expiring within the hour as danger and counts them', () => {
    const c = build({
      search: () =>
        of(
          response([
            tender({ Id: 'soon', ExpirationDate: { DateTime: inMinutes(20) } }),
            tender({ Id: 'mid', ExpirationDate: { DateTime: inMinutes(120) } }),
          ]),
        ),
    });
    c.ngOnInit();
    const soon = c['rows']().find((r) => r.tender.Id === 'soon')!;
    const mid = c['rows']().find((r) => r.tender.Id === 'mid')!;
    expect(c['urgency'](soon)).toBe('danger');
    expect(c['urgency'](mid)).toBe('warn');
    expect(c['expiringSoonCount']()).toBe(1);
  });

  it('derives customer (BillTo) and lane from entities/stops', () => {
    const c = build({
      search: () =>
        of(
          response([
            tender({
              Id: 'X',
              Entities: [
                { N1Qualifier: 'BT', Name: 'ACME CO' },
                { N1Qualifier: 'SF', City: 'Irving', State: 'TX' },
                { N1Qualifier: 'ST', City: 'Oklahoma City', State: 'OK' },
              ],
              Stops: [
                { StopId: '1', Type: 'Pickup', Entity: { City: 'Irving', State: 'TX' } },
                { StopId: '2', Type: 'Delivery', Entity: { City: 'Oklahoma City', State: 'OK' } },
              ],
            }),
          ]),
        ),
    });
    c.ngOnInit();
    const row = c['rows']()[0];
    expect(row.customer).toBe('ACME CO');
    expect(row.origin).toBe('Irving, TX');
    expect(row.destination).toBe('Oklahoma City, OK');
  });

  it('falls back to ShipmentId for the reference when LoadNumber is absent', () => {
    const c = build({ search: () => of(response([tender({ Id: 'X', ShipmentId: '98448085' })])) });
    c.ngOnInit();
    expect(c['rows']()[0].reference).toBe('98448085');
  });

  it('renders missing weight/rate as an em dash, never zero', () => {
    const c = build({ search: () => of(response([])) });
    expect(c['formatWeight'](null)).toBe('—');
    expect(c['formatRate'](null)).toBe('—');
    expect(c['formatWeight'](42360)).toBe('42,360 lb');
    expect(c['formatRate'](824)).toBe('$824');
  });

  it('surfaces an Alvys error and clears loading', () => {
    const c = build({ search: () => throwError(() => ({ message: 'boom' })) });
    c.ngOnInit();
    expect(c['error']()).toBe('boom');
    expect(c['loading']()).toBeFalse();
    expect(c['hasRows']()).toBeFalse();
  });
});
