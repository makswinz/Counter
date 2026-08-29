using Counter.Core.Abstractions;
using Counter.Core.Models;

namespace Counter.Core.Drafts;

/// <summary>Whatever is currently typed into the task editor and not yet saved.</summary>
public sealed record TaskDraft(
    string Title,
    string Note,
    bool IsCompleted,
    Guid? EditingTaskId,
    DateOnly? ScheduledDate)
{
    public static readonly TaskDraft Empty = new(string.Empty, string.Empty, false, null, null);

    public bool HasContent => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Note);
}

/// <summary>
/// Keeps the unsaved task editor alive across a crash or a quit.
///
/// It is written after a short pause in typing rather than on every keystroke, so a long note
/// costs a handful of writes instead of hundreds, and it is cleared the moment the draft is
/// either saved or deliberately abandoned - a recovery prompt for something the user already
/// dealt with is worse than no recovery at all.
/// </summary>
public sealed class DraftStore
{
    private readonly ISettingsStore _settings;

    public DraftStore(ISettingsStore settings) => _settings = settings;

    public void Save(TaskDraft draft)
    {
        _settings.Set(SettingKeys.DraftTitle, draft.Title);
        _settings.Set(SettingKeys.DraftNote, draft.Note);
        _settings.SetBool(SettingKeys.DraftCompleted, draft.IsCompleted);
        _settings.Set(
            SettingKeys.DraftEditingTaskId,
            draft.EditingTaskId?.ToString("D") ?? string.Empty);
        _settings.Set(
            SettingKeys.DraftScheduledDate,
            draft.ScheduledDate?.ToString("yyyy-MM-dd") ?? string.Empty);
    }

    public TaskDraft Load()
    {
        var title = _settings.Get(SettingKeys.DraftTitle) ?? string.Empty;
        var note = _settings.Get(SettingKeys.DraftNote) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(note))
        {
            return TaskDraft.Empty;
        }

        Guid? editing = Guid.TryParse(_settings.Get(SettingKeys.DraftEditingTaskId), out var id) ? id : null;

        DateOnly? scheduled =
            DateOnly.TryParse(_settings.Get(SettingKeys.DraftScheduledDate), out var day) ? day : null;

        return new TaskDraft(
            title, note, _settings.GetBool(SettingKeys.DraftCompleted, false), editing, scheduled);
    }

    public void Clear() => Save(TaskDraft.Empty);
}
