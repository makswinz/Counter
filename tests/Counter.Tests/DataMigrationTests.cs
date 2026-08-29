using System.IO;
using Counter.App.Services;
using Xunit;

namespace Counter.Tests;

/// <summary>
/// Moving the data folder from the application's old name to its current one.
///
/// This is the one piece of code in the project that can lose somebody their entire history, so
/// the rule it is written to is narrow: never merge, never overwrite, and never leave the paths
/// pointing somewhere the data is not. Every test below is a way that could go wrong.
/// </summary>
public class DataMigrationTests : IDisposable
{
    private readonly string _local = Path.Combine(
        Path.GetTempPath(), "counter-migration", Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_local, "FocusNotch");

    private string Current => Path.Combine(_local, "Counter");

    private void SeedLegacy(string contents = "the user's history")
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "backups"));
        File.WriteAllText(Path.Combine(Legacy, "focusnotch.db"), contents);
        File.WriteAllText(Path.Combine(Legacy, "focusnotch.db-wal"), "wal");
        File.WriteAllText(Path.Combine(Legacy, "backups", "focusnotch-20260829-130214.db"), "backup");
    }

    [Fact]
    public void The_old_folder_is_moved_and_its_files_renamed()
    {
        SeedLegacy();

        var root = AppPaths.Resolve(_local);

        Assert.Equal(Current, root);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("the user's history", File.ReadAllText(Path.Combine(root, "counter.db")));
        Assert.True(File.Exists(Path.Combine(root, "counter.db-wal")));
        Assert.True(File.Exists(Path.Combine(root, "backups", "counter-20260829-130214.db")));

        // And nothing is left behind under the old names to be opened by mistake later.
        Assert.False(File.Exists(Path.Combine(root, "focusnotch.db")));
    }

    [Fact]
    public void A_folder_that_is_already_current_is_left_alone()
    {
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "counter.db"), "current");

        Assert.Equal(Current, AppPaths.Resolve(_local));
        Assert.Equal("current", File.ReadAllText(Path.Combine(Current, "counter.db")));
    }

    [Fact]
    public void Two_histories_are_never_merged()
    {
        // The dangerous case: both folders exist because the application was run under each
        // name. The newer one wins untouched and the older one is left exactly where it is,
        // because silently combining two databases is worse than either outcome.
        SeedLegacy("old history");
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "counter.db"), "new history");

        var root = AppPaths.Resolve(_local);

        Assert.Equal(Current, root);
        Assert.Equal("new history", File.ReadAllText(Path.Combine(root, "counter.db")));
        Assert.True(Directory.Exists(Legacy));
        Assert.Equal("old history", File.ReadAllText(Path.Combine(Legacy, "focusnotch.db")));
    }

    [Fact]
    public void A_first_run_with_nothing_to_migrate_uses_the_current_folder()
    {
        Directory.CreateDirectory(_local);

        Assert.Equal(Current, AppPaths.Resolve(_local));
    }

    [Fact]
    public void An_existing_current_name_is_never_overwritten_by_the_rename()
    {
        // Half a migration is still a migration. If a current-named database is already there,
        // the old one is left beside it rather than replacing something newer.
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "counter.db"), "keep me");
        File.WriteAllText(Path.Combine(Current, "focusnotch.db"), "older");

        var root = AppPaths.Resolve(_local);

        Assert.Equal("keep me", File.ReadAllText(Path.Combine(root, "counter.db")));
        Assert.Equal("older", File.ReadAllText(Path.Combine(root, "focusnotch.db")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_local))
            {
                Directory.Delete(_local, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
