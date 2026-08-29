using FocusNotch.Core.Abstractions;
using FocusNotch.Core.Journey;
using FocusNotch.Core.Models;
using FocusNotch.Core.Statistics;

namespace FocusNotch.Tests;

/// <summary>
/// An in-memory session store that can be told to fail, so the "a write that does not land must
/// not leave a running timer on screen" rule can actually be exercised. Rows are stored as
/// clones, exactly as SQLite would: the caller's object is not the stored one.
/// </summary>
public sealed class FakeSessionRepository : IFocusSessionRepository
{
    private readonly Dictionary<Guid, FocusSession> _rows = new();
    private readonly Dictionary<Guid, FocusSegment> _segments = new();

    /// <summary>Set to make the next write throw, as a full disk or a locked file would.</summary>
    public bool FailWrites { get; set; }

    public int WriteCalls { get; private set; }

    /// <summary>How many rows the last SaveAll wrote, so batching can be asserted.</summary>
    public int LastBatchSize { get; private set; }

    public IReadOnlyCollection<FocusSession> All => _rows.Values;

    public IReadOnlyCollection<FocusSegment> AllSegments => _segments.Values;

    public void Save(FocusSession session) => SaveAll(new[] { session }, Array.Empty<FocusSegment>());

    public void SaveAll(IReadOnlyList<FocusSession> sessions, IReadOnlyList<FocusSegment> segments)
    {
        WriteCalls++;
        LastBatchSize = sessions.Count + segments.Count;

        if (FailWrites)
        {
            throw new InvalidOperationException("Simulated write failure.");
        }

        foreach (var session in sessions)
        {
            _rows[session.Id] = session.Clone();
        }

        foreach (var segment in segments)
        {
            _segments[segment.Id] = segment.Clone();
        }
    }

    public FocusSession? GetActive() => GetActiveSessions().FirstOrDefault();

    public IReadOnlyList<FocusSession> GetActiveSessions() =>
        _rows.Values
            .Where(s => s.IsActive)
            .OrderByDescending(s => s.StartedAtUtc)
            .Select(s => s.Clone())
            .ToList();

    public FocusSession? Get(Guid id) => _rows.TryGetValue(id, out var row) ? row.Clone() : null;

    public IReadOnlyList<FocusSegment> GetSegments(Guid sessionId) =>
        _segments.Values
            .Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => s.Clone())
            .ToList();

    public IReadOnlyList<FocusSegment> GetOpenSegments() =>
        _segments.Values
            .Where(s => s.IsOpen)
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => s.Clone())
            .ToList();

    public IReadOnlyList<DateTime> GetCompletionsUtc(DateTime sinceUtc) =>
        _rows.Values
            .Where(s => s.Status == FocusSessionStatus.Completed && s.CompletedAtUtc >= sinceUtc)
            .Select(s => s.CompletedAtUtc!.Value)
            .OrderBy(d => d)
            .ToList();

    public IReadOnlyList<DateOnly> GetCompletionDates(DateOnly fromInclusive, DateOnly toInclusive) =>
        _rows.Values
            .Where(s => s.Status == FocusSessionStatus.Completed
                        && s.CompletedForDate is { } day
                        && day >= fromInclusive
                        && day <= toInclusive)
            .Select(s => s.CompletedForDate!.Value)
            .ToList();

    /// <summary>Writes a row directly, bypassing the service, to set up a corrupt starting state.</summary>
    public void Seed(FocusSession session) => _rows[session.Id] = session.Clone();

    /// <summary>Writes a run directly, to set up a crash that left one open.</summary>
    public void SeedSegment(FocusSegment segment) => _segments[segment.Id] = segment.Clone();
}

/// <summary>An in-memory task store. Delete is a soft delete, exactly as the real one is.</summary>
public sealed class FakeTaskRepository : ITaskRepository
{
    private readonly Dictionary<Guid, TaskItem> _rows = new();
    private int _sort;

    public bool FailWrites { get; set; }

    /// <summary>Every row, deleted ones included, so soft deletion can be asserted.</summary>
    public IReadOnlyCollection<TaskItem> AllIncludingDeleted => _rows.Values;

