<div align="center">


# 🧬 Axon

![.NET](https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white) ![C#](https://img.shields.io/badge/c%23-%23239120.svg?style=for-the-badge&logo=c-sharp&logoColor=white)



**The Sovereign Biometric Vault**

[![Build Status](https://github.com/stevenfackley/axon-main/actions/workflows/ci.yml/badge.svg)](https://github.com/stevenfackley/axon-main/actions/workflows/ci.yml)
[](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[](#)
[](LICENSE)
[](CONTRIBUTING.md)

*A high-performance, offline-first telemetry engine that unifies Whoop, Garmin, and Oura data into a zero-knowledge, GPU-accelerated dashboard. No cloud. No subscriptions. Total data sovereignty.*

[Features](#-features) • [Architecture](#-architecture) • [Getting Started](#-getting-started) • [Roadmap](#-roadmap) • [Contributing](#-contributing) • [Security](#-security)

</div>

---

## 🚀 Features

| Feature | Description |
|---|---|
| **Universal Ingestion** | Real-time sync from Whoop, Garmin Connect, Oura Cloud, Apple HealthKit, and Android Health Connect — all normalized to the Axon Common Schema (ACS). |
| **GPU-First Rendering** | SkiaSharp-powered telemetry charts running at 120fps. Infinite zoom from a 10-year life view down to a 1-second heartbeat. |
| **Local AI / ML.NET** | On-device anomaly detection, recovery forecasting, and a drag-and-drop correlation engine. Your data never leaves your hardware for inference. |
| **Zero-Knowledge Storage** | AES-256 encrypted SQLite (SQLCipher). Keys derived exclusively from your device's TPM (Windows) or Secure Enclave (iOS/Android). |
| **Sovereign Sync** | Optional peer-to-peer gRPC sync between your desktop Power Station and mobile Satellite apps — double-encrypted, zero-knowledge. |
| **HIPAA/GDPR by Design** | Every data access is wrapped in an audit-logging decorator. A "Nuclear Option" key wipe effectively destroys all local data. |
| **Native AOT** | Compiled Ahead-of-Time for sub-second startup, minimal memory footprint, and binary-level IP protection. |
| **Air-Gap Mode** | A single toggle disables all outbound networking while retaining 100% analytical capability offline. |

---

## 🏗 Architecture

Axon is built on a **Hexagonal (Ports and Adapters)** architecture. The core biometric domain is completely isolated from external APIs, UI frameworks, and storage mechanisms.

```
┌────────────────────────────────────────────────────────────┐
│                        Axon.UI                             │
│         Avalonia MVVM + SkiaSharp 120fps Rendering         │
└──────────────────────────┬─────────────────────────────────┘
                           │  Ports (Interfaces)
┌──────────────────────────▼─────────────────────────────────┐
│                       Axon.Core                            │
│   BiometricEvent · ACS Schema · ML.NET · Business Rules    │
└──────────────────────────┬─────────────────────────────────┘
                           │  Adapters
┌──────────────────────────▼─────────────────────────────────┐
│                   Axon.Infrastructure                      │
│  SQLite/SQLCipher · Whoop · Garmin · Oura · gRPC Outbox    │
└────────────────────────────────────────────────────────────┘
```
<img src="docs/diagrams/architecture/system-architecture.png" alt="Axon Architecture" width="800"/>

### Solution Layout

```
axon-main/
├── src/
│   ├── Axon.Core/               # Domain: BiometricEvent, ACS types, Port interfaces
│   │   ├── Domain/              # Records: BiometricEvent, BiometricType, SourceMetadata
│   │   ├── Ports/               # Interfaces: IBiometricDriver, IRepository, IHardwareVault…
│   │   └── Serialization/       # AOT-safe JSON contexts
│   ├── Axon.Infrastructure/     # Adapters: Persistence, Ingestion, ML, Security
│   │   ├── Ingestion/           # IngestionOrchestrator, vendor drivers
│   │   ├── ML/                  # LocalInferenceService, BiometricInputRow
│   │   ├── Persistence/         # EF Core DbContext, Repositories, Decorators, Mappers
│   │   └── Security/            # DbAuditLogger, MockHardwareVault
│   └── Axon.UI/                 # Avalonia shell: Views, ViewModels, SkiaSharp Rendering
│       ├── Rendering/           # LttbDownsampler, SkiaTelemetryChart, SKSL shaders
│       ├── ViewModels/          # DashboardViewModel, SettingsViewModel
│       └── Views/               # AXAML views for Dashboard, Settings
├── docs/
│   ├── PRD.md                   # Product Requirements Document
│   ├── PDD.md                   # Project Design Document
│   ├── SDD.md                   # Software Design Document
│   └── diagrams/
│       ├── architecture/        # System architecture, context, UML class diagrams
│       ├── data/                # ER diagram, data normalization primitive
│       └── processes/           # Major process flow, Hybrid Sync, SkiaSharp loop, ML.NET engine
├── .github/
│   ├── ISSUE_TEMPLATE/          # Bug report & feature request templates
│   ├── workflows/               # CI pipeline
│   └── PULL_REQUEST_TEMPLATE.md
├── CONTRIBUTING.md
├── CHANGELOG.md
├── CODE_OF_CONDUCT.md
├── SECURITY.md
├── LICENSE
└── Axon.sln
```

### Key Design Patterns

| Pattern | Where Used | Why |
|---|---|---|
| **Hexagonal Architecture** | All three projects | Isolates domain from frameworks |
| **Repository Decorator** | `AuditLoggingDecorator`, `EncryptionDecorator` | HIPAA/GDPR as cross-cutting concerns |
| **Transactional Outbox** | `SyncOutbox` table | Atomic local-write + eventual remote-sync |
| **ACS Normalization** | All `IBiometricDriver` implementations | "Data Babel" prevention across vendors |
| **LTTB Downsampling** | `LttbDownsampler` in Axon.UI | Render millions of points at 120fps |
| **Provider / Strategy** | `IBiometricDriver` per vendor | Swap/add wearable sources without core changes |

### Architecture Diagrams

| Diagram | Description |
|---|---|
| [System Architecture](docs/diagrams/architecture/system-architecture.png) | High-level component overview |
| [Context Diagram](docs/diagrams/architecture/context-diagram.png) | System context and external boundaries |
| [UML Class Diagram](docs/diagrams/architecture/uml-class-diagram.png) | Core domain model |
| [ER Diagram](docs/diagrams/data/er-diagram.png) | Database schema |
| [Data Normalization Primitive](docs/diagrams/data/data-normalization-primitive.png) | ACS mapping flow |
| [Major Process Flow](docs/diagrams/processes/major-process.png) | End-to-end ingestion lifecycle |
| [Hybrid Sync Manager](docs/diagrams/processes/hybrid-sync-manager.png) | Sovereign Sync / gRPC outbox flow |
| [SkiaSharp Render Loop](docs/diagrams/processes/skia-render-loop.png) | 120fps GPU rendering pipeline |
| [ML.NET Engine](docs/diagrams/processes/mlnet-engine.png) | Local inference pipeline |

---

## 🛠 Tech Stack

| Layer | Technology | Version |
|---|---|---|
| Runtime | .NET Native AOT | 10.0 |
| Language | C# | 14 |
| UI Framework | Avalonia UI (MVVM) | Latest |
| GPU Rendering | SkiaSharp | Latest |
| Database | SQLite + SQLCipher | — |
| ORM | Entity Framework Core | 10 |
| AI / ML | ML.NET (ONNX) | Latest |
| Cross-Device Sync | gRPC over Protobuf | — |
| Serialization | System.Text.Json (AOT) | — |
| Memory | `ArrayPool<T>`, `Span<T>` | — |

---

## 🔧 Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Windows 11 (Power Station), or iOS/Android for Satellite builds
- Visual Studio 2026 **or** JetBrains Rider 2025.3+ (both needed for .NET 10)
- (Optional) Android SDK for mobile development

### Clone & Build

```bash
git clone https://github.com/stevenfackley/axon-main.git
cd axon-main
dotnet restore
dotnet build
```

### Run the Desktop App

```bash
dotnet run --project src/Axon.UI
```

### Run Tests

```bash
dotnet test
```

### Publish (Native AOT — Windows x64)

```bash
dotnet publish src/Axon.UI -r win-x64 -c Release /p:PublishAot=true
```

### Publish (Android)

```bash
build\build-android.cmd
```

See [build/README.md](build/README.md) for the Android build scripts (`build/build-android.cmd`, `build/build-android.sh`).

---

## 📱 Platform Support

| Platform | Status | Notes |
|---|---|---|
| Windows 11 | ✅ Primary (Power Station) | Native AOT, TPM key derivation |
| Android | 🚧 In Progress (Satellite) | Secure Enclave key derivation |
| iOS | 🚧 In Progress (Satellite) | Secure Enclave via `Entitlements.plist` |
| macOS | 🗓 Planned | Avalonia cross-platform |

---

## 🔌 Supported Wearables

| Source | Status | Method |
|---|---|---|
| Whoop | ✅ Implemented | REST API (OAuth 2.0) |
| Garmin Connect | ✅ Implemented | REST API |
| Oura Cloud | ✅ Implemented | REST API |
| Apple HealthKit | ✅ Implemented (iOS only) | Native bridge (iOS) |
| Android Health Connect | 🗓 Planned | Native bridge (Android) |
| CSV Import | ✅ Implemented | Manual legacy import (`CsvBiometricImporter`) |
| JSON Import | 🗓 Planned | Manual legacy import |

---

## 🧠 The Axon Common Schema (ACS)

All vendor data is normalized into a single `BiometricEvent` record on ingestion. This is the cornerstone of Axon's "Data Babel" solution:

```csharp
public sealed record BiometricEvent(
    Guid Id,
    DateTimeOffset Timestamp,
    BiometricType Type,       // HeartRate, HRV, SpO2, Sleep, Steps, etc.
    double Value,
    string Unit,              // SI unit string: "bpm", "ms", "%", "W"
    SourceMetadata Source,    // Vendor, DeviceId, ConfidenceScore
    string? CorrelationId = null  // Transactional Outbox correlation
);
```

A `HeartRate` event from Garmin is structurally identical to one from Whoop. The UI and ML engine never need to know the origin.

---

## 🔒 Security Model

Axon is built with **zero-knowledge** and **compliance-by-design** principles.

- **Encryption at Rest:** AES-256 via SQLCipher. The database is unreadable without the hardware-backed key.
- **Key Derivation:** Keys are derived from the Windows **TPM** (`IHardwareVault`) or iOS/Android **Secure Enclave** — never stored in software.
- **Audit Trail (HIPAA):** Every read/write operation is intercepted by `AuditLoggingDecorator` and recorded in an immutable `AuditLog` table.
- **PII Shield:** Domain objects mask sensitive values in `ToString()` to prevent biometric data from appearing in logs.
- **Air-Gap Mode:** A global toggle disables all outbound network I/O. No background telemetry, no analytics, no phone-home — ever.
- **Nuclear Option:** Destroying the hardware-backed key renders all local data permanently unreadable.

See [SECURITY.md](SECURITY.md) for vulnerability disclosure policy.

---

## 🗺 Roadmap

### v1.0 — Core Kernel *(Month 1)*
- [x] Hexagonal architecture scaffold (`Axon.Core`, `Axon.Infrastructure`, `Axon.UI`)
- [x] `BiometricEvent` ACS domain model
- [x] SQLite/SQLCipher persistence with `AuditLoggingDecorator` and `EncryptionDecorator`
- [x] `IHardwareVault` interface + `MockHardwareVault`
- [x] `IngestionOrchestrator` with `System.Threading.Channels` buffer
- [x] Native AOT JSON serialization context

### v1.1 — Visual Engine *(Month 2)*
- [x] `LttbDownsampler` (Largest Triangle Three Buckets)
- [x] `SkiaTelemetryChart` with SKSL stress-zone shader
- [ ] Dashboard ViewModel with live data binding
- [ ] Deep-Time zoom controls (10-year → 1-second granularity)

### v1.2 — Sovereign Intelligence *(Month 3)*
- [x] `LocalInferenceService` (ML.NET + ONNX)
- [ ] Anomaly detection for resting HR / respiratory rate spikes
- [ ] 24-hour recovery score forecasting
- [ ] Correlation drag-and-drop Lab UI

### v1.3 — Wearable Drivers *(Month 3-4)*
- [x] Whoop REST driver
- [x] Garmin Connect driver
- [x] Oura Cloud driver
- [x] Apple HealthKit bridge (iOS)
- [ ] Android Health Connect bridge

### v2.0 — Sovereign Sync + Store Launch *(Month 4)*
- [ ] gRPC Transactional Outbox sync
- [ ] Android Satellite app
- [ ] iOS Satellite app
- [ ] Windows Store MSIX packaging
- [x] Freemium / Axon Pro licensing gate ($4.99/mo · $39.99/yr · $79.99 launch lifetime)

---

## 🤝 Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) for the code standards, branch strategy, and Definition of Done before submitting a pull request.

**Quick rules:**
- All new `IBiometricDriver` implementations require 100% unit test coverage on the `NormalizationMapper`.
- No reflection-based code that breaks Native AOT.
- No third-party telemetry, analytics SDKs, or cloud-calling code may be introduced.
- The hot ingestion path must show **zero heap allocations** in a memory profiling run.

---

## 📄 License

Axon is proprietary software. See [LICENSE](LICENSE) for full terms.  
© 2026 Steven Ackley. All Rights Reserved.

---

## 🛡 Security

If you discover a security vulnerability, please **do not open a public issue**. Instead, follow the responsible disclosure process outlined in [SECURITY.md](SECURITY.md).

---

<div align="center">
  <sub>Built with 🧬 by <a href="https://github.com/stevenfackley">Steven Ackley</a>. No cloud required.</sub>
</div>