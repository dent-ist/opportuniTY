// Query set, deterministic replay order, reviewer randomness and phase tagging (E17-T04).
//
// Determinism: the query file (opportunity-bench queries) fixes the queries and a seeded replay schedule. Iteration i
// of an open-model scenario always issues queries[schedule[i % schedule.length]], whatever VU runs it, so the same
// query seed yields the same query sequence. Reviewer v's k-th session uses schedule position (v-1)*7919 + k and a
// PRNG seeded from (querySeed, v, k), so think times and documents opened are reproducible per reviewer.

import { SharedArray } from 'k6/data';
import exec from 'k6/execution';

import { config } from './config.js';

const meta = new SharedArray('opp-query-meta', () => {
  const set = JSON.parse(open(config.queriesFile()));
  if (set.format !== 'opportunity-query-set' || set.formatVersion !== 1) {
    throw new Error(`${config.queriesFile()} is not an opportunity-query-set v1 file`);
  }
  return [
    {
      taxonomyVersion: set.taxonomyVersion,
      querySeed: set.querySeed,
      pageSize: set.request.pageSize,
      highlight: set.request.highlight,
      zone: set.evaluation.zone,
      documentCount: set.corpus.documentCount,
      controlNumberPrefix: set.corpus.controlNumberPrefix,
      controlNumberDigits: set.corpus.controlNumberDigits,
      codingFixture: set.codingFixture,
    },
  ];
})[0];

const queries = new SharedArray('opp-queries', () => JSON.parse(open(config.queriesFile())).queries);
const schedule = new SharedArray('opp-schedule', () => JSON.parse(open(config.queriesFile())).schedule);

export const querySet = meta;

export function queryAt(position) {
  const index = schedule[position % schedule.length];
  return { position, query: queries[index] };
}

const REVIEWER_STRIDE = 7919;

export function reviewerQuery(vu, session) {
  return queryAt((vu - 1) * REVIEWER_STRIDE + session);
}

// --- PRNG: splitmix32-seeded xorshift (32-bit integer arithmetic, identical in every k6 runtime) ---------------------

function mix32(x) {
  let z = (x + 0x9e3779b9) | 0;
  z = Math.imul(z ^ (z >>> 16), 0x85ebca6b);
  z = Math.imul(z ^ (z >>> 13), 0xc2b2ae35);
  return (z ^ (z >>> 16)) >>> 0;
}

export function rng(...keys) {
  const seed = meta.querySeed;
  let state = mix32(mix32((seed % 4294967296) >>> 0) ^ (Math.floor(seed / 4294967296) % 4294967296 >>> 0));
  for (const key of keys) {
    state = mix32(state ^ (key >>> 0));
  }
  if (state === 0) {
    state = 0x6d2b79f5;
  }
  const next = () => {
    state ^= state << 13;
    state >>>= 0;
    state ^= state >>> 17;
    state ^= state << 5;
    state >>>= 0;
    return state / 4294967296;
  };
  return {
    next,
    int(min, max) {
      return min + Math.floor(next() * (max - min + 1));
    },
    normal() {
      const u1 = 1 - next();
      const u2 = next();
      return Math.sqrt(-2 * Math.log(u1)) * Math.cos(2 * Math.PI * u2);
    },
  };
}

// --- Reviewer session model (docs/benchmarks/query-taxonomy.md §5) ------------------------------------------------

export const session = {
  thinkMedianSeconds: 20,
  thinkP90Seconds: 60,
  thinkMinSeconds: 2,
  thinkMaxSeconds: 300,
  minDocuments: 5,
  maxDocuments: 25,
  pauseBetweenSearchesSeconds: 5,
};

const Z90 = 1.2815515655446004;
const MU = Math.log(session.thinkMedianSeconds);
const SIGMA = Math.log(session.thinkP90Seconds / session.thinkMedianSeconds) / Z90;

// Log-normal reading time, median 20 s, p90 60 s, clamped to [2 s, 300 s].
export function thinkTimeSeconds(random) {
  const t = Math.exp(MU + SIGMA * random.normal());
  return Math.min(session.thinkMaxSeconds, Math.max(session.thinkMinSeconds, t));
}

// --- Phases ------------------------------------------------------------------------------------------------------
// Results are grouped by the `phase` tag (idle baseline vs bulk load). Fixed per scenario via its env, or by elapsed
// time for scenarios that span both windows (reviewers in mixed.js: PHASE_SPLIT_MS, PHASE_BEFORE, PHASE_AFTER).

export function currentPhase() {
  const split = __ENV.PHASE_SPLIT_MS;
  if (split) {
    return exec.instance.currentTestRunDuration < Number(split) ? __ENV.PHASE_BEFORE || 'idle' : __ENV.PHASE_AFTER || 'bulk';
  }
  return __ENV.PHASE || config.phase;
}

export function controlNumber(sequence) {
  return meta.controlNumberPrefix + String(sequence).padStart(meta.controlNumberDigits, '0');
}

export function pickWeighted(random, choices) {
  let u = random.next();
  for (const choice of choices) {
    if (u < choice.share) {
      return choice.name;
    }
    u -= choice.share;
  }
  return choices[choices.length - 1].name;
}

export function codingField(queryName) {
  const field = meta.codingFixture.fields.find((f) => f.queryName === queryName);
  if (!field) {
    throw new Error(`coding field ${queryName} missing from the query set's coding fixture`);
  }
  return field;
}
