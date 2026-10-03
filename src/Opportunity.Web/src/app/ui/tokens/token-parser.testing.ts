// Test-only helpers that read src/styles/_tokens.scss. Kept out of the app bundle (only specs import them).

/** Colour tokens per theme mixin, keyed by the name after `--opp-color-`. */
export function parseThemes(scss: string): Record<string, Record<string, string>> {
  const themes: Record<string, Record<string, string>> = {};
  for (const m of scss.matchAll(/@mixin theme-([a-z-]+)\s*\{([^}]*)\}/g)) {
    const colours: Record<string, string> = {};
    for (const d of m[2].matchAll(/--opp-color-([a-z-]+):\s*([^;]+);/g)) {
      const value = d[2].trim();
      if (!/^#[0-9a-f]{6}$/i.test(value)) {
        throw new Error(
          `--opp-color-${d[1]} in theme-${m[1]} must be a 6-digit hex colour, got ${value}`,
        );
      }
      colours[d[1]] = value;
    }
    themes[m[1]] = colours;
  }
  return themes;
}

/** Size tokens of a density mixin in CSS px (rem × 16). */
export function parseDensity(scss: string, density: string): Record<string, number> {
  const block = new RegExp(`@mixin density-${density}\\s*\\{([^}]*)\\}`).exec(scss);
  if (!block) throw new Error(`density-${density} not found`);
  const sizes: Record<string, number> = {};
  for (const d of block[1].matchAll(/--opp-([a-z-]+):\s*([\d.]+)(rem|px);/g)) {
    sizes[d[1]] = Number(d[2]) * (d[3] === 'rem' ? 16 : 1);
  }
  return sizes;
}

function luminance(hex: string): number {
  const channel = (i: number) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5);
}

/** WCAG 2.x contrast ratio of two #rrggbb colours. */
export function contrastRatio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}
