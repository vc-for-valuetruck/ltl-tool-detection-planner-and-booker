#!/usr/bin/env node
// Import-boundary check for the isolated Demo Director feature.
//
// Rule: application code must NOT import Demo Director code. The demo feature lives entirely under
// web/src/app/features/ltl/demo and may only be reached through the one sanctioned lazy seam — the
// `loadChildren` dynamic import in app.routes.ts. Any other static or dynamic import of a demo
// module from application code is a boundary violation and fails CI.
//
// Direction that IS allowed (and therefore never flagged): demo → app facade (e.g. the demo
// service importing LtlService / ltl.models) and demo → core (OverlayOutletService). We only guard
// the app → demo direction here.
//
// No external dependencies — plain Node so it runs as a standalone CI step without `npm ci`.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const appRoot = resolve(scriptDir, '..', 'src', 'app');
const demoDir = resolve(appRoot, 'features', 'ltl', 'demo');

// The single sanctioned application → demo seam: the lazy route registration. A dynamic import in
// this file is the lazy-load boundary itself (it keeps the demo bundle out of the initial chunk),
// so it is explicitly allowed. Everything else is not.
const allowlist = new Set([resolve(appRoot, 'app.routes.ts')]);

/** Recursively collect every .ts file under a directory. */
function collectTsFiles(dir) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const s = statSync(full);
    if (s.isDirectory()) {
      out.push(...collectTsFiles(full));
    } else if (entry.endsWith('.ts')) {
      out.push(full);
    }
  }
  return out;
}

/** True when a file lives inside the demo feature folder. */
function isDemoFile(file) {
  const rel = relative(demoDir, file);
  return !rel.startsWith('..') && !rel.startsWith(`/`);
}

// Matches both `from '<spec>'` (static import/export) and `import('<spec>')` (dynamic import).
const specifierPattern =
  /(?:from|import)\s*\(?\s*['"]([^'"]+)['"]/g;

const violations = [];

for (const file of collectTsFiles(appRoot)) {
  if (isDemoFile(file)) continue; // demo → demo is fine
  if (allowlist.has(file)) continue; // sanctioned lazy route seam

  const source = readFileSync(file, 'utf8');
  let match;
  while ((match = specifierPattern.exec(source)) !== null) {
    const spec = match[1];
    if (!spec.startsWith('.')) continue; // bare package specifier — never demo
    const resolved = resolve(dirname(file), spec);
    const rel = relative(demoDir, resolved);
    const insideDemo = !rel.startsWith('..') && rel !== '';
    if (insideDemo) {
      const line = source.slice(0, match.index).split('\n').length;
      violations.push({ file: relative(appRoot, file), line, spec });
    }
  }
}

if (violations.length > 0) {
  console.error('\nDemo Director boundary violation(s): application code must not import demo code.');
  console.error('The only permitted app → demo seam is the lazy `loadChildren` in app.routes.ts.\n');
  for (const v of violations) {
    console.error(`  src/app/${v.file}:${v.line}  imports demo module "${v.spec}"`);
  }
  console.error('\nRoute demo access through the lazy route, or invert the dependency via');
  console.error('OverlayOutletService / an existing LTL facade service instead.\n');
  process.exit(1);
}

console.log('Demo Director boundary check passed: no application → demo imports.');
