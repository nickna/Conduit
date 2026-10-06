# Conduit WebAdmin Documentation

## Overview

This directory contains architecture, API-boundary, operations, and security documentation for WebAdmin.

## Documentation Index

### 📐 [Architecture](./ARCHITECTURE.md)
Detailed overview of the WebAdmin architecture, including:
- Local Admin and Gateway API boundaries
- Contract-generated wire types and data flow
- Authentication and security architecture
- Deployment architecture with Docker

### 🔑 [Virtual Key Management](./VIRTUAL-KEY-MANAGEMENT.md)
Complete guide to WebAdmin virtual key system:
- How ephemeral virtual keys work
- Server-side provisioning and browser caching
- Security model and best practices

### 🔌 [Admin API Boundary](./ADMIN_API_BOUNDARY.md)
The contract-derived, WebAdmin-owned integration with the Admin service.

### 🔄 [Admin Contract-Read Migration](./ADMIN_CONTRACT_READ_MIGRATION.md)
Maintenance rules for the completed generated-contract transport.

### 🔌 [Gateway API Boundary](./GATEWAY_API_BOUNDARY.md)
The focused local Gateway transport, streaming behavior, and generated wire types.

### 🔧 [Troubleshooting](./TROUBLESHOOTING.md)
Solutions to common issues:
- Authentication problems
- API boundary issues
- Network and CORS errors
- Performance optimization
- Debugging tools and techniques

### 🔒 [Security Considerations](./SECURITY-CONSIDERATIONS.md)
Important security information:
- Authentication key separation
- Virtual key exposure risks
- Security layers and best practices
- Incident response procedures

## Quick Links

### For Developers
- [Admin API Boundary](./ADMIN_API_BOUNDARY.md) - Admin integration rules
- [Admin Contract Transport](./ADMIN_CONTRACT_READ_MIGRATION.md) - Generated-operation maintenance rules
- [Gateway API Boundary](./GATEWAY_API_BOUNDARY.md) - Gateway integration rules
- [Architecture](./ARCHITECTURE.md) - Understand the system design
- [Troubleshooting](./TROUBLESHOOTING.md) - Common issues and solutions

### For Administrators
- [Virtual Key Management](./VIRTUAL-KEY-MANAGEMENT.md) - Managing API keys
- [Security Considerations](./SECURITY-CONSIDERATIONS.md) - Security best practices
- [Troubleshooting](./TROUBLESHOOTING.md) - Debugging authentication issues

### For Security Teams
- [Security Considerations](./SECURITY-CONSIDERATIONS.md) - Security overview
- [Virtual Key Management](./VIRTUAL-KEY-MANAGEMENT.md) - Key security model

## Getting Started

1. **New to WebAdmin?** Start with [Architecture](./ARCHITECTURE.md)
2. **Changing API calls?** Read the [Admin](./ADMIN_API_BOUNDARY.md) or [Gateway](./GATEWAY_API_BOUNDARY.md) boundary guide
3. **Having issues?** Check [Troubleshooting](./TROUBLESHOOTING.md)
4. **Security concerns?** Review [Security Considerations](./SECURITY-CONSIDERATIONS.md)

## Contributing

When adding new documentation:
1. Use clear, descriptive filenames
2. Include a table of contents for long documents
3. Add code examples where appropriate
4. Update this index file
5. Test all code examples

## Additional Resources

- [WebAdmin README](../README.md) - Main project documentation
- [Conduit Documentation](https://github.com/nickna/Conduit/docs) - Platform documentation
