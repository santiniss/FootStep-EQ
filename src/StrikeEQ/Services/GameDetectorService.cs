using System.Diagnostics;
using StrikeEQ.Models;

namespace StrikeEQ.Services;

/// <summary>
/// Lightweight background watcher that detects running competitive games
/// and notifies the app to auto-switch EQ profiles (and restore when the game closes).
/// </summary>
public sealed class GameDetectorService : IDisposable
{
    private readonly Func<IReadOnlyList<EqProfile>> _getProfiles;
    private System.Threading.Timer? _timer;
    private string? _lastMatchedProfileId;
    private string? _lastMatchedProcess;
    private bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public string? ActiveGameProcess => _lastMatchedProcess;

    public event Action<EqProfile, string>? GameDetected;
    public event Action? GameClosed;

    public GameDetectorService(Func<IReadOnlyList<EqProfile>> getProfiles)
    {
        _getProfiles = getProfiles;
    }

    public void Start(int pollIntervalMs = 2500)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => PollOnce(), null, 1200, Math.Max(1000, pollIntervalMs));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static (EqProfile? Profile, string? ProcessName) MatchProcesses(
        IEnumerable<string> runningProcessNames,
        IReadOnlyList<EqProfile> profiles)
    {
        var runningSet = new HashSet<string>(runningProcessNames, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            if (profile.ProcessNames is null || profile.ProcessNames.Count == 0)
                continue;

            foreach (string target in profile.ProcessNames)
            {
                string clean = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? target[..^4]
                    : target;

                if (runningSet.Contains(clean))
                    return (profile, clean + ".exe");
            }
        }

        return (null, null);
    }

    private void PollOnce()
    {
        if (!_enabled) return;

        try
        {
            var running = Process.GetProcesses().Select(p =>
            {
                try { return p.ProcessName; }
                catch { return string.Empty; }
                finally { p.Dispose(); }
            }).Where(n => !string.IsNullOrEmpty(n)).ToArray();

            var (profile, procName) = MatchProcesses(running, _getProfiles());

            if (profile is not null && procName is not null)
            {
                if (_lastMatchedProfileId != profile.Id)
                {
                    _lastMatchedProfileId = profile.Id;
                    _lastMatchedProcess = procName;
                    GameDetected?.Invoke(profile, procName);
                }
            }
            else if (_lastMatchedProfileId is not null)
            {
                _lastMatchedProfileId = null;
                _lastMatchedProcess = null;
                GameClosed?.Invoke();
            }
        }
        catch
        {
            // Ignore transient process inspection errors
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