    public IReadOnlyList<TaskItem> GetAll() =>
        _rows.Values
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAtUtc)
            .Select(t => t.Clone())
            .ToList();

    public TaskItem? Get(Guid id) => _rows.TryGetValue(id, out var row) ? row.Clone() : null;

    public void Add(TaskItem task)
    {
        Guard();
        _rows[task.Id] = task.Clone();
    }

    public void Update(TaskItem task)
    {
        Guard();
        _rows[task.Id] = task.Clone();
    }

    public void Delete(Guid id)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            row.DeletedAtUtc = DateTime.UtcNow;
        }
    }

    public void Restore(Guid id)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            row.DeletedAtUtc = null;
        }
    }

    public int NextSortOrder() => _sort++;

    public IReadOnlyList<DateOnly> GetCompletionDates(DateOnly fromInclusive, DateOnly toInclusive) =>
        _rows.Values
            .Where(t => t.IsCompleted
                        && !t.IsDeleted
                        && t.CompletedForDate is { } day
                        && day >= fromInclusive
                        && day <= toInclusive)
            .Select(t => t.CompletedForDate!.Value)
            .ToList();

    private void Guard()
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("Simulated write failure.");
        }
    }
}

/// <summary>An in-memory manual time store.</summary>
public sealed class FakeManualTimeRepository : IManualTimeRepository
{
    private readonly Dictionary<Guid, ManualTimeEntry> _rows = new();

    public bool FailWrites { get; set; }

    public IReadOnlyCollection<ManualTimeEntry> All => _rows.Values;

    public void Add(ManualTimeEntry entry)
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("Simulated write failure.");
        }

        _rows[entry.Id] = entry.Clone();
    }

    public void Delete(Guid id) => _rows.Remove(id);

    public IReadOnlyList<ManualTimeEntry> GetForTask(Guid taskId) =>
        _rows.Values.Where(e => e.TaskId == taskId).Select(e => e.Clone()).ToList();

    public IReadOnlyList<ManualTimeEntry> GetInRange(DateOnly fromInclusive, DateOnly toInclusive) =>
        _rows.Values
            .Where(e => e.LocalDate >= fromInclusive && e.LocalDate <= toInclusive)
            .Select(e => e.Clone())
            .ToList();
}

/// <summary>
/// A hand-built history. Everything the journey and the statistics compute is a pure function of
/// what this returns, so the whole surface can be asserted without a database.
/// </summary>
public sealed class FakeActivityReader : IActivityReader, ITaskTimeReader
{
    public List<TaskRecord> Tasks { get; } = new();

    public List<SessionRecord> Sessions { get; } = new();

    public List<FocusSegment> Segments { get; } = new();

    public List<ManualTimeEntry> ManualEntries { get; } = new();

    public int ReadCount { get; private set; }

    public Exception? Fail { get; set; }

    public ActivitySnapshot Read(DateOnly fromInclusive, DateOnly toInclusive, TimeZoneInfo zone)
    {
        ReadCount++;

        if (Fail is not null)
        {
            throw Fail;
        }

        return new ActivitySnapshot(
            Tasks.ToList(),
            Sessions.ToList(),
            Segments.Select(s => s.Clone()).ToList(),
            ManualEntries.Select(e => e.Clone()).ToList());
    }

    /// <summary>Closed runs only, exactly like the SQL the real reader uses.</summary>
    public IReadOnlyList<TaskTimeTotals> ReadTotals()
    {
        var focus = Segments
            .Where(s => s.TaskId.HasValue && !s.IsOpen)
            .GroupBy(s => s.TaskId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (
                    Seconds: g.Sum(s => s.SecondsAt(DateTime.UtcNow)),
                    Sessions: g.Select(s => s.SessionId).Distinct().Count(),
                    Last: g.Max(s => s.EndedAtUtc)));

        var manual = ManualEntries
            .Where(e => e.TaskId.HasValue && e.Seconds > 0)
            .GroupBy(e => e.TaskId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Seconds));

        var ids = new HashSet<Guid>(focus.Keys);
        ids.UnionWith(manual.Keys);

