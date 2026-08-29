namespace FocusNotch.Core.Abstractions;

public interface ISettingsStore
{
    string? Get(string key);

    void Set(string key, string value);

    bool GetBool(string key, bool fallback);

    void SetBool(string key, bool value);

    int GetInt(string key, int fallback);

    void SetInt(string key, int value);
}
