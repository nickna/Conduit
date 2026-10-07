# Conduit WebAdmin

Next.js-based web interface for the Conduit LLM Platform, built with React, TypeScript, and Mantine.

## Architecture Overview

WebAdmin owns focused, contract-backed clients for its API operations. Ordinary HTTP requests use
`openapi-fetch` with generated Admin and Gateway `paths` types; application-owned hooks and façades
remain stable.

### Local API boundaries
- **Gateway boundary**: LLM operations use ephemeral virtual-key authentication
- **Admin boundary**: Administrative operations use master-key authentication

### Authentication Flow
1. Admin logs in through Clerk authentication
2. WebAdmin verifies user has `siteadmin: true` in Clerk metadata
3. Server uses `CONDUIT_API_TO_API_BACKEND_AUTH_KEY` for backend API calls
4. All admin operations use master key authentication server-side

### Key Benefits
- 🚀 **Focused API clients**: No general-purpose package dependency in the application
- 🔄 **React Query Integration**: Built-in caching, optimistic updates
- 🔐 **Secure Authentication**: Virtual keys for client-side operations
- 📦 **Simplified Codebase**: Less code to maintain

## Features

- 🚀 **Next.js 16** with App Router and TypeScript
- 🎨 **Mantine UI** component library with custom theme
- 🔗 **Local API Integration** with focused Admin and Gateway clients
- ⚡ **Real-time Updates** via SignalR
- 📊 **State Management** with Zustand and React Query
- 🎯 **Type Safety** throughout the application
- 🔐 **Automatic Virtual Key Management** for secure API access

## Quick Start

1. **Install dependencies:**
   ```bash
   npm install
   ```

2. **Configure environment:**
   ```bash
   cp .env.example .env.local
   # Edit .env.local with your configuration
   ```

3. **Start development server:**
   ```bash
   npm run dev
   ```

4. **Open in browser:**
   ```
   http://localhost:3000
   ```

## Dependency maintenance

