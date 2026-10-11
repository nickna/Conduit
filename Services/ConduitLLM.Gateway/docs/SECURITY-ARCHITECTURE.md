# Gateway API Security Architecture

## Overview

The Gateway API has been enhanced with a comprehensive security architecture that provides multiple layers of protection including Virtual Key authentication, IP-based brute force protection, rate limiting, and security headers.

## Key Security Features

### 1. Virtual Key Authentication (URGENT FIX)

Previously, the Gateway API endpoints were **completely unprotected**. The new architecture enforces Virtual Key authentication on all LLM endpoints.

**Middleware**: `VirtualKeyAuthenticationMiddleware`
- Validates Virtual Keys from multiple headers (Authorization, api-key, X-API-Key, X-Virtual-Key)
- Checks if key is enabled and not expired
- Validates budget limits and model access
- Populates authentication context for downstream use

**Supported Headers**:
```
Authorization: Bearer condt_xxxx
api-key: condt_xxxx
X-API-Key: condt_xxxx
X-Virtual-Key: condt_xxxx (legacy)
```

### 2. IP-Based Brute Force Protection

**Key Innovation**: Tracks failed authentication attempts **per IP across ALL Virtual Keys** to prevent attackers from cycling through multiple keys.

**Features**:
- Bans IP after 10 failed attempts (configurable)
- Ban duration: 30 minutes (configurable)
- Shared tracking with Admin API and WebAdmin via Redis
- Automatic clearing on successful authentication

**Protection Against**:
- Single-source brute force attacks ✅
- Key enumeration attacks ✅
- Distributed attacks (requires Cloudflare/CDN) ⚠️

### 3. Rate Limiting

**Two-Layer Approach**:

1. **IP-Based Rate Limiting**
   - Default: 1000 requests/minute per IP
   - Prevents abuse from single sources
   - Applied before Virtual Key validation

2. **Virtual Key Rate Limiting**
   - Enforces per-key RPM/RPD limits from database
   - Tracks usage with sliding windows
   - Returns appropriate headers (X-RateLimit-Limit, X-RateLimit-Remaining)

### 4. IP Filtering

**Database-Driven Rules**:
- Whitelist/Blacklist support with CIDR notation
- Cached for performance (5-minute TTL)
- Private IP auto-allow option

**Environment-Based Rules**:
- Quick deployment filtering via environment variables
- Supports comma-separated IP/CIDR lists

**Client IP Resolution (trusted proxies)** — ⚠️ security-critical:
- The client IP used by IP filtering, rate limiting, and auto-banning is resolved once, up front, by
  ASP.NET Core's `ForwardedHeadersMiddleware`. It honors `X-Forwarded-For` / `CF-Connecting-IP` **only**
  when the connecting peer is an explicitly-configured trusted proxy; otherwise forwarded headers are
  ignored and the direct socket peer is used. A direct client therefore **cannot spoof its source IP** to
  bypass filters, evade bans, or poison another IP's rate-limit / ban counters.
- **Disabled by default** (`CONDUIT_TRUSTED_PROXY_ENABLED=false`). Enable only when the API sits behind a
  proxy/CDN you control.
- **Cloudflare:** set `CONDUIT_TRUSTED_PROXY_ENABLED=true` and `CONDUIT_TRUSTED_PROXY_TRUST_CLOUDFLARE=true`.
  The middleware then trusts Cloudflare's published ranges and reads `CF-Connecting-IP`.
- **⚠️ Deployment prerequisite:** when trusting a CDN you MUST also restrict origin ingress to that CDN's IPs
  (firewall / security group) or require a shared-secret header (Cloudflare Authenticated Origin Pulls).
  Otherwise an attacker can bypass the CDN by connecting to the origin directly and forge the client IP —
  the application-level trust model provides no protection without that network-level lockdown.

### 5. Security Headers

**API-Optimized Headers**:
- `X-Content-Type-Options: nosniff` - Prevent MIME sniffing
- `Strict-Transport-Security` - HTTPS enforcement
- Removes server identification headers
- Custom headers support

## Configuration

### Environment Variables

```bash
# IP Filtering
CONDUIT_CORE_IP_FILTERING_ENABLED=true
CONDUIT_CORE_IP_FILTER_MODE=permissive
CONDUIT_CORE_IP_FILTER_ALLOW_PRIVATE=true
CONDUIT_CORE_IP_FILTER_WHITELIST=192.168.1.0/24,10.0.0.0/8
CONDUIT_CORE_IP_FILTER_BLACKLIST=192.168.1.100

# IP-Based Rate Limiting
CONDUIT_CORE_RATE_LIMITING_ENABLED=true
CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS=1000
CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS=60

# Failed Authentication Protection
CONDUIT_CORE_MAX_FAILED_AUTH_ATTEMPTS=10
CONDUIT_CORE_AUTH_BAN_DURATION_MINUTES=30
CONDUIT_CORE_TRACK_FAILED_AUTH_ACROSS_KEYS=true

# Virtual Key Enforcement
CONDUIT_CORE_ENFORCE_VKEY_RATE_LIMITS=true
CONDUIT_CORE_ENFORCE_VKEY_BUDGETS=true
CONDUIT_CORE_ENFORCE_VKEY_MODELS=true

# Distributed Tracking (shared with Admin/WebAdmin)
CONDUIT_SECURITY_USE_DISTRIBUTED_TRACKING=true

# Trusted proxy / client-IP resolution (shared by Gateway + Admin; OFF by default)
CONDUIT_TRUSTED_PROXY_ENABLED=false
CONDUIT_TRUSTED_PROXY_TRUST_CLOUDFLARE=false     # trust Cloudflare ranges + read CF-Connecting-IP
CONDUIT_TRUSTED_PROXY_USE_CF_CONNECTING_IP=true
CONDUIT_TRUSTED_PROXY_KNOWN_NETWORKS=            # extra trusted proxy CIDRs (comma-separated)
CONDUIT_TRUSTED_PROXY_KNOWN_PROXIES=             # extra trusted proxy IPs (comma-separated)
CONDUIT_TRUSTED_PROXY_FORWARD_LIMIT=1
```

