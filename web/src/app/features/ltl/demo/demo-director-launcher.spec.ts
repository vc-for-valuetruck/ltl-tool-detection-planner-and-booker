import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { DemoDirectorLauncher } from './demo-director-launcher';
import { DemoDirectorService } from './demo-director.service';

class RouterStub {
  url = '/ltl/demo/director';
  navigateByUrl(): Promise<boolean> {
    return Promise.resolve(true);
  }
}

function configure(queryParams: Record<string, string>) {
  TestBed.configureTestingModule({
    imports: [DemoDirectorLauncher],
    providers: [
      DemoDirectorService,
      { provide: Router, useValue: new RouterStub() },
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
      },
    ],
  });
}

describe('DemoDirectorLauncher', () => {
  afterEach(() => {
    TestBed.inject(DemoDirectorService).exit();
  });

  it('autostarts the walkthrough when ?autostart=1', () => {
    configure({ autostart: '1' });
    const fixture = TestBed.createComponent(DemoDirectorLauncher);
    fixture.detectChanges();
    const director = TestBed.inject(DemoDirectorService);
    expect(director.active()).toBeTrue();
    expect(director.playing()).toBeTrue();
  });

  it('stays idle without autostart, exposing the act outline', () => {
    configure({});
    const fixture = TestBed.createComponent(DemoDirectorLauncher);
    fixture.detectChanges();
    const director = TestBed.inject(DemoDirectorService);
    expect(director.active()).toBeFalse();
    expect(fixture.nativeElement.querySelector('[data-testid="director-start"]')).not.toBeNull();
  });

  it('applies a ?speed override', () => {
    configure({ speed: '2' });
    const fixture = TestBed.createComponent(DemoDirectorLauncher);
    fixture.detectChanges();
    expect(TestBed.inject(DemoDirectorService).speed()).toBe(2);
  });
});
