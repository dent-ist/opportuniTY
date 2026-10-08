import { expect, openPage, test } from './support/fixtures';

// E11-T03: the app (and the viewer inside it) runs under the production CSP from deploy/docker/web, which the e2e
// server sends. Markup that reaches the DOM from document content must not be able to run script: inline script
// elements, event-handler attributes and javascript: URLs are all blocked, and each attempt is reported as a CSP
// violation.

test('the content security policy blocks inline script, event handlers and javascript: URLs', async ({
  page,
  consoleErrors,
}) => {
  await openPage(page, '/w/ws-1/documents');

  const outcome = await page.evaluate(async () => {
    const flags = window as unknown as Record<string, unknown>;
    const violations: string[] = [];
    document.addEventListener('securitypolicyviolation', (e) =>
      violations.push(e.effectiveDirective),
    );

    const script = document.createElement('script');
    script.textContent = 'window.__oppInlineRan = true;';
    document.body.append(script);

    const holder = document.createElement('div');
    holder.innerHTML = '<img src="data:," onerror="window.__oppHandlerRan = true">';
    document.body.append(holder);

    const link = document.createElement('a');
    link.href = 'javascript:window.__oppUrlRan = true';
    document.body.append(link);
    link.click();

    await new Promise((resolve) => setTimeout(resolve, 200));
    holder.remove();
    script.remove();
    link.remove();
    return {
      inline: flags['__oppInlineRan'] === true,
      handler: flags['__oppHandlerRan'] === true,
      url: flags['__oppUrlRan'] === true,
      violations,
    };
  });

  expect(outcome.inline, 'inline <script> ran').toBe(false);
  expect(outcome.handler, 'event handler attribute ran').toBe(false);
  expect(outcome.url, 'javascript: URL ran').toBe(false);
  expect(outcome.violations).toEqual(
    expect.arrayContaining(['script-src-elem', 'script-src-attr']),
  );

  // The blocked attempts are logged as CSP errors; they are this test's expected outcome, not app errors.
  const unexpected = consoleErrors.filter((e) => !/Content Security Policy/i.test(e));
  consoleErrors.splice(0, consoleErrors.length, ...unexpected);
});
