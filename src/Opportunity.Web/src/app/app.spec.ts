import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { App } from './app';
import { provideAppRouting } from './app.config';
import { provideOpportunityHttp } from './core/api/http';

describe('App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), provideHttpClientTesting()],
    });
  });

  it('creates the root component with the toast region', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('opp-toast-region')).not.toBeNull();
  });

  it('serves the component showcase on the dev route without a session', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/dev/components');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain(
      'Component showcase',
    );
    expect(document.title).toBe('Component showcase · opportuniTY');
  });
});
