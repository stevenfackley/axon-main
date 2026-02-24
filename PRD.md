# Product Requirements Document (PRD): Axon

**Project Name:** Axon

**Target Market:** High-Performance Athletes & Bio-Hackers

**Platform:** Windows Store (Primary), iOS, Android (Cross-Platform via Avalonia)

**Version:** 1.0

**Status:** Approved for Implementation

---

## 1. Executive Summary

**Axon** is an elite, offline-first biometric telemetry vault designed to return data sovereignty to the user. It unifies fragmented health data from multiple wearables (Whoop, Garmin, Oura, Apple Health, Google Health) into a single, high-performance ecosystem.

Unlike competitors that rely on cloud-based subscriptions, Axon performs all AI analysis, correlation discovery, and data storage locally. It leverages **.NET 10** and **Native AOT** to provide a secure, "air-gapped" alternative for users who prioritize privacy and professional-grade data mining.

---

## 2. Market Problem & Opportunity

* **Walled Gardens:** Current wearable ecosystems lock data into proprietary apps, making cross-device analysis difficult.
* **Subscription Fatigue:** Users are forced into monthly "rent" for access to their own biological history.
* **Privacy Concerns:** Biometric data is frequently sold or used for training massive cloud-based LLMs without granular user control.
* **The Opportunity:** A "Blue Ocean" for a desktop-class "Power Tool" on the Windows Store that offers deeper functionality than mobile apps without the cloud dependency.

---

## 3. Core Functional Requirements

### 3.1 Universal Ingestion Engine

* **FR-1:** Support real-time API sync for **Whoop**, **Garmin Connect**, and **Oura Cloud**.
* **FR-2:** Native bridges for **Apple HealthKit** (via MAUI/Avalonia bindings) and **Android Health Connect**.
* **FR-3:** Manual import for legacy CSV/JSON data from retired devices.
* **FR-4:** **Axon Common Schema (ACS):** Automated normalization of disparate metrics into a unified time-series format.

### 3.2 Local Intelligence (ML.NET)

* **FR-5:** **Anomaly Detection:** Identify physiological spikes (Resting HR, Respiratory Rate) that may indicate illness or overtraining.
* **FR-6:** **Correlation Engine:** Drag-and-drop tool to find statistical relationships between variables (e.g., "Caffeine vs. REM Latency").
* **FR-7:** **Recovery Forecasting:** Local prediction models for readiness scores 24 hours in advance.

### 3.3 High-Fidelity Visualization

* **FR-8:** **GPU-First Rendering:** Support for 120fps scrolling through 5+ years of data via **SkiaSharp**.
* **FR-9:** **Deep-Time Scoping:** Infinite zoom from a 10-year life-view down to a 1-second heartbeat granularity.
* **FR-10:** **Comparative Overlays:** Overlay multiple data streams (e.g., Heart Rate vs. Power Output) on a single synced timeline.

### 3.4 Sovereign Sync (Hybrid)

* **FR-11:** **Zero-Knowledge Architecture:** Peer-to-peer sync via **gRPC** with encryption keys stored only in the hardware TPM/Secure Enclave.
* **FR-12:** **Air-Gap Integrity:** A global toggle to disable all outbound networking while maintaining full analytical capability.

---

## 4. Non-Functional Requirements (NFRs)

| Category | Requirement | Specification |
| --- | --- | --- |
| **Performance** | Startup Time | < 1.0s to "Data Ready" state via Native AOT. |
| **Security** | Encryption | AES-256 (SQLCipher) for all local data. |
| **Compliance** | HIPAA/GDPR | Immutable audit logs for all data access events. |
| **Reliability** | Offline Support | 100% functionality without internet (excluding initial API auth). |
| **Scalability** | Data Handling | Support for >100 million biometric events with O(log n) query time. |

---

## 5. User Interface Specifications

The design language is **Industrial-Technical**. It prioritizes high-density data over whitespace, appealing to users who want a "Control Room" for their biology.

* **Main Dashboard:** A unified view of current "Vitals" across all sources.
* **Insight Lab:** A workspace for custom ML.NET queries and scatter-plot correlations.
* **Sovereign Settings:** A transparent view of the encryption status, local key management, and sync logs.

---

## 6. Monetization Architecture

* **Freemium Model:** * **Free:** Local manual import, 1-year history, basic visualization.
* **Axon Pro ($149 One-Time or $9/mo):** Automated API sync, unlimited history, ML.NET insight engine, and cross-platform "Sovereign Sync."



---

## 7. Success Metrics (KPIs)

1. **User Acquisition:** First 1,000 users within 90 days of Windows Store launch.
2. **Performance:** Maintaining 60fps+ on low-end hardware (e.g., Surface Go) during data-heavy rendering.
3. **Retention:** >40% of Pro users engaging with the "Correlation Engine" weekly.
