import { ApiError } from '../../../core/api/problem-details';
import { isStale, toPreview, toRelationships, toResult } from './relationships-api';

describe('Relationships port readers (wave-12 contract)', () => {
  it('reads relationships tolerantly: int64 strings, missing lists, coding by lower-case query name', () => {
    const r = toRelationships({
      documentId: 'doc-2',
      family: {
        familyId: 'fam-1',
        parent: { documentId: 'doc-1', controlNumber: 'ACM1', isParent: true, familySequence: '0' },
        members: [
          { documentId: 'doc-1', controlNumber: 'ACM1', isParent: true, familySequence: '0' },
          {
            documentId: 'doc-2',
            controlNumber: 'ACM2',
            isSelf: true,
            familySequence: 1,
            coding: { Responsiveness: ['Responsive'] },
          },
        ],
        restrictedCount: '2',
      },
      duplicates: { duplicateGroupId: null, members: [] },
      thread: { emailThreadId: 't-1', members: [{ documentId: 'doc-9' }], total: '450' },
    });
    expect(r.family.parent?.familySequence).toBe(0);
    expect(r.family.members[1]).toMatchObject({
      isSelf: true,
      isParent: false,
      coding: { responsiveness: ['Responsive'] },
    });
    expect(r.family.restrictedCount).toBe(2);
    expect(r.duplicates).toEqual({
      duplicateGroupId: null,
      primaryDocumentId: null,
      members: [],
      restrictedCount: 0,
    });
    expect(r.thread.total).toBe(450);
  });

  it('reads a preview with field and choice names, and the job mode above the threshold', () => {
    const preview = toPreview(
      {
        previewId: 'p-1',
        targetCount: '1500',
        conflictCount: 1,
        conflicts: [
          {
            documentId: 'doc-4',
            controlNumber: 'ACM4',
            fieldId: 1000,
            currentValues: ['2'],
            newValues: ['1'],
          },
        ],
        mode: 'job',
        threshold: 1000,
      },
      (id) => (id === '1000' ? 'responsiveness' : id),
      (_, v) => ({ '1': 'Responsive', '2': 'Not Responsive' })[v] ?? v,
    );
    expect(preview).toMatchObject({
      targetCount: 1500,
      mode: 'job',
      threshold: 1000,
      restrictedCount: 0,
      conflicts: [
        { field: 'responsiveness', currentValues: ['Not Responsive'], newValues: ['Responsive'] },
      ],
    });
  });

  it('reads both apply outcomes', () => {
    expect(toResult({ mode: 'interactive', applied: '3', skipped: 1 })).toEqual({
      mode: 'interactive',
      applied: 3,
      skipped: 1,
    });
    const job = toResult({ mode: 'job', job: { jobId: 'job-7', jobType: 'bulkCoding' } });
    expect(job.mode === 'job' && job.job.jobId).toBe('job-7');
  });

  it('recognises a stale preview (409 PREVIEW_STALE in any spelling)', () => {
    expect(isStale(new ApiError(409, { code: 'PREVIEW_STALE' }))).toBe(true);
    expect(isStale(new ApiError(409, { type: 'urn:opportunity:problem:preview-stale' }))).toBe(
      true,
    );
    expect(isStale(new ApiError(409, { code: 'conflict' }))).toBe(false);
    expect(isStale(new ApiError(412, { code: 'PREVIEW_STALE' }))).toBe(false);
  });
});
