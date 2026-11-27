# Decisions

ADR log. One entry per architectural decision. Append-only; supersede with a new entry.

## Format

```
## {{DATE}} — {{title}}
**Status:** proposed | accepted | superseded by #N
**Context:** why we had to decide
**Decision:** what we chose
**Consequences:** what follows (pros, cons, risks)
```

---

## 2026-04-17 — Initial stack: .NET 10 Native AOT

**Status:** superseded by 2026-10-29 — Actual stack: Avalonia desktop + mobile satellites
**Context:** Greenfield service under portfolio `repo-template-dotnet10-aot`. Target: fast cold-start, small image, Linux deploys.
**Decision:** .NET 10 with `PublishAot=true`, `linux-musl-x64`, distroless static runtime.
**Consequences:**
- Cold start < 100ms, image ~15MB.
- Reflection, dynamic code gen restricted — must stay AOT-compatible.
- No Application Insights SDK (banned by CI); stdout logs only.

## 2026-06-29 — Deferred: SkiaSharp 3.119.4 → 4.148.0 (major)

Dependabot #59 held. SkiaSharp 4 is a major: assembly/native-asset repackaging, removed deprecated SK* members, GPU/backend API changes. Tightly coupled to Avalonia.Skia (#57) — bump together or rendering breaks. NOTE: CI is separately red on the Axon.Core secret-scan false-positive (the `VerificationSecret` field name matches the `secret=` grep in architecture.yml), so neither this nor #57/#58 can merge until that gate is tightened. Awareness-only.

## 2026-10-29 — Actual stack: Avalonia desktop + mobile satellites

**Status:** accepted (supersedes 2026-04-17 — Initial stack); its desktop-publish line is superseded by 2026-11-27 — Desktop app ships without Native AOT
**Context:** The 2026-04-17 entry came from the `repo-template-dotnet10-aot` baseline and describes a Linux container service (`linux-musl-x64`, distroless, ~15MB image). Axon is not that: it is a local-first Avalonia app, and no container or distroless deploy exists.
**Decision:** .NET 10 (`global.json` SDK 10.0.100) with Avalonia UI. `src/Axon.UI` targets `net10.0` (desktop) plus `net10.0-android`, and `net10.0-ios` when built on macOS. CI (`ci.yml`, reusable `ci-dotnet.yml@v1` on `windows-latest`) validates a Native AOT publish for `win-x64`; Android/iOS are the Satellite builds (`build/`).
**Consequences:**
- Windows x64 desktop is the primary target; Native AOT is validated in CI and enabled at publish time with `/p:PublishAot=true`.
- AOT constraints still apply: no reflection or dynamic code gen on hot paths.
- No container image, no Linux deploy target.

## 2026-10-29 — SkiaSharp 4 + Avalonia 12 adopted (supersedes 2026-06-29 deferral)

**Status:** accepted (supersedes 2026-06-29 — Deferred: SkiaSharp 3.119.4 → 4.148.0)
**Context:** The 2026-06-29 deferral held SkiaSharp 4 and noted CI was red on the Axon.Core secret-scan false positive.
**Decision:** SkiaSharp 4 was adopted with the DrawText API migration (#62, 2026-06-30) and is on 4.151.2. Avalonia is on 12.1.4 (desktop/core packages; `Avalonia.Android`/`Avalonia.iOS` on 12.0.0). The secret-scan grep in `architecture.yml` was anchored to string literals (#61, 2026-06-29).
**Consequences:**
- The deferral no longer applies; SkiaSharp/Avalonia updates flow through Dependabot normally.
- `ci` and `architecture` are green on `main` (last runs 2026-09-24); the secret-scan gate no longer blocks merges.

## 2026-11-27 — Desktop app ships without Native AOT

**Status:** accepted (supersedes the Native AOT publish in 2026-10-29 — Actual stack)
**Context:** The documented release build published `Axon.UI` with `/p:PublishAot=true`. Under Native AOT the two ML.NET models fail at run time (a minimal ML.NET 5.0.1 probe published with AOT failed on its first schema lookup), so the anomaly and forecast cards could never work in the build people run. EF Core also builds its model by reflection.
**Decision:** Publish the desktop app (`net10.0`, `win-x64`) self-contained with ReadyToRun and without trimming (`build/build-windows.cmd`). `Axon.Core` stays Native AOT-clean, and CI keeps publishing it with AOT. Mobile build settings are unchanged.
**Consequences:**
- The models run in the shipped desktop build.
- The publish folder is about 320 MB (282 files, about 110 MB of it native debug symbols from the SkiaSharp and HarfBuzz packages), and start-up includes the JIT work that ReadyToRun does not cover.
- No C++ build tools are needed to publish the desktop app.
