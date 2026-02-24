# Software Design Document (SDD): Axon Core

**Project Codename:** Axon

**Architect:** Steven Ackley

**Version:** 1.0

**Status:** Approved for Implementation

**Technology Stack:** .NET 10 (C# 14), Avalonia UI, ML.NET, SQLite/SQLCipher, gRPC

---

## 1. System Architecture Overview

Axon utilizes a **Hexagonal (Ports and Adapters) Architecture**. This ensures the core biometric domain logic is isolated from external complexities like third-party APIs, specific file systems, and hardware-accelerated UI rendering.

### 1.1 Core Components

* **Axon.Core (Domain):** The central engine containing the Unified Biometric Schema (UBS), ML.NET inference models, and business rules for recovery calculation.
* **Axon.Infrastructure (Adapters):** Contains concrete implementations for `IBiometricDriver` (Whoop, Garmin, etc.) and the persistence logic for SQLite.
* **Axon.UI (Presentation):** An Avalonia-based shell providing cross-platform UI. It hosts the custom SkiaSharp rendering controls for high-performance telemetry.

---

## 2. Data Engineering & Persistence

### 2.1 Axon Common Schema (ACS)

To solve the "Data Babel" problem of different wearable vendors, all data is normalized into a single `BiometricEvent` record. This ensures that a "Heart Rate" event from Garmin is identical in structure to one from Whoop.

```csharp
public record BiometricEvent(
    Guid Id,
    DateTimeOffset Timestamp,
    BiometricType Type, // Enum: HeartRate, HRV, SpO2, Sleep, etc.
    double Value,
    SourceMetadata Source, // Device ID, Vendor, Confidence Score
    string? CorrelationId = null // Used for the Outbox Pattern
);

```

### 2.2 Persistence Strategy: SQLite + SQLCipher

* **Encryption:** The database is fully encrypted at rest using **AES-256**. The encryption key is derived via a hardware-backed **TPM** (Windows) or **Secure Enclave** (Mobile) to ensure zero-knowledge security.
* **Performance:** WAL (Write-Ahead Logging) is enabled. This allows the background sync service to read the Outbox table without blocking the UI's write operations.

### 2.3 Resiliency: Transactional Outbox Pattern

To maintain a robust offline-first experience, Axon uses the **Transactional Outbox Pattern**. Every biometric event saved locally is written to a `SyncOutbox` table within the same atomic transaction.

---

## 3. Intelligence & Rendering Engines

### 3.1 Local AI (ML.NET)

Axon performs all inference on-device to maintain 100% privacy.

* **Anomaly Detection:** Implements `IidSpikeEstimator` to flag unusual biometric spikes (e.g., elevated resting heart rate) that may indicate illness or overtraining.
* **Forecasting:** Uses `TimeSeries` catalogs to predict recovery scores for the upcoming 24-hour window based on previous cycles.

### 3.2 GPU Rendering (SkiaSharp)

Standard XAML rendering cannot handle the millions of data points required for longitudinal analysis. Axon uses a **Level of Detail (LoD)** strategy.

* **LTTB Downsampling:** High-frequency data is reduced in the background using the **Largest Triangle Three Buckets** algorithm before being sent to the GPU.
* **SkiaSharp Canvas:** Direct drawing to the GPU buffer using custom SKSLL shaders for real-time heatmap generation and fluid 120fps scrolling.

---

## 4. Security & Compliance (Decorator Pattern)

Compliance (HIPAA/GDPR) is treated as a cross-cutting concern. We utilize the **Decorator Pattern** to wrap repositories, ensuring core logic remains "clean" while security is strictly enforced.

* **`AuditLoggingDecorator`:** Intercepts every query and write operation to log access (Who, When, What) into an immutable audit table.
* **`EncryptionDecorator`:** Transparently handles field-level encryption of PII (Personally Identifiable Information) before data hits the EF Core provider.

---

## 5. UI/UX Specifications

### 5.1 Primary Dashboard

Provides a "Single Pane of Glass" view across all connected devices.

### 5.2 Analysis & Correlation Lab

A dedicated workspace for "Bio-Mining," allowing users to find hidden links between their behaviors and biological responses.

---

## 6. Deployment & Native AOT

To protect the proprietary technical moat and ensure instantaneous startup times:

* The application is compiled using **Native AOT (Ahead-of-Time)**.
* **Source Generators** are utilized to replace reflection-based logic, ensuring the final binary is both high-performance and difficult to reverse-engineer.
