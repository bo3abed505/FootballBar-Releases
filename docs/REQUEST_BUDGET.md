# Request Budget & Rate Limiting Strategy

## Purpose
This document establishes the API call budget, caching strategies, and dynamic polling algorithms to stay strictly within external provider rate limits (such as API-Football plan quotas).

## Key Strategies
- Adaptive polling intervals based on match state (scheduled, pre-match, live, half-time, full-time).
- Multi-tier caching (Redis/In-Memory backend cache + ETag client cache).
- Request coalescing across active clients to prevent duplicate provider queries.
