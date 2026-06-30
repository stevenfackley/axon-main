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

## {{DATE}} — Initial stack: .NET 10 Native AOT

**Status:** accepted
**Context:** Greenfield service under portfolio `repo-template-dotnet10-aot`. Target: fast cold-start, small image, Linux deploys.
**Decision:** .NET 10 with `PublishAot=true`, `linux-musl-x64`, distroless static runtime.
**Consequences:**
- Cold start < 100ms, image ~15MB.
- Reflection, dynamic code gen restricted — must stay AOT-compatible.
- No Application Insights SDK (banned by CI); stdout logs only.

## 2026-06-29 — Deferred: SkiaSharp 3.119.4 → 4.148.0 (major)

Dependabot #59 held. SkiaSharp 4 is a major: assembly/native-asset repackaging, removed deprecated SK* members, GPU/backend API changes. Tightly coupled to Avalonia.Skia (#57) — bump together or rendering breaks. NOTE: CI is separately red on the Axon.Core secret-scan false-positive (the `VerificationSecret` field name matches the `secret=` grep in architecture.yml), so neither this nor #57/#58 can merge until that gate is tightened. Awareness-only.