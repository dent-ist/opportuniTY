import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, renameSync, rmSync } from 'node:fs';
import { isAbsolute, resolve } from 'node:path';
import { repoRoot } from './slice';

// The 1K corpus is a load-file volume written by tools/Opportunity.DataGenerator (E17-T02) into the developer
// profile's import share (Import:VolumeShareRoot, mounted read-only into the worker; docs/architecture/import-volumes.md).
// The share is a bind mount, so a volume written while the stack runs is visible to the worker at once.

export const corpusSeed = Number(process.env['E2E_SLICE_SEED'] ?? 37);
const composeDir = resolve(repoRoot, 'deploy/docker-compose');
const configuredShare = process.env['OPPORTUNITY_IMPORT_SHARE'];
export const shareRoot = configuredShare
  ? isAbsolute(configuredShare)
    ? configuredShare
    : resolve(composeDir, configuredShare)
  : resolve(composeDir, 'import-share');
/** The corpus folder, relative to the share (an import profile's `paths.volumeRoot` is relative to it). */
const corpusFolder = `e2e-slice/seed-${corpusSeed}`;
const corpusDir = resolve(shareRoot, corpusFolder);

export interface Corpus {
  /** `paths.volumeRoot` of the import profile. */
  volumeRoot: string;
  dat: string;
  /** Needle term → control numbers (without the import prefix) whose text contains it. */
  needles: Record<string, string[]>;
}

/** Generates the corpus into the share unless an earlier run left it there (same seed and profile, same bytes). */
export function ensureCorpus(): Corpus {
  const dat = resolve(corpusDir, 'VOL001/DATA/VOL001.dat');
  if (!existsSync(dat)) {
    mkdirSync(resolve(shareRoot, 'e2e-slice'), { recursive: true });
    const partial = `${corpusDir}.partial`;
    rmSync(partial, { recursive: true, force: true });
    execFileSync(
      'dotnet',
      [
        'run',
        '--project',
        resolve(repoRoot, 'tools/Opportunity.DataGenerator'),
        '-c',
        'Release',
        '--',
        'generate',
        '--seed',
        String(corpusSeed),
        '--profile',
        resolve(__dirname, '../corpus-profile.json'),
        '--out',
        partial,
        '--volumes',
        '--no-images',
      ],
      { stdio: 'inherit', timeout: 5 * 60_000 },
    );
    renameSync(partial, corpusDir);
  }

  const needles: Record<string, string[]> = {};
  for (const line of readFileSync(resolve(corpusDir, 'ground-truth.jsonl'), 'utf8').split('\n')) {
    if (!line) continue;
    const truth = JSON.parse(line) as { type: string; term?: string; controlNumber: string };
    if (truth.type !== 'needle' || !truth.term) continue;
    (needles[truth.term] ??= []).push(truth.controlNumber);
  }
  for (const term of Object.keys(needles)) needles[term] = [...new Set(needles[term])].sort();
  return { volumeRoot: `${corpusFolder}/VOL001`, dat, needles };
}

/** Disk is precious on shared runners: the import copied what it needs into object storage. */
export function removeCorpus(): void {
  if (process.env['E2E_SLICE_KEEP_CORPUS'] === '1') return;
  rmSync(corpusDir, { recursive: true, force: true });
}
