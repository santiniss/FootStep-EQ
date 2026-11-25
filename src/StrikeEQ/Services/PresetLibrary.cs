using System.IO;
using System.Text.Json;
using StrikeEQ.Models;

namespace StrikeEQ.Services;

public static class PresetLibrary
{
    public static readonly double[] StandardFrequencies =
        [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrikeEQ");

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    public static List<EqProfile> CreateBuiltInProfiles() =>
    [
        BuildProfile(
            id: "flat-studio",
            name: "Flat / Studio Reference",
            category: "Referência",
            description: "Resposta neutra (0 dB). Ideal para música, edição ou perfil padrão fora de jogos.",
            processes: [],
            preampDb: 0.0,
            gains: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            qs: [0.9, 1.1, 1.2, 1.2, 1.2, 1.2, 1.4, 1.4, 1.2, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings { Enabled = false, LimiterEnabled = true, LimiterCeilingDb = -0.3 }
        ),

        BuildProfile(
            id: "cs2-precision",
            name: "CS2 — Precision Footsteps",
            category: "FPS Tático",
            description: "Corta graves excessivos de granadas/AWP (64–250 Hz) e destaca passos no concreto/metal (2–4 kHz) e som de defuse/pino (8 kHz).",
            processes: ["cs2", "csgo"],
            preampDb: -3.5,
            gains: [-4.0, -3.5, -2.5, -2.0, -0.5, 1.5, 4.5, 5.5, 3.0, 1.0],
            qs: [0.8, 1.2, 1.3, 1.4, 1.4, 1.4, 1.6, 1.5, 1.3, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings
            {
                Enabled = true,
                ThresholdDb = -20.0,
                Ratio = 4.0,
                AttackMs = 2.5,
                ReleaseMs = 95.0,
                MakeupGainDb = 4.5,
                SoftKneeDb = 6.0,
                LimiterEnabled = true,
                LimiterCeilingDb = -0.5
            }
        ),

        BuildProfile(
            id: "valorant-clarity",
            name: "Valorant — Spatial Clarity",
            category: "FPS Tático",
            description: "Realce focado entre 1–4 kHz para passos, teleporte, skills e spike plant sem fadiga nos agudos extremos.",
            processes: ["VALORANT-Win64-Shipping", "VALORANT"],
            preampDb: -3.0,
            gains: [-3.5, -3.0, -2.0, -1.5, 0.5, 2.0, 4.0, 4.5, 1.5, -0.5],
            qs: [0.8, 1.2, 1.3, 1.3, 1.3, 1.5, 1.6, 1.6, 1.4, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings
            {
                Enabled = true,
                ThresholdDb = -21.0,
                Ratio = 3.5,
                AttackMs = 3.0,
                ReleaseMs = 110.0,
                MakeupGainDb = 3.8,
                SoftKneeDb = 6.0,
                LimiterEnabled = true,
                LimiterCeilingDb = -0.5
            }
        ),

        BuildProfile(
            id: "warzone-footsteps",
            name: "Warzone / COD — Anti-Mud",
            category: "Battle Royale",
            description: "Remove o 'boomy' pesado de airstrikes e explosões (<250 Hz) enquanto amplifica passos pesados, placas de colete e paraquedas (2–6 kHz).",
            processes: ["cod", "ModernWarfare", "BlackOpsColdWar"],
            preampDb: -4.0,
            gains: [-6.5, -5.5, -4.0, -2.5, 0.0, 2.0, 5.0, 6.0, 4.0, 1.5],
            qs: [0.8, 1.1, 1.2, 1.3, 1.3, 1.4, 1.5, 1.4, 1.3, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings
            {
                Enabled = true,
                ThresholdDb = -24.0,
                Ratio = 5.5,
                AttackMs = 2.0,
                ReleaseMs = 85.0,
                MakeupGainDb = 6.0,
                SoftKneeDb = 6.0,
                LimiterEnabled = true,
                LimiterCeilingDb = -0.5
            }
        ),

        BuildProfile(
            id: "apex-legends",
            name: "Apex Legends — Tracking",
            category: "Battle Royale",
            description: "Separa passos metálicos, tirolesas e recargas no meio do caos de habilidades e tiros de energia.",
            processes: ["r5apex", "r5apex_dx12"],
            preampDb: -3.5,
            gains: [-4.5, -3.5, -2.5, -1.5, 0.5, 2.5, 4.5, 5.0, 2.5, 0.5],
            qs: [0.8, 1.2, 1.3, 1.3, 1.4, 1.5, 1.6, 1.5, 1.3, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings
            {
                Enabled = true,
                ThresholdDb = -22.0,
                Ratio = 4.5,
                AttackMs = 2.5,
                ReleaseMs = 100.0,
                MakeupGainDb = 5.0,
                SoftKneeDb = 6.0,
                LimiterEnabled = true,
                LimiterCeilingDb = -0.5
            }
        ),

        BuildProfile(
            id: "r6-siege",
            name: "Rainbow Six Siege — Intel",
            category: "FPS Tático",
            description: "Máxima definição em ruídos sutis (passos agachados, drones, barricadas quebrando e C4) com compressão dinâmica agressiva.",
            processes: ["RainbowSix", "RainbowSix_Vulkan", "RainbowSix_DX11"],
            preampDb: -4.0,
            gains: [-5.0, -4.0, -3.0, -2.0, 0.5, 2.5, 5.5, 5.5, 3.5, 1.5],
            qs: [0.8, 1.2, 1.3, 1.4, 1.4, 1.5, 1.7, 1.5, 1.3, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings
            {
                Enabled = true,
                ThresholdDb = -25.0,
                Ratio = 6.0,
                AttackMs = 2.0,
                ReleaseMs = 80.0,
                MakeupGainDb = 6.5,
                SoftKneeDb = 5.0,
                LimiterEnabled = true,
                LimiterCeilingDb = -0.4
            }
        ),

        BuildProfile(
            id: "footsteps-hyper",
            name: "Footsteps Hyper-Boost",
            category: "Especializado",
            description: "Curva agressiva de passos universal (Fortnite, PUBG, Tarkov, Overwatch): corte forte em subgraves e pico focado em 2–5 kHz.",
            processes: ["TslGame", "EscapeFromTarkov", "FortniteClient-Win64-Shipping", "Overwatch"],
            preampDb: -5.0,
            gains: [-7.5, -6.0, -4.5, -3.0, -0.5, 1.5, 6.0, 7.0, 4.0, 1.0],
            qs: [0.75, 1.1, 1.2, 1.3, 1.4, 1.5, 1.5, 1.4, 1.3, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings
            {
                Enabled = true,
                ThresholdDb = -24.0,
                Ratio = 5.5,
                AttackMs = 1.8,
                ReleaseMs = 85.0,
                MakeupGainDb = 6.5,
                SoftKneeDb = 5.0,
                LimiterEnabled = true,
                LimiterCeilingDb = -0.4
            }
        ),

        BuildProfile(
            id: "night-scout",
            name: "Scout / Night Mode (Proteção Auditiva)",
            category: "Especializado",
            description: "Nivelamento máximo: comprime fortemente tiros e explosões para proteger seus ouvidos enquanto eleva passos distantes.",
            processes: [],
            preampDb: -2.0,
            gains: [-5.0, -3.5, -2.0, -1.0, 0.5, 1.5, 3.5, 3.0, 1.0, -1.5],
            qs: [0.8, 1.1, 1.2, 1.3, 1.3, 1.4, 1.5, 1.5, 1.3, 0.9],
            types: DefaultFilterTypes(),
            compressor: new CompressorSettings
            {
                Enabled = true,
                ThresholdDb = -28.0,
                Ratio = 8.0,
                AttackMs = 1.2,
                ReleaseMs = 75.0,
                MakeupGainDb = 8.5,
                SoftKneeDb = 6.0,
                LimiterEnabled = true,
                LimiterCeilingDb = -0.8
            }
        )
    ];

    private static EqFilterType[] DefaultFilterTypes() =>
    [
        EqFilterType.LowShelf,
        EqFilterType.Peaking,
        EqFilterType.Peaking,
        EqFilterType.Peaking,
        EqFilterType.Peaking,
        EqFilterType.Peaking,
        EqFilterType.Peaking,
        EqFilterType.Peaking,
        EqFilterType.Peaking,
        EqFilterType.HighShelf
    ];

    private static EqProfile BuildProfile(
        string id,
        string name,
        string category,
        string description,
        string[] processes,
        double preampDb,
        double[] gains,
        double[] qs,
        EqFilterType[] types,
        CompressorSettings compressor)
    {
        var bands = new List<EqBand>(StandardFrequencies.Length);
        for (int i = 0; i < StandardFrequencies.Length; i++)
        {
            bands.Add(new EqBand
            {
                Index = i,
                Frequency = StandardFrequencies[i],
                GainDb = gains[i],
                Q = qs[i],
                FilterType = types[i],
                Enabled = true
            });
        }

        return new EqProfile
        {
            Id = id,
            Name = name,
            Category = category,
            Description = description,
            ProcessNames = processes.ToList(),
            PreampDb = preampDb,
            Bands = bands,
            Compressor = compressor,
            IsBuiltIn = true
        };
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Fall back to defaults on corrupted config
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
