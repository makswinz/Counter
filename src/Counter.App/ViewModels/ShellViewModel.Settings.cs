using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Counter.App.Services;
using Counter.App.Theme;
using Counter.Core.Colour;
using Counter.Core.Focus;
using Counter.Core.Models;

namespace Counter.App.ViewModels;

/// <summary>One accent family as the settings panel shows it: a swatch and a name.</summary>
public sealed partial class AccentSwatchViewModel : ObservableObject
{
    public AccentSwatchViewModel(AccentPalette palette, Action<string> select, bool isCustom = false)
    {
        Palette = palette;
        IsCustom = isCustom;
        SelectCommand = new RelayCommand(() => select(palette.Id));
    }

    /// <summary>
    /// True for the one swatch that is not a family but a door.
    ///
    /// It behaves like the other six - it shows the colour it will apply and it selects when
    /// pressed - and it additionally opens the editor, because a swatch whose colour you can
    /// change has to say where you change it.
    /// </summary>
    public bool IsCustom { get; }

    public AccentPalette Palette { get; }

    public string Id => Palette.Id;

    public string DisplayName => Palette.DisplayName;

    /// <summary>
    /// The palette's one input colour. The swatch runs it through the same accent engine the
    /// theme does, so it paints the real gradient rather than an idea of it.
    /// </summary>
    public string BaseColour => _colour ?? Palette.Base;

    private string? _colour;

    /// <summary>
    /// Repaints the custom swatch as the picker moves. The six named families never move, so
    /// this only ever applies to the seventh.
    /// </summary>
    public void ReportColour(string colour)
    {
        _colour = colour;
        OnPropertyChanged(nameof(BaseColour));
    }

    public string AccessibleName => IsCustom ? "Custom accent" : DisplayName + " accent";

    [ObservableProperty]
    private bool _isSelected;

    public IRelayCommand SelectCommand { get; }
}

/// <summary>One display the notch can be anchored to.</summary>
public sealed partial class MonitorOptionViewModel : ObservableObject
{
    public MonitorOptionViewModel(string deviceName, string label, Action<string> select)
    {
        DeviceName = deviceName;
        Label = label;
        SelectCommand = new RelayCommand(() => select(deviceName));
    }

    public string DeviceName { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public IRelayCommand SelectCommand { get; }
}

/// <summary>
/// The settings surface of the shell.
///
/// Settings is a destination of its own, not a strip along the bottom of Statistics. The two
/// were the same place because the theme buttons happened to be put there, which meant changing
/// a colour required looking at a chart, and Statistics could never be a read-only view. They
/// are separate levels now, each with its own command, tooltip, accessible name and selected
/// state, and opening either one closes the other.
///
/// Everything here follows the pattern the theme already used: the view model raises a request,
/// the application applies it because it owns the settings store and the window, and then
/// reports the value back. No view model writes a Windows setting itself.
/// </summary>
public sealed partial class ShellViewModel
{
    // =================================================================================
    // Accent
    // =================================================================================

