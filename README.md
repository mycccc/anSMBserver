# anSMBserver

A small, self-contained **SMB file server that runs on an Android phone**, so that other devices
on the same network (desktop, laptop, tablet) can browse and exchange files with the phone over
standard SMB.

It is a personal-use tool: no account, no cloud, no telemetry. Files stay on the device; the app
only exposes the storage you point it at, over your own local network.

> **中文简介**：anSMBserver 是一个运行在 Android 手机上的小型 SMB 文件服务器。启动后，同一
> 局域网内的电脑即可通过标准 SMB 协议访问手机上的共享目录（默认共享名 `Internal`，根目录
> `/storage/emulated/0/`）。无账号体系、无云端、无遥测；默认端口 `4450`，默认账号
> `ansmb / ansmb`（请在首次使用时修改）。

**Status:** 1.0 (versionCode 1) — see [release and verification](docs/release-and-verification.md).

---

## Features

- **SMB 2 / SMB 3 server** (maximum negotiated dialect: **SMB 3.0**), running on a non-privileged
  port so no root is required.
- **Start / Stop** from the UI, with a foreground service so the server keeps running while the
  app is in the background.
- **Auto-start** — optionally start the server when the app is launched.
- **Configurable** port, username and password, applied through a single *Apply Settings* gate.
- **Address helpers** — the app shows `smb://<phone-ip>:<port>/Internal`, with **Copy Address**
  and a **QR Code** for quick pairing.
- **Connections** — the current session count and list, with an explicit disconnect action.
- **Product logs** — a bounded, in-memory log of server activity (connect / authenticate / tree
  connect / file operations), with **Clear Logs** and **Copy Logs**.
- **Crash guard** — unhandled exceptions are recorded instead of killing the process silently.

## Requirements

| Item | Value |
|---|---|
| Android | **6.0 (API 23) or newer**; built against API 36 |
| CPU | `arm64-v8a` or `x86_64` |
| Storage | "All files access" on the shared volume (Android 11+ asks for it on first start) |
| Network | Phone and client on the same LAN |

## Install

anSMBserver is distributed as a **signed APK** (it is not published on Google Play).

1. Transfer `anSMBserver-<version>.apk` to the phone.
2. Allow installation from the source you used (browser / file manager) if Android asks.
3. Install and open the app.

The APK is signed with a project release key. If you previously installed a build signed with a
different key, Android will refuse the update — uninstall the old build first.

## Quick start

1. Open the app and grant **storage access** when prompted.
2. The server section shows the current state. **Auto-start is ON by default**, so the server
   starts with the app; press **Start** manually if you have turned auto-start off.
3. Note the address shown in the **INFO** section, e.g. `smb://192.0.2.10:4450/Internal`
   (the app shows your phone's real LAN address).
4. On the client, connect to `smb://<phone-ip>:4450/Internal` and sign in with the configured
   username and password (**default `ansmb` / `ansmb` — change it**).

Detailed instructions: **[Getting started](docs/getting-started.md)** ·
**[Connecting from a client](docs/connecting.md)**.

## Defaults

| Setting | Default | Notes |
|---|---|---|
| Port | **4450** | configurable, allowed range **1024–65535** |
| Username | `ansmb` | configurable |
| Password | `ansmb` | configurable — **please change it** |
| Share name | `Internal` | fixed in 1.0 |
| Shared root | `/storage/emulated/0/` | the primary shared storage volume |
| Auto-start | **ON** | starts the server when the app is launched; can be turned off while the server is stopped |

> The default port is **4450**, not 445: binding the privileged port 445 requires root, which this
> app deliberately does not request. Clients must be told the port explicitly
> (see [Connecting](docs/connecting.md) for client-specific notes).

## Documentation

| Document | Contents |
|---|---|
| [Getting started](docs/getting-started.md) | install, first run, first connection |
| [Configuration](docs/configuration.md) | port, credentials, auto-start, the Apply gate |
| [Connecting](docs/connecting.md) | connecting from macOS, Windows and other clients |
| [Limitations](docs/limitations.md) | known limitations and deferred items in 1.0 |
| [Architecture](docs/architecture.md) | how the app is put together; SMBLibrary and licensing |
| [Logging](docs/logging.md) | what the product log contains and how it is bounded |
| [Release and verification](docs/release-and-verification.md) | release identity, build and verification record |

## Building from source

The app is a .NET for Android project (`net10.0-android`) that references a **vendored, unmodified
copy of SMBLibrary** (see [Architecture](docs/architecture.md)). A source build therefore needs the
.NET 10 SDK with the `android` workload, an Android SDK and a JDK 17.

```text
src/anSMBserver/      application source and project file
SMBLibrary/           vendored SMBLibrary + Utilities (LGPL-3.0-or-later, unmodified)
docs/                 this documentation
```

## License

- **anSMBserver's own source code**: Apache License 2.0 — see [`LICENSE`](LICENSE).
- **SMBLibrary / Utilities** (vendored, unmodified): LGPL-3.0-or-later — see [`NOTICE`](NOTICE)
  and [`COPYING.GPL-3.0`](COPYING.GPL-3.0).
- **Net.Codecrete.QrCodeGenerator**: MIT.
- **.NET runtime / .NET for Android**: MIT.

`NOTICE` lists every bundled component, its version, its license and its copyright holder, and
records the corresponding-source arrangement for the LGPL-covered library.
