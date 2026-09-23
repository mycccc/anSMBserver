# Getting started

This page covers installing anSMBserver, the first run, and the first connection from another
device.

## 1. Install

anSMBserver is distributed as a **signed APK**; it is not on Google Play.

1. Copy `anSMBserver-<version>.apk` to the phone (download, USB, or any file transfer).
2. Open it from the file manager / browser and allow installation from that source if Android
   asks. On Android 8+ you may need to grant "install unknown apps" to the app you are opening
   the APK with.
3. Confirm the install.

Upgrades: install the newer APK over the existing one. The release key is stable, so Android
accepts it as an update. If you ever installed a build signed with a different key, uninstall the
old app first — otherwise Android reports an update conflict.

Requirements: **Android 6.0 (API 23) or newer**, on an `arm64-v8a` or `x86_64` device.

## 2. First run

On the first launch the app asks for the permissions it needs:

| Permission | Why |
|---|---|
| **All files access** | SMB clients browse the shared storage volume (`/storage/emulated/0/`). Without it the share cannot be served. |
| **Notifications** (Android 13+) | The server runs in a foreground service, which must show a notification while it is running. |

If you decline storage access, the app still starts but the share will not be usable — grant the
permission in Android settings and relaunch.

## 3. Start the server

The **SERVER** section at the top of the screen shows the current state:

```text
SERVER
● SMB Server Running
Stop                     Listening on <phone-ip>:4450 · Max SMB 3.0
```

- **Auto-start is ON by default**, so the server normally starts with the app.
- If you turned auto-start off, press **Start**. The state line changes to
  `SMB Server Running` and the address line shows the interface and port the server is bound to.
- Press **Stop** to shut the server down. Stopping the server does not uninstall the share or
  change your settings.

While the server is running you will see a permanent notification. That notification is owned by
the service, not by the screen — closing the app screen does **not** stop the server.

## 4. Check the address

The **INFO** section shows what clients should connect to:

| Field | Meaning |
|---|---|
| **Shared Storage** | the shared root, `/storage/emulated/0/` |
| **SMB Address** | the full URL, e.g. `smb://192.0.2.10:4450/Internal` |
| **Copy Address** | copies that address to the clipboard |
| **QR Code** | shows a QR code of the same address (handy for phones/tablets) |
| **Connections** | how many SMB sessions are open right now; the list can disconnect a session |

The address uses the phone's current LAN IP address. If the phone changes network, the address
changes with it.

## 5. Connect from a client

On the other device, connect to:

```text
smb://<phone-ip>:4450/Internal
```

and sign in with the configured **username and password**.

> **Change the default credentials before using the app on a shared network.**
> The defaults are `ansmb` / `ansmb`; see [Configuration](configuration.md).

Per-client instructions (macOS, Windows, file managers) are in
[Connecting from a client](connecting.md).

## 6. A quick sanity check

After connecting from a client, the app's **Connections** counter should show at least `1`, and
the product log (bottom of the screen) should record the session, for example:

```text
[AUTH] Session Setup: User 'ansmb' authenticated successfully
[SMB]  Tree Connect: User 'ansmb' connected to 'Internal'
```

If the client fails, see [Connecting](connecting.md) for client-specific causes and
[Limitations](limitations.md) for known gaps in 1.0.

## Next

- [Configuration](configuration.md) — port, credentials, auto-start, apply rules
- [Logging](logging.md) — what the log records and how to copy it
- [Limitations](limitations.md) — what 1.0 does not do
