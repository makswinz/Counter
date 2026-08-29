namespace FocusNotch.App.Data;

internal static class Migrations
{
    /// <summary>
    /// Schema 1. Dates that represent a calendar day are stored as 'yyyy-MM-dd' text so that a
    /// timezone change can never shift a task onto a different day. Instants are ISO-8601 UTC.
    /// </summary>
    public const string V1CreateSchema = """
        CREATE TABLE IF NOT EXISTS Tasks (
            Id               TEXT    NOT NULL PRIMARY KEY,
            Title            TEXT    NOT NULL,
            Note             TEXT    NULL,
            ScheduledDate    TEXT    NULL,
            EstimatedSeconds INTEGER NOT NULL,
            IsCompleted      INTEGER NOT NULL DEFAULT 0,
            CompletedAtUtc   TEXT    NULL,
            CreatedAtUtc     TEXT    NOT NULL,
            UpdatedAtUtc     TEXT    NOT NULL,
            SortOrder        INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_Tasks_ScheduledDate ON Tasks (ScheduledDate);
        CREATE INDEX IF NOT EXISTS IX_Tasks_SortOrder     ON Tasks (SortOrder);

        CREATE TABLE IF NOT EXISTS FocusSessions (
            Id                         TEXT    NOT NULL PRIMARY KEY,
            TaskId                     TEXT    NULL REFERENCES Tasks (Id) ON DELETE SET NULL,
            Status                     INTEGER NOT NULL,
            PlannedSeconds             INTEGER NOT NULL,
            RemainingSecondsWhenPaused INTEGER NULL,
            StartedAtUtc               TEXT    NOT NULL,
            CurrentRunStartedAtUtc     TEXT    NULL,
            CompletedAtUtc             TEXT    NULL,
            ElapsedSeconds             INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS IX_FocusSessions_Status      ON FocusSessions (Status);
        CREATE INDEX IF NOT EXISTS IX_FocusSessions_CompletedAt ON FocusSessions (CompletedAtUtc);

        CREATE TABLE IF NOT EXISTS Settings (
            Key   TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Schema 2. Adds the stable local contribution date that the journey streak counts.
    ///
    /// It is a stored calendar day rather than something derived at read time from an instant,
    /// because the day a piece of work counts for is a decision, not a conversion: completing a
    /// task that was scheduled for yesterday credits yesterday. Deriving it every time would
    /// also let a timezone change silently move contributions that were already earned.
    ///
    /// Both columns are nullable and added with ALTER TABLE, so every existing row survives
    /// untouched and the migration cannot lose data. The backfill runs separately, in the same
    /// transaction, because a correct default needs the machine's local timezone and SQLite
    /// cannot do that conversion.
    /// </summary>
    public const string V2AddContributionDates = """
        ALTER TABLE Tasks         ADD COLUMN CompletedForDate TEXT NULL;
        ALTER TABLE FocusSessions ADD COLUMN CompletedForDate TEXT NULL;

        CREATE INDEX IF NOT EXISTS IX_Tasks_CompletedForDate
            ON Tasks (CompletedForDate) WHERE CompletedForDate IS NOT NULL;

        CREATE INDEX IF NOT EXISTS IX_FocusSessions_CompletedForDate
            ON FocusSessions (CompletedForDate) WHERE CompletedForDate IS NOT NULL;
        """;

    public const string V2SelectCompletedTasks = """
        SELECT Id, ScheduledDate, CompletedAtUtc
          FROM Tasks
         WHERE IsCompleted = 1 AND CompletedForDate IS NULL;
        """;

    public const string V2SelectCompletedSessions = """
        SELECT Id, CompletedAtUtc
          FROM FocusSessions
         WHERE Status = 2 AND CompletedAtUtc IS NOT NULL AND CompletedForDate IS NULL;
        """;

    /// <summary>
    /// Schema 3. Time actually spent, recorded work, soft deletion and end reasons.
    ///
    /// Every change is additive: three nullable or defaulted columns and two new tables. No
    /// existing column is dropped, retyped or rewritten, so the file an earlier version wrote
    /// keeps every byte of its content. Durations were already stored in SQLite INTEGER columns,
    /// which are 64-bit, so raising the supported range to 99:59:59 needs no column change at
    /// all - only the code that reads them had to stop narrowing to 32 bits.
    ///
    /// Tasks are deleted by stamping DeletedAtUtc rather than by removing the row. Erasing the
    /// row would take its sessions, its runs and its recorded time with it, and the hours
    /// somebody spent on a task are not the app's to throw away when they tidy up their list.
    /// </summary>
    public const string V3AddTimeTracking = """
        ALTER TABLE Tasks         ADD COLUMN DeletedAtUtc TEXT NULL;
        ALTER TABLE FocusSessions ADD COLUMN TaskTitle     TEXT NULL;
        ALTER TABLE FocusSessions ADD COLUMN EndReason     INTEGER NOT NULL DEFAULT 0;

