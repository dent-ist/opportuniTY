import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { App } from './app';
import { routes } from './app.routes';
import { provideOpportunityHttp } from './core/api/http';
import { Home } from './home';
import { expectNoAxeViolations } from './ui/testing/axe.testing';

describe('App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), ...provideOpportunityHttp(), provideHttpClientTesting()],
    });
  });

  it('creates the root component with the toast region', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('opp-toast-region')).not.toBeNull();
  });

  it('renders the start page accessibly', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/', Home);
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('opportuniTY');
    await expectNoAxeViolations(harness.routeNativeElement!);
  });

  it('serves the component showcase on the dev route', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/dev/components');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain(
      'Component showcase',
    );
  });
});