When changing dependency overrides, follow the [verified lockfile refresh procedure](../docs/operations/npm-lockfile-overrides.md).
`npm run check:dependency-overrides` checks both the locked coverage parser and the
actual installed consumer; a successful lockfile-only npm command can retain an
obsolete nested dependency (#1486).

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `NEXT_PUBLIC_CONDUIT_ADMIN_API_URL` | Admin API endpoint | `http://localhost:5002` |
| `NEXT_PUBLIC_CONDUIT_CORE_API_URL` | Gateway API endpoint | `http://localhost:5000` |
| `CONDUIT_API_TO_API_BACKEND_AUTH_KEY` | Backend service authentication key | `alpha` |
| `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` | Clerk publishable key for authentication | Required |
| `CLERK_SECRET_KEY` | Clerk secret key for authentication | Required |
| `NEXT_PUBLIC_ENABLE_REAL_TIME_UPDATES` | Enable SignalR features | `true` |
| `ERROR_REPORTING_URL` | Server-side ingestion URL for scrubbed browser errors; empty disables reporting | Disabled |
| `ERROR_REPORTING_TOKEN` | Optional bearer token used only by the same-origin reporting proxy | None |
| `ERROR_REPORTING_SAMPLE_RATE` | Deterministic fraction of unique errors forwarded (`0`-`1`) | `1` |
| `ERROR_REPORTING_RELEASE` | Release/build identifier attached by the server proxy | `unknown` |
| `ERROR_REPORTING_ENVIRONMENT` | Deployment environment attached by the server proxy | `NODE_ENV` |

Browser errors are deduplicated and scrubbed before being posted to `/api/error-reports`; the
proxy scrubs them again and never exposes its backend URL or token to the browser. Prompts,
generated content, request/response bodies, credentials, authorization data, and URL query strings
are removed. Reporting and transport failures are best-effort and never interrupt the UI.

Production builds emit browser source maps for operator symbolication. The bundled Nginx
edge denies public `.map` requests; retain or upload the maps from the build artifact to the
configured reporting system under the same `ERROR_REPORTING_RELEASE` value.

## Project Structure

```
src/
├── app/                    # Next.js App Router pages
├── components/             # Reusable UI components
│   ├── ui/                # Base UI components
│   ├── forms/             # Form components
│   ├── charts/            # Data visualization
│   ├── layout/            # Layout components
│   └── realtime/          # SignalR components
├── hooks/                 # Custom React hooks
│   ├── signalr/           # SignalR-specific hooks
│   └── api/               # API integration hooks
├── lib/                   # Utilities and configurations
│   ├── admin-api/         # WebAdmin-owned Admin API boundary
│   ├── gateway-api/       # WebAdmin-owned Gateway API boundary
│   ├── auth/              # Authentication utilities
│   ├── signalr/           # SignalR connection management
│   └── utils/             # Helper functions
├── stores/                # Zustand stores
├── types/                 # TypeScript type definitions
└── styles/                # Mantine theme customization
```

## Available Scripts

- `npm run dev` - Start development server on port 3000
- `npm run build` - Build for production
- `npm run start` - Start production server
- `npm run lint` - Run ESLint
- `npm run type-check` - Run TypeScript type checking

## Development

### Adding New Pages

1. Create page component in `src/app/[page-name]/page.tsx`
2. Add navigation links in the layout components
3. Implement API integration through `src/lib/admin-api` or `src/lib/gateway-api`

### API integration

Admin and Gateway calls use WebAdmin-owned clients and contract-generated wire types. See
`docs/ADMIN_API_BOUNDARY.md` and `docs/GATEWAY_API_BOUNDARY.md`; do not add package or workspace SDK
imports to application code.

### Real-time Features

SignalR connections are managed centrally and provide real-time updates for:
- Virtual key spend tracking
- Provider health monitoring
- Task progress (image/video generation)
- Navigation state updates

## Video Generation

The WebAdmin provides a comprehensive video generation interface with real-time progress tracking through its local Gateway boundary.

### Features
- ✨ Real-time progress updates via SignalR
- 🔄 Automatic fallback to polling if connection fails
- 📊 Smooth progress bar animations
- 💬 Descriptive status messages
- 🎯 Queue management for multiple videos
- 🎨 Visual preview of generated videos

### Usage

```typescript
import { useVideoGeneration } from '@/app/videos/hooks/useVideoGeneration';

function VideoGenerator() {
  const { generateVideo, isGenerating, error } = useVideoGeneration();
  
  const handleGenerate = async () => {
    await generateVideo({
      prompt: "A serene lake at sunset",
      settings: { 
        model: "minimax-video-01",
        duration: 6,
        size: "1280x720",
        fps: 30
      }
    });
  };

  return (
    <div>
      <button onClick={handleGenerate} disabled={isGenerating}>
        Generate Video
      </button>
      {error && <div>Error: {error}</div>}
    </div>
  );
}
```

### Progress Tracking

The video generation hook automatically handles:
1. **SignalR Connection**: Establishes real-time connection for updates
2. **Progress Events**: Receives percentage, status, and messages
3. **Fallback Logic**: Switches to polling if SignalR fails
4. **State Management**: Updates UI with progress information

### Configuration

Enable/disable progress tracking features:

```typescript
// Use the enhanced video generation with progress tracking
const { generateVideo } = useVideoGeneration({
  useProgressTracking: true,  // Enable Gateway progress tracking
  fallbackToPolling: true,    // Enable polling fallback
});
```

### Video Queue

The WebAdmin maintains a queue of video generation tasks:
- View all pending, running, and completed videos
- Cancel in-progress generations
- Download completed videos
- Retry failed generations

## Docker Deployment

The WebAdmin is configured to run as part of the ConduitLLM Docker stack:

```bash
# Start the development stack from the repository root
./scripts/dev/start-dev.ps1 -WebAdmin

# The WebAdmin will be available at http://localhost:3000
```

### Docker Environment Variables

The following environment variables are configured in docker-compose.yml:

- `NEXT_PUBLIC_CONDUIT_CORE_API_URL`: Public URL for Gateway API (browser access)
- `NEXT_PUBLIC_CONDUIT_ADMIN_API_URL`: Public URL for Admin API (browser access)
- `CONDUIT_API_BASE_URL`: Internal URL for Gateway API (server-side)
- `CONDUIT_ADMIN_API_BASE_URL`: Internal URL for Admin API (server-side)
- `CONDUIT_API_EXTERNAL_URL`: External URL for SignalR Gateway API connections
- `CONDUIT_ADMIN_API_EXTERNAL_URL`: External URL for SignalR Admin API connections
- `CONDUIT_API_TO_API_BACKEND_AUTH_KEY`: Master key for Admin API authentication
- `SESSION_SECRET`: Secret key for session encryption
- `REDIS_URL`: Redis connection string for session storage
- `CONDUIT_GRAFANA_ADMIN_PASSWORD`: Required private Grafana bootstrap secret
- `CONDUIT_GRAFANA_ROOT_URL`: Public Grafana subpath URL (defaults to `http://localhost:3000/grafana/`)

The Compose Nginx edge service runs on port 3000 and routes `/grafana/` to the private, read-only Grafana service. The WebAdmin and Grafana containers are not published directly. Grafana access follows the same Clerk `siteadmin` check as WebAdmin, with the same development authentication bypass.

## Contributing

1. Follow the existing code patterns and conventions
2. Use TypeScript for all new code
3. Add appropriate tests for new features
4. Update documentation as needed

## License

ISC
