// The SMB server is decoupled from the Activity into a Foreground Service that
// OWNS the SMBLibrary server lifecycle.
//   Activity -> SmbService (Foreground, started+bound) -> SMBLibrary Server
//   -> AndroidFileSystemAdapter -> IAndroidStorageBackend -> PathStorageBackend
//
// Network state detection + current-IP display:
//   ConnectivityManager -> NetworkCallback -> SmbService -> Activity
// The Service holds network state; the server STAYS bound to 0.0.0.0.
//
// The default share root is the user-visible internal storage
// /storage/emulated/0 and the share name is "Internal". PathStorageBackend /
// AndroidFileSystemAdapter / IAndroidStorageBackend are unchanged by that
// root/configuration-layer choice.
using System;
using System.Linq;
using System.Net;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Runtime;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;

namespace anSMBserver
{
    // The component name's package prefix follows the ApplicationId
    // (com.github.mycccc.ansmbserver). The ".services.SmbService" structure, Exported,
    // ForegroundServiceType and the service logic are unchanged.
    [Service(Name = "com.github.mycccc.ansmbserver.services.SmbService",
        Exported = false,
        ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
    public class SmbService : Service
    {
        // The port is configurable at server start (in-memory, NOT persisted). Default
        // stays 4450. No privileged 445 binding, no conflict auto-re-select. A static
        // default is used so the value survives Activity recreation and is read by
        // StartServer() at the moment the server (re)starts (auto-start on
        // OnStartCommand too). Static is acceptable: single-process app, in-memory.
        public const int DefaultPort = 4450;
        // Default share name (1.0).
        public const string ShareName = "Internal";
        // Default credentials are a deliberate product default for a LAN
        // out-of-the-box tool. Changeable + restorable; never logged.
        public const string DefaultUser = "ansmb";
        public const string DefaultPassword = "ansmb";
        public const string ChannelId = "smb_server";
        private const int NotificationId = 1;

        // Port persistence (NOT server run-state).
        // Credential persistence (app-private SharedPreferences).
        public const string PrefName = "smb_config";
        public const string KeyConfiguredPort = "configured_port";
        public const string KeyConfiguredUsername = "configured_username";
        public const string KeyConfiguredPassword = "configured_password";

        private static volatile int _configuredPort = DefaultPort;

        /// <summary>Load the persisted configured port (default 4450). App-private SharedPreferences; no SAF/permission.</summary>
        private static int LoadPersistedPort()
        {
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(PrefName, FileCreationMode.Private);
                return prefs.GetInt(KeyConfiguredPort, DefaultPort);
            }
            catch { return DefaultPort; }
        }

        /// <summary>Persist the configured port.</summary>
        private static void SavePersistedPort(int port)
        {
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(PrefName, FileCreationMode.Private);
                prefs.Edit().PutInt(KeyConfiguredPort, port).Commit();
            }
            catch { }
        }

        /// <summary>Current (in-process) configured port; the authoritative persisted value is loaded on Service create.</summary>
        public static int ConfiguredPort
        {
            get { return _configuredPort; }
            set
            {
                if (value > 0 && value <= 65535)
                {
                    _configuredPort = value;
                    SavePersistedPort(value);
                }
            }
        }

        /// <summary>Configured port (convenience for the Activity/self-probe/notification).</summary>
        public int CurrentPort { get { return _configuredPort; } }

        /// <summary>Persisted configured port (default 4450) — for UI init before the Service binds.</summary>
        public static int PersistedPort { get { return LoadPersistedPort(); } }

        /// <summary>Current default share root (user-visible internal storage).</summary>
        public static string ShareRoot { get { return ExternalStorageRoot(); } }

        // ---- credentials ----
        // App-private SharedPreferences (minimal scheme; no Keystore). The password is
        // never logged, and the app opts out of Android backup via
        // [assembly: Application(AllowBackup = false)].

        private static volatile string _configuredUsername = DefaultUser;
        private static volatile string _configuredPassword = DefaultPassword;

        /// <summary>A credential must be non-empty (trimmed). An empty password is rejected so Guest can never be enabled.</summary>
        private static bool IsValidCredential(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Trim().Length > 0;
        }

        private static string LoadPersistedUsername()
        {
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(PrefName, FileCreationMode.Private);
                return prefs.GetString(KeyConfiguredUsername, DefaultUser);
            }
            catch { return DefaultUser; }
        }

