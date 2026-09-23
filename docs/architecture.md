# Architecture

## Shape of the app

anSMBserver is a **.NET for Android** application (`net10.0-android`) that hosts an SMB server on
the phone. It is deliberately small: a screen, a foreground service, a storage adapter, and a
vendored SMB protocol engine.

```text
┌──────────────────────────── Android app process ────────────────────────────┐
│                                                                             │
│  MainActivity (UI)                       SmbService (foreground service)    │
│  ├─ SERVER            start/stop, state  ├─ owns the SMBServer instance     │
│  ├─ SERVER SETTINGS   port/user/pass     ├─ runs the server for the app     │
│  ├─ INFO              address, QR,       ├─ owns the notification           │
│  │                    connections        ├─ run-intent / auto-start owner   │
│  └─ DIAGNOSTICS       product log        └─ persists port/credentials/…     │
│                     │                                  │                    │
│                     └────────── settings, state ───────┘                    │
│                                                        │                    │
│                            AndroidFileSystemAdapter ───┘                    │
│                                       │                                     │
│                            IAndroidStorageBackend                           │
│                                       │                                     │
│                                 PathStorageBackend                          │
│                                                                             │
│  SMBLibrary (vendored, unmodified)  ← protocol engine: SMB1/SMB2/SMB3,      │
│  + Utilities                          negotiation, NTLM, signing, shares    │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Responsibilities

| Component | Owns |
|---|---|
| `MainActivity` | The whole UI: server state, settings (with the apply gate), the address/QR/connections block, and the product log view. It never talks to SMB clients directly. |
| `SmbService` | The lifecycle: it creates and owns the server, keeps it alive as a foreground service, owns the notification, handles re-entry through its run-intent, and owns persisted configuration (port, credentials, auto-start). |
| `AndroidFileSystemAdapter` | Implements the library's file-store interface over Android storage (open/create/read/write/metadata/delete/directory enumeration). |
| `IAndroidStorageBackend` + `PathStorageBackend` | The storage abstraction the adapter is written against, so the file semantics are not hard-wired to one Android API surface. |
| `ShareAddress` | Builds the `smb://host:port/share` strings shown and copied in the UI. |
| `SMBLibrary` (+`Utilities`) | Everything protocol-level: dialect negotiation, NTLM authentication, tree connect, SMB2/3 command handling, signing. |

Design rules behind this split (kept from the project's engineering guidelines):

1. **The protocol engine is the library.** SMB behaviour is never re-implemented in the app.
2. **Android concerns stay in the app** — permissions, foreground service, notifications,
   storage access, UI.
3. **Filesystem access sits behind an abstraction**, so the adapter and the path backend can
   evolve independently.
4. **No speculative protocol behaviour**: when the library does not implement something, the app
   does not fake it. (This is why the `FileAllInformation` gap in
   [Limitations](limitations.md) is stated rather than worked around.)

## The SMB library is vendored and unmodified

`SMBLibrary/` contains the library **plus its `Utilities` project** as vendored source, not as a
binary dependency.

- It is **upstream pristine**: no local source modifications are applied. The vendored tree is kept
  byte-for-byte identical to the upstream snapshot, so upgrading is a straight replacement.
- Because it ships as LGPL-3.0-or-later source inside the application, the corresponding-source
  obligation is satisfied by shipping this directory. Since there are **no modifications**, no
  GPL §5(a) change notices are required or present.
- `Utilities` is merged into the same assembly at build time; the license and attribution are
  recorded in `NOTICE`.

Third-party components: SMBLibrary/Utilities (LGPL-3.0-or-later), `Net.Codecrete.QrCodeGenerator`
(MIT, used for the QR image), and the .NET runtime / .NET for Android (MIT). `NOTICE` is the
authoritative list.

## Build pipeline

| Stage | What happens |
|---|---|
| Toolchain | .NET 10 SDK with the `android` workload, an Android SDK, and JDK 17 |
| Configuration | `Release`, release signing identity (a fail-fast check refuses to fall back to debug signing) |
| Library merge | The library and `Utilities` are merged into a single assembly (ILRepack) as part of the build |
| Size | Assembly trimming is enabled for the release artifact |
| Output | **APK only** (1.0 pins the package format to `apk`; no AAB is produced) |

The application's own code is Apache-2.0; see `LICENSE`.

## Repository layout

```text
README.md                  entry point and overview
LICENSE                    Apache-2.0 (this project's own code)
NOTICE                     bundled components, licenses, corresponding source
COPYING.GPL-3.0            GNU GPL v3 text (LGPL-3.0 incorporates it)

src/anSMBserver/           application source + project file + Resources
SMBLibrary/                vendored SMBLibrary + Utilities (LGPL-3.0-or-later, unmodified)
docs/                      user and developer documentation
```

## Security posture

- Runs as an ordinary app: **no root**, no privileged port.
- **One account**, configured in the app; anonymous and guest access are disabled.
- Credentials are stored on the device by the app and are **never written to the product log**.
- The server binds the phone's LAN interface; it is not intended to be exposed to the internet.
