using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Counter.Core.Models;

namespace Counter.App.Services;

public sealed record TrayState(
    bool AlwaysOnTop,
    bool OpenOnHover,
    bool StartWithWindows,
    bool SoundEnabled,
    bool StopTimerWhenTaskCompleted,
    ThemePreference Theme,
    string AccentId,
    string? MonitorDeviceName);

/// <summary>
/// The tray icon, its menu and Windows notifications. The icon is rendered from the application
/// mark at whatever size the shell asks for, so the tray and the taskbar can never disagree
/// about what this application looks like, and its native handle is destroyed on shutdown.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _focusItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _newTaskItem;
    private readonly ToolStripMenuItem _accentCustomItem = new("Custom colour...");
    private readonly ToolStripMenuItem _statisticsItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _themeItem;
    private readonly ToolStripMenuItem _themeSystemItem;
    private readonly ToolStripMenuItem _themeLightItem;
    private readonly ToolStripMenuItem _themeDarkItem;
    private readonly ToolStripMenuItem _accentItem;
    private readonly Dictionary<string, ToolStripMenuItem> _accentItems = new(StringComparer.Ordinal);
    private readonly ToolStripMenuItem _stopOnCompleteItem;
    private readonly ToolStripMenuItem _alwaysOnTopItem;
    private readonly ToolStripMenuItem _openOnHoverItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly ToolStripMenuItem _monitorItem;
    private readonly ToolStripMenuItem _soundItem;
    private readonly ToolStripMenuItem _quitItem;

    private Icon? _icon;
    private IntPtr _iconHandle = IntPtr.Zero;
    private bool _suppressEvents;
    private bool _disposed;

    public TrayIconService()
    {
        _openItem = new ToolStripMenuItem("Open Counter");
        _focusItem = new ToolStripMenuItem("Start focus");
        _stopItem = new ToolStripMenuItem("Stop focus") { Enabled = false };
        _newTaskItem = new ToolStripMenuItem("New task");
        _statisticsItem = new ToolStripMenuItem("Statistics");
        _settingsItem = new ToolStripMenuItem("Settings");
        _themeItem = new ToolStripMenuItem("Theme");
        _accentItem = new ToolStripMenuItem("Accent colour");
        _themeSystemItem = new ToolStripMenuItem("System");
        _themeLightItem = new ToolStripMenuItem("Light");
        _themeDarkItem = new ToolStripMenuItem("Dark");
        _stopOnCompleteItem = new ToolStripMenuItem("Stop the timer when a task is completed")
        {
            CheckOnClick = true
        };
        _alwaysOnTopItem = new ToolStripMenuItem("Always on top") { CheckOnClick = true };
        _openOnHoverItem = new ToolStripMenuItem("Open on hover") { CheckOnClick = true };
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _monitorItem = new ToolStripMenuItem("Monitor");
        _soundItem = new ToolStripMenuItem("Sound") { CheckOnClick = true };
        _quitItem = new ToolStripMenuItem("Quit");

        _openItem.Click += (_, _) => Raise(OpenRequested);
        _focusItem.Click += (_, _) => Raise(ToggleFocusRequested);
        _stopItem.Click += (_, _) => Raise(StopFocusRequested);
        _newTaskItem.Click += (_, _) => Raise(NewTaskRequested);
        _statisticsItem.Click += (_, _) => Raise(StatisticsRequested);
        _settingsItem.Click += (_, _) => Raise(SettingsRequested);

        _themeSystemItem.Click += (_, _) => RaiseTheme(ThemePreference.System);
        _themeLightItem.Click += (_, _) => RaiseTheme(ThemePreference.Light);
        _themeDarkItem.Click += (_, _) => RaiseTheme(ThemePreference.Dark);
        _themeItem.DropDownItems.AddRange(new ToolStripItem[]
        {
            _themeSystemItem, _themeLightItem, _themeDarkItem
        });

        // The same six families the settings panel offers, so neither surface can present a
        // choice the other does not have.
        foreach (var palette in AccentPalettes.All)
        {
            var entry = new ToolStripMenuItem(palette.DisplayName);
            var id = palette.Id;
            entry.Click += (_, _) => RaiseAccent(id);

            _accentItems[id] = entry;
            _accentItem.DropDownItems.Add(entry);
        }

        // The seventh choice is a colour rather than a family, and a tray menu is no place to
        // mix one. The entry is here so the menu tells the truth about what is selected, and
        // pressing it goes where the mixing is done.
        _accentItem.DropDownItems.Add(new ToolStripSeparator());
        _accentCustomItem.Click += (_, _) => Raise(SettingsRequested);
        _accentItem.DropDownItems.Add(_accentCustomItem);
        _quitItem.Click += (_, _) => Raise(QuitRequested);

        _alwaysOnTopItem.CheckedChanged += (_, _) => RaiseToggle(AlwaysOnTopChanged, _alwaysOnTopItem.Checked);
        _openOnHoverItem.CheckedChanged += (_, _) => RaiseToggle(OpenOnHoverChanged, _openOnHoverItem.Checked);
        _startWithWindowsItem.CheckedChanged += (_, _) => RaiseToggle(StartWithWindowsChanged, _startWithWindowsItem.Checked);
        _soundItem.CheckedChanged += (_, _) => RaiseToggle(SoundChanged, _soundItem.Checked);
        _stopOnCompleteItem.CheckedChanged += (_, _) =>
            RaiseToggle(StopTimerWhenTaskCompletedChanged, _stopOnCompleteItem.Checked);

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _openItem,
            _focusItem,
            _stopItem,
            _newTaskItem,
            _statisticsItem,
            _settingsItem,
            new ToolStripSeparator(),
            _themeItem,
            _accentItem,
            _alwaysOnTopItem,
            _openOnHoverItem,
            _startWithWindowsItem,
            _stopOnCompleteItem,
            _monitorItem,
            _soundItem,
            new ToolStripSeparator(),
            _quitItem
        });

        _notifyIcon = new NotifyIcon
        {
            Text = "Counter",
            ContextMenuStrip = _menu,
            Visible = false
        };

        _notifyIcon.MouseClick += OnIconMouseClick;
        _notifyIcon.BalloonTipClicked += (_, _) => Raise(OpenRequested);
    }

    public event Action? OpenRequested;
    public event Action? ToggleFocusRequested;
    public event Action? StopFocusRequested;
    public event Action? NewTaskRequested;
    public event Action? StatisticsRequested;
    public event Action? SettingsRequested;
    public event Action<ThemePreference>? ThemeChanged;
    public event Action<string>? AccentChanged;
    public event Action<bool>? StopTimerWhenTaskCompletedChanged;
    public event Action? QuitRequested;
    public event Action<bool>? AlwaysOnTopChanged;
    public event Action<bool>? OpenOnHoverChanged;
    public event Action<bool>? StartWithWindowsChanged;
    public event Action<bool>? SoundChanged;
    public event Action<string>? MonitorChanged;

    public void Initialize(TrayState state, IReadOnlyList<MonitorInfo> monitors)
    {
        _suppressEvents = true;
        _alwaysOnTopItem.Checked = state.AlwaysOnTop;
        _openOnHoverItem.Checked = state.OpenOnHover;
        _startWithWindowsItem.Checked = state.StartWithWindows;
        _soundItem.Checked = state.SoundEnabled;
        _stopOnCompleteItem.Checked = state.StopTimerWhenTaskCompleted;
        _suppressEvents = false;

        SetTheme(state.Theme);

        SetAccent(state.AccentId);
        RefreshMonitors(monitors, state.MonitorDeviceName);

        _icon = CreateIcon();
        _notifyIcon.Icon = _icon;
        _notifyIcon.Visible = true;
    }

    /// <summary>Keeps the "Start or pause focus" item labelled for what it will actually do.</summary>
    public void UpdateFocusState(bool hasSession, bool isRunning)
    {
        _focusItem.Text = !hasSession
            ? "Start focus"
            : isRunning ? "Pause focus" : "Resume focus";

        // Stop is only offered when there is something to stop, running or paused.
        _stopItem.Enabled = hasSession;
    }

    /// <summary>Ticks whichever of the three theme entries is current.</summary>
    public void SetTheme(ThemePreference preference)
    {
        _suppressEvents = true;
        _themeSystemItem.Checked = preference == ThemePreference.System;
        _themeLightItem.Checked = preference == ThemePreference.Light;
        _themeDarkItem.Checked = preference == ThemePreference.Dark;
        _suppressEvents = false;
    }

    /// <summary>Ticks whichever accent family is current.</summary>
    public void SetAccent(string id)
    {
        _suppressEvents = true;

        var custom = id.StartsWith(AccentPalettes.CustomPrefix, StringComparison.Ordinal);
        _accentCustomItem.Checked = custom;

        foreach (var (key, item) in _accentItems)
        {
            item.Checked = !custom && string.Equals(key, id, StringComparison.Ordinal);
        }

        _suppressEvents = false;
    }

    public void SetStopTimerWhenTaskCompleted(bool value)
    {
        _suppressEvents = true;
        _stopOnCompleteItem.Checked = value;
        _suppressEvents = false;
    }

    public void SetAlwaysOnTopChecked(bool value)
    {
        _suppressEvents = true;
        _alwaysOnTopItem.Checked = value;
        _suppressEvents = false;
    }

    public void SetOpenOnHoverChecked(bool value)
    {
        _suppressEvents = true;
        _openOnHoverItem.Checked = value;
        _suppressEvents = false;
    }

    public void SetSoundChecked(bool value)
    {
        _suppressEvents = true;
        _soundItem.Checked = value;
        _suppressEvents = false;
    }

    private void RaiseTheme(ThemePreference preference)
    {
        if (_suppressEvents)
        {
            return;
        }

        try
        {
            ThemeChanged?.Invoke(preference);
        }
        catch (Exception ex)
        {
            Log.Error("A tray theme action failed.", ex);
        }
    }

    private void RaiseAccent(string id)
    {
        if (_suppressEvents)
        {
            return;
        }

        try
        {
            AccentChanged?.Invoke(id);
        }
        catch (Exception ex)
        {
            Log.Error("A tray accent action failed.", ex);
        }
    }

    public void SetStartWithWindowsChecked(bool value)
    {
        _suppressEvents = true;
        _startWithWindowsItem.Checked = value;
        _suppressEvents = false;
    }

    /// <summary>
    /// Empties a submenu and refills it, disposing whatever was there before.
    /// <para>
    /// The detaching and the disposing have to happen in that order and off a snapshot, because
    /// disposing a <see cref="ToolStripItem"/> removes it from its owner: doing it inside a
    /// <c>foreach</c> over the live collection modifies what is being enumerated and throws.
    /// </para>
    /// </summary>
    public static void ReplaceDropDown(ToolStripDropDownItem owner, IEnumerable<ToolStripItem> items)
    {
        var previous = owner.DropDownItems.Cast<ToolStripItem>().ToArray();
        owner.DropDownItems.Clear();

        foreach (var item in previous)
        {
            item.Dispose();
        }

        foreach (var item in items)
        {
            owner.DropDownItems.Add(item);
        }
    }

    public void RefreshMonitors(IReadOnlyList<MonitorInfo> monitors, string? selectedDeviceName)
    {
        var entries = new List<ToolStripItem>();

        foreach (var monitor in monitors)
        {
            var entry = new ToolStripMenuItem(monitor.DisplayName)
            {
                Checked = string.Equals(monitor.DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase)
                          || (string.IsNullOrEmpty(selectedDeviceName) && monitor.IsPrimary),
                Tag = monitor.DeviceName
            };

            entry.Click += (sender, _) =>
            {
                if (sender is ToolStripMenuItem { Tag: string device })
                {
                    Raise(() => MonitorChanged?.Invoke(device));
                }
            };

            entries.Add(entry);
        }

        ReplaceDropDown(_monitorItem, entries);
    }

    public void ShowNotification(string title, string message)
    {
        if (_disposed || !_notifyIcon.Visible)
        {
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = ToolTipIcon.None;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            // Notifications can be suppressed by focus assist or policy. Never fail the session.
            Log.Warn("Could not show a Windows notification.", ex);
        }
    }

    public void SetTooltip(string text)
    {
        if (_disposed)
        {
            return;
        }

        // NotifyIcon.Text is limited to 63 characters plus a terminator.
        _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
    }

    private void OnIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Raise(OpenRequested);
        }
    }

    private void Raise(Action? handler)
    {
        if (_suppressEvents)
        {
            return;
        }

        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("A tray menu action failed.", ex);
        }
    }

    private void RaiseToggle(Action<bool>? handler, bool value)
    {
        if (_suppressEvents)
        {
            return;
        }

        try
        {
            handler?.Invoke(value);
        }
        catch (Exception ex)
        {
            Log.Error("A tray toggle action failed.", ex);
        }
    }

    /// <summary>
    /// The tray icon: the application's own mark, at the size the shell asked for.
    ///
    /// Drawn from <see cref="Branding"/> rather than here, so the icon in the tray, the icon on
    /// the taskbar and the icon in the Start menu are the same drawing rather than three
    /// drawings that happen to look alike until one of them is edited.
    /// </summary>
    private Icon CreateIcon()
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        using var bitmap = Branding.Render(size);

        _iconHandle = bitmap.GetHicon();

        // Clone so the Icon owns managed data and the native handle can be freed on dispose.
        using var temp = Icon.FromHandle(_iconHandle);
        return (Icon)temp.Clone();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _notifyIcon.MouseClick -= OnIconMouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon = null;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon?.Dispose();
        _icon = null;

        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }
}
