import type { DocumentFieldValueResource } from '../../../../core/api/generated/models';
import { formatterFor } from '../../../../core/i18n';
import { displayValue } from './metadata-view';

const AT = '2026-03-14T15:04:05Z';

describe('Metadata values (Q-28 raw imported string)', () => {
  const field = (extra: Partial<DocumentFieldValueResource>) =>
    ({
      fieldId: 1,
      displayName: 'Date Sent',
      format: 'dateTime',
      value: AT,
      displayValue: '2026-03-14 15:04:05 +00:00',
      rawValue: null,
      ...extra,
    }) as DocumentFieldValueResource;

  it('formats instants, dates, numbers and sizes in the user display settings', () => {
    const f = formatterFor('en-GB', 'Europe/Berlin');
    expect(displayValue(field({}), f)).toBe('14/03/2026, 16:04 CET');
    expect(displayValue(field({ format: 'date', value: '2026-03-14' }), f)).toBe('14/03/2026');
    expect(displayValue(field({ format: 'number', value: 12345 }), f)).toBe('12,345');
    expect(displayValue(field({ format: 'bytes', value: 2048 }), f)).toBe('2 KB');
    expect(displayValue(field({ format: 'text', value: 'x', displayValue: 'x' }), f)).toBe('x');
    expect(displayValue(field({ value: [AT, AT] }), f)).toBe(
      '14/03/2026, 16:04 CET; 14/03/2026, 16:04 CET',
    );
  });
});
