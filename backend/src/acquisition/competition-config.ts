/**
 * competition-config.ts
 *
 * Parses and validates TARGET_COMPETITION_IDS from the environment.
 *
 * TARGET_COMPETITION_IDS contains comma-separated numeric provider-level
 * competition IDs (e.g. "39,140,233,2").  These are API-Football league IDs
 * as returned by /leagues, NOT internal comp_* string identifiers.
 *
 * Validation rules:
 *   - Empty entries are ignored.
 *   - Whitespace around commas is trimmed.
 *   - Every surviving entry must be a positive integer.
 *   - Duplicate IDs are removed (keep first occurrence order).
 *   - If the variable is absent the result is an empty array (no allowlist).
 *
 * An empty array means "no allowlist configured" — the caller decides how to
 * react (e.g. warn and refuse to run, or proceed with the full provider data).
 */

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
    // silently deduplicate repeated entries
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
