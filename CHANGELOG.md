# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.5.0] - 2026-11-04

### Changed
- Pre-release testing version for Emby 4.10+.
- Includes all P0 architectural fixes (P0-01 to P0-06).
- Includes native provider migrations for Real-Debrid and AllDebrid, plus security enhancements.

## [1.4.0.0] - 2026-11-04

### Added
- Abstraction layer for Debrid providers (IDebridProvider + ProviderRegistry).
- In-memory negative cache with exponential backoff on provider failures.
- Request coalescing for concurrent .strm resolution.

### Changed
- Translated all plugin UI strings, logs and comments to English.
- Migrated Real-Debrid logic to a native provider (`Providers/RealDebridProvider.cs`).
- Migrated AllDebrid logic to a native provider (`Providers/AllDebridProvider.cs`); success without a link is now reported as `Pending` so the resolver retries with the pending backoff.

### Security
- AllDebrid API key moved from URL query to Authorization Bearer header (P1-03).
- Added SecretRedactor to prevent API keys from leaking into logs.

## [1.3.0.0] - 2026-11-04

### Added
- Emby 4.10+ compatibility (netstandard2.0, MediaBrowser.Server.Core 4.10.0.24-beta2).
- Negative cache and exponential backoff to prevent provider rate-limits.
- Request coalescing for concurrent .strm resolution.

### Fixed
- Race condition that could overwrite `.strm.original` with the resolved URL, permanently losing the source link (P0-01).
- Unstable MediaSource ID across process restarts caused by `GetHashCode()` (P0-02).
- Mass resolution of all `.strm` files in a directory when the filename started with `(` or `[` (P0-06).
- Plugin failing to load on Emby 4.10 due to `IMediaSourceProvider` signature change.
- GUID collision with Chomyfin that caused Emby to display the wrong plugin UI.

### Changed
- `Plugin.Configuration` is now null-safe to tolerate a known Emby 4.10 bug.
- Abstracted Debrid providers into IDebridProvider + ProviderRegistry (P0-04).

## [1.2.0.0] - 2026-11-04

### Added
- Initial public release.
- Resolve .strm links to direct URLs via Debrid providers.
- Support for Real-Debrid and AllDebrid.
- Cache of resolved URLs with automatic refresh on expiry.
- Plugin settings UI for API keys and provider selection.

[1.4.5.0]: https://github.com/Johanst21/Debryfin/compare/v1.4.0.0...v1.4.5.0
[1.4.0.0]: https://github.com/Johanst21/Debryfin/compare/v1.3.0.0...v1.4.0.0
[1.3.0.0]: https://github.com/Johanst21/Debryfin/compare/v1.2.0.0...v1.3.0.0
[1.2.0.0]: https://github.com/Johanst21/Debryfin/releases/tag/v1.2.0.0
