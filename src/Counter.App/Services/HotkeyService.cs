using System.Windows.Input;
using System.Windows.Interop;
using Counter.App.Interop;

namespace Counter.App.Services;

public sealed record HotkeyDefinition(string Id, string Gesture, string Description, Action Invoke);

public sealed record HotkeyRegistration(string Id, string Gesture, string Description, bool Succeeded)
{
    public string StatusText => Succeeded
        ? Description + " - " + Gesture
        : Description + " - " + Gesture + " (already used by another app)";
}

/// <summary>
/// Registers the global shortcuts against a hidden message-only window. A gesture that another
/// application already owns is reported, not fatal: Counter keeps running without it.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Dictionary<int, HotkeyDefinition> _byId = new();
    private readonly List<HotkeyRegistration> _registrations = new();
    private HwndSource? _source;
    private int _nextId = 0x4F01;
    private bool _disposed;

    public IReadOnlyList<HotkeyRegistration> Registrations => _registrations;

    public bool HasConflicts => _registrations.Any(r => !r.Succeeded);

    public void Register(IEnumerable<HotkeyDefinition> definitions)
    {
        EnsureSource();

        foreach (var definition in definitions)
        {
            // An empty gesture is how a shortcut is turned off. Nothing is registered and nothing
            // is reported as a conflict, because deliberately having no shortcut is not a
            // failure to get one.
            if (string.IsNullOrWhiteSpace(definition.Gesture))
            {
                _registrations.Add(new HotkeyRegistration(
                    definition.Id, string.Empty, definition.Description, true));
                continue;
            }

            var parsed = ParseGesture(definition.Gesture);
            if (parsed is null)
            {
                Log.Warn("Ignoring unparseable hotkey gesture: " + definition.Gesture);
                _registrations.Add(new HotkeyRegistration(
                    definition.Id, definition.Gesture, definition.Description, false));
                continue;
            }

            var (modifiers, virtualKey) = parsed.Value;
            var id = _nextId++;

            var ok = NativeMethods.RegisterHotKey(
                _source!.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);

            if (ok)
            {
                _byId[id] = definition;
            }
            else
            {
                Log.Info("Hotkey " + definition.Gesture + " is already registered by another application.");
            }

            _registrations.Add(new HotkeyRegistration(
                definition.Id, definition.Gesture, definition.Description, ok));
        }
    }

    private void EnsureSource()
    {
        if (_source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("Counter.HotkeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            // HWND_MESSAGE: a message-only window that never appears anywhere.
            ParentWindow = new IntPtr(-3)
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        if (_byId.TryGetValue(wParam.ToInt32(), out var definition))
        {
            handled = true;
            try
            {
                definition.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey handler for " + definition.Id + " failed.", ex);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Parses gestures such as "Ctrl+Shift+Space" into Win32 modifiers and a virtual key.</summary>
    public static (uint Modifiers, uint VirtualKey)? ParseGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        uint modifiers = 0;
        Key? key = null;

        foreach (var rawPart in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();

            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    continue;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    continue;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    continue;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.MOD_WIN;
                    continue;
            }

            if (Enum.TryParse<Key>(part, ignoreCase: true, out var parsedKey))
            {
                key = parsedKey;
            }
            else
            {
                return null;
            }
        }

        if (key is null || modifiers == 0)
        {
            return null;
        }

        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key.Value));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_source is not null)
        {
            foreach (var id in _byId.Keys)
            {
                try
                {
                    NativeMethods.UnregisterHotKey(_source.Handle, id);
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not unregister hotkey " + id + ".", ex);
                }
            }

            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        _byId.Clear();
    }
}
