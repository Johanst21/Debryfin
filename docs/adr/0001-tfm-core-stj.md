# ADR 0001 — Emby 4.10 compatibility: TFM, Core, System.Text.Json

## Status
Proposed (pending smoke test on real Emby 4.10.x)

## Context
- Target host: Emby 4.10+ (runs on .NET 6).
- Plugin SDK packages are netstandard2.0.
- Current plugin: netstandard2.1, Core 4.9.1.90, System.Text.Json 8.0.5.
- System.Text.Json 8.0 references version 8.0.0.0; a .NET 6 host
  provides 6.0.x, causing FileNotFoundException in some cases
  (documented for System.Threading.Channels in Emby 4.10.0).

## Decision
- TFM: netstandard2.0
- MediaBrowser.Server.Core: 4.10.0.24-beta2 (exact, prerelease)
- System.Memory: reference 4.6.2 (satisfies assembly version requirement from MediaBrowser.Model 4.10)
- System.Text.Json: reference 6.0.10 with PrivateAssets=all and
  ExcludeAssets=runtime (do not ship the assembly; rely on host)
- Validated against Chomyfin (working Emby 4.10 plugin):
  - LangVersion=9.0 required (netstandard2.0 defaults to C# 7.3)
  - System.Memory 4.6.2 required (MediaBrowser.Model 4.10 → CS1705)
  - OpenMediaSource signature: (openToken, consumerId, liveStreams, ct)
  - Configuration pattern: SafeGetOptions() + null-coalescing fallbacks

## Consequences
- Compatible with Emby ≥ 4.10.0.24 (SDK version floor).
- Compatible with .NET 6 and .NET 8 hosts (STJ 6.0.x resolves upward).
- Requires smoke test on a real Emby 4.10.x to validate runtime.
- Rollback plan: revert to Core 4.9.1.90 if 4.10.0.24-beta2 has issues.

## References
- https://www.nuget.org/packages/MediaBrowser.Server.Core/
- https://github.com/MediaBrowser/Emby/issues/3993
