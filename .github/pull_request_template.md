## Summary

<!-- What does this change and why? Link the issue: "Closes #123". -->

## Baseline / ADR references

<!-- Architecture baseline sections (§) and ADRs this implements or affects. -->

## Testing

- [ ] `dotnet build` and `dotnet test --solution Opportunity.slnx` pass
- [ ] `npm ci && npm run build && npx ng test --watch=false` pass (if `src/Opportunity.Web` changed)
- [ ] New behavior is covered at the right layer (see docs/testing/test-strategy.md)

## Checklist

- [ ] Layering rules (ADR-019) respected; architecture tests pass
- [ ] Workspace scoping and authoritative authorization preserved (baseline §2.3, §24)
- [ ] No real client data in tests, fixtures or screenshots
- [ ] New worker type, queue, data store, external integration or protected-content surface? If yes, the threat model (docs/security/threat-model.md) and ADR-015 credential matrix are updated in this PR

## UI changes (skip if none) — [docs/ux/ai-ui-guidelines.md](../docs/ux/ai-ui-guidelines.md) §23

- [ ] Uses opportuniTY branding, design tokens and approved icons; no copied third-party assets, CSS or HTML
- [ ] Not a pixel-for-pixel reproduction of a competitor screen; visually distinct; implies no partnership or endorsement
- [ ] Familiar eDiscovery terminology and workflow; keyboard accessible; efficient for reviewers
- [ ] Web gates pass (prettier, api:check, unit tests, bundle budgets, e2e axe/keyboard/performance)
