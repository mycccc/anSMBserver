# Release and verification

This page records the **release identity**, how the release artifact is built and signed, and what
was actually verified before 1.0 was frozen.

> **Redaction note.** This is the public version of the project's release record. Operational
> details of the private build environment (host names, network addresses, user names and
> filesystem paths) are intentionally omitted; the unredacted record is kept in the project's
> local archive. Nothing in this page has been reworded to change a result: where a check was
> partial, it says partial.

## 1. Release identity

| Item | Value |
|---|---|
| Application name | **anSMBserver** |
| Package (application id) | `com.github.mycccc.ansmbserver` |
| Version | **1.0** (versionCode **1**) |
| `minSdkVersion` / `targetSdkVersion` | 23 / 36 |
| Native ABIs | `arm64-v8a`, `x86_64` |
| Package format | **APK only** (the package format is pinned to `apk`; no AAB is produced for 1.0) |
| Signing certificate subject | `CN=anSMBserver, OU=Release, O=anSMBserver` |
| Certificate SHA-256 | `f0d404b80b840fe98d9fc0005a9728978a973ecbdbe5332266cb67527f511074` |
| Certificate SHA-1 | `9fa6ff5f7cf967b6388b55e718aad67f41f1e698` |
| Signature schemes | v1 (JAR), v2 and v3 verified present; single signer |

The certificate fingerprint is the value to compare against when you want to confirm that an APK
came from this project's release identity:

```text
apksigner verify --print-certs <apk>
```

## 2. Artifact of record (1.0)

| Item | Value |
|---|---|
| File | signed release APK (`com.github.mycccc.ansmbserver-Signed.apk`) |
| Size | **7,408,206 bytes** |
| SHA-256 | `4739ef2abfa439df287333976d6f3189c6e2b294908d67dcbd978bb231ff5c91` |

This is the artifact that 1.0's device verification was performed on, including the check that the
copy installed on the device is byte-identical to it.

## 3. Signing and key handling

- The release key is kept **outside the repository** in a per-user location with restrictive
  permissions; it is never added to version control.
- The key **passphrase is not stored next to the key**. For a build it is supplied transiently to
  the build process and is never written to the build log.
- **Build logs are scanned for the passphrase before they are archived.** The 1.0 build logs
  contain **0 occurrences**. (An earlier release-build attempt did expose the passphrase through
  verbose build output; that log was removed from the archive and never committed, and the
  passphrase was rotated afterwards — the certificate, and therefore the fingerprint above, was
  unchanged by the rotation.)
- The project file contains a **fail-fast check**: a Release build refuses to fall back to debug
  signing if the release identity is not configured.
- Build output verbosity is kept at minimum precisely so that signing material cannot leak into
  logs.

## 4. How the release is built

| Stage | Configuration |
|---|---|
| SDK | .NET 10 SDK with the `android` workload (an Android SDK and JDK 17 are required) |
| Configuration | `Release` |
| Library | SMBLibrary + `Utilities` merged into a single assembly (ILRepack) during the build |
| Size | Assembly trimming enabled (verified at runtime — see the device coverage below) |
| Output | signed APK (and the unsigned intermediate); **no AAB** |
| Result | `Build succeeded`, **0 errors**, 6 warnings — all pre-existing (upstream obsolete-API warnings in the library and API-level advisory warnings in the app) |

The library is vendored **unmodified** (see [Architecture](architecture.md)); the build consumes it
as source, which is how the LGPL corresponding-source obligation is met.

## 5. Verification gates

| Gate | Scope | Result |
|---|---|---|
| **Signing configuration** | release identity configured, debug-signing fallback impossible, identity asserted with two independent toolchains | **PASS** |
| **Log specification** | tier policy, bounds, no password anywhere in the logs, crash-guard records routed through the session-tier channel | **PASS** |
| **Licensing** | `LICENSE` + `NOTICE` + `COPYING.GPL-3.0`; component inventory of the shipped APK; LGPL obligations mapped for a combined work; corresponding source supplied as the vendored library directory | **PASS** |
| **Trimmed-release runtime** | startup, listener, foreground service, NTLM authentication, tree connect, directory enumeration, SMB 3.0 negotiation and an SMB **write** on the trimmed artifact | **PARTIAL** — transferring file **data** outward was not verified (pre-existing `FileAllInformation` gap, see [Limitations](limitations.md)) |
| **Diagnostics removal** | the intermediate debug-diagnostics feature removed from the release product, library restored to upstream pristine (full-tree comparison against the upstream snapshot: 666 files vs 666 files, **0 added / 0 deleted / 0 modified**), new artifact built, signed and verified | **PASS** |

## 6. Device verification of the 1.0 artifact

Performed on a physical Android device with the artifact in §2.

| Area | Result |
|---|---|
| Install / package / signature | installed; the APK **pulled back from the device is byte-identical** to the artifact of record (same SHA-256) |
| Cold launch | app starts; server starts (auto-start ON); listener bound on the configured port; no crash |
| Auto-start OFF / ON | with OFF, a cold launch does **not** start the server; with ON it does; changing it while the server runs is correctly **rejected** by the apply gate |
| Start / Stop | both transitions recorded by the server and by the product log |
| Run intent / notification | launching via the launcher intent starts the app and the server; the foreground notification is owned by the service; the service is **not exported**, so it cannot be started by another app |
| Port | server bound to the configured unprivileged port; a client connected on that port |
| Address / QR | `Copy Address` copies the `smb://host:port/share` address; the QR code renders |
| NTLM authentication | session setup succeeds with the configured account; recorded as `authenticated successfully` |
| Tree connect | `IPC$` and the share connect successfully |
| Directory | root and nested directory enumeration succeed (a 162-entry root listing was served) |
| Write | create / write / overwrite / delete succeed, including non-ASCII names and nested directories |
| Disconnect | tree disconnect and client close are recorded in the product log |
| Connections counter | `0` idle → `1` with an open session → `0` after the session ends |
| Product logs / Clear / Copy | log fills with session lines; `Clear Logs` empties it; `Copy Logs` copies the product log |
| Debug diagnostics absent | **0** occurrences in the UI, **0** `SmbDiag`-tagged lines in the device log, and the removed types are absent from the shipped assemblies |

A repeated run of the verification client once reported two directory-creation failures caused by
the previous run's leftovers (the client creates a directory and only cleans up files). With the
leftover removed, the run is clean. This was a **test-client state artifact**, not a server defect —
creating a directory that already exists correctly reports a name collision.

## 7. Reproducing the checks

```text
1. Verify the signature and fingerprint:
     apksigner verify --print-certs <apk>
2. Verify the artifact identity:
     sha256sum <apk>          # compare with §2
3. Install over an existing installation (identical signing key ⇒ normal update):
     adb install -r <apk>
4. Confirm what the device actually has:
     adb shell pm path com.github.mycccc.ansmbserver
5. Capture the server log for the session you are testing:
     adb logcat -s SmbSrv
```

## 8. License cross-reference

The authoritative component list, its licenses, the corresponding-source arrangement and the
combined-work mapping are in [`NOTICE`](../NOTICE). The project's own code is Apache-2.0
(`LICENSE`); the vendored SMBLibrary/Utilities are LGPL-3.0-or-later and are shipped unmodified;
the GNU GPL v3 text is in `COPYING.GPL-3.0`.
