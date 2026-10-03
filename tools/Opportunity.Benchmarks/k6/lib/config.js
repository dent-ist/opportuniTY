// Workload configuration (E17-T04). Every value comes from the environment (`k6 run -e NAME=value` or env vars) so the
// same scripts drive the stub API, the developer stack and the reference environment.
//
// Endpoint paths follow ADR-019 §2 (`/api/v1/workspaces/{workspaceId}/...`, plural kebab-case nouns, non-CRUD actions
// as sub-resources, cursor pagination `{ items, nextCursor, total }`). The M1 API does not exist yet, so every path is
// a template that can be overridden without editing the scripts.

const env = __ENV;

function required(name) {
  const value = env[name];
  if (!value) {
    throw new Error(`${name} is required (see docs/benchmarks/query-taxonomy.md §6)`);
  }
  return value;
}

function int(name, fallback) {
  const raw = env[name];
  if (raw === undefined || raw === '') {
    return fallback;
  }
  const value = Number.parseInt(raw, 10);
  if (!Number.isFinite(value) || value < 0) {
    throw new Error(`${name} must be a non-negative integer, got '${raw}'`);
  }
  return value;
}

function num(name, fallback) {
  const raw = env[name];
  if (raw === undefined || raw === '') {
    return fallback;
  }
  const value = Number.parseFloat(raw);
  if (!Number.isFinite(value) || value < 0) {
    throw new Error(`${name} must be a non-negative number, got '${raw}'`);
  }
  return value;
}

export const WORKLOAD_VERSION = '1.0.0';

export const config = {
  baseUrl: (env.BASE_URL || 'http://127.0.0.1:8080').replace(/\/+$/, ''),
  apiBase: env.API_BASE || '/api/v1',
  workspaceId: env.WORKSPACE_ID || 'bench',
  paths: {
    search: env.PATH_SEARCH || '/workspaces/{workspaceId}/searches',
    document: env.PATH_DOCUMENT || '/workspaces/{workspaceId}/documents/{documentId}',
    coding: env.PATH_CODING || '/workspaces/{workspaceId}/documents/{documentId}/coding',
    bulkJobs: env.PATH_BULK_JOBS || '/workspaces/{workspaceId}/bulk-coding-jobs',
    job: env.PATH_JOB || '/workspaces/{workspaceId}/jobs/{jobId}',
  },
  // Bearer tokens: one shared token, or a comma-separated list handed out to VUs round-robin (one reviewer each).
  tokens: (env.BENCH_TOKENS || env.BENCH_TOKEN || '').split(',').map((t) => t.trim()).filter((t) => t.length > 0),
  queriesFile: () => required('QUERIES'),
  // Seconds of think time are multiplied by this (stub/CI runs use e.g. 0.001; real runs must use 1).
  thinkScale: num('THINK_SCALE', 1),
  strict: env.STRICT === '1',
  durations: {
    main: env.DURATION || '5m',
    idle: env.IDLE_DURATION || '10m',
    bulk: env.BULK_DURATION || '10m',
  },
  search: {
    rate: int('SEARCH_RATE', 20),
    preAllocatedVUs: int('SEARCH_PRE_VUS', 50),
    maxVUs: int('SEARCH_MAX_VUS', 400),
  },
  reviewers: {
    count: int('REVIEWERS', 100),
  },
  bulk: {
    docsPerSecond: int('BULK_DOCS_PER_SEC', 2000),
    batchDocs: int('BULK_BATCH', 500),
    preAllocatedVUs: int('BULK_PRE_VUS', 10),
    maxVUs: int('BULK_MAX_VUS', 100),
  },
  phase: env.PHASE || 'main',
};

export function url(template, params) {
  const path = template.replace(/\{(\w+)\}/g, (_, name) => {
    const value = name === 'workspaceId' ? config.workspaceId : params && params[name];
    if (value === undefined || value === null) {
      throw new Error(`no value for {${name}} in ${template}`);
    }
    return encodeURIComponent(String(value));
  });
  return `${config.baseUrl}${config.apiBase}${path}`;
}

// Thresholds: STRICT=1 (stub mode, CI) turns any failed contract check into a non-zero exit.
export function thresholds() {
  return config.strict ? { checks: ['rate==1'] } : {};
}

export const summaryTrendStats = ['count', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'];
