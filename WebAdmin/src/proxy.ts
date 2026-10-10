import { clerkMiddleware, createRouteMatcher } from '@clerk/nextjs/server';
import { NextResponse, type NextRequest, type NextFetchEvent } from 'next/server';

// Public routes that don't require authentication
const isPublicRoute = createRouteMatcher([
  '/access-denied',
  '/api/auth/grafana', // Returns explicit status codes for the Nginx auth subrequest
]);

const authenticatedProxy = clerkMiddleware(async (auth, req) => {
  // Skip all auth in development when explicitly disabled
  if (process.env.CLERK_AUTH_ENABLED !== 'true' && process.env.NODE_ENV === 'development') {
    return NextResponse.next();
  }

  if (!isPublicRoute(req)) {
    // Get auth state
    const { userId, sessionClaims, redirectToSignIn } = await auth();
    
    // If not authenticated, redirect to sign-in
    if (!userId) {
      return redirectToSignIn();
    }
    
    // Check if user has admin access
    const metadata = sessionClaims?.metadata as { siteadmin?: boolean } | undefined;
    const isAdmin = metadata?.siteadmin === true;
    
    // If not admin, redirect to access-denied
    if (!isAdmin) {
      return NextResponse.redirect(new URL('/access-denied', req.url));
    }
  }
}, { jwtKey: process.env.CLERK_JWT_KEY });

export default function proxy(req: NextRequest, event: NextFetchEvent) {
  // Container liveness must work independently of identity-provider availability.
  if (req.nextUrl.pathname === '/api/health') return NextResponse.next();
  return authenticatedProxy(req, event);
}

export const config = {
  matcher: ['/((?!.*\\..*|_next).*)', '/', '/(api|trpc)(.*)'],
};
