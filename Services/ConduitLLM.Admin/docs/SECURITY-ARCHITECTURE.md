# Admin API Security Architecture

## Overview

The Admin API has been enhanced with a comprehensive security architecture that provides multiple layers of protection while maintaining compatibility with existing API clients.

## Security Features

### 1. Unified Security Middleware

A single `SecurityMiddleware` handles all security checks in one pass:
- API key authentication
- IP filtering (environment and database-based)
- Rate limiting
- Failed authentication protection

### 2. API Key Authentication

- Primary header: `X-API-Key` (configurable)
- Alternative headers: `X-Master-Key` (backward compatibility)
- Uses `CONDUIT_API_TO_API_BACKEND_AUTH_KEY` environment variable
- Supports Ephemeral Master Keys (single-use, short-lived)
- Failed authentication attempts are tracked

### 3. IP Filtering

**Environment-based filtering:**
- Whitelist/blacklist with CIDR support
- Private IP detection (RFC 1918)
- Configurable modes: permissive (blacklist) or restrictive (whitelist)

**Database-based filtering:**
- Integrates with existing IP filter service
- Managed through Admin API endpoints

### 4. Rate Limiting

- Sliding window algorithm
- Configurable per-IP limits
- Excludes health and Swagger endpoints
- Returns appropriate headers (Retry-After, X-RateLimit-Limit)

### 5. Failed Authentication Protection

- Automatic IP banning after threshold
- Configurable ban duration
- Shared tracking with WebAdmin via Redis

### 6. Security Headers

- X-Content-Type-Options: nosniff
- X-XSS-Protection: 1; mode=block
- Strict-Transport-Security (HTTPS only)
- Removes server identification headers

`CONDUIT_ADMIN_SECURITY_HEADERS_ENABLED` defaults to `true`. Setting it to `false`
disables all header additions and removals by the security headers middleware,
including custom headers and the `X-Content-Type` / `X-API-Version` headers. Existing
headers and headers supplied by other middleware, endpoints, or the hosting server
are preserved. The aggregate switch takes precedence over individual switches;
when enabled, those switches still control their respective headers.

### 7. Ephemeral Master Key (EMK)

Provides one-time, short-lived authentication for Admin API operations and secure WebAdmin flows.

Key properties:
- Single-use tokens with 5-minute TTL
- Stored in Redis with automatic expiration and cleanup
- Token format: `emk_...` (URL-safe)

Generate an EMK:

```bash
curl -X POST "$ADMIN_API_BASE_URL/api/admin/auth/ephemeral-master-key" \
  -H "X-API-Key: $CONDUIT_API_TO_API_BACKEND_AUTH_KEY"
```

Response:

```json
{
  "ephemeralMasterKey": "emk_abc...",
  "expiresAt": "2025-01-01T00:00:00Z"
}
```

Use the EMK for the next request:

```bash
curl -H "X-API-Key: emk_abc..." "$ADMIN_API_BASE_URL/api/providers"
```

Validation behavior:
- Non-streaming requests: validated and marked consumed; key cannot be reused
- Streaming (e.g., SignalR/SSE): key is consumed and deleted once the connection is established; the connection remains authorized

Implementation notes:
- Backed by distributed cache with key prefix `ephemeral:master:`
- Authentication handler accepts EMK via `X-API-Key` or `X-Master-Key`

## Configuration

### Environment Variables

```bash
# IP Filtering
CONDUIT_ADMIN_IP_FILTERING_ENABLED=true
CONDUIT_ADMIN_IP_FILTER_MODE=permissive
CONDUIT_ADMIN_IP_FILTER_ALLOW_PRIVATE=true
CONDUIT_ADMIN_IP_FILTER_WHITELIST=192.168.1.0/24,10.0.0.0/8
CONDUIT_ADMIN_IP_FILTER_BLACKLIST=192.168.1.100

# Rate Limiting
CONDUIT_ADMIN_RATE_LIMITING_ENABLED=true
CONDUIT_ADMIN_RATE_LIMIT_MAX_REQUESTS=100
CONDUIT_ADMIN_RATE_LIMIT_WINDOW_SECONDS=60
CONDUIT_ADMIN_RATE_LIMIT_EXCLUDED_PATHS=/health,/swagger

# Failed Authentication Protection
CONDUIT_ADMIN_IP_BANNING_ENABLED=true
CONDUIT_ADMIN_MAX_FAILED_AUTH_ATTEMPTS=5
CONDUIT_ADMIN_AUTH_BAN_DURATION_MINUTES=30

# Distributed Tracking (shared with WebAdmin)
CONDUIT_SECURITY_USE_DISTRIBUTED_TRACKING=true

# Security Headers
CONDUIT_ADMIN_SECURITY_HEADERS_ENABLED=true
CONDUIT_ADMIN_SECURITY_HEADERS_X_CONTENT_TYPE_OPTIONS_ENABLED=true
CONDUIT_ADMIN_SECURITY_HEADERS_X_XSS_PROTECTION_ENABLED=true
CONDUIT_ADMIN_SECURITY_HEADERS_HSTS_ENABLED=true
CONDUIT_ADMIN_SECURITY_HEADERS_HSTS_MAX_AGE=31536000

# API Authentication (header names)
CONDUIT_ADMIN_API_KEY_HEADER=X-API-Key
CONDUIT_ADMIN_API_KEY_ALT_HEADERS=X-Master-Key
```

## Shared Security Tracking

The Admin API shares security tracking data with WebAdmin through Redis:

### Redis Key Structure

```
rate_limit:admin-api:{ip} - Rate limiting counters
failed_login:{ip} - Failed authentication attempts
ban:{ip} - Banned IPs
```

### Data Format

```json
{
  "bannedUntil": "2024-01-20T10:30:00Z",
  "failedAttempts": 5,
  "source": "admin-api",
  "reason": "Exceeded max failed authentication attempts"
}
```

## Integration with WebAdmin Security Dashboard

The WebAdmin Security Dashboard can display:
- Combined banned IPs from both services
- Failed authentication attempts across services
- Rate limiting statistics per service
- Source identification for security events



## Backward Compatibility

- Existing API clients continue to work without changes
- `X-API-Key` header is still supported
- `X-Master-Key` header supported for legacy clients
- Same authentication flow, enhanced with security features

## Performance Considerations

- Single middleware pass for all security checks
- Efficient Redis caching for distributed tracking
- Minimal overhead for authenticated requests
- Excluded paths bypass unnecessary checks

## Security Best Practices

1. **Enable IP Filtering**: Restrict access to known IP ranges
2. **Set Rate Limits**: Prevent API abuse
3. **Monitor Failed Attempts**: Review security dashboard regularly
4. **Use HTTPS**: Enable HSTS headers for secure transport
5. **Rotate API Keys**: Change `CONDUIT_API_TO_API_BACKEND_AUTH_KEY` periodically

## Troubleshooting

### Common Issues

1. **403 Forbidden**: Check IP filtering rules
2. **429 Too Many Requests**: Rate limit exceeded
3. **401 Unauthorized**: Invalid or missing API key
4. **Banned IP**: Check failed authentication attempts

### Debug Mode

Enable debug logging to see security decisions:
```bash
ASPNETCORE_ENVIRONMENT=Development
Logging__LogLevel__ConduitLLM.Admin.Services.SecurityService=Debug
```
