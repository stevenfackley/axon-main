# Security Policy

## Overview

**Axon** handles sensitive biometric and health data. Security is not a feature — it is a fundamental constraint. This document describes our supported versions, the disclosure process for vulnerabilities, and our security architecture.

---

## Supported Versions

| Version | Supported |
|---|---|
| `main` (latest) | ✅ Active security support |
| `dev` (pre-release) | ⚠️ Best-effort |
| All tagged releases | See release notes |

---

## Reporting a Vulnerability

> ⚠️ **Do NOT open a public GitHub Issue for security vulnerabilities.** Doing so could expose users to risk before a fix is available.

### Responsible Disclosure Process

1. **Open a [GitHub Security Advisory](https://github.com/stevenfackley/axon-main/security/advisories/new)** (private, visible only to maintainers).
2. Include as much detail as possible:
   - A description of the vulnerability
   - Steps to reproduce
   - Potential impact (data exposure, privilege escalation, integrity bypass, etc.)
   - Any proof-of-concept code (do **not** include real biometric data)
3. You will receive an acknowledgment within **48 hours**.
4. We aim to provide a fix or mitigation within **14 days** for critical issues, **30 days** for medium/low.
5. You will be credited in the security advisory and CHANGELOG unless you prefer to remain anonymous.

---

## Security Architecture

Axon's security model is designed around three non-negotiable principles:

### 1. Zero-Knowledge Encryption

- The SQLite database is encrypted at rest using **AES-256 via SQLCipher**.
- The encryption key is derived exclusively from the device's **TPM** (Windows) or **Secure Enclave** (iOS/Android) via the `IHardwareVault` interface.
- **No key is ever stored in software, config files, or environment variables.**
- The `MockHardwareVault` used in development must never be used in production builds.

### 2. HIPAA-Compliant Audit Logging

- Every read and write operation on biometric data is intercepted by `AuditLoggingDecorator`.
- Audit records are written to an immutable `AuditLog` table in the same transaction.
- Audit entries include: timestamp, operation type, record identifier, and actor context.

### 3. Air-Gap Integrity

- Axon contains **zero third-party telemetry, analytics, or crash-reporting SDKs**.
- All outbound network I/O can be disabled with the Air-Gap toggle.
- The CI pipeline enforces a dependency audit to flag any new packages that introduce outbound HTTP calls.

---

## Known Security Constraints

| Constraint | Status |
|---|---|
| `MockHardwareVault` bypasses TPM — for dev only | ⚠️ Never ship in production |
| gRPC Sovereign Sync not yet implemented | 🚧 In progress — no sync surface yet |
| Mobile Secure Enclave integration pending | 🚧 In progress |

---

## Threat Model

| Threat | Mitigation |
|---|---|
| **Physical device compromise** | AES-256 database encryption; key in TPM/Enclave |
| **Data exfiltration via network** | Air-gap mode; no outbound calls from Core |
| **Malicious dependency** | Dependency audit in CI; no auto-upgrade of transitive deps |
| **Log/debug data leakage** | PII Shield — biometric values masked in `ToString()` |
| **Key extraction** | Keys stored only in hardware vault, never in memory longer than needed |
| **Supply chain attack** | Pinned SDK version (`global.json`), no unreviewed dependencies |

---

## Security-Related Coding Rules

These are **hard requirements** enforced during code review:

- All new `IRepository` implementations must use `AuditLoggingDecorator` and `EncryptionDecorator`.
- All `ToString()` and logging methods on domain objects must mask `Value` fields.
- `IHardwareVault` must be used for all cryptographic key derivation — no `RNGCryptoServiceProvider` or static keys.
- No package that makes outbound HTTP calls may be added to `Axon.Core`.
- AOT publish must succeed — no reflection-based security libraries.

---

## Encryption Key Destruction ("Nuclear Option")

Users have the ability to permanently destroy their data by deleting the hardware-backed key via the **Sovereign Settings** screen. This renders the encrypted database permanently unreadable without any recovery path. This is by design.

---

*Security vulnerabilities in Axon represent a direct risk to user health privacy. We treat them with the highest priority.*
