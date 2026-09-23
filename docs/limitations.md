# Limitations (1.0)

This page lists what anSMBserver 1.0 does **not** do, and the known gaps that were found and
deliberately left open. They are stated as they were recorded during the release work — nothing
here is a re-interpretation of an earlier result.

## Fixed by design in 1.0

| Item | 1.0 behaviour |
|---|---|
| Port | Servers cannot use port 445 (needs root); the default is **4450** and only the unprivileged range **1024–65535** is accepted. |
| Share name / root | `Internal` → `/storage/emulated/0/` are fixed; there is no multi-share UI. |
| Log verbosity | No user-facing verbosity switch. The tier policy is fixed (see [Logging](logging.md)). |
| Settings persistence | Credentials, port and auto-start are stored on the device; there is no export/import. |
| SMB dialect | Negotiation supports up to **SMB 3.0**. |
| Anonymous access | Disabled — a username and password are required. |

## Known gaps carried into 1.0

### Reading file data outward is not verified (the `FileAllInformation` gap)

On the release artifact, **transferring file data outward was not verified**. The macOS Samba
client opens the file, requests the `FileAllInformation` information class, receives
`STATUS_NOT_SUPPORTED`, and disconnects without issuing any read.

The same pattern appears in earlier (pre-release) evidence, so it is a **pre-existing
client/server information-class gap**, not a regression introduced by the release work. Everything
else in the session — negotiation, authentication, tree connect, enumeration, and **writing**
files — was verified on the same artifact.

### Recovery without a notification (N5)

When Android recovers the service on its own after the app is killed (a "sticky" service restart),
the platform can deny the foreground-service start, so the recovered server may run **without** its
notification. This conflicts with the "running ⇒ notification" rule. It is a platform restriction
on recent Android versions, was **not** caused by the lifecycle work, and is **not** claimed to be
solved. It needs a separate foreground-service/notification decision.

### Port range is enforced by the UI only (BD8)

The 1024–65535 rule is validated in the settings UI. The code path that loads the *stored* port
does not clamp it: a value below 1024 written by a build that predates the rule would still be used
at start-up and would then fail to bind. No such value can be produced by 1.0 itself; the item is
recorded as open rather than fixed, because fixing it means touching the frozen service code.

### Bind-failure path (BD7)

A bind failure (port already in use) is recorded, but the recovery/UX path around it is only
registered, not designed.

### Username casing (F7)

Username comparison semantics for casing are registered, not normalised.

### Multi-write commit deviation (R3)

A recorded deviation in the multi-write commit path is documented but deliberately left as-is:
fixing it was judged to be a refactor outside the release scope.

### Lifecycle verification (P1/P2)

The bind-only lifecycle scenarios (P1/P2) remain at "pending implementation-stage verification".

## Log granularity is intentional

Two behaviours can look like missing information but are by design:

- A directory listing of many entries is **aggregated**, not printed per entry.
- Copying *N* files does **not** produce *N* log lines in the UI log box; failures are compressed
  to one line each (`op path status`).

The consequence that matters: the UI log box keeps the session-level lines of the most recent
session even after heavy traffic. See [Logging](logging.md).

## Historical note: the debug diagnostics channel

Between 2026-09-15 and 2026-09-18 a build-time experiment existed that added a **Debug
diagnostics** switch and a separate diagnostic box fed by observation hooks in the SMB library.

**It is not part of 1.0.** The feature was removed from the release product on 2026-09-18 together
with its library-side instrumentation: the vendored SMB library was restored to **upstream
pristine**, so no debug switch, debug box or diagnostic channel exists in this release. This is
recorded here because the removed feature appeared in intermediate builds and in the project's
change history — not because 1.0 contains it.

## What is deliberately out of scope

- No Wi-Fi/network configuration, no discovery/broadcast of the service name.
- No per-user permissions, no read-only share mode, no quotas.
- No cloud, no account, no telemetry.
- No automatic updates.
