using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using StrikeEQ.Models;

namespace StrikeEQ.Services;

/// <summary>
/// Windows System Tray integration with a crisp custom-drawn tactical EQ icon
/// and quick profile / bypass / Scout Mode context menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private readonly ContextMenuStrip _menu;

    public event Action? RestoreRequested;
    public event Action? ToggleBypassRequested;
    public event Action? ToggleScoutModeRequested;
    public event Action<string>? ProfileSelected;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _appIcon = CreateTacticalIcon();
        _menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(20, 24, 33),
            ForeColor = Color.FromArgb(230, 236, 245)
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "StrikeEQ — Equalizador Gamer",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke();
    }

    public void RebuildMenu(IReadOnlyList<EqProfile> profiles, string activeProfileId, bool bypassed, bool scoutEnabled)
    {
        _menu.Items.Clear();

        var openItem = new ToolStripMenuItem("Abrir StrikeEQ") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        openItem.Click += (_, _) => RestoreRequested?.Invoke();
        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());

        var bypassItem = new ToolStripMenuItem(bypassed ? "○ Equalizador Desligado" : "● Equalizador Ligado (Ctrl+Alt+B)");
        bypassItem.Click += (_, _) => ToggleBypassRequested?.Invoke();
        _menu.Items.Add(bypassItem);

        var scoutItem = new ToolStripMenuItem(scoutEnabled ? "● Modo Passos Ativo" : "Ativar Modo Passos (Ctrl+Alt+N)");
        scoutItem.Click += (_, _) => ToggleScoutModeRequested?.Invoke();
        _menu.Items.Add(scoutItem);

        _menu.Items.Add(new ToolStripSeparator());

        var profilesHeader = new ToolStripMenuItem("Perfis de Jogo");
        foreach (var p in profiles)
        {
            var item = new ToolStripMenuItem((p.Id == activeProfileId ? "● " : "   ") + p.Name);
            string id = p.Id;
            item.Click += (_, _) => ProfileSelected?.Invoke(id);
            profilesHeader.DropDownItems.Add(item);
        }
        _menu.Items.Add(profilesHeader);

        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Sair do StrikeEQ");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exitItem);

        string activeName = profiles.FirstOrDefault(p => p.Id == activeProfileId)?.Name ?? "StrikeEQ";
        string tooltip = $"StrikeEQ • {(bypassed ? "DESLIGADO" : activeName)}";
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
        }
        catch
        {
            // Ignore balloon notification issues
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateTacticalIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgBrush = new SolidBrush(Color.FromArgb(12, 10, 13));
        using var borderPen = new Pen(Color.FromArgb(255, 36, 66), 2.2f);
        g.FillEllipse(bgBrush, 1, 1, 30, 30);
        g.DrawEllipse(borderPen, 2, 2, 28, 28);

        // Crimson & ember 4-bar equalizer emblem
        using var redBrush = new SolidBrush(Color.FromArgb(255, 36, 66));
        using var emberBrush = new SolidBrush(Color.FromArgb(255, 110, 64));
        g.FillRectangle(redBrush, 7, 17, 3, 8);
        g.FillRectangle(redBrush, 12, 11, 3, 14);
        g.FillRectangle(emberBrush, 17, 7, 3, 18);
        g.FillRectangle(redBrush, 22, 13, 3, 12);

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _appIcon.Dispose();
    }
}