        CREATE INDEX IF NOT EXISTS IX_Tasks_Live ON Tasks (SortOrder) WHERE DeletedAtUtc IS NULL;

        CREATE TABLE IF NOT EXISTS FocusSegments (
            Id           TEXT NOT NULL PRIMARY KEY,
            SessionId    TEXT NOT NULL REFERENCES FocusSessions (Id) ON DELETE CASCADE,
            TaskId       TEXT NULL     REFERENCES Tasks (Id)         ON DELETE SET NULL,
            StartedAtUtc TEXT NOT NULL,
            EndedAtUtc   TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_FocusSegments_Session ON FocusSegments (SessionId);
        CREATE INDEX IF NOT EXISTS IX_FocusSegments_Task    ON FocusSegments (TaskId);
        CREATE INDEX IF NOT EXISTS IX_FocusSegments_Started ON FocusSegments (StartedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_FocusSegments_Open
            ON FocusSegments (SessionId) WHERE EndedAtUtc IS NULL;

        CREATE TABLE IF NOT EXISTS ManualTimeEntries (
            Id           TEXT    NOT NULL PRIMARY KEY,
            TaskId       TEXT    NULL REFERENCES Tasks (Id) ON DELETE SET NULL,
            TaskTitle    TEXT    NULL,
            LocalDate    TEXT    NOT NULL,
            Seconds      INTEGER NOT NULL,
            Note         TEXT    NULL,
            CreatedAtUtc TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_ManualTimeEntries_Date ON ManualTimeEntries (LocalDate);
        CREATE INDEX IF NOT EXISTS IX_ManualTimeEntries_Task ON ManualTimeEntries (TaskId);
        """;

    /// <summary>
    /// Gives finished sessions the end reason their status already implies. A cancelled session
    /// from before this column is deliberately left at 0, because the record genuinely does not
    /// say why it ended and inventing a reason would be worse than admitting that.
    /// </summary>
    public const string V3BackfillEndReasons = """
        UPDATE FocusSessions SET EndReason = 1 WHERE Status = 2 AND EndReason = 0;
        """;

    /// <summary>Copies the current task title onto every session that has a task.</summary>
    public const string V3BackfillSessionTitles = """
        UPDATE FocusSessions
           SET TaskTitle = (SELECT Title FROM Tasks WHERE Tasks.Id = FocusSessions.TaskId)
         WHERE TaskTitle IS NULL AND TaskId IS NOT NULL;
        """;

    /// <summary>
    /// Reconstructs one run per historical session that recorded time, so the hours a user
    /// already put in still show up as time spent instead of resetting to zero.
    ///
    /// The record only ever kept a total, not a start and an end, so the run is laid down from
    /// the session's own start for exactly the number of seconds that were stored. That is the
    /// most faithful reading the old data supports, and it can never invent time that was not
    /// already there.
    /// </summary>
    public const string V3BackfillSegments = """
        INSERT INTO FocusSegments (Id, SessionId, TaskId, StartedAtUtc, EndedAtUtc)
        SELECT
            lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
            substr(lower(hex(randomblob(2))), 2) || '-a' ||
            substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
            s.Id,
            s.TaskId,
            s.StartedAtUtc,
            strftime('%Y-%m-%dT%H:%M:%f0000Z', julianday(s.StartedAtUtc) + s.ElapsedSeconds / 86400.0)
        FROM FocusSessions s
        WHERE s.ElapsedSeconds > 0
          AND s.Status IN (2, 3)
          AND NOT EXISTS (SELECT 1 FROM FocusSegments g WHERE g.SessionId = s.Id);
        """;

    /// <summary>
    /// A session that was still live when the app last closed gets its run laid down too: an
    /// open one when it was running, so the time since is attributed and then capped at the
    /// target, and a closed one when it was paused.
    /// </summary>
    public const string V3BackfillLiveSegments = """
        INSERT INTO FocusSegments (Id, SessionId, TaskId, StartedAtUtc, EndedAtUtc)
        SELECT
            lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
            substr(lower(hex(randomblob(2))), 2) || '-a' ||
            substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
            s.Id,
            s.TaskId,
            COALESCE(s.CurrentRunStartedAtUtc, s.StartedAtUtc),
            CASE WHEN s.Status = 0 THEN NULL
                 ELSE strftime('%Y-%m-%dT%H:%M:%f0000Z',
                               julianday(s.StartedAtUtc) + s.ElapsedSeconds / 86400.0)
            END
        FROM FocusSessions s
        WHERE s.Status IN (0, 1)
          AND NOT EXISTS (SELECT 1 FROM FocusSegments g WHERE g.SessionId = s.Id)
          AND (s.Status = 0 OR s.ElapsedSeconds > 0);
        """;
}
