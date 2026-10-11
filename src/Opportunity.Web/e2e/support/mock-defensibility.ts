import type { Route } from '@playwright/test';

const DOC = '0199c3a0-0000-7000-8000-00000000d001';

/**
 * Defensibility reports (E14-T05) of ws-1: two reviewers' coding, one finalized and one voided production, a search term
 * report that ran while the index was catching up, and document `DOC`, whose native no longer matches the hash recorded
 * at import. Requests received are recorded in `requests` (path and query), as the server audits every generation.
 */
export class DefensibilityMock {
  static readonly documentId = DOC;

  readonly requests: { path: string; query: string }[] = [];

  handle(route: Route, method: string, path: string, url: URL): Promise<void> | undefined {
    if (method !== 'GET') return undefined;
    const report =
      /^\/api\/v1\/workspaces\/[^/]+\/defensibility\/(review-summary|production-history|search-term-hits)$/.exec(
        path,
      );
    const custody = /^\/api\/v1\/workspaces\/[^/]+\/documents\/([^/]+)\/chain-of-custody$/.exec(
      path,
    );
    if (!report && !custody) return undefined;
    this.requests.push({ path, query: url.search });
    const asOf = url.searchParams.get('asOf') ?? '2026-10-11T09:00:00.000000Z';
    let body: unknown;
    if (custody) {
      if (custody[1] !== DOC) {
        return route.fulfill({
          status: 404,
          contentType: 'application/problem+json',
          body: JSON.stringify({
            type: 'https://opportunity.dev/problems/not-found',
            title: 'Not found',
            status: 404,
            detail: 'The document does not exist.',
          }),
        });
      }
      body = this.custody(asOf);
    } else if (report![1] === 'review-summary') {
      body = this.review(asOf);
    } else if (report![1] === 'production-history') {
      body = this.history(asOf);
    } else {
      body = this.hits(asOf);
    }

    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
  }

  private statement(report: string, asOf: string) {
    return {
      report,
      asOf,
      snapshotId: null,
      searchGeneration: 1207,
      generatedAt: '2026-10-11T09:00:00Z',
      generatedBy: 'Alex Reviewer',
      fingerprint: '3f9a1c0e7b52d8a4e6f1093c2b7d5e8a1f4c6b9d0e2a7f3c5b8d1e4a6c9f0b2d',
      auditEventId: '0199c3a0-0000-7000-8000-00000000e101',
    };
  }

  private review(asOf: string) {
    const reviewer = (userId: string, displayName: string, documents: number, changes: number) => ({
      userId,
      displayName,
      documents,
      changes,
      interactive: changes - 1,
      bulk: 1,
      systemRule: 0,
      firstAt: '2026-10-01T09:00:00Z',
      lastAt: '2026-10-08T16:30:00Z',
    });
    return {
      statement: this.statement('ReviewSummary', asOf),
      snapshot: null,
      population: {
        documents: 1250,
        reviewed: 830,
        notReviewed: 420,
        systemCodedOnly: 12,
        bulkSkips: 3,
      },
      reviewers: [
        reviewer('a-3', 'Riley Reviewer', 610, 1400),
        reviewer('a-2', 'Sam Senior', 220, 300),
      ],
      days: [
        {
          date: '2026-10-01',
          userId: 'a-3',
          displayName: 'Riley Reviewer',
          documents: 300,
          changes: 700,
          interactive: 700,
          bulk: 0,
          systemRule: 0,
        },
        {
          date: '2026-10-02',
          userId: 'a-2',
          displayName: 'Sam Senior',
          documents: 220,
          changes: 300,
          interactive: 299,
          bulk: 1,
          systemRule: 0,
        },
      ],
      daysTruncated: false,
    };
  }

