# Changelog

All notable changes to **Axon** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added

#### Wearable Drivers — Universal Ingestion Engine
- **`IOAuthTokenStore`** port (`Axon.Core/Ports`) — platform-agnostic interface for securely storing and retrieving OAuth2 token sets. Encrypted at rest via `IHardwareVault`. Includes `OAuthTokenSet` value record with `IsExpired` helper.
- **`DriverUtilities`** (`Axon.Infrastructure/Drivers`) — shared static helpers for deterministic UUID v5 ID generation and `SourceMetadata` construction, used by all driver adapters.
- **`GuidV5`** — internal RFC 4122 UUID v5 (SHA-1) generator extracted to shared location; eliminates duplication across driver mappers.

##### Whoop 4.0 Driver (`Axon.Infrastructure/Drivers/Whoop/`)
- `WhoopModels.cs` — intermediate record structs mirroring Whoop API v1 JSON responses: `WhoopRecovery`, `WhoopSleep`, `WhoopCycle`, `WhoopWorkout`, `WhoopBodyMeasurement`, and all nested score/summary types.
- `WhoopNormalizationMapper.cs` — pure static mapper from Whoop models → ACS `BiometricEvent`. Maps recovery (HRV, RHR, SpO2, skin temp), sleep stages (ms→s), cycle strain (kJ→kcal), and workouts.
- `WhoopDriver.cs` — `IBiometricDriver` implementation with OAuth2 API mode (paginated REST, auto-refresh stub) and `ImportFileAsync` offline file-import path. `WhoopDriverOptions` with `PLACEHOLDER` sentinel credentials.

##### Garmin Connect Driver (`Axon.Infrastructure/Drivers/Garmin/`)
- `GarminModels.cs` — intermediate record structs for Garmin Health API v1: daily summaries, sleep summaries with stage maps, HRV summaries with 5-min readings, and body composition.
- `GarminNormalizationMapper.cs` — pure static mapper for daily summaries, sleep stages, HRV readings, and body composition (g→kg).
- `GarminDriver.cs` — `IBiometricDriver` implementation fetching dailies, sleep, HRV, and body composition from the Garmin Wellness API. Includes `ImportFileAsync` for Garmin Connect JSON exports. `GarminDriverOptions` with `PLACEHOLDER` sentinel credentials. Notes webhook-push production model.

##### Oura Ring Driver (`Axon.Infrastructure/Drivers/Oura/`)
- `OuraModels.cs` — intermediate record structs for Oura Ring API v2: daily readiness, daily sleep, sleep sessions (with embedded HR/HRV time-series), daily activity, heart rate samples, and SpO2.
- `OuraNormalizationMapper.cs` — pure static mapper supporting readiness scores, sleep sessions (expands `OuraTimeSeries` into individual events), activity, continuous HR, and SpO2. Handles both PAT and OAuth2.
- `OuraDriver.cs` — `IBiometricDriver` implementation with PAT and OAuth2 support, paginated fetching across all 5 endpoints, and `ImportFileAsync` for Oura app exports. `OuraDriverOptions` with `PLACEHOLDER` sentinel credentials.

#### Previously Unreleased (carried forward)
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
