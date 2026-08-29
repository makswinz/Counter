using Counter.Core.Abstractions;
using Counter.Core.Models;
using Counter.Core.Time;
using Counter.Core.Validation;

namespace Counter.Core.Focus;

/// <summary>What a press of a task's play button actually did, or what it now needs.</summary>
public enum PlayOutcome
{
    /// <summary>A new session was started for the requested task.</summary>
    Started,

    /// <summary>The requested task was the running one, so its session was paused.</summary>
    Paused,

    /// <summary>The requested task was the paused one, so its session was resumed.</summary>
    Resumed,

    /// <summary>The task has no usable duration; the caller must open the duration picker.</summary>
    NeedsDuration,

    /// <summary>Another task is active; the caller must ask before interrupting it.</summary>
    NeedsSwitchConfirmation,

    /// <summary>A command was already in flight. The press was a duplicate and did nothing.</summary>
    Ignored,

    /// <summary>The transition could not be saved. Nothing changed and the caller must say so.</summary>
    Failed
}

/// <summary>
/// The single authority over the focus session. Quick view, planner, the collapsed notch, the
/// tray menu, the statistics panel and the global shortcut all come through here, so there is
/// exactly one place that decides what a play press means and exactly one place that writes a
/// session to storage.
///
/// Every mutation is: snapshot, apply in memory, persist session and segments in one
/// transaction, and on a failed write put the engine back the way it was. That is what stops
/// the interface from showing a running timer for a session that was never saved, and what
/// guarantees a run is never left half recorded.
/// </summary>
public sealed class FocusSessionService : IDisposable
{
    private readonly FocusEngine _engine;
    private readonly IFocusSessionRepository _repository;
    private readonly IClock _clock;

    private readonly List<FocusSession> _pendingWrites = new();
    private readonly List<FocusSegment> _pendingSegments = new();
    private FocusSession? _pendingCompletion;
    private bool _capturing;
    private bool _busy;

    private Guid? _lastPlayTaskId;
    private DateTime _lastPlayAtUtc = DateTime.MinValue;

    public FocusSessionService(FocusEngine engine, IFocusSessionRepository repository, IClock clock)
    {
        _engine = engine;
        _repository = repository;
        _clock = clock;

        _engine.SessionPersisted += OnSessionPersisted;
        _engine.SegmentPersisted += OnSegmentPersisted;
        _engine.SessionCompleted += OnEngineSessionCompleted;
    }

    public FocusEngine Engine => _engine;

    public FocusSession? Current => _engine.Current;

    /// <summary>The run in progress, so a caller can add live time without querying storage.</summary>
    public FocusSegment? CurrentSegment => _engine.CurrentSegment;

    public bool HasActiveSession => _engine.HasActiveSession;

    public bool IsRunning => _engine.Current?.Status == FocusSessionStatus.Running;

    public bool IsPaused => _engine.Current?.Status == FocusSessionStatus.Paused;

    /// <summary>True while a transition is being committed. Duplicate presses are ignored.</summary>
    public bool IsCommitting => _busy;

    public TimeSpan Remaining => _engine.Remaining;

    /// <summary>How many duplicate live sessions the last repair had to cancel.</summary>
    public int RepairsApplied { get; private set; }

    /// <summary>How many runs left open by a crash the last restore had to close.</summary>
    public int SegmentsRepaired { get; private set; }

    /// <summary>
    /// Set when a session that was still running at the last shutdown had already reached zero
    /// by the time the app came back, so the interface can say so once.
    /// </summary>
    public FocusSession? CompletedWhileClosed { get; private set; }

