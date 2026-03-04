CLAUDE.md — Axon Project Guide
🛠 Tech Stack
Runtime: .NET 10 (Targeting Native AOT)

Language: C# 14

UI: Avalonia UI (MVVM Pattern)

Graphics: SkiaSharp (Direct GPU rendering)

Database: SQLite with SQLCipher (EF Core 10)

AI: ML.NET (Local ONNX inference)

📐 Architecture Patterns
Hexagonal Architecture: Strictly separate Domain, Infrastructure, and UI.

Repository Decorators: Mandatory use of AuditDecorator and EncryptionDecorator.

Outbox Pattern: All remote syncs (gRPC) must go through the SyncOutbox table.

ACS Normalization: Map all external vendor data to the BiometricEvent schema.

📝 Coding Standards
Naming: Use PascalCase for methods/classes, camelCase for private fields (with _ prefix).

Memory: Use Span<T> for data parsing. Avoid unnecessary heap allocations in ingestion loops.

Async: Use Task.Run for CPU-bound ML/Rendering. Use ValueTask for high-frequency DB writes where applicable.

Types: Use record for immutable data transfer objects (DTOs) and BiometricEvent.

🚀 Common Commands
Build: dotnet build

Run: dotnet run --project src/Axon.UI

Test: dotnet test

AOT Publish: dotnet publish -r win-x64 -c Release /p:PublishAot=true

🛡 Security Guardrails
No Cloud: Block any attempt to add telemetry (AppInsights/Segment).

PII Shield: Ensure ToString() methods on domain objects do not leak biometric values.

Encryption: Any new storage service must utilize the IHardwareVault interface for key derivation.