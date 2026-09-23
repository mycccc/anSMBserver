// The SMB server is OWNED by the foreground SmbService, NOT by this Activity.
// The Activity binds to SmbService to start/stop and reflect state; it never
// directly owns/starts/stops the SMBLibrary server. The server therefore
// survives UI exit / backgrounding.
//
// The UI is the product main screen - HEADER -> SERVER -> SERVER SETTINGS ->
// INFO -> DIAGNOSTICS. Programmatic UI only (no XML, no Material/AppCompat).
// The Self-Probe entry is intentionally not part of the product UI;
// SelfProbe.cs is retained.
using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using SMBLibrary;
using SMBLibrary.Server;

// Network socket (server + probe) and network-state/interface reading require
// these permissions on Android.
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessWifiState)]
// Foreground-service + notification permissions (device API 33 / targetSdk 36).
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundServiceDataSync)]
[assembly: UsesPermission(Android.Manifest.Permission.PostNotifications)]
// Broad shared-storage access for the default share root /storage/emulated/0.
// API 30+ uses the MANAGE_EXTERNAL_STORAGE ("All files access") settings grant;
// the legacy read/write declarations cover devices below API 30 (minSdk 23).
[assembly: UsesPermission(Android.Manifest.Permission.ManageExternalStorage)]
[assembly: UsesPermission(Android.Manifest.Permission.ReadExternalStorage)]
[assembly: UsesPermission(Android.Manifest.Permission.WriteExternalStorage)]
// Credentials must NOT enter Android backup (cloud or D2D).
// The app's only persistent data is the smb_config prefs, so opting out of backup
// entirely is the minimal-impact scheme (no prefs split, no backup-rule XML).
// The launcher icon is a pure-XML adaptive icon
// (Resources/mipmap-anydpi-v26/ic_launcher.xml) with a legacy vector fallback for API 23-25
// (Resources/mipmap/ic_launcher.xml). Owner-supplied artwork; no third-party assets.
[assembly: Application(AllowBackup = false, Icon = "@mipmap/ic_launcher")]

namespace anSMBserver
{
    // NoActionBar — the product identity row is drawn by the app itself
    // (title + version + repo link). An ActionBar label would duplicate the
    // in-page title.
    [Activity(Label = "anSMBserver", MainLauncher = true,
        Theme = "@android:style/Theme.Material.Light.NoActionBar")]
    public class MainActivity : Activity
    {
        private const string ChannelId = SmbService.ChannelId;
        private const int NotificationId = 1;
        private const int StorageRequestCode = 1002;

        // Product identity row target.
        private const string RepoUrl = "https://github.com/mycccc/anSMBserver";

        // ---- visual tokens ----------------------------------------------------
        // Defined once, Light theme only (dark mode is not implemented).
        private static readonly Android.Graphics.Color CAccent =
            new Android.Graphics.Color(unchecked((int)0xFF1A73E8));
        private static readonly Android.Graphics.Color CSuccess =
            new Android.Graphics.Color(unchecked((int)0xFF1E8E3E));
        private static readonly Android.Graphics.Color CWarning =
            new Android.Graphics.Color(unchecked((int)0xFFE37400));
        private static readonly Android.Graphics.Color CDanger =
            new Android.Graphics.Color(unchecked((int)0xFFD93025));
        private static readonly Android.Graphics.Color CTextPrimary =
            new Android.Graphics.Color(unchecked((int)0xFF1F1F1F));
        private static readonly Android.Graphics.Color CTextSecondary =
            new Android.Graphics.Color(unchecked((int)0xFF5F6368));
        private static readonly Android.Graphics.Color CTextTertiary =
            new Android.Graphics.Color(unchecked((int)0xFF80868B));
        private static readonly Android.Graphics.Color CDivider =
            new Android.Graphics.Color(unchecked((int)0xFFE8EAED));
        // Log window fill — one step off the page background so the box reads as a
        // distinct surface without looking like a card.
        private static readonly Android.Graphics.Color CLogFill =
            new Android.Graphics.Color(unchecked((int)0xFFF7F8FA));

        // The log container is capped in dp, never in raw pixels (a fixed pixel
        // container would leave a large empty gap on some densities).
        private const int LogMaxHeightDp = 160;
        // The log window keeps a usable minimum height, so it always reads as a
        // fixed viewport (an independent log viewer) instead of a gap that grows
        // on demand.
        private const int LogMinHeightDp = 64;
        // Slack when deciding whether the log is scrolled to the bottom, so a
        // near-bottom position still counts as "following".
        private const int LogBottomToleranceDp = 4;

        // ---- fixed button captions -------------------------------------------
        // Unicode-prefixed captions, defined once so the initial text and every
        // runtime text update can never drift apart.
        private const string LabelAbout = "ⓘ About";
        private const string LabelStart = "▶ Start";
        private const string LabelStop = "■ Stop";
        private const string LabelApplySettings = "✓ Apply Settings";
        private const string LabelRestoreDefaults = "↺ Restore Defaults";
        private const string LabelCopyAddress = "▣ Copy Address";
        private const string LabelQrCode = "▦ QR Code";
        private const string LabelClearLogs = "⊠ Clear Logs";
        private const string LabelCopyLogs = "▣ Copy Logs";
        private const string LabelDisconnect = "↮ Disconnect";

        // Product rule (1.0): anSMBserver runs as an ordinary, non-root Android app,
        // so the listenable range is the unprivileged port range — 1024-65535 —
        // not 1-65535. Single source of truth for validation, the inline error text
        // and the "not yet applied" hint.
        private const int MinPort = 1024;
        private const int MaxPort = 65535;

        // The three section dividers (the header separator is separate). Kept
        // small: the section title carries its own top padding, so a large margin
        // here would double the same gap.
        private const int SectionDividerTopDp = 10;
        private const int SectionDividerBottomDp = 4;

        // Process-scoped Server Run Intent coordination.
        //   s_firstResume : the Auto-start policy governs the FIRST resume of this app
        //                   process (a real cold launch) only.
        //   s_runIntent   : what the user last asked for; governs every later resume, so
        //                   returning to the app can never override an explicit Stop.
        // Deliberately NOT persisted (no new preference key): after a process death the
        // next launch is a cold launch again and Auto-start decides.
        private static volatile bool s_firstResume = true;
        private static volatile bool s_runIntent;

        // Auto-start configuration is kept in the Activity layer. SmbService carries
        // only the Server Run Intent / notification ownership.
        private const string KeyAutoStart = "configured_autostart";
        private static volatile bool s_autoStartCommitted = true;

        private TextView m_log;
        private ScrollView m_logScroll;
        private TextView m_startStop;
        private TextView m_status;
        private TextView m_statusLine2;
        private Android.Widget.EditText m_portInput;
        private Android.Widget.EditText m_userInput;
        private Android.Widget.EditText m_passInput;
        private TextView m_userError;
        private TextView m_passError;
        private TextView m_portError;
        private TextView m_portHint;
        private Android.Widget.Switch m_autoStartSwitch;
        private TextView m_autoStartError;
        private TextView m_connCount;
        private LinearLayout m_connList;
        private TextView m_connEmpty;
        private string m_connSignature;
        private TextView m_shareUrl;
        private TextView m_copyUrl;
        private TextView m_qrToggle;
        private ImageView m_qrImage;
        private TextView m_shareUrlLabel;
        private string m_shareUrlSig;
        private bool m_qrExpanded;
        private bool m_passwordHidden;

