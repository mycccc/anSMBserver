# Configuration

All settings live in the **SERVER SETTINGS** section.

| Setting | Default | Range / rule |
|---|---|---|
| Username | `ansmb` | must not be empty |
| Password | `ansmb` | must not be empty |
| Port | `4450` | **1024 – 65535** |
| Start server automatically | **ON** | on / off |

Settings are committed with **Apply Settings**. **Restore Defaults** returns username, port and
auto-start to the table above.

## The Apply gate

`Apply Settings` is **all-or-nothing**: if any field fails validation, nothing is committed — the
running server is left untouched and no partial state is created. The app reports this in the
product log as `settings: apply rejected (nothing committed)`, and the reason appears inline under
the offending field.

Two settings affect the server lifecycle and therefore **cannot be changed while the server is
running**:

| Setting | Rule while the server is running |
|---|---|
| **Port** | rejected — `Stop the server before changing the port` |
| **Start server automatically** | rejected — `Stop the server before changing this setting` |

Username and password can be applied while the server is running.

Because `Restore Defaults` may change the port and auto-start, it obeys the **same** gate: while
the server is running it is rejected with the same inline messages.

A successful apply is recorded in the product log:

```text
settings: applied (username=<user>, port=<port>, auto-start=<true|false>)
```

## Port

The default port is **4450**.

It is not 445 on purpose: binding port 445 requires root, and anSMBserver deliberately runs as an
ordinary app. The allowed range is therefore the **unprivileged range, 1024–65535** — the app
validates this directly (on the device the platform's `net.ipv4.ip_unprivileged_port_start` is
1024).

Practical consequences:

- Clients must be given the port explicitly, because their defaults assume 445.
  See [Connecting](connecting.md).
- Ports below 1024 are rejected by the settings validation.

## Credentials

anSMBserver uses a **single account**, configured in the app. The username and password are used
for SMB session setup (NTLM); the server does not use Android accounts, and no credentials are
sent anywhere except to the client that connects.

The defaults (`ansmb` / `ansmb`) exist so that a first connection works out of the box. Change
them before using the app on a network you do not fully control.

Credentials are stored by the app on the device. They are never written to the product log.

## Auto-start

With auto-start **ON** (the default) the server starts when the app is launched, and the app's
server run-intent keeps it owned by the foreground service. With auto-start **OFF** the server
starts only when you press **Start**.

Auto-start can only be changed while the server is stopped (see the Apply gate above).

## Notifications

The running server is a foreground service and must display a notification. The notification is
owned by the service, so the server survives closing the app screen. Denying the notification
permission on Android 13+ does not stop the server, but the platform may restrict the service
later.

## Not in 1.0

- No user-facing verbosity switch for the product log (the tiers are fixed; see
  [Logging](logging.md)).
- The share name (`Internal`) and the shared root (`/storage/emulated/0/`) are fixed.
- Settings are **not** exported or imported.
