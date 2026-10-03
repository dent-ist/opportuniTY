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
