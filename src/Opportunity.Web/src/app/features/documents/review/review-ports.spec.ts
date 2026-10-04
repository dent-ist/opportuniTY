import { HttpHeaders, HttpResponse } from '@angular/common/http';
import type { DocumentCodingResource, FieldResource } from '../../../core/api/generated/models';
import { catalogOf, toChange, toCoding } from './review-ports';

const field = (f: Partial<FieldResource>) => f as FieldResource;
const catalog = catalogOf([
  field({
    fieldId: 1000,
    queryName: 'responsiveness',
    type: 'singleChoice',
    choices: [
      { choiceId: 1, name: 'Responsive', isActive: true },
      { choiceId: 2, name: 'Not Responsive', isActive: true },
    ],
  }),
  field({
    fieldId: 1001,
    queryName: 'issues',
    type: 'multiChoice',
    choices: [
      { choiceId: 7, name: 'Pricing', isActive: true },
      { choiceId: 8, name: 'Termination', isActive: true },
    ],
  }),
  field({ fieldId: 1002, queryName: 'notes', type: 'text', choices: null }),
]);

describe('coding API adapter (E10-T01 ↔ Review mode)', () => {
  it('reads field ids and choice ids as query names and choice names', () => {
    const body = {
      documentId: 'd1',
      documentVersion: 5,
      indexingState: 'pending',
      fields: [
        { fieldId: 1000, value: 2 },
        { fieldId: 1001, value: [7, 8] },
        { fieldId: 1002, value: null },
        { fieldId: 1003, value: 'not in the catalogue' },
      ],
    } as unknown as DocumentCodingResource;
    const coding = toCoding(
      new HttpResponse({ body, headers: new HttpHeaders({ ETag: 'W/"5"' }) }),
      catalog,
    );
    expect(coding).toEqual({
      documentId: 'd1',
      version: '5',
      values: { responsiveness: 'Not Responsive', issues: ['Pricing', 'Termination'] },
      indexState: 'pending',
    });
  });

  it('writes query names and choice names as field ids and choice ids', () => {
    expect(toChange(catalog, 'Responsiveness', 'Responsive')).toEqual({
      fieldId: '1000',
      operation: 'set',
      value: '1',
    });
    expect(toChange(catalog, 'issues', ['Termination'])).toEqual({
      fieldId: '1001',
      operation: 'set',
      value: ['8'],
    });
    expect(toChange(catalog, 'issues', 'Pricing').value).toEqual(['7']);
    expect(toChange(catalog, 'notes', 'text').value).toBe('text');
    expect(toChange(catalog, 'responsiveness', null).value).toBeNull();
    expect(() => toChange(catalog, 'unknown', 'x')).toThrow(/Unknown coding field/);
  });
});
