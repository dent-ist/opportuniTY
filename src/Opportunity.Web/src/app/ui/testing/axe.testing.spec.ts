import { expectNoAxeViolations } from './axe.testing';

// Guards the guard: the helper must actually fail on violations, or every axe test is meaningless.
describe('expectNoAxeViolations', () => {
  afterEach(() => document.body.replaceChildren());

  it('fails on an unlabelled control', async () => {
    const host = document.createElement('div');
    host.innerHTML = '<button type="button"></button><input type="text">';
    document.body.append(host);
    await expect(expectNoAxeViolations(host)).rejects.toThrow(/button-name/);
  });

  it('passes on accessible markup', async () => {
    const host = document.createElement('div');
    host.innerHTML =
      '<label for="q">Query</label><input id="q" type="text"><button type="button">Run</button>';
    document.body.append(host);
    await expectNoAxeViolations(host);
  });
});
