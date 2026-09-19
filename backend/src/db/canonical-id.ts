import { randomBytes } from 'node:crypto';

export type CanonicalEntityType = 'comp' | 'team' | 'fix';

/**
 * Canonical uppercase Crockford Base32 alphabet.
 * Excludes ambiguous characters: I, L, O, U.
 */
export const CROCKFORD_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const ENCODING_LEN = 32;
const TIME_CHARS = 10;
const ENTROPY_BYTES = 10;
const ENTROPY_CHARS = 16;
export const CANONICAL_PAYLOAD_LENGTH = TIME_CHARS + ENTROPY_CHARS; // 26 chars

// 48-bit timestamp maximum: 2^48 - 1 = 281,474,976,710,655
const MAX_48BIT_TIMESTAMP = 281474976710655;

export interface GenerateCanonicalIdOptions {
  timestamp?: number;
  entropy?: Uint8Array | Buffer;
}

/**
 * Encodes a 48-bit millisecond timestamp into 10 Crockford Base32 characters (big-endian).
 */
export function encodeTime(timestamp: number): string {
  if (!Number.isInteger(timestamp) || timestamp < 0 || timestamp > MAX_48BIT_TIMESTAMP) {
    throw new Error(`Timestamp must be an integer between 0 and ${MAX_48BIT_TIMESTAMP}, received: ${timestamp}`);
  }

  let str = '';
  let current = timestamp;
  for (let i = TIME_CHARS - 1; i >= 0; i--) {
    const mod = current % ENCODING_LEN;
    str = CROCKFORD_ALPHABET[mod] + str;
    current = Math.floor(current / ENCODING_LEN);
  }
  return str;
}

/**
 * Encodes 80 bits (10 bytes) of entropy into 16 Crockford Base32 characters.
 */
export function encodeEntropy(entropy: Uint8Array | Buffer): string {
  if (entropy.length !== ENTROPY_BYTES) {
    throw new Error(`Entropy must be exactly ${ENTROPY_BYTES} bytes (80 bits), received ${entropy.length} bytes`);
  }

  let str = '';
  // 10 bytes = two 5-byte chunks -> 8 chars each
  for (let chunk = 0; chunk < 2; chunk++) {
    const offset = chunk * 5;
    const b0 = entropy[offset];
    const b1 = entropy[offset + 1];
    const b2 = entropy[offset + 2];
    const b3 = entropy[offset + 3];
    const b4 = entropy[offset + 4];

    str += CROCKFORD_ALPHABET[(b0 >> 3) & 0x1f];
    str += CROCKFORD_ALPHABET[((b0 & 0x07) << 2) | ((b1 >> 6) & 0x03)];
    str += CROCKFORD_ALPHABET[(b1 >> 1) & 0x1f];
    str += CROCKFORD_ALPHABET[((b1 & 0x01) << 4) | ((b2 >> 4) & 0x0f)];
    str += CROCKFORD_ALPHABET[((b2 & 0x0f) << 1) | ((b3 >> 7) & 0x01)];
    str += CROCKFORD_ALPHABET[(b3 >> 2) & 0x1f];
    str += CROCKFORD_ALPHABET[((b3 & 0x03) << 3) | ((b4 >> 5) & 0x07)];
    str += CROCKFORD_ALPHABET[b4 & 0x1f];
  }
  return str;
}

/**
 * Generates an opaque, type-identifiable, time-ordered, collision-resistant canonical ID.
 *
 * Structure:
 * ft_<type>_<26-character Crockford Base32 ULID payload>
 *
 * Types:
 * - ft_comp_<26 Crockford chars>
 * - ft_team_<26 Crockford chars>
 * - ft_fix_<26 Crockford chars>
 *
 * The payload comprises:
 * - 48-bit millisecond timestamp (10 characters, big-endian)
 * - 80-bit cryptographically secure randomness (16 characters)
 */
export function generateCanonicalId(
  type: CanonicalEntityType,
  options?: GenerateCanonicalIdOptions
): string {
  const timestamp = options?.timestamp !== undefined ? options.timestamp : Date.now();
  const entropy = options?.entropy !== undefined ? options.entropy : randomBytes(ENTROPY_BYTES);

  const timePart = encodeTime(timestamp);
  const entropyPart = encodeEntropy(entropy);

  return `ft_${type}_${timePart}${entropyPart}`;
}
