# Torrent Downloader

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Avalonia UI](https://img.shields.io/badge/UI-Avalonia-6E48AA)
![MonoTorrent](https://img.shields.io/badge/BitTorrent-MonoTorrent-2E7D32)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-0078D4)
![Tests](https://img.shields.io/badge/tests-37%20passing-brightgreen)

A cross-platform desktop downloader for **Windows 11** and **Ubuntu Linux** — one C# codebase, no backend server. It handles `.torrent` files, magnet links, and plain `http(s)://` direct links side by side, with resume, speed limits, and a live ETA, all wrapped in a clean rounded-card UI.

---

## Table of contents

- [Why no server](#why-no-server)
- [Features](#features)
- [Architecture](#architecture)
- [Project layout](#project-layout)
- [Getting started](#getting-started)
- [Publishing a standalone build](#publishing-a-standalone-build)
- [Configuration & data locations](#configuration--data-locations)
- [How the trickier parts work](#how-the-trickier-parts-work)
- [Testing](#testing)
- [Known limitations](#known-limitations)

---

## Why no server

BitTorrent is peer-to-peer by design: the app talks directly to **trackers**, the **DHT** network, and other **peers** over the open internet. There is no proprietary backend to run, host, or pay for — the entire "infrastructure" already exists as part of the BitTorrent protocol. Direct links are even simpler: a plain HTTP GET straight to the source.

```
   Your desktop app                     The internet
 ┌─────────────────────┐        ┌───────────────────────────┐
 │  Avalonia UI         │        │  BitTorrent trackers       │
 │       │              │◄──────┤  DHT network                │
 │  TorrentEngineService│        │  Other peers/seeders       │
 │  HttpDownloadService │◄──────┤  Direct-link HTTP servers  │
 │       │              │        └───────────────────────────┘
 │  Local disk          │
 └─────────────────────┘
```

## Features

### Torrents (`.torrent` file or magnet link)
- DHT peer discovery (bound and working — not just requested) + tracker announce, both surfaced live in the UI
- UPnP port-forwarding attempt, with honest status reporting when it fails
- Tuned for faster swarm ramp-up (raised simultaneous-connection ceiling)
- Per-torrent seed/leech counts and tracker status, not just a raw peer number
- Pause / resume / remove

### Direct links (`http://` / `https://`)
- Resume via HTTP `Range` requests — falls back to a clean restart if the server ignores the range
- Filenames sanitized and capped under the filesystem limit (handles 300+ character signed/proxy URLs), preferring the real name from `Content-Disposition` when the server sends one
- **Stall detection**: a connection that goes quiet for 30s is treated as a failure instead of hanging forever
- **Bounded auto-retry**: stalls retry automatically (5s backoff, up to 5 attempts) — but only while genuinely making zero progress, so a flaky-but-working link keeps retrying while a truly dead one gives up and asks for a manual retry

### Shared across both
- **Global speed limits** (KB/s, `0` = unlimited) — a real token-bucket limiter for direct links, engine-level limiting for torrents; both apply live, no restart needed
- **Live ETA** per item, computed from remaining bytes ÷ current speed (`45s`, `2h 15m`, `1d 3h`, or `—` when it can't be estimated)
- **Auto-pause / auto-resume on network changes** — disconnect pauses everything active, reconnect resumes only what *was* auto-paused (a download you paused yourself stays paused). Detected via the OS network-change event *and* a 5-second fallback poll, so it doesn't depend on the OS event firing reliably
- **Session persistence** — active torrents and downloads survive closing and reopening the app. Saved on clean exit and every 10 seconds in the background (so a crash or force-kill doesn't lose everything). HTTP downloads resume from the real bytes on disk, not just the last-saved number, in case the app died mid-write
- **Diagnostics readout** (DHT status/node count, port-forwarding status) so "it's slow" has an actual answer instead of a guess

## Architecture

```
┌──────────────────────────── TorrentDownloader.UI (Avalonia) ─────────────────────────────┐
│  MainWindow.axaml            MainViewModel                                                │
│  (bubble-card UI)     ┌──►  TorrentItemViewModel  ─────┐                                  │
│                       │     HttpDownloadItemViewModel   │                                 │
│                       └────────────────┬─────────────────┘                                │
└────────────────────────────────────────┼──────────────────────────────────────────────────┘
                                          │
┌─────────────────────────────────────────▼─────────────────── TorrentDownloader.Core ──────┐
│  TorrentEngineService        HttpDownloadService        NetworkMonitorService (shared)     │
│    wraps MonoTorrent           chunked HTTP GET            OS event + 5s fallback poll      │
│    ClientEngine                + RateLimiter                                                │
│                                                                                              │
│  SettingsService · SessionStore · Formatting (ByteSize / Rate / Duration / Eta)             │
└──────────────────────────────────────────────────────────────────────────────────────────┘
                                          │
                              ┌───────────┴────────────┐
                              │      Local disk         │
                              │  settings.json           │
                              │  session.json             │
                              │  DHT cache / fast-resume    │
                              │  downloaded files              │
                              └────────────────────────────────┘
```

## Project layout

```
src/
  TorrentDownloader.Core/     No UI dependency — fully unit-testable
    Models/                    TorrentInfo, HttpDownloadInfo, session/diagnostics records
    Services/                  TorrentEngineService, HttpDownloadService, RateLimiter,
                                NetworkMonitorService, SettingsService, SessionStore
    Utils/                     Formatting (byte sizes, rates, durations, ETA)

  TorrentDownloader.UI/       Avalonia MVVM application
    ViewModels/                 MainViewModel, TorrentItemViewModel, HttpDownloadItemViewModel
    Views/                      MainWindow.axaml (rounded "bubble" card UI)

tests/
  TorrentDownloader.Tests/    xUnit — see Testing below
```

## Getting started

**Prerequisites:** .NET 10 SDK

```bash
# Build
dotnet build

# Run (dev)
dotnet run --project src/TorrentDownloader.UI/TorrentDownloader.UI.csproj

# Test
dotnet test
```

## Publishing a standalone build

Self-contained, single-file — no separate .NET install needed on the target machine.

```bash
# Windows
dotnet publish src/TorrentDownloader.UI -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64

# Ubuntu Linux
dotnet publish src/TorrentDownloader.UI -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64
```

## Configuration & data locations

Everything lives under a per-user app-data folder — nothing touches the registry or system paths.

| | Windows | Linux |
|---|---|---|
| App data (settings, session, DHT cache) | `%APPDATA%\TorrentDownloader` | `~/.config/TorrentDownloader` |
| Default download folder | `Downloads\TorrentDownloader` | `~/Downloads/TorrentDownloader` |

Downloaded files' location, and both speed limits, are configurable from the UI and persisted immediately.

## How the trickier parts work

**Rate limiting.** `RateLimiter` is a small token bucket: it starts with one second's worth of "burst" allowance, then refills continuously at the configured rate. Direct links check the bucket after every chunk read; the torrent engine gets the same number handed to MonoTorrent's own limiter. Set either to `0` for unlimited.

**Stall detection.** Each read from an HTTP response has its own rolling timeout, reset on every successful chunk. If 30 seconds pass with nothing received, that's treated as a failure — distinct from the server *closing* the connection, which is easy to detect on its own. A stall that occurs while genuinely offline is folded into the same auto-pause/resume path as a real disconnect, rather than showing as a scary permanent error.

**Session persistence.** Every active torrent and download is tracked as a small persisted record (URL/magnet/torrent-file copy + save path + progress) and written to `session.json`. Torrent files you add are copied into the app's own storage first, so restoring never depends on your original file staying where it was.

## Testing

37 xUnit tests, run against the real services rather than mocks wherever practical (a real `ClientEngine`, real small HTTP downloads, a real simulated stall/reconnect):

- `RateLimiterTests` — token-bucket timing, burst allowance, unlimited mode
- `FormattingTests` — byte-size/duration/ETA formatting edge cases
- `HttpDownloadService*Tests` — filename sanitization, session save/restore, the offline-vs-online failure race, real download completion
- `AutoResumeOnReconnectTests` — a live download paused/resumed via simulated connectivity flips
- `TorrentEngineServiceTests`, `SessionStoreTests`, `SettingsServiceTests` — rate limits, session round-trips, settings persistence

A handful of tests reach real external hosts (e.g. `releases.ubuntu.com`) to verify actual download behavior end-to-end; these can fail in a network-restricted environment for reasons unrelated to the code.

## Known limitations

- No per-torrent rate limits — the speed limit setting is global across all torrents and downloads
- No sequential/prioritized piece ordering within a multi-file torrent
- No native installer yet (Inno Setup for Windows / `.deb`/AppImage for Linux) or file associations for `.torrent` / `magnet:` links
- Direct links download over a single connection — no parallel/segmented downloading (the technique download accelerators use to beat a per-connection server cap)
