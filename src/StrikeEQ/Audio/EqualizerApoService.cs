using System.Globalization;
using System.IO;
using System.Text;
using StrikeEQ.Models;

namespace StrikeEQ.Audio;

/// <summary>
/// Integrates with Equalizer APO (zero-latency Windows APO driver) by generating
/// a live 'strikeeq_active.txt' filter config and optionally wiring 'Include: strikeeq_active.txt'
/// into Equalizer APO's config.txt.
/// </summary>
public sealed class EqualizerApoService
{
    public const string ActiveIncludeFileName = "strikeeq_active.txt";

    public string ConfigDirectory { get; set; } = @"C:\Program Files\EqualizerAPO\config";

    public string MainConfigPath => Path.Combine(ConfigDirectory, "config.txt");
    public string ActiveProfilePath => Path.Combine(ConfigDirectory, ActiveIncludeFileName);

    public bool IsInstalled => Directory.Exists(ConfigDirectory) && File.Exists(MainConfigPath);

    public bool IsLinkedToMainConfig
    {
        get
        {
            if (!IsInstalled) return false;
            try
            {
                string content = File.ReadAllText(MainConfigPath);
                return content.Contains(ActiveIncludeFileName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string GenerateApoConfigText(EqProfile profile, bool bypassed)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.AppendLine($"# StrikeEQ — Competitive Gaming Equalizer (Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine($"# Profile: {profile.Name} ({profile.Category})");

        if (bypassed)
        {
            sb.AppendLine("# STATUS: BYPASSED");
            sb.AppendLine("Preamp: 0.0 dB");
            return sb.ToString();
        }

        sb.AppendLine(string.Format(inv, "Preamp: {0:0.00} dB", profile.PreampDb));
        sb.AppendLine();

        foreach (var band in profile.Bands.OrderBy(b => b.Frequency))
        {
            string state = band.Enabled ? "ON" : "OFF";
            string line = band.FilterType switch
            {
                EqFilterType.Peaking => string.Format(inv, "Filter: {0} PK Fc {1:0.#} Hz Gain {2:0.00} dB Q {3:0.00}",
                    state, band.Frequency, band.GainDb, band.Q),
                EqFilterType.LowShelf => string.Format(inv, "Filter: {0} LSC Fc {1:0.#} Hz Gain {2:0.00} dB Q {3:0.00}",
                    state, band.Frequency, band.GainDb, band.Q),
                EqFilterType.HighShelf => string.Format(inv, "Filter: {0} HSC Fc {1:0.#} Hz Gain {2:0.00} dB Q {3:0.00}",
                    state, band.Frequency, band.GainDb, band.Q),
                EqFilterType.HighPass => string.Format(inv, "Filter: {0} HPQ Fc {1:0.#} Hz Q {2:0.00}",
                    state, band.Frequency, band.Q),
                EqFilterType.LowPass => string.Format(inv, "Filter: {0} LPQ Fc {1:0.#} Hz Q {2:0.00}",
                    state, band.Frequency, band.Q),
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(line))
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    public bool SyncProfile(EqProfile profile, bool bypassed, out string status)
    {
        if (!IsInstalled)
        {
            status = "Equalizer APO não detectado em C:\\Program Files\\EqualizerAPO\\config";
            return false;
        }

        try
        {
            string text = GenerateApoConfigText(profile, bypassed);
            File.WriteAllText(ActiveProfilePath, text, Encoding.UTF8);
            status = IsLinkedToMainConfig
                ? $"APO Sincronizado ({(bypassed ? "Bypass" : profile.Name)})"
                : "APO gravado — clique em 'Vincular config.txt' para ativar";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            status = "Permissão negada no Equalizer APO (execute como Administrador ou ajuste permissão da pasta config).";
            return false;
        }
        catch (Exception ex)
        {
            status = $"Erro ao sincronizar APO: {ex.Message}";
            return false;
        }
    }

    public bool LinkToMainConfig(out string message)
    {
        if (!IsInstalled)
        {
            message = "Equalizer APO não encontrado. Instale o Equalizer APO ou use o modo WASAPI DSP Bridge.";
            return false;
        }

        try
        {
            string existing = File.ReadAllText(MainConfigPath);
            if (existing.Contains(ActiveIncludeFileName, StringComparison.OrdinalIgnoreCase))
            {
                message = "StrikeEQ já está vinculado ao config.txt do Equalizer APO.";
                return true;
            }

            string backupPath = MainConfigPath + ".strikeeq.bak";
            if (!File.Exists(backupPath))
                File.Copy(MainConfigPath, backupPath);

            string updated = existing.TrimEnd() + Environment.NewLine + Environment.NewLine
                + "# StrikeEQ Competitive Gaming Audio Equalizer" + Environment.NewLine
                + $"Include: {ActiveIncludeFileName}" + Environment.NewLine;

            File.WriteAllText(MainConfigPath, updated, Encoding.UTF8);
            message = "Vinculado com sucesso ao config.txt do Equalizer APO (backup criado).";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            message = "Permissão negada ao editar config.txt. Execute o StrikeEQ como Administrador para vincular.";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Falha ao vincular: {ex.Message}";
            return false;
        }
    }
}
