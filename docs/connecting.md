# Connecting from a client

## The address

```text
smb://<phone-ip>:<port>/Internal
```

The app shows the exact string in the **INFO** section (**Copy Address**, **QR Code**). With the
defaults it looks like:

```text
smb://192.0.2.10:4450/Internal
        ^ phone's LAN IP   ^ port   ^ share name
```

Sign in with the configured username and password; the domain field can be left empty. The
defaults are `ansmb` / `ansmb`.

## Two things that explain most connection failures

**1. The port must be specified.** anSMBserver listens on **4450**, not 445 (binding 445 needs
root; see [Configuration](configuration.md)). Most clients assume 445 unless you tell them
otherwise.

**2. The client must be able to accept a port.** Some built-in clients only speak to port 445 and
offer no field for a different port. With those, the server on 4450 is simply not reachable —
this is a client limitation, not a server failure. Details per client below.

## Client notes

| Client | How to connect | Status |
|---|---|---|
| **macOS Finder** | *Go → Connect to Server…* (`⌘K`), enter `smb://<phone-ip>:4450/Internal`, then the username/password. Finder accepts the port inside the URL. | Supported (see the caveat in [Limitations](limitations.md) about reading file data) |
| **macOS `mount_smbfs` / `smbutil`** | `mount_smbfs //<user>:<password>@<phone-ip>:<port>/Internal <mountpoint>` | Supported (port must be in the URL) |
| **Windows Explorer (UNC)** | `\\<phone-ip>\Internal` — **there is no port field in a UNC path**, so this only works if something maps 445 to the server, which this app does not do. | **Not supported as-is** — measured: `net use \\<ip>@<port>\Internal` is rejected (`System error 53`) |
| **Clients with a "server" + "port" field** (many SMB-capable Android file managers and desktop clients) | enter the server address and port separately | Recommended path on Windows |
| **Generic SMB2 libraries** (e.g. an SMB client library that accepts an explicit port) | connect to `<phone-ip>` on the configured port, then authenticate and tree-connect to `Internal` | Used for the project's own verification |

The share name is **`Internal`**; the shared root is `/storage/emulated/0/`.

## Windows: the simplest way to connect

Windows Explorer has no port field (see the UNC row above), so the simplest route on Windows is
**Total Commander** with the **SMBClient4TotalCommander** file-system plugin (WFX):

```text
https://github.com/dkxce/SMBClient4TotalCommander
```

Its stated features are what make it suitable here: it can use a **non-standard SMB port** (not
445), and it supports **SMBLibrary** — the SMB stack anSMBserver is built on. It therefore connects
straight to `<phone-ip>:4450`: Windows' system SMB service on port 445 is not involved, and **no
port forwarding or address mapping is required**.

Steps:

1. **Install the plugin.** Download it from the project's *Releases* page — `SMBStorage.wfx64` for
   64-bit Total Commander, `SMBStorage.wfx` for the 32-bit build (the `SMBStorage.zip` archive
   contains both). In Total Commander open *Configuration → Options → Plugins → File system plugins
   (.WFX) → Configure → Add* and select the downloaded file; if Total Commander offers to copy it
   into its own plugin folder, accept.
2. **Fill in the connection.** Open the plugin from the drive list (it appears under *Network
   Neighborhood*) and enter: server = the phone's LAN IP shown in the app's **INFO** section, port =
   the SMB port shown there (**4450** by default), username and password = the configured
   credentials (`ansmb` / `ansmb` by default). The domain field can stay empty.
3. **Connect.** Open the **`Internal`** share — the shared root is `/storage/emulated/0/`.
   The connection is then established through the plugin using the SMB service provided by anSMBserver.

For the one operation that is not verified on the 1.0 artifact — reading file data back out of the
share — see [Limitations](limitations.md).

## What has been verified on 1.0

On a real device, with the release build:

- **SMB 3.0 negotiation**, **NTLM session setup**, **share list**, **tree connect** (`IPC$` and
  `Internal`) — verified.
- **Directory enumeration** on the share root and on subdirectories — verified (a 162-entry root
  listing was served).
- **Creating, writing and deleting files** — verified, including non-ASCII names and nested
  directories.
- **Disconnect** — the session close is recorded in the product log
  (`Tree Disconnect` → `The client closed the connection`).

Not verified on the release artifact: **reading file data back out of the share**. The client used
for verification asks for an information class that 1.0 does not implement and then aborts the
read. See [Limitations](limitations.md) for the precise statement.

## Multiple clients

Several clients can be connected at the same time. The **Connections** section shows the current
session count and lets you disconnect a session explicitly.

## Troubleshooting checklist

1. Are the phone and the client on the **same network** (no client isolation / guest-network
   isolation on the Wi-Fi)?
2. Is the server **running** (the SERVER section shows `SMB Server Running` and a bound address)?
3. Does the address you typed match **the one shown in the app**, including the **port**?
4. Did you grant the app **all files access**? Without it the share exists but cannot be served.
5. Are the **username/password** the ones currently configured? Password changes apply without a
   restart; port changes require a stop/start.
6. Check the app's product log: a rejected or failed session is recorded there (see
   [Logging](logging.md)). `Copy Logs` copies the visible log for support purposes.