        private SmbService m_service;          // bound service (server owner)
        private bool m_bound;

        // bound-service connection
        private readonly SmbServiceConnection _connection = new SmbServiceConnection();

        private sealed class SmbServiceConnection : Java.Lang.Object, IServiceConnection
        {
            public MainActivity Host;
            public void OnServiceConnected(ComponentName name, IBinder service)
            {
                if (Host != null)
                {
                    Host.OnServiceConnected(((SmbService.SmbBinder)service).Service);
                }
            }
            public void OnServiceDisconnected(ComponentName name)
            {
                if (Host != null)
                {
                    Host.OnServiceDisconnected();
                }
            }
        }

        // The page scroller. A plain platform ScrollView parent intercepts every
        // vertical drag before a nested ScrollView can start scrolling, so the log
        // box could never be dragged by finger. This subclass stands aside for a
        // gesture that STARTS inside the exempt view, as long as that view has
        // something to scroll: the log box then scrolls itself and, on reaching its
        // own edge, hands the leftover movement to the page through the platform
        // nested-scrolling chain. Touches anywhere else scroll the page as before.
        // Platform classes only — no AndroidX / Material dependency.
        private sealed class GestureAwareScrollView : ScrollView
        {
            public View ExemptView;
            private readonly int[] mSelfLoc = new int[2];
            private readonly int[] mExemptLoc = new int[2];

            public GestureAwareScrollView(Context context) : base(context)
            {
            }

            public override bool OnInterceptTouchEvent(MotionEvent e)
            {
                try
                {
                    var action = e.ActionMasked;
                    if (ExemptView != null
                        && (action == MotionEventActions.Down || action == MotionEventActions.Move))
                    {
                        bool scrollable = ExemptView.CanScrollVertically(-1)
                            || ExemptView.CanScrollVertically(1);
                        if (scrollable)
                        {
                            GetLocationOnScreen(mSelfLoc);
                            ExemptView.GetLocationOnScreen(mExemptLoc);
                            int x = mSelfLoc[0] + (int)e.GetX();
                            int y = mSelfLoc[1] + (int)e.GetY();
                            var rect = new Android.Graphics.Rect(
                                mExemptLoc[0], mExemptLoc[1],
                                mExemptLoc[0] + ExemptView.Width, mExemptLoc[1] + ExemptView.Height);
                            if (rect.Contains(x, y))
                            {
                                return false;   // the exempt view owns this gesture
                            }
                        }
                    }
                }
                catch
                {
                }
                return base.OnInterceptTouchEvent(e);
            }
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RegisterCrashGuards();
            _connection.Host = this;
            // Receive mapped product log lines into the UI log.
            SmbService.ProductLog += AppendProductLog;
            // Auto-start is a committed configuration value; load it before the switch
            // row is built.
            s_autoStartCommitted = LoadAutoStart();

            // Section order: HEADER -> SERVER -> SERVER SETTINGS -> INFO -> DIAGNOSTICS.
            // Programmatic UI only (no XML layout, no Material/AppCompat).
            var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            // A little extra top space so the title is not occluded by the status bar
            // on device (12dp -> 20dp; no window-inset work here).
            content.SetPadding(Dp(16), Dp(20), Dp(16), Dp(16));

            // ---- HEADER: title (NOT clickable) + About on the right ---------------
            var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            header.SetGravity(GravityFlags.CenterVertical);
            var headerTitle = new TextView(this) { Text = "anSMBserver", TextSize = 20 };
            headerTitle.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            headerTitle.SetTextColor(CTextPrimary);
            header.AddView(headerTitle);
            var headerVersion = new TextView(this)
            {
                Text = "  " + AppVersionDisplay(),
                TextSize = 12
            };
            headerVersion.SetTextColor(CTextTertiary);
            header.AddView(headerVersion);
            var headerSpacer = new Space(this);
            header.AddView(headerSpacer, new LinearLayout.LayoutParams(0, 1, 1f));
            var aboutButton = TextButton(LabelAbout);   // accent text, no border
            aboutButton.Click += OnAboutClicked;
            header.AddView(aboutButton, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, Dp(32)));
            content.AddView(header, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(48)));
            // Header separator — deliberately NOT one of the three section dividers.
            content.AddView(Divider(0, 6));

            // ---- SERVER ---------------------------------------------------------
            content.AddView(SectionTitle("SERVER"));

