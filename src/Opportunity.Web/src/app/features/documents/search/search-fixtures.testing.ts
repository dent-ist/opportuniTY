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
