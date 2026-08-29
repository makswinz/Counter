using FocusNotch.Core.Models;

namespace FocusNotch.Core.Validation;

public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok() => new(true, null);

    public static ValidationResult Fail(string error) => new(false, error);
}

public static class TaskValidator
{
    public const int MaxTitleLength = 140;
    public const int MaxNoteLength = 500;

    public static ValidationResult ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return ValidationResult.Fail("A title is required.");
        }

        return title.Trim().Length > MaxTitleLength
            ? ValidationResult.Fail("Keep the title under " + MaxTitleLength + " characters.")
            : ValidationResult.Ok();
    }

    public static ValidationResult ValidateNote(string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return ValidationResult.Ok();
        }

        return note.Length > MaxNoteLength
            ? ValidationResult.Fail("Keep the note under " + MaxNoteLength + " characters.")
            : ValidationResult.Ok();
    }

    public static ValidationResult ValidateDuration(long totalSeconds)
    {
        if (totalSeconds < FocusDefaults.MinimumSeconds)
        {
            return ValidationResult.Fail("Minimum " + FocusDefaults.MinimumSeconds + " seconds.");
        }

        return totalSeconds > FocusDefaults.MaxSeconds
            ? ValidationResult.Fail("Maximum " + FocusDefaults.MaxHours + " hours 59 minutes 59 seconds.")
            : ValidationResult.Ok();
    }

    /// <summary>Checks every stored field of a task before it is written to the database.</summary>
    public static ValidationResult ValidateForSave(TaskItem task)
    {
        var title = ValidateTitle(task.Title);
        if (!title.IsValid)
        {
            return title;
        }

        var note = ValidateNote(task.Note);
        if (!note.IsValid)
        {
            return note;
        }

        return ValidateDuration(task.EstimatedSeconds);
    }
}
