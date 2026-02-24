# Contributing to Axon

Thank you for your interest in contributing to **Axon — The Sovereign Biometric Vault**. This document outlines everything you need to know to contribute effectively while maintaining the project's strict performance, security, and architectural standards.

> ⚠️ **Core Principle:** Axon is a zero-knowledge, offline-first system. Any contribution that introduces cloud telemetry, third-party analytics, or reflection-heavy code that breaks Native AOT will be rejected regardless of other merits.

---

## Table of Contents

1. [Code of Conduct](#code-of-conduct)
2. [Project Architecture Primer](#project-architecture-primer)
3. [Development Setup](#development-setup)
4. [Branch Strategy](#branch-strategy)
5. [Making a Contribution](#making-a-contribution)
6. [Coding Standards](#coding-standards)
7. [Definition of Done (DoD)](#definition-of-done-dod)
8. [Pull Request Process](#pull-request-process)
9. [Adding a New Wearable Driver](#adding-a-new-wearable-driver)
10. [Reporting Bugs](#reporting-bugs)
11. [Security Vulnerabilities](#security-vulnerabilities)

---

## Code of Conduct

All contributors are expected to adhere to our [Code of Conduct](CODE_OF_CONDUCT.md). Be professional, be constructive, and respect the sovereignty principle: no contributor's PR should make Axon dependent on any external service.

---

## Project Architecture Primer

Before contributing, please read these documents in `docs/`:

| Document | Purpose |
|---|---|
| [PRD.md](docs/PRD.md) | Product vision and functional requirements |
| [PDD.md](docs/PDD.md) | Design decisions and technical rationale |
| [SDD.md](docs/SDD.md) | Software architecture and implementation detail |

**The three architectural layers you must respect:**

```
Axon.Core        → Domain only. No I/O, no framework references.
Axon.Infrastructure → All concrete adapters (DB, APIs, ML). Implements Core ports.
Axon.UI          → Avalonia MVVM presentation. No business logic.
```

Dependency flow is **always inward**: `UI → Core ← Infrastructure`. The `Core` project must never reference `Infrastructure` or `UI`.

---

## Development Setup

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (see `global.json` for pinned version)
- Visual Studio 2022 17.12+ **or** JetBrains Rider 2024.3+
- Windows 11 for full TPM/hardware vault testing
- (Optional) Android SDK for mobile `Satellite` contributions

### Clone and Build

```bash
git clone https://github.com/stevenfackley/axon-main.git
cd axon-main
dotnet restore
dotnet build
```

### Run the App

```bash
dotnet run --project src/Axon.UI
```

### Run Tests

```bash
dotnet test
```

### Native AOT Publish (validates AOT compatibility)

```bash
dotnet publish src/Axon.UI -r win-x64 -c Release /p:PublishAot=true
```

Always run this before submitting a PR that touches `Axon.Core` or `Axon.Infrastructure` to ensure your changes are AOT-compatible.

---

## Branch Strategy

| Branch | Purpose |
|---|---|
| `main` | Stable, releasable code |
| `dev` | Integration branch for completed features |
| `feature/<name>` | New features (branch from `dev`) |
| `fix/<name>` | Bug fixes (branch from `dev` or `main` for hotfixes) |
| `chore/<name>` | Non-functional changes (docs, CI, refactors) |

**Never commit directly to `main`.** All changes go through a PR targeting `dev`.

---

## Making a Contribution

1. **Check existing issues** — look for an open issue or discussion before starting new work.
2. **Open an issue first** for significant changes (new drivers, architectural shifts). This prevents duplicate effort.
3. **Fork the repo** and create your branch from `dev`:
   ```bash
   git checkout -b feature/garmin-driver dev
   ```
4. **Write your code** following the standards below.
5. **Run the full test suite** and ensure it passes.
6. **Open a Pull Request** against `dev` using the PR template.

---

## Coding Standards

### Naming

| Element | Convention | Example |
|---|---|---|
| Classes, Records, Methods | `PascalCase` | `BiometricEventMapper` |
| Private fields | `_camelCase` | `_repository` |
| Interfaces | `IPascalCase` | `IBiometricDriver` |
| Constants | `PascalCase` | `MaxBatchSize` |
| Local variables | `camelCase` | `eventBatch` |

### Language Features

- Use `record` for all immutable DTOs and domain events.
- Use `primary constructors` where they improve readability.
- Use `ValueTask` for high-frequency I/O operations (e.g., hot-path DB writes).
- Use `Task.Run` to offload CPU-bound work (ML.NET inference, heavy rendering) off the UI thread.

### Memory & Performance

```csharp
// ✅ CORRECT — zero-allocation parsing in ingestion hot paths
Span<byte> buffer = stackalloc byte[128];
ArrayPool<BiometricEvent> pool = ArrayPool<BiometricEvent>.Shared;
var batch = pool.Rent(MaxBatchSize);
try { /* process */ }
finally { pool.Return(batch); }

// ❌ WRONG — heap allocation per event in a loop
var events = new List<BiometricEvent>();
```

- **No LINQ in hot ingestion loops** — prefer `for` loops and `Span<T>`.
- **No `dynamic` or reflection** — breaks Native AOT.
- All data requests spanning **> 24 hours** must use `LttbDownsampler` before reaching the UI.
- The UI thread must **never be blocked**. All I/O and ML inference runs on background threads.

### Security Rules (Non-Negotiable)

- **No hardcoded keys, salts, or connection strings.** Use `IHardwareVault` for all key derivation.
- **No third-party analytics or telemetry SDKs** (e.g., no AppInsights, Sentry, Segment, Mixpanel).
- **No direct HTTP calls** from `Axon.Core`. All external I/O lives in `Axon.Infrastructure`.
- All `ToString()` overrides on domain objects **must mask biometric values**:
  ```csharp
  public override string ToString() =>
      $"BiometricEvent {{ Id={Id}, Type={Type}, Timestamp={Timestamp}, Value=*** }}";
  ```
- Every new repository **must** be decorated with both `AuditLoggingDecorator` and `EncryptionDecorator`.

### AOT Compatibility

- Avoid `Type.GetType()`, `Assembly.Load()`, `Activator.CreateInstance()`, and dynamic code generation.
- Use `[JsonSerializable]` source generators for all new types added to `AxonJsonContext`.
- Test AOT publish (`/p:PublishAot=true`) before submitting a PR.

---

## Definition of Done (DoD)

A PR is only ready to merge when **all** of the following are true:

- [ ] Code compiles cleanly with `dotnet build` (zero warnings).
- [ ] AOT publish succeeds: `dotnet publish -r win-x64 -c Release /p:PublishAot=true`
- [ ] All existing tests pass: `dotnet test`
- [ ] New `NormalizationMapper` implementations have **100% unit test coverage**.
- [ ] Memory profiling (dotnet-trace / BenchmarkDotNet) shows **zero hot-path heap allocations** for ingestion loops.
- [ ] No reflection-based code was introduced.
- [ ] No third-party analytics, telemetry, or cloud-calling code was introduced.
- [ ] `ToString()` on any new domain objects masks biometric values.
- [ ] Architecture boundary respected: `Core` has no references to `Infrastructure` or `UI`.
- [ ] `docs/` updated if a new `IBiometricDriver` was implemented.
- [ ] PR description references the issue being addressed.

---

## Pull Request Process

1. Fill out the [PR template](.github/PULL_REQUEST_TEMPLATE.md) completely.
2. Link the related issue using `Closes #<issue-number>`.
3. Ensure the CI pipeline passes (build + test + AOT check).
4. Request review from at least one maintainer.
5. Address all review comments before requesting re-review.
6. PRs with failing CI will not be merged.

---

## Adding a New Wearable Driver

Adding a new `IBiometricDriver` is the most common contribution type. Follow this checklist:

### 1. Create the Driver

```
src/Axon.Infrastructure/Drivers/<VendorName>/
    <VendorName>Driver.cs          # Implements IBiometricDriver
    <VendorName>NormalizationMapper.cs  # Vendor JSON → BiometricEvent (ACS)
    <VendorName>ApiClient.cs       # HTTP calls (if real-time sync)
    <VendorName>Models.cs          # Vendor-specific deserialization models
```

### 2. Implement `IBiometricDriver`

```csharp
public sealed class FitbitDriver : IBiometricDriver
{
    public string VendorName => "Fitbit";

    public async IAsyncEnumerable<BiometricEvent> FetchAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Fetch from vendor API, normalize via FitbitNormalizationMapper
    }
}
```

### 3. Implement the Normalization Mapper

The mapper must convert **all** vendor-specific fields to ACS. Every branch must be covered by unit tests:

```csharp
public static class FitbitNormalizationMapper
{
    public static BiometricEvent Map(FitbitHeartRateEntry entry) => new(
        Id: Guid.NewGuid(),
        Timestamp: DateTimeOffset.Parse(entry.DateTime),
        Type: BiometricType.HeartRate,
        Value: entry.Value.RestingHeartRate,
        Source: new SourceMetadata("Fitbit", entry.DeviceId, 1.0)
    );
}
```

### 4. Write Tests

Create `tests/Axon.Infrastructure.Tests/Drivers/<VendorName>/` with 100% coverage of the mapper:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

### 5. Update Docs

- Add a row to the "Supported Wearables" table in `README.md`.
- Document the driver in `docs/`.

---

## Reporting Bugs

Use the [Bug Report template](.github/ISSUE_TEMPLATE/bug_report.md). Include:

- Axon version / commit hash
- Operating system and version
- Steps to reproduce
- Expected vs. actual behavior
- Relevant logs (ensure no biometric values are included in logs you share)

---

## Security Vulnerabilities

**Do not open a public issue for security vulnerabilities.** Follow the responsible disclosure process in [SECURITY.md](SECURITY.md).

---

*Thank you for helping build the sovereign future of biometric data. No cloud required.*