        private static string LoadPersistedPassword()
        {
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(PrefName, FileCreationMode.Private);
                return prefs.GetString(KeyConfiguredPassword, DefaultPassword);
            }
            catch { return DefaultPassword; }
        }

        private static void SavePersistedCredentials(string username, string password)
        {
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(PrefName, FileCreationMode.Private);
                prefs.Edit()
                    .PutString(KeyConfiguredUsername, username)
                    .PutString(KeyConfiguredPassword, password)
                    .Commit();
            }
            catch { }
        }

        /// <summary>Configured username (in-process; authoritative persisted value loaded on Service create).</summary>
        public static string ConfiguredUsername { get { return _configuredUsername; } }

        /// <summary>Configured password (NEVER log this).</summary>
        public static string ConfiguredPassword { get { return _configuredPassword; } }

        /// <summary>Set both credentials atomically. Returns false (no change) if either is empty.</summary>
        public static bool TrySetCredentials(string username, string password)
        {
            if (!IsValidCredential(username) || !IsValidCredential(password))
            {
                return false;
            }
            _configuredUsername = username.Trim();
            _configuredPassword = password;
            SavePersistedCredentials(_configuredUsername, _configuredPassword);
            return true;
        }

        /// <summary>Restore Defaults — credentials ONLY (does NOT touch port/share).</summary>
        public static void RestoreAccountDefaults()
        {
            TrySetCredentials(DefaultUser, DefaultPassword);
        }

        /// <summary>
        /// NTLM auth delegate (invoked per SESSION_SETUP). Returns the configured
        /// password ONLY for the configured username (case-insensitive); returns null
        /// for any other account (-> STATUS_LOGON_FAILURE). Must return null — NOT
        /// string.Empty — for unknown users, otherwise Guest login would be enabled.
        /// Reading the live static fields makes a credential change apply to new
        /// SESSION_SETUPs without restarting the server (existing sessions are not
        /// invalidated).
        /// </summary>
        private static string GetConfiguredPassword(string userName)
        {
            string configuredUser = _configuredUsername;
            if (!string.IsNullOrEmpty(configuredUser) &&
                string.Equals(userName, configuredUser, StringComparison.OrdinalIgnoreCase))
            {
                return _configuredPassword;
            }
            return null;
        }

        private readonly object _lock = new object();
        private PortableSmbServer _server;
        private bool _started;

        // ---- network state (owned by Service, not Activity) ----
        private ConnectivityManager _cm;
        private NetworkMonitor _netCallback;
        private volatile string _lanIpv4;
        private volatile bool _networkAvailable;

        private readonly SmbBinder _binder = new SmbBinder();

        public class SmbBinder : Binder
        {
            public SmbService Service { get; set; }
        }

        public override IBinder OnBind(Intent intent)
        {
            return _binder;
        }

        public bool IsRunning
        {
            get { lock (_lock) { return _started; } }
        }

        /// <summary>Current LAN IPv4 observed by the network callback; null if none / network down.</summary>
        public string LanIpv4
        {
            get { return _lanIpv4; }
        }

        /// <summary>True if a network is currently available (per the callback).</summary>
        public bool NetworkAvailable
        {
            get { return _networkAvailable; }
        }

        // ---- lifecycle ----

        public override void OnCreate()
        {
            base.OnCreate();
            _binder.Service = this;
            // Restore the persisted configured port (default 4450) so a process restart
            // keeps the user's port. Run-state is NOT persisted (no auto-start).
            _configuredPort = LoadPersistedPort();
            // Restore persisted credentials so a process restart keeps them.
            _configuredUsername = LoadPersistedUsername();
            _configuredPassword = LoadPersistedPassword();
            CreateNotificationChannel();
            StartNetworkMonitor();
        }

        // ---- Server Run Intent ------------------------------------------------
        // CONTRACT:
        //   * A start command means "the server must run": start it and show the
        //     foreground notification.
        //   * START_STICKY may only recover a service that is still in the *started*
        //     state, i.e. one the user asked to run, so a null-intent restart always
        //     means "recover a requested RUN". It can never resurrect a user Stop
        //     *because* the Stop path below removes the started state (StopSelf()).
        //   * INVARIANT: an explicit Stop MUST remove the started state. Do not remove
        //     the StopSelf() call - the null-intent reasoning above depends on it.
        //   * The notification is owned by UpdateNotification() and follows the REAL
        //     running state; nothing else may post it (network callbacks only update it).
        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            // Idempotent: if the server is not already running, start it; promote to
            // foreground with the notification.
            if (!_started)
            {
                StartServer();
            }
            UpdateNotification();
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            StopNetworkMonitor();
            lock (_lock)
            {
                try
                {
                    _server?.Stop();
                }
                catch { }
                _server = null;
                _started = false;
            }
            // A destroyed service must never leave a stale "server running" notification.
            UpdateNotification();
            base.OnDestroy();
        }

        // ---- bound API for the Activity ----

        public void Start()
        {
            lock (_lock)
            {
                if (_started) return;
                StartServer();
            }
            UpdateNotification();
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_started)
                {
                    try
                    {
                        _server?.Stop();
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Error("SmbService", "Stop server error: " + ex);
                    }
                    _server = null;
                    _started = false;
                }
            }
            UpdateNotification();
            // Clear the platform started-state: without this a user Stop remains
            // restartable by START_STICKY.
            try { StopSelf(); } catch { }
        }

        // ---- Connections ----
        // Read-only snapshot of authenticated SMB sessions via SMBLibrary's public
        // SMBServer.GetSessionsInformation() (no SMBLibrary change).
        //   * the count is the AUTHENTICATED SESSION count (not a device count).
        //   * connected time only (no session history / disconnect timestamp).
        //   * Disconnect is endpoint-based — it terminates the TCP connection,
        //     it is NOT a per-session logoff.
        //   * minimal display data only (IP:port, username, dialect, connected time).

        public class ConnectionInfo
        {
            public string Ip;
            public int Port;
            public string EndPoint;    // "ip:port"
            public string UserName;
            public string Dialect;     // display string
            public System.DateTime ConnectedUtc;
        }

        /// <summary>Authenticated-session snapshot for the UI (empty when the server is not running).</summary>
        public System.Collections.Generic.List<ConnectionInfo> GetConnections()
        {
            var result = new System.Collections.Generic.List<ConnectionInfo>();
            PortableSmbServer server;
            lock (_lock) { server = _server; }
            if (server == null) return result;
            try
            {
                foreach (SessionInformation s in server.GetSessionsInformation())
                {
                    result.Add(new ConnectionInfo
                    {
                        Ip = s.ClientEndPoint != null ? s.ClientEndPoint.Address.ToString() : "?",
                        Port = s.ClientEndPoint != null ? s.ClientEndPoint.Port : 0,
                        EndPoint = s.ClientEndPoint != null
                            ? (s.ClientEndPoint.Address + ":" + s.ClientEndPoint.Port) : "?",
                        UserName = s.UserName,
                        Dialect = DialectDisplay(s.Dialect),
                        ConnectedUtc = s.CreationDT
                    });
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("SmbService", "GetConnections error: " + ex);
            }
            return result;
        }

        /// <summary>
        /// Terminate the TCP connection for the given endpoint. Endpoint-based:
        /// this drops the connection (and all its sessions), not a single session.
        /// </summary>
        public bool Disconnect(ConnectionInfo connection)
        {
            if (connection == null || string.IsNullOrEmpty(connection.Ip)) return false;
            PortableSmbServer server;
            lock (_lock) { server = _server; }
            if (server == null) return false;
            try
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(connection.Ip), connection.Port);
                server.TerminateConnection(endpoint);
                Android.Util.Log.Info("SmbService", "Connection terminated: " + connection.EndPoint);
                return true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("SmbService", "Disconnect error: " + ex);
                return false;
            }
        }

        private static string DialectDisplay(SMBDialect dialect)
        {
            switch (dialect)
            {
                case SMBDialect.NTLM012: return "SMB 1.0";
                case SMBDialect.SMB202: return "SMB 2.0.2";
                case SMBDialect.SMB210: return "SMB 2.1";
                case SMBDialect.SMB300: return "SMB 3.0";
                default: return dialect.ToString();
            }
        }

        // ---- product event log ----
        // Product-level event log. SMBLibrary's LogEntryAdded carries EVERY severity,
        // including Verbose per-packet lines ("SMB1/SMB2 message received ..."), which
        // must NOT reach the product log (no packet sniffer).
        //
        // Classification is by EVENT CLASS x OUTCOME rather than by a prefix allowlist: a
        // failure must be kept even when its wording varies, and successes must not arrive
        // in bulk and evict the session-level lines a user actually needs.
        //
        // Tiers:
        //   T1 Session -> logcat + UI  (connect / negotiate / auth / session / tree /
        //                               disconnect / protocol violations / server start-stop)
        //   T2 Failure -> logcat + UI  (any failing file / directory / metadata operation)
        //   T3 Detail  -> logcat ONLY  (successful file/dir/metadata operations and
        //                               capability-probe fallbacks)
        //
        // Credentials are never logged (SMBLibrary supplies user names only; nothing here
        // adds a password). Packet traces stay out.

        /// <summary>Where a mapped product line is allowed to appear.</summary>
        private enum LogTier { Session, Failure, Detail }

        /// <summary>One classified library event: the rendered line plus its tier.</summary>
        private sealed class MappedLog
        {
            public string Line;
            public LogTier Tier;
        }

        /// <summary>Raised for each mapped T1 / T2 product log line (already written to logcat).</summary>
        public static event System.Action<string> ProductLog;

        /// <summary>T1 / T2: written to logcat AND surfaced in the UI log.</summary>
        internal static void EmitProductLog(string line)
        {
            try { Android.Util.Log.Info("SmbSrv", line); } catch { }
            try { ProductLog?.Invoke(line); } catch { }
        }

        /// <summary>
        /// T3: written to logcat ONLY. This single channel is what keeps successful
        /// file/directory/metadata traffic out of the UI log, so no per-session counters,
        /// timers or new synchronisation are required.
        /// </summary>
        internal static void EmitProductLogDetail(string line)
        {
            try { Android.Util.Log.Info("SmbSrv", line); } catch { }
        }

        /// <summary>
        /// Map an SMBLibrary log entry to a product line + tier, or null to drop it.
        /// The library severity is carried into the judgement.
        /// </summary>
        private static MappedLog MapSmbLogEvent(Utilities.LogEntry entry)
        {
            string message = entry.Message ?? string.Empty;
            // Connection-scoped entries carry a "[ip:port] " prefix (ConnectionState.LogToServer).
            // Only the copy used for classification is stripped; the emitted line keeps the
            // prefix so the originating client remains identifiable.
            string body = message;
            if (body.Length > 0 && body[0] == '[')
            {
                int close = body.IndexOf("] ");
                if (close > 0) body = body.Substring(close + 2);
            }

            LogTier tier;
            string category = ClassifyProductEvent(body, entry.Severity, out tier);
            if (category == null) return null;

            return new MappedLog
            {
                Line = entry.Time.ToString("HH:mm:ss") + " " + category + " " + message,
                Tier = tier
            };
        }

        /// <summary>
        /// Event-class x outcome classification.
        /// Returns the product category and sets the tier, or returns null to drop the event.
        /// </summary>
        private static string ClassifyProductEvent(string body, Utilities.Severity severity, out LogTier tier)
        {
            // ---- T1: session-level events (kept in detail, always) -------------------
            // APP
            if (body.StartsWith("Starting server")) { tier = LogTier.Session; return "[APP]"; }
            if (body.StartsWith("Stopping server")) { tier = LogTier.Session; return "[APP]"; }
            // NETWORK (the "... was terminated, Socket error code: {0}" variant shares this
            // prefix and is therefore covered as well)
            if (body.StartsWith("New connection request accepted")) { tier = LogTier.Session; return "[NETWORK]"; }
            if (body.StartsWith("New connection request rejected")) { tier = LogTier.Session; return "[NETWORK]"; }
            if (body.StartsWith("The client closed the connection")) { tier = LogTier.Session; return "[NETWORK]"; }
            if (body.StartsWith("The connection was terminated")) { tier = LogTier.Session; return "[NETWORK]"; }
            if (body.StartsWith("The connection was forcibly closed")) { tier = LogTier.Session; return "[NETWORK]"; }
            if (body.StartsWith("Failed to send packet")) { tier = LogTier.Session; return "[NETWORK]"; }
            // SMB negotiation and protocol violations
            if (body.StartsWith("Negotiate failure")) { tier = LogTier.Session; return "[SMB]"; }
            if (body.StartsWith("Invalid SMB message")) { tier = LogTier.Session; return "[SMB]"; }
            if (body.StartsWith("Invalid SMB1 message")) { tier = LogTier.Session; return "[SMB]"; }
            if (body.StartsWith("Invalid SMB2 request chain")) { tier = LogTier.Session; return "[SMB]"; }
            if (body.StartsWith("Inappropriate NetBIOS session packet")) { tier = LogTier.Session; return "[SMB]"; }
            if (body.StartsWith("Rejected Invalid NetBIOS")) { tier = LogTier.Session; return "[SMB]"; }
            // AUTH
            if (body.StartsWith("Session Setup:")) { tier = LogTier.Session; return "[AUTH]"; }
            if (body.StartsWith("Logoff:")) { tier = LogTier.Session; return "[AUTH]"; }
            // SMB tree path: a Tree Connect *failure* is a session-level diagnostic and sits
            // next to its success sibling, which is what makes "authenticated but cannot
            // reach the share" diagnosable.
            if (body.StartsWith("Tree Connect:")) { tier = LogTier.Session; return "[SMB]"; }
            if (body.StartsWith("Tree Connect to")) { tier = LogTier.Session; return "[SMB]"; }
            if (body.StartsWith("Tree Disconnect:")) { tier = LogTier.Session; return "[SMB]"; }

            // ---- filesystem / metadata: the OUTCOME decides the tier -------------------
            if (body.StartsWith("Create: Opened")) { tier = LogTier.Detail; return "[FILESYSTEM]"; }
            if (body.StartsWith("Create: Opening")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Read failed")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Read from")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Write failed")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Write to")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Flush failed")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Flush '")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Close: Closed")) { tier = LogTier.Detail; return "[FILESYSTEM]"; }
            if (body.StartsWith("Close: Closing")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Close failed")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Query Directory")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("GetFileInformation")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("SetFileInformation")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("GetFileSystemInformation")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("SetFileSystemInformation")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("GetSecurityInformation")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("SetSecurityInformation")) return FileSystemOutcome(body, out tier);
            // SMB1 file paths plus the remaining SMB1/SMB2 families.
            if (body.StartsWith("FindFirst2")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Create Directory")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Delete")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Check Directory")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Set Information")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Lock")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("IOCTL")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("Cancel:")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("NotifyChange")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("TransactNamedPipe")) return FileSystemOutcome(body, out tier);
            if (body.StartsWith("TransactWaitNamedPipe")) return FileSystemOutcome(body, out tier);

            // ---- severity safety net -------------------------------------------------
            // An event the library itself flags as Warning or Error is a real event: keep it
            // rather than dropping it just because its wording is not in the known set.
            // Packet traces are Verbose/Trace and therefore still excluded.
            if (severity == Utilities.Severity.Critical
                || severity == Utilities.Severity.Error
                || severity == Utilities.Severity.Warning)
            {
                tier = LogTier.Failure;
                return "[SMB]";
            }

            // Everything else - incl. packet-level "SMB1/SMB2 message received" noise and
            // successful Read/Write (the library does not log those at all) - is dropped.
            tier = LogTier.Detail;
            return null;
        }

        /// <summary>
        /// A filesystem / metadata line counts as a FAILURE only when the library reports it
        /// as failed. Capability-probe fallbacks are demoted to detail instead, because they
        /// are the client negotiating an information class or level - not an error.
        /// </summary>
        private static string FileSystemOutcome(string body, out LogTier tier)
        {
            tier = (IsFailure(body) && !IsCapabilityFallback(body)) ? LogTier.Failure : LogTier.Detail;
            return "[FILESYSTEM]";
        }

        /// <summary>True when the library reported the operation as failed.</summary>
        private static bool IsFailure(string body)
        {
            return body.Contains("failed");
        }

        /// <summary>
        /// Capability-probe fallbacks. The client is probing which
        /// information class or level the server supports and the "failure" is the expected
        /// answer, so it must never be presented as an error.
        /// </summary>
        private static bool IsCapabilityFallback(string body)
        {
            return body.Contains("STATUS_NOT_SUPPORTED")
                || body.Contains("STATUS_INVALID_INFO_CLASS")
                || body.Contains("STATUS_OS2_INVALID_LEVEL")
                || body.Contains("Not a pass-through information level");
        }

        // The negotiated dialect is not an SMBLibrary log line. Derive it host-side from
        // the session snapshot and emit one product line per newly observed session.
        private System.Threading.Thread _sessionObserver;
        private readonly System.Collections.Generic.HashSet<string> _observedSessions =
            new System.Collections.Generic.HashSet<string>();

        private void StartSessionObserver()
        {
            _observedSessions.Clear();
            _sessionObserver = new System.Threading.Thread(() =>
            {
                while (IsRunning)
                {
                    try { ObserveSessionsOnce(); } catch { }
                    System.Threading.Thread.Sleep(2000);
                }
            });
            _sessionObserver.IsBackground = true;
            _sessionObserver.Start();
        }

        private void ObserveSessionsOnce()
        {
            var current = GetConnections();
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (ConnectionInfo c in current)
            {
                seen.Add(c.EndPoint);
                if (!_observedSessions.Contains(c.EndPoint))
                {
                    EmitProductLog(System.DateTime.Now.ToString("HH:mm:ss")
                        + " [SMB] dialect negotiated " + c.EndPoint
                        + " user=" + c.UserName + " dialect=" + c.Dialect);
                }
            }
            _observedSessions.Clear();
            foreach (string ep in seen) _observedSessions.Add(ep);
        }

        // ---- server (stays bound to 0.0.0.0; NOT tied to network policy) ----

        private void StartServer()
        {
            // Declared outside the lock so the "listening on ..." product line can
            // be emitted AFTER the lock is released, using the port that was actually bound
            // instead of re-reading a configured port that may have moved meanwhile.
            int bindPort;

            lock (_lock)
            {
                if (_server != null) return;
                // Default share root = user-visible internal storage (/storage/emulated/0).
                // Requires MANAGE_EXTERNAL_STORAGE (API 30+), requested by the Activity.
                string shareRoot = ExternalStorageRoot();
                System.IO.Directory.CreateDirectory(shareRoot);

                IAndroidStorageBackend storage = new PathStorageBackend(shareRoot);
                var shares = new SMBShareCollection();
                shares.Add(new FileSystemShare(ShareName, new AndroidFileSystemAdapter(storage)));

                GSSProvider provider = new GSSProvider(
                    new IndependentNTLMAuthenticationProvider(GetConfiguredPassword, 5, TimeSpan.FromMinutes(5)));

                var server = new PortableSmbServer(shares, provider);
                server.LogEntryAdded += (s, e) =>
                {
                    MappedLog mapped = MapSmbLogEvent(e);
                    if (mapped == null) return;
                    // T3 detail is logcat-only and never reaches the UI log.
                    if (mapped.Tier == LogTier.Detail) EmitProductLogDetail(mapped.Line);
                    else EmitProductLog(mapped.Line);
                };
                System.TimeSpan? inactivity = System.TimeSpan.FromMinutes(5);
                // Bind the CONFIGURED port (default 4450). No conflict auto-re-select:
                // a bind failure throws and the server does not silently fall back.
                // (bindPort is declared above the lock - see the note there.)
                bindPort = _configuredPort;
                server.StartOnPort(IPAddress.Any, SMBTransportType.DirectTCPTransport, bindPort,
                    enableSMB1: true, enableSMB2: true, enableSMB3: true, connectionInactivityTimeout: inactivity);
                _server = server;
                _started = true;
                Android.Util.Log.Info("SmbService", "SMB server listening on 0.0.0.0:" + bindPort);
            }

            // "Is it listening, and where" is a session-level fact, so it belongs in the UI
            // log too. Only a reachable LAN IPv4 is shown: 0.0.0.0 is never shown, and no
            // address is invented when there is none - the address is simply omitted.
            // Emitted after the lock, so the log path is never taken while _lock is held.
            string listeningIp = ShareAddress.Resolve(_lanIpv4);
            EmitProductLog(System.DateTime.Now.ToString("HH:mm:ss")
                + " [APP] SMB server listening on "
                + (string.IsNullOrEmpty(listeningIp) ? "port " + bindPort : listeningIp + ":" + bindPort));

            StartSessionObserver();
        }

        // ---- network monitor: ConnectivityManager -> NetworkCallback -> this ----

        private void StartNetworkMonitor()
        {
            try
            {
                _cm = (ConnectivityManager)GetSystemService(ConnectivityService);
                if (_cm == null) return;
                _netCallback = new NetworkMonitor(this);
                var request = new NetworkRequest.Builder()
                    .AddTransportType(TransportType.Wifi)
                    .AddTransportType(TransportType.Ethernet)
                    .Build();
                _cm.RegisterNetworkCallback(request, _netCallback);
                Android.Util.Log.Info("SmbService", "Network monitor started");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("SmbService", "StartNetworkMonitor error: " + ex);
            }
        }

        private void StopNetworkMonitor()
        {
            try
            {
                if (_cm != null && _netCallback != null)
                {
                    _cm.UnregisterNetworkCallback(_netCallback);
                }
            }
            catch { }
            _netCallback = null;
            _cm = null;
        }

        private class NetworkMonitor : ConnectivityManager.NetworkCallback
        {
            private readonly SmbService _svc;
            public NetworkMonitor(SmbService svc) { _svc = svc; }
            public override void OnAvailable(Network network)
            {
                base.OnAvailable(network);
                _svc.RefreshNetworkState(network);
            }
            public override void OnLost(Network network)
            {
                base.OnLost(network);
                _svc.RefreshNetworkState(null); // rescan: another net may still be up
            }
            public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities)
            {
                base.OnCapabilitiesChanged(network, capabilities);
                _svc.RefreshNetworkState(network);
            }
        }

        private void RefreshNetworkState(Network network)
        {
            try
            {
                string ip = null;
                bool available = false;
                if (_cm != null && network != null)
                {
                    LinkProperties props = _cm.GetLinkProperties(network);
                    if (props != null && props.LinkAddresses != null)
                    {
                        foreach (LinkAddress la in props.LinkAddresses)
                        {
                            if (la.Address is Java.Net.Inet4Address)
                            {
                                ip = la.Address.HostAddress;
                                available = true;
                                break;
                            }
                        }
                    }
                }
                // Fallback: if this callback path didn't yield an IPv4 (e.g. network was
                // null on loss, or the specific net has no IPv4), do a lightweight scan
                // so the displayed IP reflects whatever is reachable now.
                if (string.IsNullOrEmpty(ip))
                {
                    ip = ScanLanIpv4();
                    available = !string.IsNullOrEmpty(ip);
                }
                _lanIpv4 = ip;
                _networkAvailable = available;
                Android.Util.Log.Info("SmbService", "Network state: available=" + available + " ip=" + (ip ?? "none"));
                // A network change may only UPDATE the notification - it must never
                // imply "the server is running" (UpdateNotification is state-aware).
                UpdateNotification();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("SmbService", "RefreshNetworkState error: " + ex);
            }
        }

        // ---- notification ----

        private void CreateNotificationChannel()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
            var nm = (NotificationManager)GetSystemService(NotificationService);
            nm.CreateNotificationChannel(new NotificationChannel(ChannelId, "SMB Server", NotificationImportance.Low));
        }

        private Notification BuildNotification()
        {
            var builder = new Notification.Builder(this, ChannelId);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                builder.SetSmallIcon(Android.Resource.Drawable.IcMenuInfoDetails);
            else
#pragma warning disable CS0618
                builder.SetSmallIcon(Android.Resource.Drawable.IcMenuInfoDetails);
#pragma warning restore CS0618
            builder.SetContentTitle("SMB Server");
            // The notification address uses the SAME source as the UI (the reachable LAN
            // IPv4), so the notification can never disagree with the SERVER block.
            builder.SetContentText("SMB server running on "
                + (ShareAddress.Resolve(_lanIpv4) ?? "no-network") + ":" + _configuredPort);
            builder.SetOngoing(true);
            builder.SetContentIntent(PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)),
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
            return builder.Build();
        }

        /// <summary>
        /// Single owner of the product notification. It follows the REAL
        /// running state: a running server is promoted to foreground with the current
        /// address, a stopped server has its notification cancelled. Callers may only ask
        /// it to refresh - none of them may imply "the server is running".
        /// </summary>
        private void UpdateNotification()
        {
            try
            {
                if (_started)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(29))
                    {
                        StartForeground(NotificationId, BuildNotification(),
                            Android.Content.PM.ForegroundService.TypeDataSync);
                    }
                    else
                    {
#pragma warning disable CS0618
                        StartForeground(NotificationId, BuildNotification());
#pragma warning restore CS0618
                    }
                }
                else
                {
                    StopForeground(true);
                    var nm = (NotificationManager)GetSystemService(NotificationService);
                    nm?.Cancel(NotificationId);
                }
            }
            catch { }
        }

        private static string ScanLanIpv4()
        {
            foreach (System.Net.NetworkInformation.NetworkInterface ni in
                System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ua.Address.ToString();
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The default share root — user-visible internal storage.
        /// Falls back to the conventional path if the platform value is unavailable.
        /// </summary>
        private static string ExternalStorageRoot()
        {
#pragma warning disable CS0618
            string root = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
#pragma warning restore CS0618
            return string.IsNullOrEmpty(root) ? "/storage/emulated/0" : root;
        }
    }
}