            var statusRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            statusRow.SetGravity(GravityFlags.CenterVertical);
            m_status = new TextView(this) { TextSize = 16 };
            m_status.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            statusRow.AddView(m_status, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));
            m_startStop = PrimaryButton(LabelStart);
            m_startStop.Click += OnStartStopClicked;
            statusRow.AddView(m_startStop, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, Dp(ButtonHeightDp)));
            content.AddView(statusRow);

            m_statusLine2 = new TextView(this) { TextSize = 13 };
            m_statusLine2.SetTextColor(CTextSecondary);
            m_statusLine2.SetPadding(0, Dp(6), 0, 0);
            content.AddView(m_statusLine2);

            // The SMB address block (URL + Copy/QR + QR image) lives in INFO.

            // ---- section divider 1 -----------------------------------------------
            content.AddView(Divider(SectionDividerTopDp, SectionDividerBottomDp));

            // ---- SERVER SETTINGS ------------------------------------------------
            content.AddView(SectionTitle("SERVER SETTINGS"));

            // One row per field (label left, control right).
            m_userInput = new Android.Widget.EditText(this)
            {
                Text = SmbService.ConfiguredUsername,
                InputType = Android.Text.InputTypes.ClassText
            };
            content.AddView(SettingsRow("Username", m_userInput));
            m_userError = ErrorText();
            content.AddView(m_userError);

            // The password visibility toggle is an eye / eye-off icon pair drawn
            // inside the field (two minimal vector drawables).
            m_passInput = new Android.Widget.EditText(this)
            {
                Text = SmbService.ConfiguredPassword,
                InputType = Android.Text.InputTypes.ClassText   // default: plaintext
            };
            m_passInput.Touch += OnPasswordFieldTouch;
            UpdatePasswordIcon();
            content.AddView(SettingsRow("Password", m_passInput));
            m_passError = ErrorText();
            content.AddView(m_passError);

            m_portInput = new Android.Widget.EditText(this)
            {
                Text = SmbService.PersistedPort.ToString(),
                InputType = Android.Text.InputTypes.ClassNumber
            };
            m_portInput.AfterTextChanged += (s, e) => UpdatePortHint();
            content.AddView(SettingsRow("Port", m_portInput));
            m_portError = ErrorText();
            content.AddView(m_portError);
            m_portHint = new TextView(this) { TextSize = 11 };
            m_portHint.SetTextColor(CTextTertiary);
            content.AddView(m_portHint);

            // Auto-start switch row (default ON; committed by Apply only).
            m_autoStartSwitch = new Android.Widget.Switch(this) { Checked = s_autoStartCommitted };
            content.AddView(SwitchRow("Start server automatically", m_autoStartSwitch));
            m_autoStartError = ErrorText();
            content.AddView(m_autoStartError);

            // Settings actions: Primary (Apply) + Secondary (Restore), 1:1 equal width.
            var settingsActions = ButtonRow(16);
            var applySettings = PrimaryButton(LabelApplySettings);
            applySettings.Click += OnApplySettingsClicked;
            var restoreDefaults = SecondaryButton(LabelRestoreDefaults);
            restoreDefaults.Click += OnRestoreSettingsDefaultsClicked;
            AddPairButton(settingsActions, applySettings, restoreDefaults);
            content.AddView(settingsActions);

            // ---- section divider 2 -----------------------------------------------
            content.AddView(Divider(SectionDividerTopDp, SectionDividerBottomDp));

            // ---- INFO -----------------------------------------------------------
            content.AddView(SectionTitle("INFO"));
            content.AddView(FieldLabel("Shared Storage"));
            var sharedRoot = new TextView(this) { TextSize = 13 };
            sharedRoot.SetTextColor(CTextPrimary);
            sharedRoot.Text = SmbService.ShareRoot + "/";
            content.AddView(sharedRoot);
            // NO divider inside INFO.

            // SMB Address lives here, not in the SERVER area. The label is held in
            // a field so its visibility can never disagree with the URL's.
            m_shareUrlLabel = FieldLabel("SMB Address");
            content.AddView(m_shareUrlLabel);
            m_shareUrl = new TextView(this) { TextSize = 15 };
            m_shareUrl.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            m_shareUrl.SetTextColor(CTextPrimary);
            content.AddView(m_shareUrl);

            // Copy / QR: two Secondary buttons sharing the row 1:1.
            var urlActions = ButtonRow(8);
            m_copyUrl = SecondaryButton(LabelCopyAddress);
            m_copyUrl.Click += OnCopyUrlClicked;
            m_qrToggle = SecondaryButton(LabelQrCode);
            m_qrToggle.Click += OnQrToggleClicked;
            AddPairButton(urlActions, m_copyUrl, m_qrToggle);
            content.AddView(urlActions);

            var qrWrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
            qrWrap.SetGravity(GravityFlags.CenterHorizontal);
            m_qrImage = new ImageView(this);
            m_qrImage.Visibility = ViewStates.Gone;   // QR hidden by default
            qrWrap.AddView(m_qrImage);
            content.AddView(qrWrap);

            var connHeader = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            connHeader.SetGravity(GravityFlags.CenterVertical);
            connHeader.AddView(FieldLabel("Connections"), new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));
            m_connCount = new TextView(this) { TextSize = 13 };
            m_connCount.SetTextColor(CTextPrimary);
            connHeader.AddView(m_connCount);
            content.AddView(connHeader);

            m_connEmpty = new TextView(this) { TextSize = 12 };
            m_connEmpty.SetTextColor(CTextTertiary);
            content.AddView(m_connEmpty);
            m_connList = new LinearLayout(this) { Orientation = Orientation.Vertical };
            content.AddView(m_connList);

            // ---- section divider 3 -----------------------------------------------
            content.AddView(Divider(SectionDividerTopDp, SectionDividerBottomDp));

            // ---- DIAGNOSTICS ----------------------------------------------------
            content.AddView(SectionTitle("DIAGNOSTICS"));
            // The `Logs` label has its own row; the log actions sit on their own row
            // below the box.
            content.AddView(FieldLabel("Logs"));

            m_log = new TextView(this) { TextSize = 11 };
            m_log.SetTypeface(Android.Graphics.Typeface.Monospace,
                Android.Graphics.TypefaceStyle.Normal);
            m_log.SetTextColor(CTextSecondary);
            m_log.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));
            // The log area is its own scroll container: a bordered, padded box with
            // its own scrollbar kept inside the box, so it reads as an independent
            // log viewer instead of plain text sitting on the page.
            m_logScroll = new ScrollView(this);
            // The log box is a nested scrolling child of the page scroller, so that
            // reaching its own edge hands the remaining movement to the page.
            m_logScroll.NestedScrollingEnabled = true;
            m_logScroll.VerticalScrollBarEnabled = true;
            m_logScroll.ScrollBarStyle = ScrollbarStyles.InsideInset;
            m_logScroll.ScrollBarDefaultDelayBeforeFade = 2500;
            m_logScroll.Background = RoundedBg(CLogFill, CDivider, 6);
            m_logScroll.AddView(m_log);
            content.AddView(m_logScroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(LogMinHeightDp)));

            // Log actions: two Secondary buttons sharing the row 1:1.
            var logsActions = ButtonRow(8);
            var clearLogs = SecondaryButton(LabelClearLogs);
            clearLogs.Click += OnClearLogsClicked;
            var copyLogs = SecondaryButton(LabelCopyLogs);
            copyLogs.Click += OnCopyLogsClicked;
            AddPairButton(logsActions, clearLogs, copyLogs);
            content.AddView(logsActions);

            // The Self-Probe UI entry is intentionally NOT part of the product UI.
            // SelfProbe.cs source is retained, not deleted.

            var page = new GestureAwareScrollView(this) { ExemptView = m_logScroll };
            page.AddView(content);
            SetContentView(page);

            AppendLog("P11 host start; user=" + SmbService.ConfiguredUsername
                + "; share=" + SmbService.ShareName + "; root=" + SmbService.ShareRoot);

            RequestNotificationPermission();
            RequestStorageAccessIfNeeded();
        }

        private void RequestNotificationPermission()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) // API 33
            {
                if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
                {
                    RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 1001);
                }
            }
        }

        // Request broad shared-storage access for the /storage/emulated/0 default share.
        // API 30+ needs the "All files access" (MANAGE_EXTERNAL_STORAGE) settings grant;
        // below API 30 the READ/WRITE_EXTERNAL_STORAGE runtime perms apply.
        private bool HasAllFilesAccess()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                return Android.OS.Environment.IsExternalStorageManager;
            }
            return CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage)
                == Android.Content.PM.Permission.Granted;
        }

        private void RequestStorageAccessIfNeeded()
        {
            try
            {
                if (HasAllFilesAccess())
                {
                    AppendLog("storage: all-files access granted");
                    return;
                }
                AppendLog("storage: requesting all-files access for /storage/emulated/0");
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    StartActivityForResult(new Intent(
                        Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                        Android.Net.Uri.Parse("package:" + PackageName)), StorageRequestCode);
                }
                else
                {
                    RequestPermissions(new[]
                    {
                        Android.Manifest.Permission.ReadExternalStorage,
                        Android.Manifest.Permission.WriteExternalStorage
                    }, StorageRequestCode);
                }
            }
            catch (Exception ex)
            {
                AppendLog("storage permission request error: " + ex.Message);
            }
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == StorageRequestCode)
            {
                AppendLog("storage: all-files access now=" + HasAllFilesAccess());
            }
        }

        private volatile bool _resumed;
        private System.Threading.Thread _refreshThread;

        protected override void OnResume()
        {
            base.OnResume();
            // Started + bound = the service survives Activity unbind.
            // The Auto-start policy governs a real cold launch
            // (the FIRST resume of this app process) only. Every later resume follows the
            // Server Run Intent, so returning to the app can never restart a server the
            // user stopped. With a running server this is the previous behaviour.
            bool coldLaunch = s_firstResume;
            s_firstResume = false;
            bool shouldStart;
            if (coldLaunch)
            {
                shouldStart = s_autoStartCommitted;
                s_runIntent = shouldStart;
            }
            else
            {
                // With Auto-start OFF and no explicit Start the service stays bind-only,
                // so it is never promoted to a started/foreground component.
                shouldStart = s_runIntent;
            }

            var startIntent = new Intent(this, typeof(SmbService));
            if (shouldStart)
            {
                StartService(startIntent);
            }
            BindService(startIntent, _connection, Bind.AutoCreate);
            StartStateRefresh();
        }

        protected override void OnPause()
        {
            StopStateRefresh();
            base.OnPause();
            if (m_bound)
            {
                UnbindService(_connection);
                m_bound = false;
            }
            m_service = null;
        }

        // While resumed, poll the bound Service every ~2s so the displayed IP / network
        // state updates on Wi-Fi/AP/IP change WITHOUT an Activity relaunch.
        private void StartStateRefresh()
        {
            _resumed = true;
            if (_refreshThread != null) return;
            _refreshThread = new System.Threading.Thread(() =>
            {
                while (_resumed)
                {
                    try
                    {
                        RunOnUiThread(() => { if (_resumed) UpdateUi(); });
                    }
                    catch { }
                    System.Threading.Thread.Sleep(2000);
                }
            });
            _refreshThread.IsBackground = true;
            _refreshThread.Start();
        }

        private void StopStateRefresh()
        {
            _resumed = false;
            _refreshThread = null;
        }

        internal void OnServiceConnected(SmbService service)
        {
            m_service = service;
            m_bound = true;
            // Reconcile with the authoritative service state, upwards only: a running
            // server (e.g. recovered by START_STICKY with no Activity involved) means RUN,
            // while an explicit Stop in this process must never be flipped back to RUN.
            if (service != null && service.IsRunning)
            {
                s_runIntent = true;
            }
            // Reflect current state (server may already be running from a prior start).
            UpdateUi();
        }

        internal void OnServiceDisconnected()
        {
            m_service = null;
            m_bound = false;
            AppendLog("SMB service disconnected");
        }

        // ---- SERVER SETTINGS ------------------------------------------------
        // `Apply Settings` is the ONLY commit point. It is transactional: all three
        // fields are validated first and any failure commits NOTHING. While the
        // server is Running the Port cannot change.

        private void OnApplySettingsClicked(object sender, EventArgs e)
        {
            ClearInlineErrors();

            string user = m_userInput?.Text;
            string pass = m_passInput?.Text;
            string portText = m_portInput?.Text?.Trim();

            bool userOk = !string.IsNullOrEmpty(user) && user.Trim().Length > 0;
            bool passOk = !string.IsNullOrEmpty(pass);
            bool portParsed = int.TryParse(portText, out int port);
            bool portOk = portParsed && port >= MinPort && port <= MaxPort;
            bool running = m_service != null && m_service.IsRunning;
            bool portChanged = portOk && port != SmbService.ConfiguredPort;
            bool autoStartChanged = m_autoStartSwitch != null
                && m_autoStartSwitch.Checked != s_autoStartCommitted;
            // BOTH lifecycle-affecting settings share ONE gate.
            bool portGateOk = !(running && portChanged);
            bool autoStartGateOk = !(running && autoStartChanged);

            if (!userOk) SetInlineError(m_userError, "Username must not be empty");
            if (!passOk) SetInlineError(m_passError, "Password must not be empty");
            if (!portOk)
            {
                SetInlineError(m_portError,
                    "Port must be between " + MinPort + " and " + MaxPort);
            }
            else if (!portGateOk)
            {
                SetInlineError(m_portError, "Stop the server before changing the port");
            }
            if (!autoStartGateOk)
            {
                SetInlineError(m_autoStartError,
                    "Stop the server before changing this setting");
            }

            if (!userOk || !passOk || !portOk || !portGateOk || !autoStartGateOk)
            {
                // All-or-nothing: nothing is committed, the running server is
                // untouched, and no pending state is created.
                AppendLog("settings: apply rejected (nothing committed)");
                Toast.MakeText(this, "Settings not applied", ToastLength.Short).Show();
                return;
            }

            SmbService.TrySetCredentials(user, pass);
            SmbService.ConfiguredPort = port;
            s_autoStartCommitted = m_autoStartSwitch.Checked;
            SaveAutoStart(s_autoStartCommitted);
            AppendLog("settings: applied (username=" + SmbService.ConfiguredUsername
                + ", port=" + SmbService.ConfiguredPort
                + ", auto-start=" + s_autoStartCommitted + ")");
            UpdatePortHint();
            Toast.MakeText(this, "Settings applied", ToastLength.Short).Show();
        }

        // `Restore Defaults` obeys the SAME unified gate - while the server is Running
        // it would have to change the Port and/or Auto-start, so the whole restore is
        // rejected with the same inline errors.
        private void OnRestoreSettingsDefaultsClicked(object sender, EventArgs e)
        {
            ClearInlineErrors();
            bool running = m_service != null && m_service.IsRunning;
            bool portWouldChange = SmbService.ConfiguredPort != SmbService.DefaultPort;
            bool autoStartWouldChange = s_autoStartCommitted;   // default is ON
            if (running && (portWouldChange || autoStartWouldChange))
            {
                if (portWouldChange)
                {
                    SetInlineError(m_portError, "Stop the server before changing the port");
                }
                if (autoStartWouldChange)
                {
                    SetInlineError(m_autoStartError,
                        "Stop the server before changing this setting");
                }
                AppendLog("settings: restore rejected (nothing committed)");
                Toast.MakeText(this, "Defaults not restored", ToastLength.Short).Show();
                return;
            }

            SmbService.RestoreAccountDefaults();
            SmbService.ConfiguredPort = SmbService.DefaultPort;
            s_autoStartCommitted = true;                        // default is ON
            SaveAutoStart(true);
            if (m_userInput != null) m_userInput.Text = SmbService.ConfiguredUsername;
            if (m_passInput != null) m_passInput.Text = SmbService.ConfiguredPassword;
            if (m_portInput != null) m_portInput.Text = SmbService.ConfiguredPort.ToString();
            if (m_autoStartSwitch != null) m_autoStartSwitch.Checked = true;
            UpdatePortHint();
            AppendLog("settings: restored defaults (username=" + SmbService.ConfiguredUsername
                + ", port=" + SmbService.ConfiguredPort + ", auto-start=True)");
            Toast.MakeText(this, "Defaults restored", ToastLength.Short).Show();
        }

        // Clear the bounded in-memory product log (no persistence).
        private void OnClearLogsClicked(object sender, EventArgs e)
        {
            lock (m_logLock)
            {
                m_logBuffer.Clear();
                m_logFlushQueued = false;
            }
            if (m_log != null) m_log.Text = string.Empty;
            UpdateLogHeight();
        }

        // ONE button. Product lines only (the visible Product Logs text, i.e.
        // m_log.Text - NOT m_logBuffer, which is only a staging buffer).
        // Nothing to copy => the clipboard is left unchanged.
        private void OnCopyLogsClicked(object sender, EventArgs e)
        {
            string product = m_log != null ? m_log.Text : null;
            if (string.IsNullOrEmpty(product))
            {
                AppendLog("logs: nothing to copy");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("===== anSMBserver product logs =====\n");
            sb.Append(product);
            if (product[product.Length - 1] != '\n') sb.Append('\n');

            try
            {
                var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                cm.PrimaryClip = Android.Content.ClipData.NewPlainText(
                    "anSMBserver logs", sb.ToString());
                AppendLog("logs: copied (product)");
                Toast.MakeText(this, "Logs copied", ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                AppendLog("logs: copy error: " + ex.Message);
            }
        }

        // `About` opens the project repository (the title is not clickable).
        private void OnAboutClicked(object sender, EventArgs e)
        {
            try
            {
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(RepoUrl)));
            }
            catch (Exception ex)
            {
                AppendLog("about: cannot open " + RepoUrl + " (" + ex.Message + ")");
                Toast.MakeText(this, "No app can open " + RepoUrl, ToastLength.Short).Show();
            }
        }

        // The eye / eye-off icons live inside the field itself. Default is plaintext;
        // tapping the icon masks the value, tapping again reveals it.
        private void OnPasswordFieldTouch(object sender, View.TouchEventArgs e)
        {
            try
            {
                if (e.Event.Action != MotionEventActions.Up) return;
                var drawables = m_passInput?.GetCompoundDrawablesRelative();
                var icon = (drawables != null && drawables.Length > 2) ? drawables[2] : null;
                if (icon == null) return;
                int right = m_passInput.Width - m_passInput.PaddingRight;
                // The icon stays 24dp, but the tap target is at least 48dp: anchor
                // the target to the field's right edge and grow it leftwards, which
                // keeps it entirely inside the field.
                int left = right - Math.Max(icon.IntrinsicWidth, Dp(48));
                if (left < 0) left = 0;
                float x = e.Event.GetX();
                if (x >= left && x <= right)
                {
                    TogglePasswordVisibility();
                    e.Handled = true;
                }
            }
            catch
            {
            }
        }

        private void TogglePasswordVisibility()
        {
            m_passwordHidden = !m_passwordHidden;
            if (m_passInput != null)
            {
                m_passInput.TransformationMethod = m_passwordHidden
                    ? new Android.Text.Method.PasswordTransformationMethod()
                    : null;
            }
            UpdatePasswordIcon();
        }

        // plaintext -> ic_eye ; masked -> ic_eye_off
        private void UpdatePasswordIcon()
        {
            if (m_passInput == null) return;
            m_passInput.SetCompoundDrawablesRelativeWithIntrinsicBounds(
                0, 0,
                m_passwordHidden ? Resource.Drawable.ic_eye_off : Resource.Drawable.ic_eye,
                0);
        }

        // QR is hidden by default; expanding/collapsing never scrolls the page.
        private void OnQrToggleClicked(object sender, EventArgs e)
        {
            m_qrExpanded = !m_qrExpanded;
            UpdateQrVisibility();
        }

        // `Start` uses the committed configured port only - it never reads an un-applied
        // value from the Port field. `Stop` only stops the server; it never restarts it
        // and never changes the committed port.
        // With Auto-start OFF the service may be bind-only, so an explicit Start MUST
        // promote it through StartService — otherwise it would be destroyed as soon as
        // the Activity unbinds and the server would stop unexpectedly.
        private void OnStartStopClicked(object sender, EventArgs e)
        {
            if (m_service == null)
            {
                AppendLog("SMB service not bound yet; cannot toggle");
                return;
            }
            if (m_service.IsRunning)
            {
                m_service.Stop();
                s_runIntent = false;   // explicit Stop: a later resume must not restart it
                AppendLog("SMB server stopped (from UI)");
                UpdateUi();
                return;
            }
            StartService(new Intent(this, typeof(SmbService)));
            m_service.Start();
            s_runIntent = true;        // explicit Start, independent of the Auto-start setting
            AppendLog("SMB server starting on port " + SmbService.ConfiguredPort + " (from UI)");
            UpdateUi();
        }

        // ---- visual helpers ---------------------------------------------------

        private int Dp(int value)
        {
            return (int)(value * Resources.DisplayMetrics.Density + 0.5f);
        }

        private Android.Graphics.Drawables.GradientDrawable RoundedBg(
            Android.Graphics.Color fill, Android.Graphics.Color stroke, int radiusDp)
        {
            var d = new Android.Graphics.Drawables.GradientDrawable();
            d.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
            d.SetCornerRadius(Dp(radiusDp));
            d.SetColor(fill);
            if (stroke != Android.Graphics.Color.Transparent)
            {
                d.SetStroke(Dp(1), stroke);
            }
            return d;
        }

        // Buttons are plain `TextView`s with a custom background: the platform
        // `Button` style forces ALL CAPS and a raised look we do not want. TextView
        // also avoids any Material/AppCompat dependency.
        private TextView MakeAction(string text, int textSize, int hPadDp,
            Android.Graphics.Color fill, Android.Graphics.Color textColor,
            Android.Graphics.Color stroke, int radiusDp)
        {
            var v = new TextView(this) { Text = text, TextSize = textSize };
            v.Gravity = GravityFlags.Center;
            v.SetPadding(Dp(hPadDp), 0, Dp(hPadDp), 0);
            v.SetTextColor(textColor);
            v.Background = ActionBackground(fill, stroke, radiusDp, textColor);
            v.Clickable = true;
            return v;
        }

        // Platform-only touch feedback for a `TextView` used as a button: a pressed
        // colour state plus a rounded, masked ripple (API 21+). No AndroidX, no
        // Material, no new dependency. If the ripple is unavailable for any reason
        // the plain rounded background is used instead.
        private Android.Graphics.Drawables.Drawable ActionBackground(
            Android.Graphics.Color fill, Android.Graphics.Color stroke, int radiusDp,
            Android.Graphics.Color textColor)
        {
            try
            {
                bool filled = fill.ToArgb() != 0;
                var states = new Android.Graphics.Drawables.StateListDrawable();
                states.AddState(new[] { Android.Resource.Attribute.StatePressed },
                    RoundedBg(PressedFill(fill, textColor), stroke, radiusDp));
                states.AddState(new int[0], RoundedBg(fill, stroke, radiusDp));

                var mask = RoundedBg(Android.Graphics.Color.White,
                    Android.Graphics.Color.Transparent, radiusDp);
                var ripple = Android.Content.Res.ColorStateList.ValueOf(filled
                    ? new Android.Graphics.Color(unchecked((int)0x33FFFFFF))  // light ripple on the fill
                    : Tint(textColor, 0x33));                                // own-colour ripple when flat
                return new Android.Graphics.Drawables.RippleDrawable(ripple, states, mask);
            }
            catch
            {
                return RoundedBg(fill, stroke, radiusDp);
            }
        }

        // Pressed fill: a darker accent for filled buttons, a wash of the button's own
        // text colour for outlined ones - so press feedback stays blue on the accent
        // buttons and red on the danger button.
        private static Android.Graphics.Color PressedFill(
            Android.Graphics.Color fill, Android.Graphics.Color textColor)
        {
            return fill.ToArgb() == 0
                ? Tint(textColor, 0x26)
                : new Android.Graphics.Color(unchecked((int)0xFF1665CE));
        }

        // The same RGB as `color`, carrying the given alpha byte (0x00-0xFF).
        private static Android.Graphics.Color Tint(Android.Graphics.Color color, int alpha)
        {
            return new Android.Graphics.Color((alpha << 24) | (color.ToArgb() & 0x00FFFFFF));
        }

        // ---- unified button tokens ---------------------------------------------
        // Every button in the product button system shares ONE height, ONE corner
        // radius, ONE font size and ONE horizontal padding, and belongs to exactly
        // one of three semantic tiers. (About is header navigation, not a button.)
        private const int ButtonHeightDp = 40;
        private const int ButtonRadiusDp = 6;
        private const int ButtonTextSize = 14;
        private const int ButtonPadDp = 16;
        private const int ButtonGapDp = 12;

        // A row holding a 1:1 pair of buttons (used by Settings / Info / Diagnostics).
        private LinearLayout ButtonRow(int topDp)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, Dp(topDp), 0, 0);
            return row;
        }

        // Both buttons take exactly half the row (weight 1) and the same height.
        private void AddPairButton(LinearLayout row, TextView first, TextView second)
        {
            row.AddView(first, new LinearLayout.LayoutParams(0, Dp(ButtonHeightDp), 1f));
            var gap = new Space(this);
            row.AddView(gap, new LinearLayout.LayoutParams(Dp(ButtonGapDp), 1));
            row.AddView(second, new LinearLayout.LayoutParams(0, Dp(ButtonHeightDp), 1f));
        }

        // Tier 1 - primary: filled accent, white text.
        private TextView PrimaryButton(string text)
        {
            return MakeAction(text, ButtonTextSize, ButtonPadDp,
                CAccent, Android.Graphics.Color.White,
                Android.Graphics.Color.Transparent, ButtonRadiusDp);
        }

        // Tier 2 - secondary: outlined accent, accent text.
        private TextView SecondaryButton(string text)
        {
            return MakeAction(text, ButtonTextSize, ButtonPadDp,
                Android.Graphics.Color.Transparent, CAccent, CAccent, ButtonRadiusDp);
        }

        // Tier 3 - danger: outlined danger red, used by the connection action.
        private TextView DangerButton(string text)
        {
            return MakeAction(text, ButtonTextSize, ButtonPadDp,
                Android.Graphics.Color.Transparent, CDanger, CDanger, ButtonRadiusDp);
        }

        // Header navigation (`About`): plain accent text, deliberately outside the
        // button system above - it is a link, not an action.
        private TextView TextButton(string text)
        {
            return MakeAction(text, 13, 8,
                Android.Graphics.Color.Transparent, CAccent,
                Android.Graphics.Color.Transparent, 0);
        }

        // Disabled controls are dimmed explicitly (TextView does not do it for us).
        private void SetControlEnabled(TextView control, bool enabled)
        {
            if (control == null) return;
            control.Enabled = enabled;
            control.Alpha = enabled ? 1f : 0.4f;
        }

        private TextView FieldLabel(string text)
        {
            var tv = new TextView(this) { Text = text, TextSize = 12 };
            tv.SetTextColor(CTextSecondary);
            tv.SetPadding(0, Dp(12), 0, 0);
            return tv;
        }

        // Label for a single-line settings row (no own vertical padding — the row
        // carries the spacing).
        private TextView InlineLabel(string text)
        {
            var tv = new TextView(this) { Text = text, TextSize = 12 };
            tv.SetTextColor(CTextSecondary);
            return tv;
        }

        // `Username / Password / Port` = label (fixed width) + control.
        private LinearLayout SettingsRow(string label, View field)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, Dp(12), 0, 0);
            row.AddView(InlineLabel(label), new LinearLayout.LayoutParams(
                Dp(96), ViewGroup.LayoutParams.WrapContent));
            row.AddView(field, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));
            return row;
        }

        // The label takes the remaining width and the Switch sits at the right.
        private LinearLayout SwitchRow(string label, View toggle)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, Dp(12), 0, 0);
            row.AddView(InlineLabel(label), new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.AddView(toggle, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            return row;
        }

        // Inline validation text. Gone while empty, so a clean form shows no blank
        // rows (the field rows carry the spacing).
        private TextView ErrorText()
        {
            var tv = new TextView(this) { TextSize = 11 };
            tv.SetTextColor(CDanger);
            tv.Visibility = ViewStates.Gone;
            return tv;
        }

        private View Divider(int topDp, int bottomDp)
        {
            var v = new View(this);
            v.SetBackgroundColor(CDivider);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(1));
            lp.SetMargins(0, Dp(topDp), 0, Dp(bottomDp));
            v.LayoutParameters = lp;
            return v;
        }

        private TextView SectionTitle(string text)
        {
            var tv = new TextView(this) { Text = text, TextSize = 13 };
            tv.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            tv.SetTextColor(CTextSecondary);
            tv.LetterSpacing = 0.06f;
            // Modest top padding: the section divider already contributes its own
            // margin, so a large value here would double the same gap.
            tv.SetPadding(0, Dp(14), 0, Dp(8));
            return tv;
        }

        // The version string always comes from the APK (never hard-coded).
        private string AppVersionDisplay()
        {
            try
            {
                string v = PackageManager.GetPackageInfo(PackageName, 0).VersionName;
                return string.IsNullOrEmpty(v) ? "v?" : "v" + v;
            }
            catch
            {
                return "v?";
            }
        }

        private void ClearInlineErrors()
        {
            SetInlineError(m_userError, null);
            SetInlineError(m_passError, null);
            SetInlineError(m_portError, null);
            SetInlineError(m_autoStartError, null);
        }

        // Auto-start persistence: kept in the Activity layer, using the same
        // app-private prefs file the Service uses (SmbService.PrefName).
        private static bool LoadAutoStart()
        {
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(
                    SmbService.PrefName, FileCreationMode.Private);
                return prefs.GetBoolean(KeyAutoStart, true);   // default ON
            }
            catch
            {
                return true;
            }
        }

        private static void SaveAutoStart(bool value)
        {
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(
                    SmbService.PrefName, FileCreationMode.Private);
                prefs.Edit().PutBoolean(KeyAutoStart, value).Commit();
            }
            catch
            {
            }
        }

        private void SetInlineError(TextView target, string message)
        {
            if (target == null) return;
            bool hasText = !string.IsNullOrEmpty(message);
            target.Text = hasText ? message : string.Empty;
            // Empty inline text must not reserve a blank row in the form.
            target.Visibility = hasText ? ViewStates.Visible : ViewStates.Gone;
        }

        // Hint shown when the Port field holds a parseable value that differs from the
        // committed one; `Start` will not use it.
        private void UpdatePortHint()
        {
            if (m_portHint == null) return;
            string text = m_portInput?.Text?.Trim();
            bool dirty = int.TryParse(text, out int p)
                && p >= MinPort && p <= MaxPort
                && p != SmbService.ConfiguredPort;
            m_portHint.Text = dirty ? "Press Apply Settings to commit the port." : string.Empty;
            m_portHint.Visibility = dirty ? ViewStates.Visible : ViewStates.Gone;
        }

        // The user's expand/collapse choice survives the 2s refresh.
        private void UpdateQrVisibility()
        {
            if (m_qrImage == null) return;
            bool running = m_service != null && m_service.IsRunning;
            bool hasUrl = running && !string.IsNullOrEmpty(ShareAddress.BuildShareUrl(m_service?.LanIpv4));
            m_qrImage.Visibility = (m_qrExpanded && hasUrl) ? ViewStates.Visible : ViewStates.Gone;
        }

        // The log container grows with its content and is capped at 160dp, so a nearly
        // empty log no longer leaves a large empty gap.
        private void UpdateLogHeight()
        {
            if (m_logScroll == null || m_log == null) return;
            try
            {
                int lines = m_log.LineCount;
                if (lines < 1) lines = 1;
                int h = lines * m_log.LineHeight
                    + m_log.PaddingTop + m_log.PaddingBottom + Dp(4);
                int max = Dp(LogMaxHeightDp);
                if (h > max) h = max;
                // Keep a usable minimum so the log always reads as a fixed window
                // rather than as a gap that grows on demand.
                if (h < Dp(LogMinHeightDp)) h = Dp(LogMinHeightDp);
                var lp = m_logScroll.LayoutParameters;
                if (lp != null && lp.Height != h)
                {
                    lp.Height = h;
                    m_logScroll.LayoutParameters = lp;
                }
            }
            catch
            {
            }
        }

        // A connection row is a layout, NOT a Button. Only the `Disconnect` affordance
        // is clickable; it keeps the endpoint-based TerminateConnection semantics
        // unchanged.
        private View ConnectionRow(SmbService.ConnectionInfo c)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, Dp(12), 0, Dp(12));

            var left = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var line1 = new TextView(this) { Text = c.EndPoint, TextSize = 13 };
            line1.SetTextColor(CTextPrimary);
            var line2 = new TextView(this) { TextSize = 12 };
            line2.SetTextColor(CTextSecondary);
            line2.Text = c.UserName + " · " + c.Dialect + " · "
                + c.ConnectedUtc.ToLocalTime().ToString("HH:mm:ss");
            left.AddView(line1);
            left.AddView(line2);
            row.AddView(left, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));

            var disconnect = DangerButton(LabelDisconnect);
            var captured = c;
            disconnect.Click += (s, e) =>
            {
                if (m_service != null && m_service.Disconnect(captured))
                {
                    m_connSignature = null;   // force a rebuild on the next refresh
                    AppendLog("connections: disconnect requested for " + captured.EndPoint);
                }
            };
            row.AddView(disconnect, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, Dp(ButtonHeightDp)));
            return row;
        }

        private void UpdateUi()
        {
            bool running = m_service != null && m_service.IsRunning;
            // The status address line and the SMB URL use the SAME address source, so
            // the two can never disagree.
            string addr = ShareAddress.Resolve(m_service?.LanIpv4);
            bool hasAddr = !string.IsNullOrEmpty(addr);
            int port = SmbService.ConfiguredPort;
            RunOnUiThread(() =>
            {
                if (running)
                {
                    m_status.Text = "● SMB Server Running";
                    m_status.SetTextColor(CSuccess);
                    m_statusLine2.Text = hasAddr
                        ? "Listening on " + addr + ":" + port + " · Max SMB 3.0"
                        : "No network address";
                    m_statusLine2.SetTextColor(hasAddr ? CTextSecondary : CWarning);
                }
                else
                {
                    m_status.Text = "○ SMB Server Stopped";
                    m_status.SetTextColor(CTextSecondary);
                    m_statusLine2.Text = "Server is not running";
                    m_statusLine2.SetTextColor(CTextSecondary);
                }
                m_startStop.Text = running ? LabelStop : LabelStart;
                RefreshConnections();
                RefreshSharing();
            });
        }

        // The count is the AUTHENTICATED SESSION count. The list is rebuilt only when
        // the snapshot changes, to avoid flicker on the ~2s refresh. The count is
        // right-aligned and the empty state is its own line; rows are layouts, not
        // Buttons.
        private void RefreshConnections()
        {
            if (m_connCount == null || m_connList == null) return;
            var connections = m_service?.GetConnections()
                ?? new System.Collections.Generic.List<SmbService.ConnectionInfo>();
            m_connCount.Text = connections.Count.ToString();
            if (m_connEmpty != null)
            {
                bool none = connections.Count == 0;
                m_connEmpty.Text = none ? "No active connections" : string.Empty;
                // Gone while connections exist, so the empty state never leaves a
                // blank row above the list.
                m_connEmpty.Visibility = none ? ViewStates.Visible : ViewStates.Gone;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var c in connections)
            {
                sb.Append(c.EndPoint).Append('|').Append(c.UserName).Append('|')
                  .Append(c.Dialect).Append('|').Append(c.ConnectedUtc.Ticks).Append('\n');
            }
            string signature = connections.Count + ":" + sb;
            if (signature == m_connSignature) return;
            m_connSignature = signature;

            m_connList.RemoveAllViews();
            foreach (var c in connections)
            {
                m_connList.AddView(ConnectionRow(c));
                m_connList.AddView(Divider(0, 0));
            }
        }

        // Sharing / URL / QR.
        // Address selection lives in ShareAddress (sharing layer only; the service-side
        // network state is untouched). IPv4-only. No usable IPv4 -> no URL, Copy/QR
        // disabled. While the server is STOPPED the whole connection-info block is
        // hidden - it must not advertise an address for a server that is not listening.
        private void RefreshSharing()
        {
            if (m_shareUrl == null) return;
            bool running = m_service != null && m_service.IsRunning;
            string url = running ? ShareAddress.BuildShareUrl(m_service?.LanIpv4) : null;
            bool hasUrl = !string.IsNullOrEmpty(url);
            // Label, URL and both URL actions share ONE visibility rule, so they can
            // never disagree. When the server runs without a usable IPv4 the status
            // line already reports "No network address", so nothing disappears
            // without an explanation.
            bool showUrl = running && hasUrl;

            if (m_shareUrlLabel != null)
            {
                m_shareUrlLabel.Visibility = showUrl ? ViewStates.Visible : ViewStates.Gone;
            }
            m_shareUrl.Visibility = showUrl ? ViewStates.Visible : ViewStates.Gone;
            if (m_copyUrl != null)
            {
                m_copyUrl.Visibility = showUrl ? ViewStates.Visible : ViewStates.Gone;
                SetControlEnabled(m_copyUrl, hasUrl);
            }
            if (m_qrToggle != null)
            {
                m_qrToggle.Visibility = showUrl ? ViewStates.Visible : ViewStates.Gone;
                SetControlEnabled(m_qrToggle, hasUrl);
            }
            m_shareUrl.Text = hasUrl ? url : "(no address — no usable IPv4)";

            if (hasUrl && url != m_shareUrlSig)
            {
                m_shareUrlSig = url;
                RenderQr(url);
            }
            else if (!hasUrl && m_shareUrlSig != null)
            {
                m_shareUrlSig = null;
                if (m_qrImage != null) m_qrImage.SetImageBitmap(null);
            }
            UpdateQrVisibility();
        }

        // The QR is rendered on demand (the [QR] button), capped at ~148dp and centred,
        // with a quiet zone of >= 4 modules for reliable scanning.
        private void RenderQr(string url)
        {
            try
            {
                var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(
                    url, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium);
                int n = qr.Size;
                const int quiet = 4;              // quiet zone in modules
                int maxPx = Dp(148);              // on-screen cap
                int scale = Math.Max(2, maxPx / (n + quiet * 2));
                int dim = (n + quiet * 2) * scale;
                int[] px = new int[dim * dim];
                for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)0xFFFFFFFF);
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        if (!qr.GetModule(x, y)) continue;
                        int ox = (quiet + x) * scale;
                        int oy = (quiet + y) * scale;
                        for (int dy = 0; dy < scale; dy++)
                        {
                            int rowBase = (oy + dy) * dim + ox;
                            for (int dx = 0; dx < scale; dx++)
                            {
                                px[rowBase + dx] = unchecked((int)0xFF000000);
                            }
                        }
                    }
                }
                var bmp = Android.Graphics.Bitmap.CreateBitmap(
                    px, dim, dim, Android.Graphics.Bitmap.Config.Argb8888);
                if (m_qrImage != null) m_qrImage.SetImageBitmap(bmp);
            }
            catch (Exception ex)
            {
                AppendLog("sharing: QR render error: " + ex.Message);
            }
        }

        private void OnCopyUrlClicked(object sender, EventArgs e)
        {
            bool running = m_service != null && m_service.IsRunning;
            string url = running ? ShareAddress.BuildShareUrl(m_service?.LanIpv4) : null;
            if (string.IsNullOrEmpty(url))
            {
                AppendLog("sharing: no URL to copy");
                return;
            }
            try
            {
                var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                cm.PrimaryClip = Android.Content.ClipData.NewPlainText("SMB URL", url);
                AppendLog("sharing: copied " + url);
                Toast.MakeText(this, "SMB address copied", ToastLength.Short).Show();
            }
            catch (Exception ex)
            {
                AppendLog("sharing: copy error: " + ex.Message);
            }
        }

        private void RunSelfProbe()
        {
            AppendLog("=== self-probe: SMBLibrary client -> 127.0.0.1:" + SmbService.ConfiguredPort + " ===");
            var steps = new System.Collections.Generic.List<string>();
            bool ok;
            try
            {
                ok = SelfProbe.Run(System.Net.IPAddress.Loopback, SmbService.ConfiguredPort,
                    SmbService.ConfiguredUsername, SmbService.ConfiguredPassword, steps);
            }
            catch (Exception ex)
            {
                steps.Add("EXCEPTION: " + ex);
                ok = false;
            }
            foreach (string s in steps)
            {
                AppendLog(s);
            }
            AppendLog(ok ? "SELF-PROBE: PASS" : "SELF-PROBE: FAIL");
        }

        // ---- logging (UI/UI-thread safe) ----

        private readonly object m_logLock = new object();
        private readonly System.Text.StringBuilder m_logBuffer = new System.Text.StringBuilder();
        private volatile bool m_logFlushQueued;
        private const int MaxLogChars = 60000;

        private void AppendLog(string line) { AppendLogCore(line, true); }

        // Product log lines are already written to logcat by
        // SmbService.EmitProductLog, so do not duplicate the logcat write here.
        private void AppendProductLog(string line) { AppendLogCore(line, false); }

        private void AppendLogCore(string line, bool alsoLogcat)
        {
            var lockSw = System.Diagnostics.Stopwatch.StartNew();
            lock (m_logLock)
            {
                m_logBuffer.Append(line).Append('\n');
                if (m_logBuffer.Length > MaxLogChars)
                {
                    string tail = m_logBuffer.ToString(m_logBuffer.Length - MaxLogChars, MaxLogChars);
                    m_logBuffer.Clear();
                    m_logBuffer.Append(tail);
                }
                if (m_logFlushQueued)
                {
                    if (lockSw.ElapsedMilliseconds >= 200)
                    {
                        Android.Util.Log.Warn("SmbDiag", "[LOGPATH t" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] m_logLock early-return " + lockSw.ElapsedMilliseconds + "ms");
                    }
                    return;
                }
                m_logFlushQueued = true;
            }
            if (lockSw.ElapsedMilliseconds >= 200)
            {
                Android.Util.Log.Warn("SmbDiag", "[LOGPATH t" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] m_logLock flag-flip " + lockSw.ElapsedMilliseconds + "ms");
            }
            if (alsoLogcat) Android.Util.Log.Info("SmbSrv", line);
            try
            {
                RunOnUiThread(FlushLogToUi);
            }
            catch
            {
                m_logFlushQueued = false;
            }
        }

        private void FlushLogToUi()
        {
            try
            {
                // Decide BEFORE appending: the question is where the user was, not
                // where the taller content will put them.
                bool follow = LogAtBottom();
                string text;
                lock (m_logLock)
                {
                    text = m_logBuffer.ToString();
                    m_logBuffer.Clear();
                    m_logFlushQueued = false;
                }
                m_log.Text += text;
                if (follow)
                {
                    // Keep the inner log scrolled to the newest line WITHOUT scrolling
                    // the page: `fullScroll` propagates a "reveal this rectangle"
                    // request up the parent chain and would scroll the outer page
                    // ScrollView too (cold launch must stay at scroll 0) once the page
                    // became taller than the viewport. A plain ScrollTo has no such
                    // side effect.
                    int logMax = m_log.Height - m_logScroll.Height;
                    m_logScroll.ScrollTo(0, logMax > 0 ? logMax : 0);
                }
                // Keep the container height in sync with the content (dp-capped).
                m_log.Post(UpdateLogHeight);
            }
            catch
            {
            }
        }

        // True when the log window is showing its newest line (within a small
        // tolerance). While the user is browsing history this is false, so new lines
        // are appended without moving the view; scrolling back to the bottom restores
        // the follow behaviour on the next append.
        private bool LogAtBottom()
        {
            if (m_log == null || m_logScroll == null) return true;
            int scrollMax = m_log.Height - m_logScroll.Height;
            if (scrollMax <= 0) return true;   // nothing hidden -> nothing to follow
            return m_logScroll.ScrollY >= scrollMax - Dp(LogBottomToleranceDp);
        }

        protected override void OnDestroy()
        {
            // The server is owned by the Service and is NOT stopped here — it must
            // survive this Activity being destroyed.
            SmbService.ProductLog -= AppendProductLog;
            base.OnDestroy();
        }

        // Guard rail: never let a missed exception escape / kill the process.
        private static void RegisterCrashGuards()
        {
            string logPath = System.IO.Path.Combine(Application.Context.FilesDir.AbsolutePath, "crash_guard.log");
            Action<string> record = msg =>
            {
                Android.Util.Log.Error("P11Guard", msg);
                try
                {
                    System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n");
                }
                catch { }
                // The same crash-guard record must also reach the T1 product-log channel,
                // the only path into the UI Logs box. EmitProductLog writes logcat SmbSrv
                // and raises ProductLog; it is internally guarded, so a crash handler
                // cannot throw from here. Class [APP] and the "<HH:mm:ss> [APP] <message>"
                // shape follow the existing host-side T1 convention. The two writes above
                // are unchanged.
                SmbService.EmitProductLog(DateTime.Now.ToString("HH:mm:ss") + " [APP] crash-guard: " + msg);
            };
            AndroidEnvironment.UnhandledExceptionRaiser += (s, e) => { record("UnhandledExceptionRaiser: " + e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { record("AppDomain.UnhandledException: " + e.ExceptionObject); };
            TaskScheduler.UnobservedTaskException += (s, e) => { record("UnobservedTaskException: " + e.Exception); e.SetObserved(); };
        }
    }
}
