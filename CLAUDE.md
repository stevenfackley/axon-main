# Axon

Biometric ingestion + analysis platform. Local-first; no cloud.

## Tech stack
- **Runtime:** .NET 10. The desktop app ships self-contained with ReadyToRun, not Native AOT (ML.NET and EF Core need reflection); `Axon.Core` stays Native AOT-clean
- **Language:** C# 14
- **UI:** Avalonia UI (MVVM)
- **Graphics:** SkiaSharp (direct GPU rendering)
- **Database:** SQLite + SQLCipher via EF Core 10
- **AI:** ML.NET (local ONNX inference)

## Architecture patterns
- **Hexagonal architecture** — strictly separate `Domain`, `Infrastructure`, `UI`
- **Repository decorators** — mandatory `AuditDecorator` and `EncryptionDecorator`
- **Outbox pattern** — all remote syncs (gRPC) go through the `SyncOutbox` table
- **ACS normalization** — map all external vendor data to the `BiometricEvent` schema

## Coding standards
- **Naming:** PascalCase for methods/classes; camelCase with `_` prefix for private fields
- **Memory:** `Span<T>` for parsing. Avoid heap allocations in ingestion loops.
- **Async:** `Task.Run` for CPU-bound ML/rendering. `ValueTask` for high-frequency DB writes.
- **Types:** `record` for immutable DTOs and for `BiometricEvent`

## Layout
- `src/` — application + library projects (`Axon.Core`, `Axon.Infrastructure`, `Axon.UI`)
- `tests/` — single test project `tests/Axon.Tests` (unit + integration)
- `Axon.sln` — solution file (CI consumes this)
- `Directory.Build.props` — shared MSBuild props (enables NuGet lock files via `RestorePackagesWithLockFile`)
- `NuGet.Config` — feed configuration consumed by CI

## CI/CD
- `.github/workflows/ci.yml` — wraps shared `stevenfackley/gh-actions/.github/workflows/ci-dotnet.yml@v1`:
  - `project-name: axon`, `solution-path: Axon.sln`
  - `runs-on: windows-latest`, `aot-rid: win-x64`
  - `install-workloads: android`, `nuget-config-path: NuGet.Config`
- `.github/workflows/architecture.yml` — separate architecture verification
- Conventional Commits. `main` protected; squash-merge via PR.

## Commands
- Build: `dotnet build`
- Run: `dotnet run --project src/Axon.UI`
- Test: `dotnet test`
- Desktop publish: `dotnet publish src/Axon.UI -f net10.0 -p:TargetFrameworks=net10.0 -r win-x64 -c Release --self-contained true -p:PublishReadyToRun=true -p:PublishTrimmed=false`

## Security guardrails
- **No cloud:** block any attempt to add telemetry (ApplicationInsights/Segment/Datadog/etc).
- **PII shield:** `ToString()` methods on domain objects must not leak biometric values.
- **Encryption:** any new storage service must use the `IHardwareVault` interface for key derivation.

## Do not
- Break `Axon.Core`'s Native AOT compatibility (CI publishes it with AOT). The desktop app itself is not AOT: ML.NET and EF Core need reflection.
- Disable `TreatWarningsAsErrors`.
- Bypass the `EncryptionDecorator` on biometric data writes.
- Add `using` directives that aren't used (`IDE0005` is fatal under `TreatWarningsAsErrors`).
- Add telemetry SDKs.
