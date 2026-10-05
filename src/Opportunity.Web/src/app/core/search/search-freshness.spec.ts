import {
  footnoteText,
  fromServed,
  generationText,
  lagText,
  pillText,
  toIndexFreshness,
  toServedFreshness,
} from './search-freshness';

describe('search freshness (wave-10 contract, Q-10)', () => {
  it('reads the served freshness, including the M1 shape without a state', () => {
    expect(
      toServedFreshness({
        asOf: '2026-10-04T10:42:00Z',
        current: false,
        servedGeneration: '18432',
        state: 'updating',
        indexedThroughGeneration: 18432,
        pendingChanges: '85',
        lagSeconds: 12.4,
      }),
    ).toEqual({
      state: 'updating',
      servedGeneration: '18432',
      indexedThroughGeneration: '18432',
      pendingChanges: 85,
      lagSeconds: 12.4,
      asOf: '2026-10-04T10:42:00Z',
    });
    expect(toServedFreshness({ asOf: 'x', current: true, servedGeneration: null }).state).toBe(
      'current',
    );
    expect(toServedFreshness({ asOf: 'x', current: null, servedGeneration: null }).state).toBe(
      'updating',
    );
    expect(toServedFreshness({ current: false, lagSeconds: 400 }).state).toBe('delayed');
    // A current result never reports pending work.
    expect(toServedFreshness({ state: 'current', pendingChanges: 3, lagSeconds: 2 })).toMatchObject(
      { pendingChanges: 0, lagSeconds: 0 },
    );
  });

  it('reads GET …/search-freshness and derives an index state from a served result', () => {
    const index = toIndexFreshness({
      state: 'delayed',
      indexedThroughGeneration: '18432',
      latestGeneration: '18517',
      pendingChanges: 1240,
      lagSeconds: 185,
      asOf: '2026-10-04T10:42:05Z',
    });
    expect(index).toEqual({
      state: 'delayed',
      indexedThroughGeneration: '18432',
      latestGeneration: '18517',
      pendingChanges: 1240,
      lagSeconds: 185,
      asOf: '2026-10-04T10:42:05Z',
    });
    expect(toIndexFreshness({ state: 'bogus', current: true }).state).toBe('current');
    const served = toServedFreshness({ asOf: 'a', current: true, servedGeneration: 7 });
    expect(fromServed(served)).toMatchObject({
      state: 'current',
      indexedThroughGeneration: '7',
      latestGeneration: null,
    });
  });

  it('says it in plain language: pill, lag and footnote', () => {
    expect(pillText({ state: 'current', pendingChanges: 0, lagSeconds: 0 }, 'en-US')).toBe(
      'Current',
    );
    expect(pillText({ state: 'updating', pendingChanges: 1240, lagSeconds: 12 }, 'en-US')).toBe(
      'Updating · ≈ 1,240 changes pending · ~12 s behind',
    );
    expect(pillText({ state: 'updating', pendingChanges: 0, lagSeconds: 0 }, 'en-US')).toBe(
      'Updating',
    );
    expect(pillText({ state: 'delayed', pendingChanges: 9, lagSeconds: 300 }, 'en-US')).toBe(
      'Delayed · ~5 min behind',
    );
    expect(pillText({ state: 'delayed', pendingChanges: 9, lagSeconds: 0 }, 'en-US')).toBe(
      'Delayed · more than 2 min behind',
    );
    expect(lagText(0.2)).toBe('~1 s behind');
    expect(lagText(7200)).toBe('~2 h behind');
    expect(footnoteText(85, 'en-US')).toBe('Counts may not include 85 recent changes.');
    expect(footnoteText(1, 'en-US')).toBe('Counts may not include 1 recent change.');
    expect(footnoteText(0, 'en-US')).toBe('Counts may not include recent changes.');
    expect(generationText('18432', 'en-US')).toBe('18,432');
    expect(generationText('99999999999999999999', 'en-US')).toBe('99999999999999999999');
    expect(generationText(null, 'en-US')).toBe('—');
  });
});
