import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { OverlayOutletService } from './overlay-outlet.service';

@Component({ standalone: true, template: '' })
class OverlayA {}

@Component({ standalone: true, template: '' })
class OverlayB {}

describe('OverlayOutletService', () => {
  let service: OverlayOutletService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(OverlayOutletService);
  });

  it('starts empty', () => {
    expect(service.component()).toBeNull();
  });

  it('mounts and exposes the registered component', () => {
    service.mount(OverlayA);
    expect(service.component()).toBe(OverlayA);
  });

  it('mount is last-write-wins', () => {
    service.mount(OverlayA);
    service.mount(OverlayB);
    expect(service.component()).toBe(OverlayB);
  });

  it('clear() with no argument unmounts whatever is mounted', () => {
    service.mount(OverlayA);
    service.clear();
    expect(service.component()).toBeNull();
  });

  it('clear(component) only unmounts when it matches the mounted one', () => {
    service.mount(OverlayA);
    // A late teardown for a different component must not stomp the current mount.
    service.clear(OverlayB);
    expect(service.component()).toBe(OverlayA);
    service.clear(OverlayA);
    expect(service.component()).toBeNull();
  });
});
