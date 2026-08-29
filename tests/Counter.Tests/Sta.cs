using System.Threading;

namespace Counter.Tests;

/// <summary>
/// Runs a piece of work on a single-threaded apartment.
///
/// WPF visuals want one, and xUnit does not provide one. Anything that creates or measures a
/// control goes through here rather than each test growing its own thread.
/// </summary>
public static class Sta
{
    public static void Run(Action work) => Run<object?>(() =>
    {
        work();
        return null;
    });

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("The work on the STA thread failed: " + failure);
        }

        return result;
    }
}
