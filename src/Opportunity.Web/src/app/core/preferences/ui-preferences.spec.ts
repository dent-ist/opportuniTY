import { TestBed } from '@angular/core/testing';
import { PreferenceStorage } from './preference-storage';
import { UiPreferences } from './ui-preferences';

describe('UiPreferences', () => {
  const root = document.documentElement;

  beforeEach(() => {
    localStorage.clear();
    root.removeAttribute('data-theme');
    root.removeAttribute('data-density');
  });

  it('applies theme, density and locale to <html> and persists them', () => {
    const prefs = TestBed.inject(UiPreferences);
    TestBed.tick();
    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(root.getAttribute('data-density')).toBe('comfortable');

    prefs.theme.set('high-contrast');
    prefs.density.set('compact');
    prefs.locale.set('en-GB');
    TestBed.tick();
    expect(root.getAttribute('data-theme')).toBe('high-contrast');
    expect(root.getAttribute('data-density')).toBe('compact');
    expect(root.getAttribute('lang')).toBe('en-GB');
    expect(TestBed.inject(PreferenceStorage).read('ui')).toEqual({
      theme: 'high-contrast',
      density: 'compact',
      locale: 'en-GB',
    });
  });

  it('ignores unknown stored values', () => {
    TestBed.inject(PreferenceStorage).write('ui', { theme: 'neon', density: 'compact' });
    const prefs = TestBed.inject(UiPreferences);
    expect(prefs.theme()).toBe('system');
    expect(prefs.density()).toBe('compact');
  });
});