    /// <summary>The six accent families, in the order the panel shows them.</summary>
    public ObservableCollection<AccentSwatchViewModel> Accents { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentName))]
    private string _accentId = AccentPalettes.DefaultId;

    /// <summary>The name of the active family, shown under the live gradient preview.</summary>
    public string AccentName => AccentPalettes.Parse(AccentId).DisplayName;

    /// <summary>Raised when the user picks an accent. The host applies it and reports back.</summary>
    public event Action<string>? AccentRequested;

    /// <summary>Called by the host once an accent has actually been applied.</summary>
    public void ReportAccent(string id)
    {
        AccentId = AccentPalettes.Parse(id).Id;

        var custom = AccentId.StartsWith(AccentPalettes.CustomPrefix, StringComparison.Ordinal);

        foreach (var swatch in Accents)
        {
            // The custom swatch's own identifier moves with its colour, so it is selected by
            // being the custom one rather than by matching a string that changes underneath it.
            swatch.IsSelected = swatch.IsCustom
                ? custom
                : string.Equals(swatch.Id, AccentId, StringComparison.Ordinal);
        }

        if (custom)
        {
            ReportCustomAccent(AccentId.Substring(AccentPalettes.CustomPrefix.Length));
        }
    }

    private void BuildAccents()
    {
        Accents.Clear();

        foreach (var palette in AccentPalettes.All)
        {
            Accents.Add(new AccentSwatchViewModel(palette, id => AccentRequested?.Invoke(id)));
        }

        // The seventh. It carries whatever the picker last held, and pressing it both applies
        // that colour and opens the editor, so there is one thing to find rather than two.
        _customSwatch = new AccentSwatchViewModel(
            AccentPalettes.Custom(CustomHex), _ => OpenCustomAccent(), isCustom: true);

        Accents.Add(_customSwatch);

        ReportAccent(AccentId);
    }

    // =================================================================================
    // The custom accent
    // =================================================================================

    private AccentSwatchViewModel? _customSwatch;

    /// <summary>
    /// True while the view model is writing its own coordinates, so that reporting a colour
    /// back does not look like the user having moved a strip and ask for it all over again.
    /// </summary>
    private bool _settingCustom;

    /// <summary>Whether the three strips are showing.</summary>
    [ObservableProperty]
    private bool _isCustomAccentOpen;

    /// <summary>
    /// The colour as text, in both directions: it shows what the strips are pointing at, and
    /// typing into it moves them. Anything a person would reasonably write is accepted - a
    /// name, three digits, six, eight, hash or no hash - and anything else is simply not
    /// applied, because a half-typed colour is what a field being typed into looks like.
    /// </summary>
    [ObservableProperty]
    private string _customText = "#E5484D";

    /// <summary>The canonical eight-digit form. What is stored, and what the engine is handed.</summary>
    [ObservableProperty]
    private string _customHex = "#FFE5484D";

    [ObservableProperty]
    private double _customLightness = 0.60;

    [ObservableProperty]
    private double _customChroma = 0.16;

    [ObservableProperty]
    private double _customHue = 25;

    /// <summary>
    /// Raised continuously while a strip is dragged.
    ///
    /// The interface follows the thumb, which is the entire point of a picker: a colour you can
    /// only judge after committing it is a colour you will commit four times. The host applies
    /// it without writing it down; <see cref="AccentRequested"/> is what writes it down.
    /// </summary>
    public event Action<string>? AccentPreviewRequested;

    [RelayCommand]
    private void OpenCustomAccent()
    {
        IsCustomAccentOpen = true;
        AccentRequested?.Invoke(AccentPalettes.CustomPrefix + CustomHex);
    }

    [RelayCommand]
    private void CloseCustomAccent() => IsCustomAccentOpen = false;

    /// <summary>Applies where the strips are now, and stores it. Raised when a drag ends.</summary>
    [RelayCommand]
    private void CommitCustomAccent() => AccentRequested?.Invoke(AccentPalettes.CustomPrefix + CustomHex);

    /// <summary>Reads the text field. Refuses quietly rather than fighting somebody mid-word.</summary>
    [RelayCommand]
    private void ApplyCustomText()
    {
        if (!ColourInput.TryNormalise(CustomText, out var hex))
        {
            // Put back what is actually selected, so the field never keeps a value the
            // interface is not wearing.
            CustomText = Shorten(CustomHex);
            return;
        }

        ReportCustomAccent(hex);
        AccentRequested?.Invoke(AccentPalettes.CustomPrefix + CustomHex);
    }

    /// <summary>
    /// Sets the picker to one colour: the coordinates, the text, the swatch and the stored form,
    /// all from the single value, so no two of them can disagree.
    /// </summary>
    public void ReportCustomAccent(string colour)
    {
        if (!ColourInput.TryNormalise(colour, out var hex))
        {
            return;
        }

        var oklch = Perceptual.FromHex(hex);

        _settingCustom = true;

        CustomHex = hex;
        CustomText = Shorten(hex);
        CustomLightness = Math.Clamp(
            oklch.L, AccentEngine.MinimumBaseLightness, AccentEngine.MaximumBaseLightness);
        CustomChroma = Math.Clamp(oklch.C, 0, 0.22);
        CustomHue = ((oklch.H * 180 / Math.PI) % 360 + 360) % 360;

        _settingCustom = false;

        _customSwatch?.ReportColour(hex);
    }

    /// <summary>
    /// Rebuilds the colour whenever a strip moves, and shows it immediately without storing it.
    /// </summary>
    private void OnCustomCoordinateChanged()
    {
        if (_settingCustom)
        {
            return;
        }

        var hex = Perceptual.ToHex(
            new Oklch(CustomLightness, CustomChroma, CustomHue * Math.PI / 180));

        _settingCustom = true;
        CustomHex = hex;
        CustomText = Shorten(hex);
        _settingCustom = false;

        _customSwatch?.ReportColour(hex);
        AccentPreviewRequested?.Invoke(AccentPalettes.CustomPrefix + hex);
    }

    partial void OnCustomLightnessChanged(double value) => OnCustomCoordinateChanged();

    partial void OnCustomChromaChanged(double value) => OnCustomCoordinateChanged();

    partial void OnCustomHueChanged(double value) => OnCustomCoordinateChanged();

    /// <summary>The six-digit form, which is what a person reads and writes.</summary>
    private static string Shorten(string hex) =>
        hex.Length == 9 && hex.StartsWith('#') ? "#" + hex.Substring(3) : hex;

    // =================================================================================
    // Behaviour
    // =================================================================================

    [ObservableProperty]
    private bool _alwaysOnTop = true;

    [ObservableProperty]
    private bool _openOnHover = true;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _soundEnabled = true;

    public event Action<bool>? AlwaysOnTopRequested;

    public event Action<bool>? OpenOnHoverRequested;

    public event Action<bool>? StartWithWindowsRequested;

    public event Action<bool>? SoundRequested;

    [RelayCommand]
    private void ToggleAlwaysOnTop() => AlwaysOnTopRequested?.Invoke(!AlwaysOnTop);

    [RelayCommand]
    private void ToggleOpenOnHover() => OpenOnHoverRequested?.Invoke(!OpenOnHover);

    [RelayCommand]
    private void ToggleStartWithWindows() => StartWithWindowsRequested?.Invoke(!StartWithWindows);

    [RelayCommand]
    private void ToggleSound() => SoundRequested?.Invoke(!SoundEnabled);

    /// <summary>Called by the host after it has written and applied one of these.</summary>
    public void ReportBehaviour(bool alwaysOnTop, bool openOnHover, bool startWithWindows, bool soundEnabled)
    {
        AlwaysOnTop = alwaysOnTop;
        OpenOnHover = openOnHover;
        StartWithWindows = startWithWindows;
        SoundEnabled = soundEnabled;
    }

    // =================================================================================
    // Monitor
    // =================================================================================

    public ObservableCollection<MonitorOptionViewModel> Monitors { get; } = new();

    [ObservableProperty]
    private string _monitorLabel = "Primary display";

    public event Action<string>? MonitorRequested;

    /// <summary>
    /// Replaces the list of displays. Called at start-up and whenever Windows reports the
    /// arrangement has changed, so unplugging a screen cannot leave a dead entry selected.
    /// </summary>
    public void ReportMonitors(IEnumerable<(string DeviceName, string Label)> monitors, string? selected)
    {
        Monitors.Clear();

        foreach (var (device, label) in monitors)
        {
            Monitors.Add(new MonitorOptionViewModel(device, label, d => MonitorRequested?.Invoke(d))
            {
                IsSelected = string.Equals(device, selected, StringComparison.Ordinal)
            });
        }

        MonitorLabel = Monitors.FirstOrDefault(m => m.IsSelected)?.Label
                       ?? Monitors.FirstOrDefault()?.Label
                       ?? "Primary display";
    }

    // =================================================================================
    // Focus defaults
    // =================================================================================

    /// <summary>
    /// The duration a brand new task starts with. Edited with the same three-column control the
    /// task picker uses, so hours, minutes and seconds behave identically in both places.
    /// </summary>
    public DurationPickerViewModel DefaultDuration { get; } = new();

    public string DefaultDurationLabel => TimeFormat.Compact(DefaultDurationSeconds);

    /// <summary>Raised when the default duration has been edited into a valid value.</summary>
    public event Action<long>? DefaultDurationRequested;

    private bool _loadingDefaultDuration;

    private void AttachDefaultDuration()
    {
        DefaultDuration.Load(DefaultDurationSeconds);
        DefaultDuration.PropertyChanged += OnDefaultDurationChanged;
    }

    private void OnDefaultDurationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loadingDefaultDuration || e.PropertyName != nameof(DurationPickerViewModel.TotalSeconds))
        {
            return;
        }

        if (!DefaultDuration.CanStart)
        {
            // An intermediate value while somebody is still typing. Nothing is written until it
            // is a duration the timer would actually accept.
            return;
        }

        DefaultDurationRequested?.Invoke(DefaultDuration.TotalSeconds);
    }

    /// <summary>Called by the host once the default has been stored.</summary>
    public void ReportDefaultDuration(long seconds)
    {
        DefaultDurationSeconds = (int)Math.Clamp(seconds, 0, int.MaxValue);

        _loadingDefaultDuration = true;
        DefaultDuration.Load(DefaultDurationSeconds);
        _loadingDefaultDuration = false;

        OnPropertyChanged(nameof(DefaultDurationLabel));
    }

    // =================================================================================
    // Data
    // =================================================================================

    /// <summary>Where the database actually is, shown in full so it can be found and copied.</summary>
    public string DatabasePath => AppPaths.DatabasePath;

    /// <summary>The folder the rotating backups are written to.</summary>
    public string BackupDirectory => DatabaseMaintenance.BackupDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataMessage))]
    private string _dataMessage = string.Empty;

    public bool HasDataMessage => !string.IsNullOrEmpty(DataMessage);

    public event Action? BackupRequested;

    public event Action? RestoreRequested;

    public event Action? ExportRequested;

    public event Action? RevealDatabaseRequested;

    [RelayCommand]
    private void CreateBackup() => BackupRequested?.Invoke();

    [RelayCommand]
    private void RestoreBackup() => RestoreRequested?.Invoke();

    [RelayCommand]
    private void ExportData() => ExportRequested?.Invoke();

    [RelayCommand]
    private void RevealDatabase() => RevealDatabaseRequested?.Invoke();

    /// <summary>One line of feedback under the Data section. Never a dialog.</summary>
    public void ReportData(string message)
    {
        DataMessage = message;
        ContentSizeChanged?.Invoke();
    }

    // =================================================================================
    // Navigation
    // =================================================================================

    /// <summary>
    /// Where Back returns to. Recorded on the way in, so leaving Statistics from the planner
    /// goes back to the planner and leaving it from the quick view goes back to the quick view.
    /// </summary>
    private PanelLevel _returnLevel = PanelLevel.Quick;

    public bool IsSettingsVisible => Overlay.Level == PanelLevel.Settings;

    [RelayCommand]
    public void OpenSettings()
    {
        RememberReturnLevel();
        Overlay.Pin();
        Overlay.RequestLevel(PanelLevel.Settings, TransitionReason.Command);
    }

    [RelayCommand]
    public void CloseSettings() => Back();

    /// <summary>
    /// Statistics and Settings are separate destinations with separate commands, but each
    /// button is also the way out of the place it leads to.
    /// </summary>
    [RelayCommand]
    public void ToggleSettings()
    {
        if (IsSettingsVisible)
        {
            Back();
            return;
        }

        OpenSettings();
    }

    [RelayCommand]
    public void ToggleStatistics()
    {
        if (IsStatisticsVisible)
        {
            Back();
            return;
        }

        OpenStatistics();
    }

    /// <summary>Returns to whichever panel Statistics or Settings was opened from.</summary>
    [RelayCommand]
    public void Back()
    {
        var target = Overlay.Level is PanelLevel.Statistics or PanelLevel.Settings
            ? _returnLevel
            : PanelLevel.Quick;

        Overlay.RequestLevel(target, TransitionReason.Command);
    }

    private void RememberReturnLevel()
    {
        if (Overlay.Level is PanelLevel.Quick or PanelLevel.Planner)
        {
            _returnLevel = Overlay.Level;
        }
    }
}
