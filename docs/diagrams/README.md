# Axon — Diagram Index

This directory contains all architectural, data, and process diagrams for the Axon project. Diagrams are organized into three categories.

---

## 📐 Architecture Diagrams

Visual representations of the system's structural design.

| Diagram | File | Description |
|---|---|---|
| System Architecture | [system-architecture.png](architecture/system-architecture.png) | High-level overview of the three-layer Hexagonal Architecture (`Axon.Core`, `Axon.Infrastructure`, `Axon.UI`) and their boundaries. |
| Context Diagram | [context-diagram.png](architecture/context-diagram.png) | System context showing Axon's external interactions: wearable APIs (Whoop, Garmin, Oura), device hardware (TPM/Secure Enclave), and the mobile Satellite apps. |
| UML Class Diagram | [uml-class-diagram.png](architecture/uml-class-diagram.png) | Core domain model class relationships: `BiometricEvent`, `SourceMetadata`, `SyncOutboxEntry`, `AuditLogEntry`, and the Port interfaces. |

---

## 🗄 Data Diagrams

Database schema and data transformation flows.

| Diagram | File | Description |
|---|---|---|
| ER Diagram | [er-diagram.png](data/er-diagram.png) | Entity-Relationship diagram for the SQLite/SQLCipher schema: `BiometricEvents`, `SyncOutbox`, `AuditLog` tables and their foreign key relationships. |
| Data Normalization Primitive | [data-normalization-primitive.png](data/data-normalization-primitive.png) | Illustrates how vendor-specific raw data (Whoop, Garmin, Oura JSON) flows through a `NormalizationMapper` and is mapped to the **Axon Common Schema (ACS)** `BiometricEvent` record. |

---

## ⚙️ Process Diagrams

Runtime behavior and pipeline flows.

| Diagram | File | Description |
|---|---|---|
| Major Process Flow | [major-process.png](processes/major-process.png) | End-to-end lifecycle of a biometric data event: from wearable API → `IBiometricDriver` → `IngestionOrchestrator` → `SyncOutbox` → `BiometricRepository` → UI. |
| Hybrid Sync Manager | [hybrid-sync-manager.png](processes/hybrid-sync-manager.png) | The Transactional Outbox pattern in action: how local writes are atomically paired with `SyncOutbox` entries and eventually relayed via gRPC to the Satellite app. |
| SkiaSharp Render Loop | [skia-render-loop.png](processes/skia-render-loop.png) | The 120fps GPU rendering pipeline: data query → `LttbDownsampler` (>24h datasets) → SkiaSharp canvas → SKSL shader → GPU buffer. |
| ML.NET Engine | [mlnet-engine.png](processes/mlnet-engine.png) | The local inference pipeline: `BiometricInputRow` preparation → ML.NET `IidSpikeEstimator` (anomaly) / `TimeSeries` (forecasting) → `IInferenceService` result → ViewModel. |

---

## Related Documentation

- [System Design Document (SDD)](../SDD.md) — detailed software architecture write-up
- [Project Design Document (PDD)](../PDD.md) — design rationale and technical decisions
- [Product Requirements Document (PRD)](../PRD.md) — functional requirements and product vision
