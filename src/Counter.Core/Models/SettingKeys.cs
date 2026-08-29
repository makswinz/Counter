namespace Counter.Core.Models;

public static class SettingKeys
{
    public const string AlwaysOnTop = "always_on_top";
    public const string OpenOnHover = "open_on_hover";
    public const string SoundEnabled = "sound_enabled";
    public const string MonitorDeviceName = "monitor_device_name";
    public const string TopOffset = "top_offset";
    public const string DefaultDurationSeconds = "default_duration_seconds";
    public const string SchemaSeeded = "schema_seeded";

    /// <summary>Light, Dark or System. System on first run.</summary>
    public const string Theme = "theme";

    /// <summary>
    /// The identifier of the chosen accent family, and nothing else. The gradients are derived
    /// from it, so a stored value can never describe a colour combination that was not designed.
    /// </summary>
    public const string AccentPalette = "accent_palette";

    /// <summary>Which glass the panels are made of: Solid, Frosted or Liquid.</summary>
    public const string GlassMaterial = "glass_material";

    /// <summary>
    /// The last colour chosen in the custom accent editor, so reopening it lands where it was
    /// left even after a spell on one of the named families. The accent itself is still decided
    /// entirely by <see cref="AccentPalette"/>; this only remembers where the picker was.
    /// </summary>
    public const string CustomAccent = "custom_accent";

    /// <summary>Stops a running or paused session when its own task is ticked off. On by default.</summary>
    public const string StopTimerWhenTaskCompleted = "stop_timer_when_task_completed";

    /// <summary>The planner's last selected calendar day, so reopening lands where you left it.</summary>
    public const string LastSelectedDate = "last_selected_date";

    /// <summary>Day or Unscheduled.</summary>
    public const string LastPlannerFilter = "last_planner_filter";

    /// <summary>Today, Last7, Last30 or AllTime.</summary>
    public const string StatisticsRange = "statistics_range";

    // The unsaved task editor, written after a short debounce so a crash cannot lose typing.
    public const string DraftTitle = "draft_title";
    public const string DraftNote = "draft_note";
    public const string DraftCompleted = "draft_completed";
    public const string DraftEditingTaskId = "draft_editing_task_id";
    public const string DraftScheduledDate = "draft_scheduled_date";

    /// <summary>When the rotating local backup last ran, so it runs at most once a day.</summary>
    public const string LastBackupUtc = "last_backup_utc";
}
