import { TestBed } from '@angular/core/testing';
import { Component } from '@angular/core';
import { LtlStatusChip, StatusChipCategory, statusChipCategory } from './ltl-status-chip';

describe('statusChipCategory', () => {
  const cases: Array<[string, StatusChipCategory]> = [
    // In-transit / movement -> blue
    ['In Transit', 'transit'],
    ['In-Transit', 'transit'],
    ['En Route', 'transit'],
    ['Picked Up', 'transit'],
    ['At Shipper', 'transit'],
    ['At Consignee', 'transit'],
    ['Loaded', 'transit'],
    ['Out for Delivery', 'transit'], // has "deliver" but transit wins (blue, not green)

    // Delivered / complete -> green
    ['Delivered', 'delivered'],
    ['Completed', 'delivered'],
    ['POD Received', 'delivered'],
    ['Empty', 'delivered'],

    // Open / available / pre-assignment -> slate
    ['Open', 'open'],
    ['Available', 'open'],
    ['New', 'open'],
    ['Planning', 'open'],
    ['Tendered', 'open'],
    ['Unassigned', 'open'], // "assign" substring must NOT win here

    // Assigned / covered / dispatched -> indigo
    ['Assigned', 'assigned'],
    ['Covered', 'assigned'],
    ['Dispatched', 'assigned'],
    ['Booked', 'assigned'],

    // At-risk / warning -> amber
    ['At Risk', 'risk'],
    ['Delayed', 'risk'],
    ['Running Late', 'risk'],
    ['On Hold', 'risk'],
    ['Detention', 'risk'],

    // Exception / blocked / cancelled -> red
    ['Exception', 'exception'],
    ['Blocked', 'exception'],
    ['Cancelled', 'exception'],
    ['Canceled', 'exception'],
    ['TONU', 'exception'],
    ['Rejected', 'exception'],

    // Billing lifecycle -> purple
    ['Bill', 'billing'],
    ['Billed', 'billing'],
    ['Billing', 'billing'],
    ['Ready to Bill', 'billing'],
    ['Invoiced', 'billing'],
  ];

  for (const [input, expected] of cases) {
    it(`maps "${input}" to ${expected}`, () => {
      expect(statusChipCategory(input)).toBe(expected);
    });
  }

  it('is case- and whitespace-insensitive', () => {
    expect(statusChipCategory('  in transit  ')).toBe('transit');
    expect(statusChipCategory('DELIVERED')).toBe('delivered');
  });

  it('falls back to unknown for null, blank, and unrecognised statuses', () => {
    expect(statusChipCategory(null)).toBe('unknown');
    expect(statusChipCategory(undefined)).toBe('unknown');
    expect(statusChipCategory('')).toBe('unknown');
    expect(statusChipCategory('   ')).toBe('unknown');
    expect(statusChipCategory('Zorptastic')).toBe('unknown');
    expect(statusChipCategory('Match')).toBe('unknown');
  });
});

@Component({
  standalone: true,
  imports: [LtlStatusChip],
  template: `<app-ltl-status-chip [status]="value" />`,
})
class HostComponent {
  value: string | null = 'In Transit';
}

describe('LtlStatusChip component', () => {
  function render(value: string | null) {
    const fixture = TestBed.configureTestingModule({ imports: [HostComponent] }).createComponent(
      HostComponent,
    );
    fixture.componentInstance.value = value;
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('.status-chip') as HTMLElement;
  }

  it('applies the category class and renders the raw status text', () => {
    const chip = render('In Transit');
    expect(chip.classList).toContain('status-chip--transit');
    expect(chip.getAttribute('data-status-category')).toBe('transit');
    expect(chip.textContent?.trim()).toBe('In Transit');
  });

  it('renders a neutral em dash for a blank status without guessing', () => {
    const chip = render(null);
    expect(chip.classList).toContain('status-chip--unknown');
    expect(chip.textContent?.trim()).toBe('—');
  });
});
