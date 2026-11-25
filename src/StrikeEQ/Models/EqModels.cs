namespace StrikeEQ.Models;

public enum EqFilterType
{
    Peaking,
    LowShelf,
    HighShelf,
    HighPass,
    LowPass
}

public sealed class EqBand
{
    public int Index { get; set; }
    public double Frequency { get; set; } = 1000.0;
    public double GainDb { get; set; } = 0.0;
    public double Q { get; set; } = 1.41;
    public EqFilterType FilterType { get; set; } = EqFilterType.Peaking;
    public bool Enabled { get; set; } = true;

    public EqBand Clone() => new()
    {
        Index = Index,
        Frequency = Frequency,
        GainDb = GainDb,
        Q = Q,
        FilterType = FilterType,
        Enabled = Enabled
    };
}

public sealed class CompressorSettings
{
    public bool Enabled { get; set; } = false;
    public double ThresholdDb { get; set; } = -22.0;
    public double Ratio { get; set; } = 4.5;
    public double AttackMs { get; set; } = 3.0;
    public double ReleaseMs { get; set; } = 110.0;
    public double MakeupGainDb { get; set; } = 5.0;
    public double SoftKneeDb { get; set; } = 6.0;
    public bool LimiterEnabled { get; set; } = true;
    public double LimiterCeilingDb { get; set; } = -0.5;

    public CompressorSettings Clone() => new()
    {
        Enabled = Enabled,
        ThresholdDb = ThresholdDb,
        Ratio = Ratio,
        AttackMs = AttackMs,
        ReleaseMs = ReleaseMs,
        MakeupGainDb = MakeupGainDb,
        SoftKneeDb = SoftKneeDb,
        LimiterEnabled = LimiterEnabled,
        LimiterCeilingDb = LimiterCeilingDb
    };
}

public sealed class EqProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Flat / Studio";
    public string Category { get; set; } = "General";
    public string Description { get; set; } = string.Empty;
    public List<string> ProcessNames { get; set; } = new();
    public double PreampDb { get; set; } = 0.0;
    public List<EqBand> Bands { get; set; } = new();
    public CompressorSettings Compressor { get; set; } = new();
    public bool IsBuiltIn { get; set; } = false;

    public EqProfile Clone(string? newName = null, bool isBuiltIn = false) => new()
    {
        Id = newName is null ? Id : Guid.NewGuid().ToString("N"),
        Name = newName ?? Name,
        Category = Category,
        Description = Description,
        ProcessNames = new List<string>(ProcessNames),
        PreampDb = PreampDb,
        Bands = Bands.Select(b => b.Clone()).ToList(),
        Compressor = Compressor.Clone(),
        IsBuiltIn = isBuiltIn
    };
}

public enum AudioEngineMode
{
    ApoSyncAndMonitor,
    WasapiDspBridge,
    SoundTestLab
}

public sealed class AppSettings
{
    public AudioEngineMode EngineMode { get; set; } = AudioEngineMode.ApoSyncAndMonitor;
    public string? CaptureDeviceId { get; set; }
    public string? RenderDeviceId { get; set; }
    public string ApoConfigDirectory { get; set; } = @"C:\Program Files\EqualizerAPO\config";
    public bool AutoSyncApo { get; set; } = true;
    public bool AutoGameDetect { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string ActiveProfileId { get; set; } = "cs2-precision";
    public string FallbackProfileId { get; set; } = "flat-studio";
    public int BufferMs { get; set; } = 12;
    public string GitHubRepo { get; set; } = "santiniss/FootStep-EQ";
    public List<EqProfile> CustomProfiles { get; set; } = new();
}