### Discovery Rate Limits

Configure discovery limits under `CoreApi:Security:RateLimiting:Discovery` in
`appsettings.json`. The general IP limiter must be enabled for discovery limits to
run. Discovery has its own quotas and windows, and `Discovery.Enabled=false`
disables discovery checks while retaining the general IP limit:

```json
{
  "CoreApi": {
    "Security": {
      "RateLimiting": {
        "Discovery": {
          "Enabled": true,
          "MaxRequests": 500,
          "WindowSeconds": 300,
          "MaxCapabilityChecksPerModel": 20,
          "CapabilityCheckWindowSeconds": 600,
          "DiscoveryPaths": [
            "/v1/conduit/discovery/",
            "/v1/models/",
            "/capabilities/"
          ]
        }
      }
    }
  }
}
```

The example shows the defaults. Omitted settings retain their defaults; a configured
`DiscoveryPaths` array replaces the default routes. Paths match a route root and its
descendants, without matching unrelated prefixes or substrings. A trailing slash on
a configured root is optional, so `/v1/models/` also covers `/v1/models`.
Roots must be nonblank absolute paths starting with `/`; invalid configuration is
rejected during startup. An empty array disables discovery route matching.

The standard ASP.NET Core environment-variable form is supported, for example
`CoreApi__Security__RateLimiting__Discovery__MaxRequests=7` and
`CoreApi__Security__RateLimiting__Discovery__DiscoveryPaths__0=/v1/models`.
The flat `CONDUIT_CORE_RATE_LIMITING_ENABLED`, `CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS`,
and `CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS` variables continue to take precedence
over the corresponding parent `CoreApi:Security:RateLimiting` settings; they do not
override the discovery subsection.

## Shared Security Tracking

The Gateway API shares security data with Admin API and WebAdmin through Redis:

### Redis Key Structure
```
rate_limit:core-api:{ip}      - IP rate limiting
failed_login:{ip}             - Failed auth attempts (cross-service)
ban:{ip}                      - Banned IPs (cross-service)
vkey_rate:rpm:{keyId}         - Virtual Key RPM tracking
vkey_rate:rpd:{keyId}         - Virtual Key RPD tracking
```

### Shared Ban List
When an IP is banned by any service (Core, Admin, WebAdmin), all services respect the ban.

## Request Flow

1. **Security Headers** - Added to all responses
2. **Virtual Key Authentication** - Extract and validate key
3. **Security Checks** - IP bans, rate limits, IP filters
4. **Rate Limiter** - Apply Virtual Key specific limits
5. **Request Processing** - Handle the API request

## Migration Impact

### Breaking Changes
- **All endpoints now require authentication** (previously open)
- Virtual Key header is mandatory for LLM endpoints

### Backward Compatibility
- Supports multiple header formats
- Legacy X-Virtual-Key header still works
- Existing Virtual Keys continue to function

## Security Best Practices

### For Single-Source Attacks
1. **Enable IP-based rate limiting** - Prevents high-volume attacks
2. **Configure failed auth limits** - 10-20 attempts recommended
3. **Monitor ban list** - Check Redis for banned IPs
4. **Use IP filtering** - Restrict to known IP ranges when possible

### For Distributed Attacks
1. **Use Cloudflare/CDN** - Application-level protection has limits
2. **Enable Cloudflare features**:
   - Rate limiting rules
   - Bot fight mode
   - Geographic restrictions
   - DDoS protection
3. **Monitor usage patterns** - Identify anomalies early

## Monitoring and Troubleshooting

### Common HTTP Status Codes
- `401 Unauthorized` - Missing/invalid Virtual Key
- `403 Forbidden` - IP banned or filtered
- `429 Too Many Requests` - Rate limit exceeded

### Debug Logging
```bash
# Enable debug logging
Logging__LogLevel__ConduitLLM.Gateway.Services.SecurityService=Debug
Logging__LogLevel__ConduitLLM.Gateway.Middleware=Debug
```

### Check Security Status
```bash
# View banned IPs
redis-cli KEYS "ban:*"

# View failed login attempts
redis-cli KEYS "failed_login:*"

# Check rate limit status
redis-cli GET "rate_limit:core-api:192.168.1.100"

# Virtual Key rate limits
redis-cli KEYS "vkey_rate:*"
```

## Performance Considerations

- Security checks add ~1-2ms latency
- Redis caching minimizes database queries
- IP filter rules cached for 5 minutes
- Virtual Key validation cached for 60 seconds

## Future Enhancements

1. **Anomaly Detection** - ML-based pattern recognition
2. **Geo-blocking** - Country-level restrictions
3. **Advanced Rate Limiting** - Tiered limits by key type
4. **Security Analytics** - Detailed attack dashboards
