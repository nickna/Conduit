# syntax=docker/dockerfile:1

# Shared Node/npm configuration for development and production builds.
FROM node:22-alpine AS node-base
WORKDIR /app
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm config set fetch-timeout 600000 && \
    npm config set fetch-retry-mintimeout 20000 && \
    npm config set fetch-retry-maxtimeout 120000 && \
    npm config set registry https://registry.npmjs.org/

# Install WebAdmin dependencies from its standalone lockfile.
FROM node-base AS webadmin-deps
RUN mkdir -p /app/WebAdmin && chown -R node:node /app/WebAdmin
COPY --chown=node:node WebAdmin/package.json WebAdmin/package-lock.json ./WebAdmin/
WORKDIR /app/WebAdmin
ENV HUSKY=0
USER node
RUN --mount=type=cache,target=/home/node/.npm,uid=1000,gid=1000 npm ci --no-audit --no-fund

# Development image: dependencies and initial source are baked into the image.
# Compose Watch synchronizes later source edits without exposing node_modules to
# the Windows host filesystem.
FROM webadmin-deps AS dev
COPY --chown=node:node WebAdmin /app/WebAdmin
RUN mkdir -p /app/WebAdmin/.next
WORKDIR /app/WebAdmin
ENV NODE_ENV=development
ENV PORT=3000
ENV HOSTNAME=0.0.0.0
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=15s --start-period=90s --retries=5 \
  CMD node -e "require('http').get('http://127.0.0.1:3000/api/health',r=>process.exit(r.statusCode===200?0:1)).on('error',()=>process.exit(1))"
CMD ["npm", "run", "dev"]

# Production build reuses the locked dependency layer.
FROM webadmin-deps AS builder
COPY --chown=node:node WebAdmin /app/WebAdmin
WORKDIR /app/WebAdmin
RUN mkdir -p public && npm run build

# Production stage - minimal image using Next.js standalone output.
FROM node:22-alpine AS runner
WORKDIR /app
RUN addgroup -g 1001 -S nodejs && adduser -S nextjs -u 1001
ENV NODE_ENV=production
ENV PORT=3000
ENV HOSTNAME=0.0.0.0
ENV NEXT_TELEMETRY_DISABLED=1
COPY --from=builder --chown=nextjs:nodejs /app/WebAdmin/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/WebAdmin/.next/static ./.next/static
COPY --from=builder --chown=nextjs:nodejs /app/WebAdmin/public ./public
USER nextjs
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:3000/api/health || exit 1
CMD ["node", "server.js"]
