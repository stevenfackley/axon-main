# Axon Data Model & Privacy Architecture

This document describes exactly what Axon stores, where, and how it is protected.
It exists because you should not have to trust a marketing claim about your biometric
data — you should be able to read the schema and verify it yourself.

> **Where your data lives:** a single encrypted SQLite database on *your* machine, under
> your local app-data directory (`%LOCALAPPDATA%/Axon` on Windows). No account. No cloud.
> No copy on our servers — we don't have servers in the data path.

---

## Axon Common Schema (ACS)

Every measurement from every source — Whoop, Garmin, Oura, Apple Health, or a manual
CSV/JSON import — is normalized at the ingestion boundary into one immutable record,
`BiometricEvent`. No vendor-native units or shapes survive past the normalization mapper.

| Field | Type | Notes |
|---|---|---|
| `Id` | GUID | Deterministic, derived from `DeviceId + Timestamp + Type` (dedupes re-imports). |
| `Timestamp` | UTC instant | Moment of measurement. |
| `Type` | `BiometricType` | ACS category (HeartRate, HeartRateVariability, SpO2, SleepEfficiency, StrainScore, …). |
| `Value` | double | Always in ACS canonical units — never a vendor-native unit. |
| `Unit` | string | Human-readable SI unit (`bpm`, `ms`, `%`, `W`). |
| `Source` | `SourceMetadata` | Provenance: device, vendor, firmware, confidence, ingestion time. |
| `CorrelationId` | string? | Optional; groups events from one sync batch (Transactional Outbox). |

`BiometricEvent.ToString()` deliberately redacts the value and device identity (PII shield),
so biometric data cannot leak into logs.

---

## Tables

The local SQLite database contains three tables:

### `BiometricEvents`
The normalized ACS events above (stored flat for AOT-friendly EF mapping). The `DeviceId`
column is **encrypted at rest** with AES-256-GCM (see Encryption below); all other columns
are stored plainly inside the SQLCipher-encrypted database file.

### `SyncOutbox`
Transactional Outbox entries. When events are ingested, their outbox rows are written in the
**same database transaction** — no event is ever persisted without its sync record, and no
network I/O happens inside the transaction.

### `AuditLog`
An append-only, immutable record of every read and write against biometric data (HIPAA-style
audit trail). Caller identity is stored only as a SHA-256 hash — never a raw username —
satisfying data-minimization. You can inspect this table to see exactly what touched your data.

---

## Encryption & integrity

Axon uses two independent layers so a single mistake can't expose plaintext biometrics:

1. **Whole-database encryption** — the SQLite file is opened through SQLCipher (AES-256).
2. **Field-level encryption** — sensitive identity fields (`DeviceId`) are additionally
   encrypted with AES-256-GCM via the mandatory `EncryptionDecorator` before they reach
   the database, and decrypted on read. The format is `nonce[12] || tag[16] || ciphertext`.

Encryption keys are derived from a hardware-backed key vault (`IHardwareVault`):
- **Windows** → DPAPI / TPM-sealed key (per-user).
- A development mock vault is used only in non-hardware environments and is clearly labeled
  as such in **Settings → Vault**.

OAuth tokens for wearable APIs are stored in separate, individually AES-256-GCM-encrypted
files (one per vendor) — never in plaintext, and never in the same cryptographic envelope as
your biometric data.

---

## Your data is portable

- **Export everything** — one click produces your full data set as CSV/JSON you can take
  anywhere. There is no lock-in; ownership is the point.
- **Destroy everything** — the GDPR "nuclear option" destroys the hardware-bound key, after
  which the encrypted database is permanently unreadable. No recovery, by design.

---

## How insights are computed

All analysis (anomaly detection, recovery forecasting, correlation, training load) runs
**locally** via ML.NET — your biometric data is never sent anywhere for processing. Each
insight surfaces its own method, confidence, and sample size so you can judge it rather than
trust a black box. Methodology notes live alongside each feature in-app.
