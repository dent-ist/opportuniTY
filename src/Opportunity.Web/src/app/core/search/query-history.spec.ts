import { TestBed } from '@angular/core/testing';
import { WorkspaceContext } from '../workspace/workspace-context';
import { QueryHistory, QueryHistoryBackend, QueryHistoryEntry } from './query-history';

class RecordingBackend extends QueryHistoryBackend {
  readonly recorded: [string, QueryHistoryEntry][] = [];
  constructor(private readonly stored: QueryHistoryEntry[] = []) {
    super();
  }
  load(): Promise<readonly QueryHistoryEntry[]> {
    return Promise.resolve(this.stored);
  }
  record(workspaceId: string, entry: QueryHistoryEntry): Promise<void> {
    this.recorded.push([workspaceId, entry]);
    return Promise.resolve();
  }
}

describe('QueryHistory', () => {
  function create(backend = new RecordingBackend()): QueryHistory {
    TestBed.configureTestingModule({
      providers: [
        QueryHistory,
        { provide: WorkspaceContext, useValue: { workspaceId: 'ws-1' } },
        { provide: QueryHistoryBackend, useValue: backend },
      ],
    });
    return TestBed.inject(QueryHistory);
  }

  it('keeps the last 50 distinct queries, newest first', () => {
    const history = create();
    for (let i = 1; i <= 60; i++) history.record(`term${i}`);
    history.record('term30');
    history.record('   ');
    const queries = history.entries().map((e) => e.query);
    expect(queries).toHaveLength(QueryHistory.MAX);
    expect(QueryHistory.MAX).toBe(50);
    expect(queries.slice(0, 3)).toEqual(['term30', 'term60', 'term59']);
    expect(queries).not.toContain('term10');
  });

  it('records through the backend for the current workspace and merges stored history', async () => {
    const backend = new RecordingBackend([
      { query: 'older', ranAt: '2026-10-01T09:00:00.000Z' },
      { query: 'again', ranAt: '2026-10-01T08:00:00.000Z' },
    ]);
    const history = create(backend);
    history.record('again', new Date('2026-10-03T10:00:00Z'));
    await history.load();
    expect(history.entries()).toEqual([
      { query: 'again', ranAt: '2026-10-03T10:00:00.000Z' },
      { query: 'older', ranAt: '2026-10-01T09:00:00.000Z' },
    ]);
    expect(backend.recorded).toEqual([
      ['ws-1', { query: 'again', ranAt: '2026-10-03T10:00:00.000Z' }],
    ]);
  });
});
