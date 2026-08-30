using System.Windows;
using System.Windows.Threading;
using Counter.App.Data;
using Counter.App.Services;
using Counter.App.Theme;
using Counter.App.ViewModels;
using Counter.App.Views;
using Counter.Core.Focus;
using Counter.Core.Journey;
using Counter.Core.Models;
using Counter.Core.Statistics;
using Counter.Core.Time;
using Microsoft.Win32;

namespace Counter.App;

public partial class App : Application
{
    private const string HotkeyToggleFocus = "hotkey_toggle_focus";
    private const string HotkeyNewTask = "hotkey_new_task";
    private const string HotkeyReveal = "hotkey_reveal";
    private const string HotkeyStatistics = "hotkey_statistics";

    private SingleInstance? _singleInstance;
    private FocusDatabase? _database;
    private SqliteTaskRepository? _taskRepository;
    private SqliteFocusSessionRepository? _sessionRepository;
    private SqliteManualTimeRepository? _manualTimeRepository;
    private SqliteSettingsStore? _settings;
    private SqliteActivityReader? _activityReader;
    private FocusEngine? _engine;
    private FocusSessionService? _focus;
    private JourneyActivityService? _journey;
    private StatisticsService? _statistics;
    private ThemeService? _theme;
    private ShellViewModel? _shell;
    private NotchWindow? _window;
    private TrayIconService? _tray;
    private HotkeyService? _hotkeys;
    private bool _hidden;
    private ChimePlayer? _chime;
    private DispatcherTimer? _tick;
    private bool _soundEnabled = true;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled non-UI exception.", args.ExceptionObject as Exception);

        // Regenerating the application icon. A build-time chore rather than a feature, but it
        // lives here so that the icon on the taskbar is produced by the same code that draws the
        // one in the tray, and cannot drift from it. See tools/New-AppIcon.ps1.
        if (WriteIconAndExit(e.Args))
        {
            return;
        }

        AppPaths.EnsureCreated();
        Diag.CaptureBindingFailures();
        Log.Info("Counter starting.");

        // If this copy is set to start with Windows, make sure the entry names this copy. A
        // portable run that later becomes a proper install would otherwise leave the setting
        // pointing at a file that may no longer exist.
        StartupService.RefreshPath();

        _singleInstance = SingleInstance.Acquire();
        if (!_singleInstance.IsFirstInstance)
        {
            Log.Info("Another instance is already running; asking it to reveal itself.");
            _singleInstance.SignalExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        var demo = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        if (!InitializeStorage(demo))
        {
            return;
        }

        BuildUi();
        RestoreSession();
        StartTicking();

        _singleInstance.ListenForSecondInstance(() =>
            Dispatcher.BeginInvoke(new Action(RevealWindow)));

        Log.Info("Counter ready.");
    }

    // =================================================================================
    // Storage
    // =================================================================================

