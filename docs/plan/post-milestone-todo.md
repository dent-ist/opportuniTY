# Post-milestone to-do

Changes the product owner asked to revisit **after all milestones and waves are complete**. Nothing here is scheduled
yet; each item becomes a ticket (and, where it changes a decision, a new Q- entry) when it is picked up.

| # | Requested | Item | Affects |
|---|---|---|---|
| 1 | 2026-10-06 | **Reuse Bates numbers of a deactivated production.** A production can be created and sent to the opposing party, then found to be the wrong set. It cannot be deleted from the system, but it should be possible to mark it **inactive** ("Deactivate") and reuse the same Bates numbering for the corrected production, so duplicate Bates numbers across an inactive and an active production are allowed. | Q-54 (numbers of a produced production are locked forever), Q-75 / #102 (Bates uniqueness per workspace + prefix, voided ranges never reissued, `bates_range` ledger, integrity check), the Bates → document lookup (must say which production a number belongs to and whether it is active), clawback / re-production (#113) |
