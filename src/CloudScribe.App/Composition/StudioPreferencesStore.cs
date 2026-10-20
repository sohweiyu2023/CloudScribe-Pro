using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;

namespace CloudScribe.App.Composition;

/// <summary>Non-secret user intent only. Never stores capability, price or spend approval.</summary>
public sealed class StudioPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private bool _readable = true;
    public StudioPreferences State { get; private set; } = new();
    public bool IsFreshProfile { get; }
    public string? Warning { get; private set; }
    public event EventHandler? Changed;

    public StudioPreferencesStore(AppPaths paths) : this(Path.Combine(paths.RootDirectory, "studio-preferences.json")) { }

    public StudioPreferencesStore(string path)
    {
        _path = Path.GetFullPath(path);
        IsFreshProfile = !File.Exists(_path);
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 128 * 1024) throw new InvalidDataException("Preferences exceed the size limit.");
            State = JsonSerializer.Deserialize<StudioPreferences>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Preferences are empty.");
            Validate(State);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            State = new();
            _readable = false;
            Warning = "Saved voice setups could not be read. The existing preferences file is preserved; no settings will be overwritten.";
        }
    }

    public void Remember(VoiceSetup setup) => Commit(State with { LastUsed = setup });
    public void RememberDocument(Guid documentId)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("A local document identity is required.", nameof(documentId));
        if (State.LastDocumentId != documentId) Commit(State with { LastDocumentId = documentId });
    }
    public void RememberQuickStartSeen() => Commit(State with { QuickStartSeen = true });
    public void RememberDisplay(CloudScribe.App.Design.ThemePreference theme, bool reducedMotion) => Commit(State with { Theme = theme, ReducedMotion = reducedMotion });

    public void SaveSetup(VoiceSetup setup)
    {
        VoiceSetup[] items = State.Setups.Where(s => !string.Equals(s.Name, setup.Name, StringComparison.OrdinalIgnoreCase)).Append(setup).ToArray();
        Commit(State with { Setups = items, LastUsed = setup });
    }

    public void DeleteSetup(string name) => Commit(State with
    {
        Setups = State.Setups.Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray(),
    });

    private void Commit(StudioPreferences next)
    {
        if (!_readable) throw new InvalidOperationException(Warning);
        Validate(next);
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".studio-preferences-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, JsonOptions));
            if (File.Exists(_path)) File.Copy(_path, _path + ".previous", overwrite: true);
            File.Move(temporary, _path, overwrite: true);
            State = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Validate(StudioPreferences state)
    {
        if (state.Version != 1 || state.Setups is null || state.Setups.Length > 30 || !Enum.IsDefined(state.Theme))
            throw new InvalidDataException("Unsupported voice setup preferences.");
        foreach (VoiceSetup setup in state.Setups.Concat(state.LastUsed is null ? [] : new[] { state.LastUsed }))
        {
            if (setup is null) throw new InvalidDataException("A voice setup is missing.");
            Check(setup.Name, 60); Check(setup.Locale, 32); Check(setup.VoiceName, 100);
        }
    }

    private static void Check(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("Voice setup fields are missing or invalid.");
    }
}
