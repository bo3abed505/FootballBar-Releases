/**
 * competition-config.ts
 *
 * Defines the canonical target competitions catalog and parses/validates
 * TARGET_COMPETITION_IDS from the environment.
 *
 * Numeric IDs correspond directly to API-Football provider league IDs.
 *
 * Validation rules:
 *   - Empty entries are ignored.
 *   - Whitespace around commas is trimmed.
 *   - Every surviving entry must be a positive integer.
 *   - Duplicate IDs are removed (keep first occurrence order).
 *   - If the variable is absent the result is an empty array (no allowlist).
 */

export interface TargetCompetitionMetadata {
  id: number;
  name: string;
  country: string;
  type: 'league' | 'cup';
  code: string;
}

/**
 * Standard top-tier club and international competitions supported by FootballTray.
 */
export const TARGET_COMPETITIONS: readonly TargetCompetitionMetadata[] = [
  // Top 5 European Leagues
  { id: 39, name: 'Premier League', country: 'England', type: 'league', code: 'PL' },
  { id: 140, name: 'La Liga', country: 'Spain', type: 'league', code: 'PD' },
  { id: 135, name: 'Serie A', country: 'Italy', type: 'league', code: 'SA' },
  { id: 78, name: 'Bundesliga', country: 'Germany', type: 'league', code: 'BL1' },
  { id: 61, name: 'Ligue 1', country: 'France', type: 'league', code: 'FL1' },

  // Major European Club Cups
  { id: 2, name: 'UEFA Champions League', country: 'World', type: 'cup', code: 'UCL' },
  { id: 3, name: 'UEFA Europa League', country: 'World', type: 'cup', code: 'UEL' },
  { id: 848, name: 'UEFA Europa Conference League', country: 'World', type: 'cup', code: 'UECL' },

  // Domestic Cups
  { id: 143, name: 'Copa del Rey', country: 'Spain', type: 'cup', code: 'CDR' },
  { id: 45, name: 'FA Cup', country: 'England', type: 'cup', code: 'FAC' },
  { id: 48, name: 'EFL Cup', country: 'England', type: 'cup', code: 'EFL' },
  { id: 137, name: 'Coppa Italia', country: 'Italy', type: 'cup', code: 'CIT' },
  { id: 81, name: 'DFB-Pokal', country: 'Germany', type: 'cup', code: 'DFB' },
  { id: 66, name: 'Coupe de France', country: 'France', type: 'cup', code: 'CDF' },

  // International Tournaments
  { id: 1, name: 'FIFA World Cup', country: 'World', type: 'cup', code: 'WC' },
  { id: 4, name: 'UEFA European Championship', country: 'World', type: 'cup', code: 'EURO' },
  { id: 6, name: 'Africa Cup of Nations', country: 'World', type: 'cup', code: 'AFCON' },
  { id: 34, name: 'Africa Cup of Nations - Qualification', country: 'World', type: 'cup', code: 'AFCON_Q' },
  { id: 36, name: 'Africa Cup of Nations - Qualification', country: 'World', type: 'cup', code: 'AFCON_Q' },
  { id: 9, name: 'Copa America', country: 'World', type: 'cup', code: 'CA' },
  { id: 15, name: 'FIFA Club World Cup', country: 'World', type: 'cup', code: 'CWC' },

  // Regional Leagues
  { id: 233, name: 'Egyptian Premier League', country: 'Egypt', type: 'league', code: 'EPL' },
  { id: 307, name: 'Saudi Pro League', country: 'Saudi Arabia', type: 'league', code: 'SPL' },
];

export const DEFAULT_TARGET_COMPETITION_IDS: readonly number[] = TARGET_COMPETITIONS.map((c) => c.id);

/** Thrown when TARGET_COMPETITION_IDS contains a value that cannot be parsed. */
export class CompetitionConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'CompetitionConfigError';
  }
}

/**
 * Parses a raw comma-separated string of competition IDs.
 *
 * @param raw - The raw string value (typically from process.env.TARGET_COMPETITION_IDS).
 * @returns A deduplicated array of positive integer provider competition IDs.
 * @throws {CompetitionConfigError} if any entry cannot be parsed as a positive integer.
 */
export function parseCompetitionIds(raw: string | undefined): number[] {
  if (!raw || raw.trim() === '') {
    return [];
  }

  const parts = raw.split(',');
  const seen = new Set<number>();
  const result: number[] = [];

  for (const part of parts) {
    const trimmed = part.trim();
    if (trimmed === '') continue; // tolerate trailing commas

    const parsed = Number(trimmed);

    if (!Number.isInteger(parsed) || parsed <= 0 || String(parsed) !== trimmed) {
      throw new CompetitionConfigError(
        `TARGET_COMPETITION_IDS contains an invalid competition ID: "${trimmed}". ` +
        `All values must be positive integers (e.g. "39,140,233").`
      );
    }

    if (!seen.has(parsed)) {
      seen.add(parsed);
      result.push(parsed);
    }
  }

  return result;
}

/**
 * Reads and parses TARGET_COMPETITION_IDS from process.env.
 *
 * @returns Parsed numeric competition IDs, or [] if the variable is absent.
 */
export function getTargetCompetitionIds(): number[] {
  return parseCompetitionIds(process.env.TARGET_COMPETITION_IDS);
}