    /// <summary>
    /// How long a second press on the same task is treated as a stutter rather than a new
    /// instruction. A double click on play must not start a session and immediately pause it,
    /// and disabling the button visually is not enough on its own: the press has already been
    /// queued by the time the first one commits.
    /// </summary>
    public TimeSpan PlayDebounce { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Raised when a write fails, with a message fit for a non-blocking banner.</summary>
    public event Action<string, Exception?>? PersistenceFailed;

    /// <summary>Raised whenever the session reference or status changes.</summary>
    public event Action? StateChanged
    {
        add => _engine.StateChanged += value;
        remove => _engine.StateChanged -= value;
    }

    /// <summary>
    /// Raised exactly once per session that reaches zero, and only after that completion has
    /// been committed. Anything that reads storage in response - the journey surface and the
    /// statistics panel above all - is therefore guaranteed to see the finished session.
    /// </summary>
    public event Action<FocusSession>? SessionCompleted;

    /// <summary>Raised after any committed transition, so time totals can be re-read.</summary>
    public event Action? Committed;

    // =================================================================================
    // The play contract
    // =================================================================================

    /// <summary>
    /// Decides what a play press on <paramref name="task"/> means without changing anything.
    /// The view binds its icon to this, so what the button shows and what pressing it does are
    /// always derived from the same state.
    /// </summary>
    public PlayOutcome Preview(TaskItem task)
    {
        if (_busy)
        {
            return PlayOutcome.Ignored;
        }

        var current = _engine.Current;

        if (current is { IsActive: true } && current.TaskId == task.Id)
        {
            return current.Status == FocusSessionStatus.Running ? PlayOutcome.Paused : PlayOutcome.Resumed;
        }

        if (!TaskValidator.ValidateDuration(task.EstimatedSeconds).IsValid)
        {
            return PlayOutcome.NeedsDuration;
        }

        return current is { IsActive: true } ? PlayOutcome.NeedsSwitchConfirmation : PlayOutcome.Started;
    }

    /// <summary>
    /// Applies a play press. The state is re-read here rather than trusted from the caller, so a
    /// second press that arrives while the first is still committing cannot start a second
    /// session, and a stale view cannot ask for a transition that no longer makes sense.
    /// </summary>
    public PlayOutcome Play(TaskItem task)
    {
        if (_busy)
        {
            return PlayOutcome.Ignored;
        }

        var now = _clock.UtcNow;
        if (_lastPlayTaskId == task.Id && now - _lastPlayAtUtc < PlayDebounce)
        {
            return PlayOutcome.Ignored;
        }

        var decision = Preview(task);

        // Only a press that is going to do something counts as the press to debounce against.
        // A press that merely opens the duration picker or a confirmation may be repeated.
        if (decision is PlayOutcome.Started or PlayOutcome.Paused or PlayOutcome.Resumed)
        {
            _lastPlayTaskId = task.Id;
            _lastPlayAtUtc = now;
        }

        return decision switch
        {
            PlayOutcome.Paused => Pause() ? PlayOutcome.Paused : PlayOutcome.Failed,
            PlayOutcome.Resumed => Resume() ? PlayOutcome.Resumed : PlayOutcome.Failed,
            PlayOutcome.Started => Start(task) ? PlayOutcome.Started : PlayOutcome.Failed,
            _ => decision
        };
    }

    /// <summary>Starts a task, ending any session already in flight, in a single write.</summary>
    public bool ConfirmSwitch(TaskItem task)
    {
        if (_busy || !TaskValidator.ValidateDuration(task.EstimatedSeconds).IsValid)
        {
            return false;
        }

        return Commit(() => _engine.Switch(task.Id, task.EstimatedSeconds, task.Title));
    }

    // =================================================================================
    // Individual transitions
    // =================================================================================

    public bool Start(TaskItem task)
    {
        if (_busy || _engine.HasActiveSession)
        {
            return false;
        }

        if (!TaskValidator.ValidateDuration(task.EstimatedSeconds).IsValid)
        {
            return false;
        }

        return Commit(() => _engine.Start(task.Id, task.EstimatedSeconds, task.Title));
    }

    public bool Pause()
        => !_busy && _engine.Current?.Status == FocusSessionStatus.Running && Commit(_engine.Pause);

    public bool Resume()
        => !_busy && _engine.Current?.Status == FocusSessionStatus.Paused && Commit(_engine.Resume);

    /// <summary>The notch transport and the global shortcut. Does nothing without a session.</summary>
    public bool Toggle()
    {
        if (_busy)
        {
            return false;
        }

        return _engine.Current?.Status switch
        {
            FocusSessionStatus.Running => Pause(),
            FocusSessionStatus.Paused => Resume(),
            _ => false
        };
    }

    /// <summary>
    /// Ends the session, keeping every second it actually ran. The reason is stored, so the
    /// history can tell a session the user stopped from one its own task ended.
    /// </summary>
    public bool Stop(SessionEndReason reason = SessionEndReason.StoppedByUser)
        => !_busy && _engine.HasActiveSession && Commit(() => _engine.Cancel(reason));

    /// <summary>Kept for callers that only mean "end whatever is running".</summary>
    public bool Cancel() => Stop();

    /// <summary>
    /// Ends the session only if it belongs to <paramref name="taskId"/>.
    ///
    /// This is the whole of the "completing a task stops its timer" rule: a session pointing at
    /// a different task is left completely alone, and a running and a paused session are treated
    /// the same, because both are the user's attention still being held by a task they have just
    /// declared finished.
    /// </summary>
    public bool StopFor(Guid taskId, SessionEndReason reason)
    {
        if (_engine.Current is not { IsActive: true } session || session.TaskId != taskId)
        {
            return false;
        }

        return Stop(reason);
    }

    /// <summary>Ends a session pointing at a task that is about to be deleted.</summary>
    public bool CancelFor(Guid taskId) => StopFor(taskId, SessionEndReason.TaskDeleted);

    /// <summary>
    /// Advances the session to Completed when its target instant has passed. Returns true only
    /// on the single transition into Completed, so completion can never fire twice.
    /// </summary>
    public bool CompleteIfDue()
    {
        if (_busy || _engine.Current is not { Status: FocusSessionStatus.Running })
        {
            return false;
        }

        var completed = false;
        Commit(() => completed = _engine.Poll());
        return completed;
    }

    // =================================================================================
    // Startup
    // =================================================================================

    /// <summary>
    /// Re-attaches the session that survived the last shutdown, after repairing storage that
    /// somehow holds more than one live session or a run that was never closed.
    ///
    /// A session whose target passed while the process was closed is finished here, at its saved
    /// target instant and not at "now", so the time credited is exactly what was planned and the
    /// completion is recorded once.
    /// </summary>
    public FocusSession? Restore()
    {
        CompletedWhileClosed = null;
        SegmentsRepaired = 0;

        var survivor = RepairDuplicateActiveSessions();

        IReadOnlyList<FocusSegment> open;
        try
        {
            open = _repository.GetOpenSegments();
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke("Could not read the previous focus run.", ex);
            open = Array.Empty<FocusSegment>();
        }

        var mine = survivor is null
            ? null
            : open.FirstOrDefault(segment => segment.SessionId == survivor.Id);

        // Runs belonging to sessions that are no longer live were orphaned by a crash. They are
        // closed at the instant they were last known to be running, never deleted.
        var orphans = open.Where(segment => survivor is null || segment.SessionId != survivor.Id).ToList();
        if (orphans.Count > 0)
        {
            CloseOrphanedSegments(orphans);
        }

        var wasRunning = survivor is { Status: FocusSessionStatus.Running };

        _pendingWrites.Clear();
        _pendingSegments.Clear();
        _capturing = true;
        var completed = false;

        void OnCompleted(FocusSession session) => completed = true;

        _engine.SessionCompleted += OnCompleted;

        try
        {
            _engine.Restore(survivor, mine);
        }
        finally
        {
            _engine.SessionCompleted -= OnCompleted;
            _capturing = false;
        }

        Flush("Could not finish restoring the previous focus session.");

        if (completed && wasRunning)
        {
            CompletedWhileClosed = survivor;

            if (survivor is not null)
            {
                SessionCompleted?.Invoke(survivor);
            }
        }

        return _engine.Current;
    }

    /// <summary>
    /// Enforces the "at most one live session" invariant. The newest live session is kept; the
    /// rest are cancelled with the time they had already accumulated. Nothing is ever deleted.
    /// </summary>
    public FocusSession? RepairDuplicateActiveSessions()
    {
        RepairsApplied = 0;

        IReadOnlyList<FocusSession> active;
        try
        {
            active = _repository.GetActiveSessions();
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke("Could not read the previous focus session.", ex);
            return null;
        }

        if (active.Count <= 1)
        {
            return active.Count == 1 ? active[0] : null;
        }

        // GetActiveSessions is ordered newest first, so index 0 is the one to keep.
        var survivor = active[0];
        var now = _clock.UtcNow;
        var repairs = new List<FocusSession>();

        foreach (var stale in active.Skip(1))
        {
            var elapsed = (long)Math.Round(stale.ElapsedAt(now).TotalSeconds, MidpointRounding.AwayFromZero);
            stale.ElapsedSeconds = Math.Clamp(elapsed, 0, stale.PlannedSeconds);
            stale.Status = FocusSessionStatus.Cancelled;
            stale.EndReason = SessionEndReason.RepairedDuplicate;
            stale.CurrentRunStartedAtUtc = null;
            stale.RemainingSecondsWhenPaused = null;
            repairs.Add(stale);
        }

        try
        {
            _repository.SaveAll(repairs, Array.Empty<FocusSegment>());
            RepairsApplied = repairs.Count;
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke("Could not repair the stored focus sessions.", ex);
        }

        return survivor;
    }

    private void CloseOrphanedSegments(IReadOnlyList<FocusSegment> orphans)
    {
        var now = _clock.UtcNow;
        var closed = new List<FocusSegment>(orphans.Count);

        foreach (var orphan in orphans)
        {
            FocusSession? owner = null;
            try
            {
                owner = _repository.Get(orphan.SessionId);
            }
            catch (Exception ex)
            {
                PersistenceFailed?.Invoke("Could not read a stored focus session.", ex);
            }

            // The run ends where its own session ended, and in any case no later than the plan
            // allowed and no later than now. A run whose session is no longer live cannot be
            // credited with the days that passed while the process was gone, and it must never
            // be credited with more than the session was ever planned to take.
            var end = owner?.CompletedAtUtc ?? now;

            if (owner is { PlannedSeconds: > 0 })
            {
                var planned = orphan.StartedAtUtc.AddSeconds(owner.PlannedSeconds);
                if (end > planned)
                {
                    end = planned;
                }
            }

            if (end > now)
            {
                end = now;
            }

            orphan.EndedAtUtc = end < orphan.StartedAtUtc ? orphan.StartedAtUtc : end;
            closed.Add(orphan);
        }

        try
        {
            _repository.SaveAll(Array.Empty<FocusSession>(), closed);
            SegmentsRepaired = closed.Count;
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke("Could not close an unfinished focus run.", ex);
        }
    }

    // =================================================================================
    // Commit plumbing
    // =================================================================================

    /// <summary>
    /// Runs one engine transition with the reentrancy guard held, collects every session and
    /// segment the engine wanted written, and commits them as a single transaction. A failed
    /// write puts the engine back to the snapshot taken before the transition.
    /// </summary>
    private bool Commit(Action transition)
    {
        if (_busy)
        {
            return false;
        }

        _busy = true;
        var sessionSnapshot = _engine.Current?.Clone();
        var segmentSnapshot = _engine.CurrentSegment?.Clone();

        _pendingWrites.Clear();
        _pendingSegments.Clear();
        _capturing = true;

        try
        {
            transition();
        }
        catch (Exception ex)
        {
            _capturing = false;
            _pendingWrites.Clear();
            _pendingSegments.Clear();
            _pendingCompletion = null;
            _busy = false;
            _engine.Adopt(sessionSnapshot, segmentSnapshot);
            PersistenceFailed?.Invoke("Could not change the focus session.", ex);
            return false;
        }
        finally
        {
            _capturing = false;
        }

        var sessions = _pendingWrites.ToList();
        var segments = _pendingSegments.ToList();
        _pendingWrites.Clear();
        _pendingSegments.Clear();

        try
        {
            if (sessions.Count > 0 || segments.Count > 0)
            {
                _repository.SaveAll(sessions, segments);
            }
        }
        catch (Exception ex)
        {
            _pendingCompletion = null;
            _engine.Adopt(sessionSnapshot, segmentSnapshot);
            PersistenceFailed?.Invoke("Could not save the focus session.", ex);
            return false;
        }
        finally
        {
            _busy = false;
        }

        // The completion is announced only once the write has landed, so a listener that reads
        // storage back cannot race ahead of the row it is looking for.
        if (_pendingCompletion is { } completed)
        {
            _pendingCompletion = null;
            SessionCompleted?.Invoke(completed);
        }

        // Only a transition that actually wrote something counts as a commit. Polling a running
        // session happens on every tick and almost always changes nothing; announcing that as a
        // commit would put a history query behind every single tick.
        if (sessions.Count > 0 || segments.Count > 0)
        {
            Committed?.Invoke();
        }

        return true;
    }

    /// <summary>Writes whatever a captured, non-transactional sequence produced.</summary>
    private void Flush(string failureMessage)
    {
        var sessions = _pendingWrites.ToList();
        var segments = _pendingSegments.ToList();
        _pendingWrites.Clear();
        _pendingSegments.Clear();

        if (sessions.Count == 0 && segments.Count == 0)
        {
            return;
        }

        try
        {
            _repository.SaveAll(sessions, segments);
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke(failureMessage, ex);
        }
    }

    private void OnEngineSessionCompleted(FocusSession session)
    {
        if (_busy)
        {
            _pendingCompletion = session;
            return;
        }

        if (_capturing)
        {
            // Restore raises this outside a commit; Restore itself decides what to announce.
            return;
        }

        SessionCompleted?.Invoke(session);
    }

    private void OnSessionPersisted(FocusSession session)
    {
        if (_capturing)
        {
            // Order matters: Switch ends the old session before starting the new one, and the
            // batch must be written in that order so the invariant holds at every point.
            _pendingWrites.RemoveAll(s => s.Id == session.Id);
            _pendingWrites.Add(session.Clone());
            return;
        }

        try
        {
            _repository.SaveAll(new[] { session.Clone() }, Array.Empty<FocusSegment>());
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke("Could not save the focus session.", ex);
        }
    }

    private void OnSegmentPersisted(FocusSegment segment)
    {
        if (_capturing)
        {
            _pendingSegments.RemoveAll(s => s.Id == segment.Id);
            _pendingSegments.Add(segment.Clone());
            return;
        }

        try
        {
            _repository.SaveAll(Array.Empty<FocusSession>(), new[] { segment.Clone() });
        }
        catch (Exception ex)
        {
            PersistenceFailed?.Invoke("Could not save the focus run.", ex);
        }
    }

    public void Dispose()
    {
        _engine.SessionPersisted -= OnSessionPersisted;
        _engine.SegmentPersisted -= OnSegmentPersisted;
        _engine.SessionCompleted -= OnEngineSessionCompleted;
    }
}