        return ids
            .Select(id =>
            {
                focus.TryGetValue(id, out var f);
                return new TaskTimeTotals(
                    id, f.Seconds, manual.GetValueOrDefault(id), f.Sessions, f.Last);
            })
            .ToList();
    }

    // ---------------------------------------------------------------------------------
    // Fluent set-up
    // ---------------------------------------------------------------------------------

    public FakeActivityReader WithCompletedTask(DateOnly on, string title = "Task", Guid? id = null)
    {
        Tasks.Add(new TaskRecord(id ?? Guid.NewGuid(), title, on, true, on, 1800, false));
        return this;
    }

    public FakeActivityReader WithOpenTask(DateOnly scheduled, string title = "Task", Guid? id = null)
    {
        Tasks.Add(new TaskRecord(id ?? Guid.NewGuid(), title, scheduled, false, null, 1800, false));
        return this;
    }

    public FakeActivityReader WithCompletedSession(DateOnly on, Guid? taskId = null, string? title = null)
    {
        var completedAt = on.ToDateTime(new TimeOnly(12, 0));
        Sessions.Add(new SessionRecord(
            Guid.NewGuid(), taskId, title, FocusSessionStatus.Completed, SessionEndReason.Completed,
            1800, completedAt.AddMinutes(-30), completedAt, on));
        return this;
    }

    public FakeActivityReader WithRun(
        DateTime startUtc, DateTime endUtc, Guid? taskId = null, Guid? sessionId = null)
    {
        Segments.Add(new FocusSegment
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            TaskId = taskId,
            StartedAtUtc = startUtc,
            EndedAtUtc = endUtc
        });

        return this;
    }

    public FakeActivityReader WithManual(DateOnly on, long seconds, Guid? taskId = null, string? title = null)
    {
        ManualEntries.Add(new ManualTimeEntry
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            TaskTitle = title,
            LocalDate = on,
            Seconds = seconds,
            CreatedAtUtc = on.ToDateTime(TimeOnly.MinValue)
        });

        return this;
    }
}

/// <summary>An in-memory settings store, so persistence can be asserted without a file.</summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new();

    public int Writes { get; private set; }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string value)
    {
        Writes++;
        _values[key] = value;
    }

    public bool GetBool(string key, bool fallback) =>
        Get(key) is { } raw ? raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) : fallback;

    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var parsed) ? parsed : fallback;

    public void SetInt(string key, int value) => Set(key, value.ToString());
}

/// <summary>
/// Reads history straight out of the in-memory repositories, so a shell test exercises the whole
/// pipeline - change, commit, read back, recompute, publish - exactly as the real reader does,
/// including the fact that it only ever sees what was actually written.
/// </summary>
public sealed class RepositoryActivityReader : IActivityReader, ITaskTimeReader
{
    private readonly FakeTaskRepository _tasks;
    private readonly FakeSessionRepository _sessions;
    private readonly FakeManualTimeRepository _manual;

    public RepositoryActivityReader(
        FakeTaskRepository tasks,
        FakeSessionRepository sessions,
        FakeManualTimeRepository? manual = null)
    {
        _tasks = tasks;
        _sessions = sessions;
        _manual = manual ?? new FakeManualTimeRepository();
    }

    public int Reads { get; private set; }

    public ActivitySnapshot Read(DateOnly fromInclusive, DateOnly toInclusive, TimeZoneInfo zone)
    {
        Reads++;

        var tasks = _tasks.AllIncludingDeleted
            .Select(t => new TaskRecord(
                t.Id, t.Title, t.ScheduledDate, t.IsCompleted, t.CompletedForDate,
                t.EstimatedSeconds, t.IsDeleted))
            .ToList();

        var sessions = _sessions.All
            .Select(s => new SessionRecord(
                s.Id, s.TaskId, s.TaskTitle, s.Status, s.EndReason, s.PlannedSeconds,
                s.StartedAtUtc, s.CompletedAtUtc, s.CompletedForDate))
            .ToList();

        var segments = _sessions.AllSegments.Select(s => s.Clone()).ToList();
        var manual = _manual.All.Select(e => e.Clone()).ToList();

        return new ActivitySnapshot(tasks, sessions, segments, manual);
    }

    public IReadOnlyList<TaskTimeTotals> ReadTotals()
    {
        var focus = _sessions.AllSegments
            .Where(s => s.TaskId.HasValue && !s.IsOpen)
            .GroupBy(s => s.TaskId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (
                    Seconds: g.Sum(s => s.SecondsAt(DateTime.UtcNow)),
                    Sessions: g.Select(s => s.SessionId).Distinct().Count(),
                    Last: g.Max(s => s.EndedAtUtc)));

        var manual = _manual.All
            .Where(e => e.TaskId.HasValue && e.Seconds > 0)
            .GroupBy(e => e.TaskId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Seconds));

        var ids = new HashSet<Guid>(focus.Keys);
        ids.UnionWith(manual.Keys);

        return ids
            .Select(id =>
            {
                focus.TryGetValue(id, out var f);
                return new TaskTimeTotals(id, f.Seconds, manual.GetValueOrDefault(id), f.Sessions, f.Last);
            })
            .ToList();
    }
}
