import { test } from '@playwright/test';
import { removeCorpus } from './support/corpus';

test('removes the generated corpus from the import share', () => {
  removeCorpus();
});
