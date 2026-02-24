# Project Design Document (PDD): Axon

**Project Codename:** Axon

**Architect:** Steven Ackley

**Version:** 1.0

**Target Completion:** Q4 2026

**Confidentiality:** Sovereign-Class (Zero-Knowledge)

---

## 1. Project Purpose & Scope

**Axon** is designed to be the "Gold Standard" for private biometric telemetry. It bridges the gap between fragmented consumer wearables and professional-grade data analysis. The project scope includes a Windows desktop "Power Station" and mobile "Satellite" apps (iOS/Android) that communicate through a secure, peer-to-peer gRPC tunnel.

---

## 2. Technical Stack & Implementation Details

| Layer | Technology | Justification |
| --- | --- | --- |
| **Runtime** | .NET 10 (Native AOT) | Extreme performance, sub-second startup, and IP protection via binary obfuscation. |
| **Presentation** | Avalonia UI + SkiaSharp | Cross-platform GPU rendering capable of 120fps telemetry drawing. |
| **Persistence** | SQLite with SQLCipher | Local-first, industry-standard encrypted relational storage. |
| **Intelligence** | ML.NET (ONNX) | Local inference for privacy; no biometric data leaves the device for AI processing. |
| **Communication** | gRPC over Protobuf | Binary serialization for low-latency, low-bandwidth cross-device sync. |

---

## 3. High-Level System Architecture

Axon utilizes a **Hexagonal Architecture** to ensure that the core analysis engine remains unaffected by changes in third-party APIs (like a Whoop API update) or UI framework shifts.

---

## 4. Feature Design Specifications

### 4.1 The "Universal Bridge" (Ingestion)

The ingestion engine uses a **Provider Pattern**. Each wearable source (Garmin, Oura, Whoop) has a dedicated "Adapter" that translates vendor JSON into the **Axon Common Schema (ACS)**.

* **Normalization Engine:** Handles timezone offsets and unit conversions (e.g., converting Oura's raw sleep data into standardized heart-rate-variability buckets).
* **Buffer Strategy:** Uses `System.Threading.Channels` to handle bursts of data from real-time syncs without dropping frames in the UI.

### 4.2 The "Visual Vault" (Rendering)

To handle millions of data points, Axon implements **Level of Detail (LoD)** rendering.

* **Macro-View:** Renders pre-calculated aggregates (Min/Max/Avg) stored in the DB.
* **Micro-View:** As the user zooms in, the engine dynamically fetches raw high-frequency data and renders it via **SkiaSharp** directly to the GPU buffer.

### 4.3 "Sovereign Sync" (Hybrid Cloud)

While Axon is offline-first, users can enable a **Zero-Knowledge Hybrid Sync**.

* **Transactional Outbox:** Any change made locally is written to a "Sync Task" table in the same atomic transaction as the biometric data.
* **Encrypted Relay:** Data is double-encrypted using keys derived from the device's **TPM** or **Secure Enclave** before being sent to the sync relay.

---

## 5. Security & Compliance Architecture

Axon is built with "Compliance-by-Design." We utilize the **Decorator Pattern** to wrap all data access.

* **HIPAA Audit Trail:** Every query is logged with a timestamp and access type.
* **GDPR Protection:** A "Nuclear Option" is provided to wipe the local hardware-backed keys, effectively incinerating the data.
* **Zero-Knowledge:** No one—including the developer—can access the user's data.

---

## 6. User Interface (UX) Design

The UI follows an **Industrial-Dark** aesthetic, optimized for clarity in high-density data environments.

### 6.1 Unified Telemetry Dashboard

The primary view for cross-source correlation.

### 6.2 The Analysis Lab

A workspace for running local ML.NET correlation models.

---

## 7. Strategic Deployment Roadmap

1. **Core Kernel (Month 1):** Implementation of the ACS, SQLite/SQLCipher storage, and basic Whoop/Apple Health ingestion.
2. **Visual Engine (Month 2):** Development of the SkiaSharp chart controls and LTTB downsampling logic.
3. **Sovereign Intelligence (Month 3):** Integration of ML.NET for local recovery forecasting and the gRPC sync bridge.
4. **App Store Launch (Month 4):** Packaging via MSIX for Windows and submission to Apple/Google stores.

