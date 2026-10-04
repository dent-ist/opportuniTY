import type { FieldCapabilitiesResource, FieldResource } from '../../../core/api/generated/models';
import { SearchField } from '../../../core/search/search-fields';

/** Fields of a workspace seeded with the default template (familiarity guide §3.4) plus a few structural ones. */
export const TEST_FIELDS: readonly SearchField[] = [
  { queryName: 'custodian', displayName: 'Custodian', type: 'keyword' },
  { queryName: 'date', displayName: 'Document Date', type: 'dateTime' },
  { queryName: 'filename', displayName: 'File Name', type: 'text' },
  {
    queryName: 'responsiveness',
    displayName: 'Responsiveness',
    type: 'singleChoice',
    choices: ['Responsive', 'Not Responsive', 'Needs Further Review'],
  },
  {
    queryName: 'privilege_status',
    displayName: 'Privilege Status',
    type: 'singleChoice',
    choices: ['Not Privileged', 'Withhold', 'Redact'],
  },
  {
    queryName: 'issues',
    displayName: 'Issues',
    type: 'multiChoice',
    choices: ['Pricing', 'Termination "for cause"'],
  },
  { queryName: 'key_document', displayName: 'Key Document', type: 'boolean' },
];

const ALL_CAPABILITIES: FieldCapabilitiesResource = {
  sortable: true,
  filterable: true,
  rangeable: true,
  aggregatable: true,
  fullText: true,
  wildcard: true,
  leadingWildcard: true,
  highlightable: true,
  exists: true,
};

const NO_CAPABILITIES: FieldCapabilitiesResource = {
  sortable: false,
  filterable: false,
  rangeable: false,
  aggregatable: false,
  fullText: false,
  wildcard: false,
  leadingWildcard: false,
  highlightable: false,
  exists: false,
};

/** `TEST_FIELDS` as `GET …/fields` returns them, plus a field search cannot use (the source drops it). */
export const TEST_FIELD_RESOURCES: readonly FieldResource[] = [
  ...TEST_FIELDS.map((f, i) => fieldResource(1000 + i, f)),
  {
    ...fieldResource(1100, {
      queryName: 'internal_note',
      displayName: 'Internal Note',
      type: 'text',
    }),
    capabilities: NO_CAPABILITIES,
  },
];

function fieldResource(fieldId: number, f: SearchField): FieldResource {
  const dateTime = f.type === 'dateTime';
  return {
    fieldId,
    displayName: f.displayName,
    queryName: f.queryName,
    type: dateTime ? 'date' : f.type,
    datePrecision: dateTime ? 'dateTime' : f.type === 'date' ? 'date' : null,
    storage: f.choices ? 'coding' : 'metadata',
    multiValue: f.type === 'multiChoice',
    isSystem: false,
    isHidden: false,
    isSecurityAffecting: false,
    capabilities: ALL_CAPABILITIES,
    reducedCapabilities: false,
    choices:
      f.choices?.map((name, c) => ({ choiceId: fieldId * 10 + c, name, isActive: true })) ?? null,
  };
}
