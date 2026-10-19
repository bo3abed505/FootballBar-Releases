export class ProviderError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ProviderError';
  }
}

export class ProviderChallengeError extends ProviderError {
  constructor(message: string = 'Provider returned an HTML challenge or 403 Forbidden') {
    super(message);
    this.name = 'ProviderChallengeError';
  }
}

export class ProviderRateLimitError extends ProviderError {
  public retryAfter?: number;
  constructor(message: string = 'Provider rate limit exceeded', retryAfter?: number) {
    super(message);
    this.name = 'ProviderRateLimitError';
    this.retryAfter = retryAfter;
  }
}

export class ProviderSchemaError extends ProviderError {
  constructor(message: string = 'Provider payload failed Zod schema validation') {
    super(message);
    this.name = 'ProviderSchemaError';
  }
}

export class ProviderNetworkError extends ProviderError {
  constructor(message: string = 'Transient network error communicating with provider') {
    super(message);
    this.name = 'ProviderNetworkError';
  }
}

export class ProviderResourceNotFoundError extends ProviderError {
  constructor(message: string = 'Provider resource not found (404)') {
    super(message);
    this.name = 'ProviderResourceNotFoundError';
  }
}
