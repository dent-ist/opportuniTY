-- V0064: minimal batch review (#178, Q-53). A Batch Set may order its documents before they are cut into batches:
-- 1 Control Number (the frozen set's own order: families by their parent's control number), 2 Family Date, 3 Document
-- Date. Keep-together groups (families, threads) stay whole and are ordered by the group's earliest date (descending:
-- its latest), documents without a date last. Existing sets keep the frozen set's order.

ALTER TABLE opportunity.review_batch_set
    ADD COLUMN sort_field      smallint NOT NULL DEFAULT 1,
    ADD COLUMN sort_descending boolean  NOT NULL DEFAULT false,
    ADD CONSTRAINT review_batch_set_sort_ck CHECK (sort_field BETWEEN 1 AND 3);