    /// <summary>
    /// Handles <c>--write-icon &lt;path&gt;</c> and stops the application if it was asked for.
    ///
    /// Deliberately before anything else runs: no database is opened, no window is created and
    /// no single-instance lock is taken, so regenerating the icon while the application is
    /// running is harmless.
    /// </summary>
    private bool WriteIconAndExit(string[] args)
    {
        var index = Array.FindIndex(
            args, a => string.Equals(a, "--write-icon", StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return false;
        }

        if (index + 1 >= args.Length)
        {
            Shutdown(2);
            return true;
        }

        try
        {
            var path = System.IO.Path.GetFullPath(args[index + 1]);
            var directory = System.IO.Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllBytes(path, Branding.IconBytes());
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log.Error("Could not write the application icon.", ex);
            Shutdown(1);
        }

        return true;
    }

    private bool InitializeStorage(bool demo)
    {
        // A backup the user chose is swapped in here, before anything opens the file. Doing it
        // underneath a live connection is the one operation that could genuinely lose history,
        // so it is only ever done at this moment and only after the file has been checked.
        DataTransfer.ApplyPendingRestore();

        try
        {
            _database = new FocusDatabase(AppPaths.DatabasePath);
            _database.Migrate();
        }
        catch (DatabaseUnavailableException ex)
        {
            Log.Error("The database could not be opened.", ex);
            MessageBox.Show(
                ex.Message + Environment.NewLine + Environment.NewLine +
                "Your data file has not been modified. Details are in:" + Environment.NewLine +
                AppPaths.LogDirectory,
                "Counter",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(2);
            return false;
        }

        _taskRepository = new SqliteTaskRepository(_database);
        _sessionRepository = new SqliteFocusSessionRepository(_database);
        _manualTimeRepository = new SqliteManualTimeRepository(_database);
        _settings = new SqliteSettingsStore(_database);
        _activityReader = new SqliteActivityReader(_database);

        // A damaged file is reported and left exactly as it is. The one copy of somebody's
        // history is never replaced, rebuilt or deleted on a hunch.
        var integrity = DatabaseMaintenance.CheckIntegrity(_database);
        if (integrity is not null)
        {
            MessageBox.Show(
                "Counter found a problem in its data file and has not changed it." +
                Environment.NewLine + Environment.NewLine + integrity +
                Environment.NewLine + Environment.NewLine +
                "Recent backups are in:" + Environment.NewLine +
                DatabaseMaintenance.BackupDirectory,
                "Counter",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // A rotating local copy, at most one a day, seven kept. Taken through SQLite's own
        // backup API so it is consistent even though the live connection stays open.
        DatabaseMaintenance.BackupIfDue(_database, _settings, DateTime.UtcNow);

        if (demo)
        {
            var seeded = DemoData.SeedIfEmpty(_taskRepository, SystemClock.Instance);
            Log.Info(seeded
                ? "Demo tasks were written into the empty database."
                : "The database already contains tasks; demo content was not inserted.");
        }

        return true;
    }

    // =================================================================================
    // Composition
    // =================================================================================

    private void BuildUi()
    {
        // The theme is applied before the window exists, so the first frame is already correct
        // and nothing has to be repainted after it is shown.
        // The glass grain, generated once and cached. It has no theme and no accent - it is
        // texture rather than colour - so it is installed before the theme rather than with it.
        GlassNoise.Install(Resources);

        // A hairline is one physical pixel. The window recomputes this for whichever display it
        // is on, but the value has to exist before the first frame is measured.
        DpiService.Apply(Resources, 1.0);

        // The glass the panels are made of. Seeded before the theme so the very first template
        // to resolve finds a value rather than falling back to the property default and then
        // being corrected a frame later.
        Resources[ThemeService.MaterialKey] = GlassMaterials.Parse(_settings!.Get(SettingKeys.GlassMaterial));

        _theme = new ThemeService(_settings!, Resources);
        _theme.Initialize();
        _theme.Changed += () =>
        {
            _shell?.ReportTheme(_theme!.Preference);
            _window?.RefreshAccentVisuals();
            _window?.SyncBackdrop(_theme!.Material, _theme.IsLight);

            if (_window is not null)
            {
                _shell?.ReportGlassBlur(_window.Backdrop.BlurRefused);
            }
        };

        _engine = new FocusEngine(SystemClock.Instance);

        // One authority over the session, and one place that writes it. Every caller - the
        // notch, both task lists, the tray and the global shortcut - goes through this.
        _focus = new FocusSessionService(_engine, _sessionRepository!, SystemClock.Instance);
        _focus.PersistenceFailed += (message, ex) => Log.Error(message, ex);

        // The journey query is the one piece of database work that runs off the UI thread, so
        // it gets its own reader and its own connection.
        var scheduler = new DispatcherScheduler(Dispatcher);

        _journey = new JourneyActivityService(_activityReader!, SystemClock.Instance, scheduler);
        _statistics = new StatisticsService(_activityReader!, SystemClock.Instance, scheduler);

        _shell = new ShellViewModel(
            _taskRepository!,
            _manualTimeRepository!,
            _settings!,
            _focus,
            _journey,
            _statistics,
            _activityReader!,
            SystemClock.Instance,
            scheduler);

        _shell.FocusCompleted += OnFocusCompleted;

        // Every one of these follows the same rule the theme already did: the view model asks,
        // the application applies it because it owns the settings store and the window, and then
        // reports the value back. The tray and the settings panel are therefore two views of one
        // value rather than two copies of it, and neither can drift.
        _shell.ThemeRequested += ApplyTheme;
        _shell.AccentRequested += ApplyAccent;
        _shell.AccentPreviewRequested += PreviewAccent;
        _shell.GlassRequested += ApplyGlass;
        _shell.AlwaysOnTopRequested += ApplyAlwaysOnTop;
        _shell.OpenOnHoverRequested += ApplyOpenOnHover;
        _shell.StartWithWindowsRequested += ApplyStartWithWindows;
        _shell.SoundRequested += ApplySound;
        _shell.MonitorRequested += ApplyMonitor;
        _shell.DefaultDurationRequested += ApplyDefaultDuration;

        _shell.BackupRequested += CreateBackupNow;
        _shell.RestoreRequested += RestoreFromBackup;
        _shell.ExportRequested += ExportData;
        _shell.RevealDatabaseRequested += () => DataTransfer.Reveal(AppPaths.RootDirectory);

        _shell.ReportTheme(_theme.Preference);

        // The picker is loaded before the accent, so that a run wearing one of the six named
        // families still opens the editor on the colour it was last left at rather than on a
        // default nobody chose.
        _shell.ReportCustomAccent(_settings.Get(SettingKeys.CustomAccent) ?? DefaultCustomAccent);
        _shell.ReportAccent(_theme.Accent.Id);
        _shell.ReportGlass(_theme.Material);

        _shell.HideRequested += () => SetHidden(true);

        _shell.PlacementRequested += placement =>
        {
            _settings!.Set(SettingKeys.HorizontalPlacement, placement.ToString());
            _window!.SetPlacement(placement);
            _shell!.ReportPlacement(placement);
        };

        // Straight to the page that governs it. Telling somebody a setting exists and making
        // them find it are two different amounts of help.
        _shell.TransparencySettingRequested += () =>
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ms-settings:colors") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Could not open the Windows colour settings.", ex);
                _shell?.ReportError("Could not open Windows Settings. Look for Personalisation, Colours.");
            }
        };

        // Switching a shortcut off stores an empty gesture, which is what tells the service to
        // register nothing at all. Everything is rebuilt rather than patched, so the registered
        // set and the list on screen cannot drift apart.
        _shell.ShortcutRequested += (id, gesture) =>
        {
            _settings!.Set(id, gesture);
            BuildHotkeys();
        };

        // Restored last, and only after the window exists: starting hidden is a state the user
        // chose, and forgetting it means the notch reappears over their tabs on every restart.
        if (_settings.GetBool(SettingKeys.Hidden, false))
        {
            SetHidden(true);
        }

        // The setting can be changed from either the statistics panel or the tray, so the tray
        // follows the view model rather than the two keeping separate copies.
        _shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.StopTimerWhenTaskCompleted))
            {
                _tray?.SetStopTimerWhenTaskCompleted(_shell!.StopTimerWhenTaskCompleted);
            }
        };

        _soundEnabled = _settings!.GetBool(SettingKeys.SoundEnabled, true);
        var alwaysOnTop = _settings.GetBool(SettingKeys.AlwaysOnTop, true);
        var openOnHover = _settings.GetBool(SettingKeys.OpenOnHover, true);
        var topOffset = _settings.GetInt(SettingKeys.TopOffset, 0);
        var monitorDevice = _settings.Get(SettingKeys.MonitorDeviceName);
        var monitor = MonitorService.Resolve(monitorDevice);

        _chime = new ChimePlayer();

        _window = new NotchWindow { OpenOnHover = openOnHover };
        _window.Attach(_shell, monitor, topOffset, alwaysOnTop);
        _window.Show();

        // Whether a real blur is behind the panels decides how the glass is mixed, so the two
        // have to agree. The guard is what stops them agreeing forever: a repaint tells the
        // backdrop which material is chosen, the backdrop reports whether it managed a blur, and
        // that only asks for another repaint when the answer actually moved.
        _window.Backdrop.BlurChanged += () =>
        {
            if (_theme is null || _window is null || _theme.Blurred == _window.Backdrop.IsBlurred)
            {
                return;
            }

            _theme.Blurred = _window.Backdrop.IsBlurred;
            _theme.Repaint();
        };

        _window.SyncBackdrop(_theme!.Material, _theme.IsLight);
        _shell.ReportGlassBlur(_window.Backdrop.BlurRefused);

        var placement = NotchPlacements.Parse(_settings.Get(SettingKeys.HorizontalPlacement));
        _window.SetPlacement(placement);
        _shell.ReportPlacement(placement);

        Log.Info("Glass backdrop: " + AcrylicBackdrop.Method + ".");

        _shell.ReportBehaviour(alwaysOnTop, openOnHover, StartupService.IsEnabled(), _soundEnabled);
        _shell.ReportDefaultDuration(_shell.DefaultDurationSeconds);
        PublishMonitors(monitor.DeviceName);

        _shell.Load();

        BuildTray(alwaysOnTop, openOnHover, monitor);
        BuildHotkeys();
    }

    /// <summary>
    /// One place applies a theme, so the tray, the statistics panel and the resources can never
    /// disagree about which one is current.
    /// </summary>
    private void ApplyTheme(ThemePreference preference)
    {
        _theme?.Apply(preference);
        _shell?.ReportTheme(preference);
        _tray?.SetTheme(preference);
    }

    /// <summary>
    /// The glass. Independent of both the theme and the accent, and applied by the same repaint,
    /// so changing it never disturbs a running timer or rebuilds a window.
    /// </summary>
    private void ApplyGlass(GlassMaterial material)
    {
        _theme?.ApplyMaterial(material);
        _shell?.ReportGlass(material);
    }

    /// <summary>Where the custom picker starts on a run that has never used it.</summary>
    private const string DefaultCustomAccent = "#FFE5484D";

    /// <summary>
    /// The colour under the thumb, while the thumb is still moving.
    ///
    /// Applied but not stored. A drag is one decision expressed as several hundred mouse-moves,
    /// and only the decision belongs in the database.
    /// </summary>
    private void PreviewAccent(string id) => _theme?.ApplyAccent(id, persist: false);

    /// <summary>The accent family. Independent of the theme: switching one never resets the other.</summary>
    private void ApplyAccent(string id)
    {
        _theme?.ApplyAccent(id);
        _shell?.ReportAccent(id);
        _tray?.SetAccent(_theme?.Accent.Id ?? id);

        // A custom colour is remembered separately from the accent, so that trying Green for an
        // afternoon does not throw away the colour that was mixed before it.
        if (_theme?.Accent.Id is { } chosen
            && chosen.StartsWith(AccentPalettes.CustomPrefix, StringComparison.Ordinal))
        {
            _settings!.Set(
                SettingKeys.CustomAccent, chosen.Substring(AccentPalettes.CustomPrefix.Length));
        }
    }

    private void ApplyAlwaysOnTop(bool value)
    {
        _settings!.SetBool(SettingKeys.AlwaysOnTop, value);
        _window!.SetTopmost(value);
        _tray?.SetAlwaysOnTopChecked(value);
        PublishBehaviour();
    }

    private void ApplyOpenOnHover(bool value)
    {
        _settings!.SetBool(SettingKeys.OpenOnHover, value);
        _window!.OpenOnHover = value;
        _tray?.SetOpenOnHoverChecked(value);
        PublishBehaviour();
    }

    private void ApplyStartWithWindows(bool value)
    {
        if (!StartupService.SetEnabled(value))
        {
            _shell!.ReportError("Could not change the Windows startup setting.");
        }

        // Read back rather than assumed: the registry is the authority, and a refused write
        // must leave both the tray and the settings panel showing what is actually true.
        var actual = StartupService.IsEnabled();
        _tray?.SetStartWithWindowsChecked(actual);
        PublishBehaviour();
    }

    private void ApplySound(bool value)
    {
        _soundEnabled = value;
        _settings!.SetBool(SettingKeys.SoundEnabled, value);
        _tray?.SetSoundChecked(value);
        PublishBehaviour();
    }

    private void ApplyMonitor(string device)
    {
        _settings!.Set(SettingKeys.MonitorDeviceName, device);

        var resolved = MonitorService.Resolve(device);
        _window!.SetMonitor(resolved);
        _tray?.RefreshMonitors(MonitorService.GetMonitors(), resolved.DeviceName);
        PublishMonitors(resolved.DeviceName);
    }

    private void ApplyDefaultDuration(long seconds)
    {
        _settings!.SetInt(SettingKeys.DefaultDurationSeconds, (int)Math.Clamp(seconds, 0, int.MaxValue));
        _shell!.ReportDefaultDuration(seconds);
    }

    private void PublishBehaviour() => _shell?.ReportBehaviour(
        _settings!.GetBool(SettingKeys.AlwaysOnTop, true),
        _settings.GetBool(SettingKeys.OpenOnHover, true),
        StartupService.IsEnabled(),
        _soundEnabled);

    private void PublishMonitors(string? selected) => _shell?.ReportMonitors(
        MonitorService.GetMonitors().Select(m => (m.DeviceName, m.DisplayName)), selected);

    // =================================================================================
    // Data
    // =================================================================================

    private void CreateBackupNow()
    {
        try
        {
            var directory = DatabaseMaintenance.BackupDirectory;
            System.IO.Directory.CreateDirectory(directory);

            var name = "counter-" + DateTime.UtcNow.ToString(
                "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".db";

            _database!.BackupTo(System.IO.Path.Combine(directory, name));
            DatabaseMaintenance.Trim(directory);

            _shell!.ReportData("Backed up to " + directory + ".");
        }
        catch (Exception ex)
        {
            Log.Error("Could not write the backup.", ex);
            _shell!.ReportData("Could not write the backup.");
        }
    }

    /// <summary>
    /// Stages a chosen backup rather than swapping it in underneath a live connection. The swap
    /// happens at the next start, after the database being replaced has itself been copied, so
    /// picking the wrong file is a mistake rather than a loss.
    /// </summary>
    private void RestoreFromBackup()
    {
        var directory = DatabaseMaintenance.BackupDirectory;
        System.IO.Directory.CreateDirectory(directory);

        var dialog = new OpenFileDialog
        {
            Title = "Choose a Counter backup",
            InitialDirectory = directory,
            Filter = "Counter database (*.db)|*.db",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var problem = DataTransfer.StageRestore(dialog.FileName);

        _shell!.ReportData(problem
            ?? "Backup checked and ready. It is restored the next time Counter starts, and "
             + "the current data is kept as a backup first.");
    }

    private void ExportData()
    {
        try
        {
            var folder = DataTransfer.Export(_database!);
            DataTransfer.Reveal(folder);
            _shell!.ReportData("Exported to " + folder + ".");
        }
        catch (Exception ex)
        {
            Log.Error("Could not export the data.", ex);
            _shell!.ReportData("Could not export the data.");
        }
    }

    private void BuildTray(bool alwaysOnTop, bool openOnHover, MonitorInfo monitor)
    {
        _tray = new TrayIconService();

        _tray.OpenRequested += RevealWindow;
        _tray.ToggleFocusRequested += () => _shell!.ToggleFocus();
        _tray.StopFocusRequested += () => _shell!.StopFocus();
        _tray.StatisticsRequested += () =>
        {
            RevealWindow();
            _shell!.OpenStatistics();
        };
        _tray.ThemeChanged += ApplyTheme;
        _tray.AccentChanged += ApplyAccent;
        _tray.HiddenToggled += ToggleHidden;
        _tray.SettingsRequested += () =>
        {
            RevealWindow();
            _shell!.OpenSettings();
        };
        _tray.StopTimerWhenTaskCompletedChanged += value =>
            _shell!.StopTimerWhenTaskCompleted = value;
        _tray.NewTaskRequested += () =>
        {
            RevealWindow();
            _shell!.BeginAddTask();
        };
        _tray.QuitRequested += QuitApplication;

        // The tray calls the same appliers the settings panel does, so the two are two views of
        // one value rather than two code paths that have to be kept in step.
        _tray.AlwaysOnTopChanged += ApplyAlwaysOnTop;
        _tray.OpenOnHoverChanged += ApplyOpenOnHover;
        _tray.StartWithWindowsChanged += ApplyStartWithWindows;
        _tray.SoundChanged += ApplySound;
        _tray.MonitorChanged += ApplyMonitor;

        var state = new TrayState(
            alwaysOnTop,
            openOnHover,
            StartupService.IsEnabled(),
            _soundEnabled,
            _shell!.StopTimerWhenTaskCompleted,
            _theme!.Preference,
            _theme.Accent.Id,
            monitor.DeviceName);

        _tray.Initialize(state, MonitorService.GetMonitors());
        _tray.UpdateFocusState(_engine!.HasActiveSession, _engine.Current?.Status == FocusSessionStatus.Running);
    }

    /// <summary>
    /// The shortcuts, and what they default to when nothing has been stored.
    ///
    /// <para>
    /// Ctrl+Alt rather than Ctrl+Shift, and that is a bug fix rather than a preference. A global
    /// hotkey outranks every application shortcut on the machine, and Ctrl+Shift is where
    /// applications keep their own: Ctrl+Shift+N is a private window in every browser there is,
    /// Ctrl+Shift+S is Save As, Ctrl+Shift+F is find-in-files in most editors. Registering those
    /// does not fail and does not warn - it silently takes them away from every other program,
    /// which is precisely what happened.
    /// </para>
    /// </summary>
    public static readonly (string Id, string Default, string Description)[] HotkeyDefaults =
    {
        (HotkeyToggleFocus, "Ctrl+Alt+Space", "Start or pause focus"),
        (HotkeyNewTask, "Ctrl+Alt+N", "New task"),
        (HotkeyReveal, "Ctrl+Alt+C", "Show or hide Counter"),
        (HotkeyStatistics, "Ctrl+Alt+S", "Statistics")
    };

    /// <summary>The stored gesture, the default, or empty when the user turned it off.</summary>
    private string GestureFor(string id, string fallback)
    {
        var stored = _settings!.Get(id);

        if (stored is null)
        {
            return fallback;
        }

        // Stored-but-empty is off. Distinguishing that from never-stored is the whole reason
        // this reads the raw value rather than using the null-coalescing default.
        return stored.Trim();
    }

    private void BuildHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService();

        var actions = new Dictionary<string, Action>
        {
            [HotkeyToggleFocus] = () => _shell!.ToggleFocus(),
            [HotkeyNewTask] = () =>
            {
                RevealWindow();
                _shell!.BeginAddTask();
            },
            [HotkeyReveal] = ToggleHidden,
            [HotkeyStatistics] = () =>
            {
                RevealWindow();
                _shell!.OpenStatistics();
            }
        };

        var definitions = HotkeyDefaults
            .Select(entry => new HotkeyDefinition(
                entry.Id,
                GestureFor(entry.Id, entry.Default),
                entry.Description,
                actions[entry.Id]))
            .ToArray();

        _hotkeys.Register(definitions);

        _shell!.ReportShortcuts(HotkeyDefaults
            .Select(entry => new ShortcutViewModel(
                entry.Id,
                entry.Description,
                GestureFor(entry.Id, entry.Default),
                entry.Default))
            .ToList());

        if (_hotkeys.HasConflicts)
        {
            var conflicts = _hotkeys.Registrations
                .Where(r => !r.Succeeded)
                .Select(r => r.Gesture);

            var message = "These shortcuts are already used by another app: " +
                          string.Join(", ", conflicts) +
                          ". Counter keeps running without them.";

            Log.Info(message);
            _shell!.ReportError(message);
        }
    }

    // =================================================================================
    // Runtime
    // =================================================================================

    private void RestoreSession()
    {
        try
        {
            // Restore repairs storage first: if more than one session somehow survived as live,
            // the newest is kept and the others are cancelled with the time they had actually
            // accumulated. Nothing is deleted.
            var restored = _focus!.Restore();

            if (_focus.RepairsApplied > 0)
            {
                Log.Warn("Repaired " + _focus.RepairsApplied +
                         " duplicate active focus session(s) left in the database.");
            }

            if (_focus.SegmentsRepaired > 0)
            {
                Log.Warn("Closed " + _focus.SegmentsRepaired +
                         " focus run(s) left open by an unclean shutdown.");
            }

            if (restored is not null)
            {
                Log.Info("Restored a " + restored.Status + " focus session from the last run.");
            }

            // A session that ran out while the process was closed was finished at its saved
            // target instant, not at now, so exactly the planned time was credited. Say so once.
            if (_focus.CompletedWhileClosed is { } offline)
            {
                Log.Info("A focus session completed while Counter was closed.");
                _shell!.ReportCompletedWhileClosed(offline);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not restore the previous focus session.", ex);
            _shell!.ReportError("Could not restore the previous focus session.");
        }

        _shell!.Load();

        // Anything that was still being typed when the app last stopped comes back.
        if (_shell.RestoreDraft())
        {
            Log.Info("Restored an unsaved task draft.");
        }

        UpdateTrayFocusState();
    }

    private void StartTicking()
    {
        // 500 ms keeps the visible second accurate without any measurable CPU cost. The tick
        // only refreshes the display; the countdown itself is derived from an absolute instant.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };

        _tick.Tick += (_, _) =>
        {
            _shell!.Tick();
            UpdateTrayTooltip();
        };

        _tick.Start();
    }

    private void OnFocusCompleted(Core.Models.FocusSession session)
    {
        if (_soundEnabled)
        {
            _chime?.Play();
        }

        _window?.PlayCompletionPulse();
        _tray?.ShowNotification("Focus session complete", _shell!.CompletionTitle);
        UpdateTrayFocusState();
    }

    private void UpdateTrayFocusState()
    {
        _tray?.UpdateFocusState(
            _engine!.HasActiveSession,
            _engine.Current?.Status == FocusSessionStatus.Running);
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null || _shell is null)
        {
            return;
        }

        UpdateTrayFocusState();
        _tray.SetTooltip(_shell.HasSession
            ? "Counter - " + _shell.TimerText + " - " + _shell.ActiveTaskTitle
            : "Counter");
    }

    private void RevealWindow()
    {
        if (_window is null)
        {
            return;
        }

        SetHidden(false);
        _window.Reveal();
        _shell?.OpenQuickView();
    }

    /// <summary>
    /// Puts the notch away, or brings it back.
    ///
    /// It sits at the top centre of the screen, which is exactly where a browser keeps its tabs,
    /// so there has to be a way to get it out of the way that is faster than quitting. Hidden is
    /// a real state rather than a collapse: the window is not shown at all, the timer keeps
    /// running, and the tray icon is how it comes back.
    /// </summary>
    private void SetHidden(bool hidden)
    {
        if (_window is null || _hidden == hidden)
        {
            return;
        }

        _hidden = hidden;

        if (hidden)
        {
            _shell?.Collapse();
            _window.Hide();
        }
        else
        {
            _window.Show();
            _window.Reveal();
        }

        _settings?.SetBool(SettingKeys.Hidden, hidden);
        _tray?.SetHidden(hidden);
        _shell?.ReportHidden(hidden);
    }

    private void ToggleHidden() => SetHidden(!_hidden);

    // =================================================================================
    // Shutdown
    // =================================================================================

    private void QuitApplication()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        Log.Info("Counter shutting down.");

        _tick?.Stop();
        _tick = null;

        _hotkeys?.Dispose();
        _hotkeys = null;

        _tray?.Dispose();
        _tray = null;

        if (_shell is not null)
        {
            _shell.FocusCompleted -= OnFocusCompleted;
            _shell.ThemeRequested -= ApplyTheme;
            _shell.Dispose();
            _shell = null;
        }

        _theme?.Dispose();
        _theme = null;

        _focus?.Dispose();
        _focus = null;
        _journey = null;
        _statistics = null;

        _window?.ShutdownWindow();
        _window = null;

        _chime?.Dispose();
        _chime = null;

        _database?.Dispose();
        _database = null;

        _singleInstance?.Dispose();
        _singleInstance = null;

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shuttingDown)
        {
            _tick?.Stop();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _theme?.Dispose();
            _chime?.Dispose();
            _database?.Dispose();
            _singleInstance?.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the UI thread.", e.Exception);
        _shell?.ReportError("Something went wrong. Details are in " + AppPaths.LogDirectory + ".");
        e.Handled = true;
    }
}
