## Summary

<!-- A concise description of what this PR does. Link the related issue: -->

Closes #<!-- issue number -->

## Type of Change

- [ ] 🐛 Bug fix (non-breaking change that fixes an issue)
- [ ] 🚀 New feature (non-breaking change that adds functionality)
- [ ] 💥 Breaking change (fix or feature that causes existing functionality to change)
- [ ] 🔌 New wearable driver (`IBiometricDriver` implementation)
- [ ] 🔒 Security fix
- [ ] 🧹 Chore / refactor / docs (no functional changes)

## Changes Made

<!-- List the specific files and what changed in each. -->

- 
- 
- 

## Architecture Boundary Check

- [ ] `Axon.Core` does **not** reference `Axon.Infrastructure` or `Axon.UI`.
- [ ] All new I/O and external calls are in `Axon.Infrastructure`.
- [ ] No business logic was added to `Axon.UI`.

## Security & Privacy Checklist

- [ ] No hardcoded keys, salts, or connection strings were introduced.
- [ ] No third-party telemetry, analytics, or crash-reporting SDK was added.
- [ ] All new domain `ToString()` methods mask biometric `Value` fields.
- [ ] All new repositories are decorated with `AuditLoggingDecorator` and `EncryptionDecorator`.
- [ ] `IHardwareVault` is used for any new cryptographic key derivation.

## Performance & Memory

- [ ] No `dynamic` types or reflection introduced in `Axon.Core` (it stays Native AOT-clean).
- [ ] Hot-path ingestion loops use `Span<T>` / `ArrayPool<T>` — no per-event heap allocations.
- [ ] Data requests spanning >24 hours route through `LttbDownsampler`.
- [ ] UI thread is never blocked (heavy work dispatched via `Task.Run`).

## Definition of Done

- [ ] `dotnet build` — zero warnings.
- [ ] `dotnet test` — all tests pass.
- [ ] Desktop publish: `dotnet publish src/Axon.UI -f net10.0 -p:TargetFrameworks=net10.0 -r win-x64 -c Release --self-contained true -p:PublishReadyToRun=true -p:PublishTrimmed=false` — succeeds.
- [ ] New `NormalizationMapper` has **100% unit test coverage** (if applicable).
- [ ] Memory profiling shows zero hot-path heap allocations (if touching ingestion).
- [ ] `docs/` updated for any new `IBiometricDriver` or architectural change.
- [ ] `CHANGELOG.md` updated under `[Unreleased]`.

## Testing

<!-- Describe how you tested this change. -->

- [ ] Unit tests added / updated
- [ ] Integration tests added / updated
- [ ] Manually tested on: <!-- Windows 11 / Android / iOS -->

## Screenshots / Recordings

<!-- If this is a UI change, include before/after screenshots. -->

## Additional Notes

<!-- Anything reviewers should pay special attention to. -->