  private history(asOf: string) {
    const production = (id: string, name: string, status: string, begin: string, end: string) => ({
      productionId: id,
      name,
      version: 1,
      status,
      snapshotId: '0199c3a0-0000-7000-8000-00000000c201',
      finalizedAt: '2026-10-03T12:00:00Z',
      finalizedBy: 'Sam Senior',
      voidedAt: status === 'Voided' ? '2026-10-06T12:00:00Z' : null,
      voidReason: status === 'Voided' ? 'Clawback of a privileged document.' : null,
      batesBegin: begin,
      batesEnd: end,
      documents: 120,
      pages: 840,
      specificationSha256: 'b'.repeat(64),
      assignmentsSha256: 'c'.repeat(64),
      manifestSha256: 'd'.repeat(64),
      qc: {
        qcRunId: '0199c3a0-0000-7000-8000-00000000f001',
        runAt: '2026-10-03T11:59:00Z',
        outcome: 'Passed',
        warningsAcknowledged: true,
        reportSha256: 'e'.repeat(64),
        checks: [],
      },
      volumes: [],
    });
    return {
      statement: this.statement('ProductionHistory', asOf),
      productions: [
        production(
          '0199c3a0-0000-7000-8000-00000000b001',
          'First production',
          'Finalized',
          'ACME0000001',
          'ACME0000840',
        ),
        production(
          '0199c3a0-0000-7000-8000-00000000b002',
          'Second production',
          'Voided',
          'ACME0000841',
          'ACME0001680',
        ),
      ],
    };
  }

  private hits(asOf: string) {
    return {
      statement: this.statement('SearchTermHits', asOf),
      truncated: false,
      reports: [
        {
          reportId: '0199c3a0-0000-7000-8000-00000000a501',
          name: 'Meet and confer terms',
          run: 2,
          scope: 'Workspace',
          snapshotId: '0199c3a0-0000-7000-8000-00000000c301',
          searchGeneration: 1180,
          indexCurrent: false,
          indexPendingChanges: 3,
          indexLagSeconds: 4.5,
          executedBy: 'Sam Senior',
          executedAt: '2026-10-05T10:00:00Z',
          completedAt: '2026-10-05T10:01:00Z',
          totals: {
            documentsInScope: 1250,
            documentsWithHits: 310,
            documentsWithHitsIncludingFamily: 402,
            documentsWithoutHits: 940,
          },
          terms: [],
        },
      ],
    };
  }

  private custody(asOf: string) {
    const verification = (status: string, sha256: string | null) => ({
      status,
      sha256,
      verifiedAt: '2026-10-11T09:00:00Z',
    });
    return {
      statement: this.statement('ChainOfCustody', asOf),
      documentId: DOC,
      controlNumber: 'ACME-000001',
      fileName: 'memo.docx',
      fileSize: 48213,
      createdAt: '2026-09-01T10:00:00Z',
      importHashes: { sha256: '1'.repeat(64), sha1: null, md5: null, upstream: null },
      verified: 3,
      mismatches: 1,
      imports: [
        {
          importId: '0199c3a0-0000-7000-8000-00000000a601',
          name: 'Vendor volume 1',
          mode: 'Append',
          loadFileName: 'VOL001.dat',
          loadFileSha256: '2'.repeat(64),
          loadFileSize: 9120,
          loadFileVerification: verification('Match', '2'.repeat(64)),
          rowNumber: 1,
          action: 'Imported',
          startedBy: 'Alex Reviewer',
          startedAt: '2026-09-01T09:00:00Z',
          completedAt: '2026-09-01T09:05:00Z',
          jobId: '0199c3a0-0000-7000-8000-00000000a701',
        },
      ],
      objects: [
        {
          objectId: '0199c3a0-0000-7000-8000-00000000a801',
          area: 'Native',
          current: true,
          pageSetId: null,
          page: null,
          sizeBytes: 48213,
          contentType: 'application/octet-stream',
          storedAt: '2026-09-01T09:01:00Z',
          jobId: '0199c3a0-0000-7000-8000-00000000a701',
          recordedSha256: '1'.repeat(64),
          verification: verification('Mismatch', '9'.repeat(64)),
        },
        {
          objectId: '0199c3a0-0000-7000-8000-00000000a802',
          area: 'Text',
          current: true,
          pageSetId: null,
          page: null,
          sizeBytes: 3120,
          contentType: 'text/plain',
          storedAt: '2026-09-01T09:01:00Z',
          jobId: '0199c3a0-0000-7000-8000-00000000a701',
          recordedSha256: '3'.repeat(64),
          verification: verification('Match', '3'.repeat(64)),
        },
      ],
      objectsTruncated: false,
      pageSets: [],
      overlays: [],
      productions: [
        {
          productionId: '0199c3a0-0000-7000-8000-00000000b001',
          name: 'First production',
          version: 1,
          finalizedAt: '2026-10-03T12:00:00Z',
          voidedAt: null,
          output: 'Image',
          begBates: 'ACME0000001',
          endBates: 'ACME0000004',
          volumeIds: [],
        },
      ],
    };
  }
}
