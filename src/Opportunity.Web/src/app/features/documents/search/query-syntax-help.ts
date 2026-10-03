import { ChangeDetectionStrategy, Component } from '@angular/core';

/** Reference card for the query syntax (ADR-008 M1 subset), written in our own words. */
@Component({
  selector: 'opp-query-syntax-help',
  template: `<h3 class="help__title">Search syntax</h3>
    <dl class="help__list">
      @for (row of rows; track row.example) {
        <div class="help__row">
          <dt>
            <code>{{ row.example }}</code>
          </dt>
          <dd>{{ row.meaning }}</dd>
        </div>
      }
    </dl>
    <p class="help__note">
      AND, OR, NOT and TO work only in capitals; lower-case and, or, not are searched as words.
      Searches ignore upper/lower case and accents. Start typing a word to get field-name
      suggestions.
    </p>`,
  styleUrl: './query-syntax-help.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'region', 'aria-label': 'Search syntax' },
})
export class QuerySyntaxHelp {
  protected readonly rows = [
    { example: 'contract termination', meaning: 'Both words. Words next to each other mean AND.' },
    { example: 'contract OR agreement', meaning: 'Either word.' },
    { example: 'NOT draft', meaning: 'Leaves out documents with the word.' },
    { example: '"trade secret"', meaning: 'The exact phrase.' },
    {
      example: 'apple W/10 iphone',
      meaning: 'Both words within 10 words of each other, either order.',
    },
    {
      example: 'terminat*   wom?n',
      meaning: '* stands for any characters, ? for exactly one. Type at least 3 letters first.',
    },
    { example: '(contract OR agreement) AND NOT draft', meaning: 'Parentheses group terms.' },
    { example: 'custodian:"John Smith"', meaning: 'A value in a field.' },
    {
      example: 'date:[2025-01-01 TO 2025-12-31]',
      meaning: 'A range including both ends. { } leaves the ends out; * leaves an end open.',
    },
    {
      example: 'privilege:*',
      meaning: 'The field has a value (is set). NOT privilege:* is not set.',
    },
    { example: 'AT\\&T', meaning: 'A backslash makes the next character part of the word.' },
  ] as const;
}
