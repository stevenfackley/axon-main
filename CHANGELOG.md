# Changelog

All notable changes to **Axon** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- `LttbDownsampler` — Largest Triangle Three Buckets algorithm for rendering >24h datasets at 120fps without UI-thread heap allocations.
- `SkiaTelemetryChart` — SkiaSharp-based GPU chart control with SKSL stress-zone shader (`StressZoneShader.sksl`).
- `LocalInferenceService` — ML.NET on-device ONNX inference service implementing `IInferenceService`.
- `BiometricInputRow` — low-allocation input struct for ML.NET pipeline.
- `DashboardViewModel`, `SettingsViewModel`, `MainWindowViewModel` — Avalonia MVVM view models.
- Android `MainActivity` scaffold for Satellite app.
- iOS `Entitlements.plist` for Secure Enclave access.

### Changed
- N/A

### Fixed
- N/A

---

## [0.3.0] — 2026-02-01

### Added
- `IngestionOrchestrator` — `System.Threading.Channels`-backed ingestion pipeline implementing `IIngestionOrchestrator`.
- `AuditLoggingDecorator` — HIPAA-compliant decorator that intercepts all repository reads/writes and writes to the immutable `AuditLog` table.
- `EncryptionDecorator` — GDPR field-level encryption decorator for PII in the persistence layer.
- `DbAuditLogger` — concrete `IAuditLogger` implementation backed by EF Core.
- `MockHardwareVault` — development-time `IHardwareVault` implementation for local testing without a TPM.

### Changed
- All repositories now require decoration with both `AuditLoggingDecorator` and `EncryptionDecorator` at composition root.

---

## [0.2.0] — 2026-01-15

### Added
- `AxonDbContext` (EF Core 10) with WAL mode and SQLCipher encryption.
- `AxonDbContextFactory` for design-time migrations.
- `BiometricRepository` — concrete `IBiometricRepository` with `ValueTask` hot-path writes.
- `SyncOutboxRepository` — concrete `ISyncOutboxRepository` for Transactional Outbox reads.
- EF Core entity models: `BiometricEventEntity`, `SyncOutboxEntity`, `AuditLogEntity`.
- Domain-to-entity mappers: `BiometricEventMapper`, `SyncOutboxMapper`, `AuditLogMapper`.

---

## [0.1.0] — 2026-01-01

### Added
- Initial solution scaffold: `Axon.Core`, `Axon.Infrastructure`, `Axon.UI`.
- Core domain records: `BiometricEvent`, `BiometricType`, `SourceMetadata`, `SyncOutboxEntry`, `AuditLogEntry`.
- Port interfaces: `IBiometricDriver`, `IBiometricRepository`, `IRepository<T>`, `ISyncOutboxRepository`, `IHardwareVault`, `IAuditLogger`, `IInferenceService`, `IIngestionOrchestrator`.
- `AxonJsonContext` — Native AOT-safe `System.Text.Json` source-generation context.
- Avalonia UI shell: `App.axaml`, `MainWindow.axaml`, `MainView.axaml`, `Program.cs`.
- Solution-level `global.json` pinning .NET 10 SDK.
- `build-android.cmd` for Android Satellite builds.
- Architecture, ER, UML, and process diagrams in `docs/diagrams/`.
- `PRD.md`, `PDD.md`, `SDD.md` design documents.

---

[Unreleased]: https://github.com/stevenfackley/axon-main/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/stevenfackley/axon-main/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/stevenfackley/axon-main/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/stevenfackley/axon-main/releases/tag/v0.1.0
