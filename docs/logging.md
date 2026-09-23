# Logging

The app keeps a **bounded, in-memory product log** of what the server is doing. It exists to answer
the practical question "the client cannot connect — what did the server see?".

## What goes in the log

Events are classified into three tiers. The tiers are fixed in 1.0; there is no verbosity switch.

| Tier | Contents | Where it appears |
|---|---|---|
| **T1 — Session** | connection accepted/rejected (with client `ip:port`), dialect negotiation result, authentication success/failure with `NTStatus`, session setup, tree connect success/failure, disconnect and its reason, server start/stop, listener bind, settings apply/reject, crash-guard records | **log box + logcat** |
| **T2 — Failure** | any failing file/directory/metadata/protocol operation, compressed to a single line: `op path status` (plus session context) | **log box + logcat** |
| **T3 — Detail** | successful `Create`/`Close`/`Query Directory`/`Get|SetFileInformation`, listing details, `FileAccess`/`ShareAccess`/`SessionID`/`TreeID`/`FileId`, per-packet lines | **logcat only** |

Lines carry a category tag, for example:

```text
[AUTH]       Session Setup: User 'ansmb' authenticated successfully
[SMB]        Tree Connect: User 'ansmb' connected to 'Internal'
[NETWORK]    The client closed the connection
[FILESYSTEM] Query Directory on 'Internal\', Searched for '*', found 162 matching entries
[APP]        SMB server listening on <phone-ip>:4450
```

## Aggregation (and why some things are not line-per-item)

To keep the log readable and bounded, the UI log box **aggregates**:

- a directory listing is summarised (entry count), not printed entry by entry;
- copying *N* files does **not** produce *N* lines; failures are compressed to one line each.

The intended result: after heavy traffic the log box still contains the **session-level lines of
the most recent session**, not a wall of per-file lines.

## Bounds and lifetime

- The log lives **in memory only** — it is not written to disk and does not survive the process.
- It is capped (60,000 characters) and truncated from the **tail** when full, so the newest lines
  are always the ones you see.
- Moving the app to the background does not stop logging; the log box is part of the app screen.

## The buttons

| Button | Behaviour |
|---|---|
| **Clear Logs** | Empties the log box. |
| **Copy Logs** | Copies the lines currently shown in the log box to the clipboard, under a short header, so they can be pasted into a message or an issue. |

`Copy Logs` copies **the visible product log only**. There is no separate diagnostic log in 1.0
(see below).

## Capturing the log from a computer

The same lines are mirrored to the Android system log, so a full-session capture is possible over
`adb`:

```text
adb logcat -s SmbSrv
```

T3 detail (which is not shown in the app's log box) is available through the same capture.

## Privacy

- **Passwords are never logged**, in any tier, and this is a verified acceptance criterion for the
  release.
- File **contents** are never logged.
- The log does contain operational metadata: client `ip:port`, the configured username, share and
  file **paths** inside the shared volume, SMB status codes, session/tree/file identifiers and byte
  counts.

Because the log contains paths and client addresses, review it before pasting it into a public
issue tracker.

## A note on the removed debug channel

An intermediate build (2026-09-15 → 2026-09-18) had a `Debug diagnostics` switch that fed a
separate diagnostic box from observation hooks in the SMB library, mirrored to a `SmbDiag` log tag.

**1.0 does not have it.** The feature was removed from the release product on 2026-09-18 and the
library-side instrumentation was removed with it (the vendored library is upstream pristine). The
only remaining `SmbDiag`-tagged output is an internal log-pipeline health warning that reports a
slow log-lock, which is a diagnostic signal about the **logging path itself** and is unchanged.
