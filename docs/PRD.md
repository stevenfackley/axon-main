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

### Model: Freemium → Subscription / Annual

Axon uses a freemium funnel targeting high-performance athletes and bio-hackers who already pay for multiple wearable subscriptions and are privacy-conscious. Pricing is positioned below direct competitors (Training Peaks at $19.99/mo, Whoop at $30/mo) while delivering more depth than cheaper alternatives (Athlytic at $3.33/mo, HRV4Training at $9.99 one-time).

| Tier | Price | Features |
| --- | --- | --- |
| **Free** | $0 | Manual CSV/JSON import, 1-year history, basic single-source visualization, local storage only |
| **Axon Pro** | **$4.99/mo** or **$39.99/yr** | Automated API sync (Whoop, Garmin, Oura), unlimited history, ML.NET Insight Engine (anomaly detection, recovery forecasting, correlation lab), cross-platform Sovereign Sync, comparative overlays |

### Pricing Rationale

* **$4.99/month** is an impulse-buy for the target demographic, who already spend $30–$300/month on wearable hardware and subscriptions. Conversion friction is low.
* **$39.99/year (~$3.33/month)** rewards commitment and improves LTV predictability. The annual discount encourages early lock-in.
* A **$79.99 lifetime deal** may be offered as a time-limited launch promotion to seed the initial user base and generate word-of-mouth in quantified-self communities (Reddit, Discord, X). This should be retired once the product reaches 500+ reviews.

### Scale Path

After establishing brand trust and accumulating App Store reviews (target: 6–12 months post-launch), pricing will be adjusted:

* **$6.99/month** or **$59.99/year** for new subscribers.
* Existing subscribers grandfathered at their original rate.

### Competitive Positioning

| Competitor | Price | Offline | Cross-Device | Local AI |
| --- | --- | --- | --- | --- |
| Training Peaks | $19.99/mo | ❌ | ✅ | ❌ |
| Exist.io | $6-8/mo | ❌ | ✅ | ❌ |
| Athlytic | $3.33/mo | ✅ | ❌ | Limited |
| HRV4Training | $9.99 OTP | ✅ | ❌ | ❌ |
| **Axon Pro** | **$4.99/mo** | ✅ | ✅ | ✅ |

Axon's unique position: the **only** cross-wearable, fully offline, AI-enabled analytics tool in this category at this price point.



---

## 7. Success Metrics (KPIs)

1. **User Acquisition:** First 1,000 users within 90 days of Windows Store launch.
2. **Performance:** Maintaining 60fps+ on low-end hardware (e.g., Surface Go) during data-heavy rendering.
3. **Retention:** >40% of Pro users engaging with the "Correlation Engine" weekly.
