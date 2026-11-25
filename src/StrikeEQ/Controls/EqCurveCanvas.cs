using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StrikeEQ.Dsp;
using StrikeEQ.Models;

namespace StrikeEQ.Controls;

/// <summary>
/// Gamer-focused Obsidian Black & Crimson Red interactive EQ curve with
/// live spectrum bars, glowing crimson response line, and intuitive drag nodes.
/// </summary>
public sealed class EqCurveCanvas : FrameworkElement
{
    private const double MinFreq = 20.0;
    private const double MaxFreq = 20000.0;
    private const double MinDb = -15.0;
    private const double MaxDb = 15.0;
    private const double PadLeft = 40.0;
    private const double PadRight = 18.0;
    private const double PadTop = 28.0;
    private const double PadBottom = 26.0;

    private static readonly double[] GridFreqs = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private static readonly double[] GridDbs = [-12, -6, 0, 6, 12];

    private EqProfile? _profile;
    private DspChain? _dspChain;
    private readonly float[] _fftBars = new float[FftAnalyzer.BarCount];
    private readonly float[] _fftPeaks = new float[FftAnalyzer.BarCount];

    private int _selectedBandIndex = 6;
    private int _hoveredBandIndex = -1;
    private int _draggingBandIndex = -1;
    private bool _bypassed;

    private readonly Brush _bgBrush;
    private readonly Brush _footstepZoneBrush;
    private readonly Brush _fftFillBrush;
    private readonly Pen _gridPen;
    private readonly Pen _zeroLinePen;
    private readonly Pen _fftPeakPen;
    private readonly Pen _curveGlowOuterPen;
    private readonly Pen _curveGlowPen;
    private readonly Pen _curvePen;
    private readonly Pen _curveBypassPen;
    private readonly Brush _curveFillBrush;
    private readonly Brush _textMutedBrush;
    private readonly Brush _textAccentBrush;
    private readonly Brush _tooltipBgBrush;
    private readonly Pen _tooltipBorderPen;
    private readonly Typeface _uiTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private readonly Typeface _monoTypeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public event Action<EqBand>? BandChanged;
    public event Action<int>? SelectedBandChanged;

    public int SelectedBandIndex
    {
        get => _selectedBandIndex;
        set
        {
            if (_selectedBandIndex != value)
            {
                _selectedBandIndex = value;
                InvalidateVisual();
            }
        }
    }

    public bool Bypassed
    {
        get => _bypassed;
        set
        {
            _bypassed = value;
            InvalidateVisual();
        }
    }

    public EqCurveCanvas()
    {
        ClipToBounds = true;
        Focusable = true;

        _bgBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromRgb(10, 7, 9), 0.0),
                new(Color.FromRgb(18, 9, 13), 0.55),
                new(Color.FromRgb(9, 6, 8), 1.0)
            },
            new Point(0, 0), new Point(1, 1));
        _bgBrush.Freeze();

        _footstepZoneBrush = new LinearGradientBrush(
            Color.FromArgb(46, 255, 42, 74),
            Color.FromArgb(8, 255, 90, 50),
            90.0);
        _footstepZoneBrush.Freeze();

        _fftFillBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(145, 255, 40, 75), 0.0),
                new(Color.FromArgb(80, 195, 16, 48), 0.55),
                new(Color.FromArgb(16, 80, 8, 20), 1.0)
            },
            90.0);
        _fftFillBrush.Freeze();

        _gridPen = new Pen(new SolidColorBrush(Color.FromArgb(42, 220, 120, 145)), 1.0);
        _gridPen.Freeze();

        _zeroLinePen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 90, 115)), 1.2);
        _zeroLinePen.Freeze();

        _fftPeakPen = new Pen(new SolidColorBrush(Color.FromArgb(145, 255, 105, 85)), 1.2);
        _fftPeakPen.Freeze();

        _curveGlowOuterPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 28, 60)), 11.0) { LineJoin = PenLineJoin.Round };
        _curveGlowOuterPen.Freeze();

        _curveGlowPen = new Pen(new SolidColorBrush(Color.FromArgb(95, 255, 45, 72)), 5.5) { LineJoin = PenLineJoin.Round };
        _curveGlowPen.Freeze();

        var curveGrad = new LinearGradientBrush(
            Color.FromRgb(255, 40, 72),
            Color.FromRgb(255, 105, 62),
            0.0);
        curveGrad.Freeze();
        _curvePen = new Pen(curveGrad, 2.6) { LineJoin = PenLineJoin.Round };
        _curvePen.Freeze();

        _curveBypassPen = new Pen(new SolidColorBrush(Color.FromArgb(130, 140, 125, 132)), 2.0)
        {
            DashStyle = DashStyles.Dash,
            LineJoin = PenLineJoin.Round
        };
        _curveBypassPen.Freeze();

        _curveFillBrush = new LinearGradientBrush(
            Color.FromArgb(72, 255, 36, 66),
            Color.FromArgb(8, 255, 36, 66),
            90.0);
        _curveFillBrush.Freeze();

        _textMutedBrush = new SolidColorBrush(Color.FromRgb(155, 130, 140));
        _textMutedBrush.Freeze();

        _textAccentBrush = new SolidColorBrush(Color.FromRgb(255, 92, 108));
        _textAccentBrush.Freeze();

        _tooltipBgBrush = new SolidColorBrush(Color.FromArgb(240, 16, 10, 13));
        _tooltipBgBrush.Freeze();

        _tooltipBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 40, 72)), 1.2);
        _tooltipBorderPen.Freeze();
    }

    public void Bind(EqProfile profile, DspChain dspChain)
    {
        _profile = profile;
        _dspChain = dspChain;
        InvalidateVisual();
    }

    public void RefreshFrame()
    {
        if (_dspChain is not null)
        {
            _dspChain.FftAnalyzer.GetSpectrumSnapshot(_fftBars, _fftPeaks);
        }
        InvalidateVisual();
    }

    private Rect PlotRect => new(
        PadLeft,
        PadTop,
        Math.Max(10, ActualWidth - PadLeft - PadRight),
        Math.Max(10, ActualHeight - PadTop - PadBottom));

    private double FreqToX(double freq, Rect plot)
    {
        double logMin = Math.Log10(MinFreq);
        double logMax = Math.Log10(MaxFreq);
        double t = (Math.Log10(Math.Clamp(freq, MinFreq, MaxFreq)) - logMin) / (logMax - logMin);
        return plot.Left + t * plot.Width;
    }

    private double XToFreq(double x, Rect plot)
    {
        double t = Math.Clamp((x - plot.Left) / plot.Width, 0.0, 1.0);
        return MinFreq * Math.Pow(MaxFreq / MinFreq, t);
    }

    private double DbToY(double db, Rect plot)
    {
        double t = (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb);
        return plot.Bottom - t * plot.Height;
    }

    private double YToDb(double y, Rect plot)
    {
        double t = Math.Clamp((plot.Bottom - y) / plot.Height, 0.0, 1.0);
        return MinDb + t * (MaxDb - MinDb);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 40) return;

        var plot = PlotRect;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 1. Background
        dc.DrawRoundedRectangle(_bgBrush, null, new Rect(0, 0, w, h), 8, 8);

        // 2. Highlight Footsteps Zone (1.5 kHz - 5.5 kHz)
        double xFootStart = FreqToX(1500, plot);
        double xFootEnd = FreqToX(5500, plot);
        dc.DrawRectangle(_footstepZoneBrush, null, new Rect(xFootStart, plot.Top, xFootEnd - xFootStart, plot.Height));

        // Gamer-friendly zone headers
        DrawZoneLabel(dc, "EXPLOSÕES", FreqToX(22, plot), FreqToX(120, plot), 7, _textMutedBrush, dpi);
        DrawZoneLabel(dc, "GRAVES", FreqToX(120, plot), FreqToX(420, plot), 7, _textMutedBrush, dpi);
        DrawZoneLabel(dc, "VOZ / TIROS", FreqToX(420, plot), FreqToX(1500, plot), 7, _textMutedBrush, dpi);
        DrawZoneLabel(dc, "★ PASSOS", xFootStart, xFootEnd, 7, _textAccentBrush, dpi);
        DrawZoneLabel(dc, "DETALHES", xFootEnd, plot.Right, 7, _textMutedBrush, dpi);

        // 3. Grid lines
        foreach (double f in GridFreqs)
        {
            double x = FreqToX(f, plot);
            dc.DrawLine(_gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            string label = f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
            var ft = MakeText(label, 10, _textMutedBrush, _monoTypeface, dpi);
            dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 6));
        }

        foreach (double db in GridDbs)
        {
            double y = DbToY(db, plot);
            dc.DrawLine(db == 0 ? _zeroLinePen : _gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            string label = db > 0 ? $"+{db:0}" : $"{db:0}";
            var ft = MakeText(label, 10, _textMutedBrush, _monoTypeface, dpi);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 7, y - ft.Height / 2));
        }

        // 4. Live Spectrum Bars
        DrawSpectrum(dc, plot);

        // 5. EQ Curve + Draggable Nodes
        if (_profile is not null && _dspChain is not null)
        {
            DrawEqCurve(dc, plot);
            DrawBandNodes(dc, plot, dpi);
        }
    }

    private void DrawSpectrum(DrawingContext dc, Rect plot)
    {
        int count = _fftBars.Length;
        double barW = plot.Width / count;

        var peakGeom = new StreamGeometry();
        using (var ctx = peakGeom.Open())
        {
            bool started = false;
            for (int i = 0; i < count; i++)
            {
                float level = _fftBars[i];
                double x = plot.Left + i * barW;
                if (level > 0.01f)
                {
                    double barH = level * plot.Height * 0.88;
                    dc.DrawRectangle(_fftFillBrush, null, new Rect(x + 1.0, plot.Bottom - barH, Math.Max(1.5, barW - 2.0), barH));
                }

                double peakY = plot.Bottom - _fftPeaks[i] * plot.Height * 0.88;
                var pt = new Point(x + barW * 0.5, peakY);
                if (!started) { ctx.BeginFigure(pt, false, false); started = true; }
                else ctx.LineTo(pt, true, true);
            }
        }
        peakGeom.Freeze();
        dc.DrawGeometry(null, _fftPeakPen, peakGeom);
    }

    private void DrawEqCurve(DrawingContext dc, Rect plot)
    {
        const int samples = 220;
        double zeroY = DbToY(0, plot);
        var points = new Point[samples];

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / (samples - 1);
            double x = plot.Left + t * plot.Width;
            double freq = XToFreq(x, plot);
            // Plot pure EQ curve (without hidden preamp offset so nodes always sit cleanly on the curve)
            double db = 0.0;
            foreach (var b in _profile!.Bands)
                db += BiquadFilter.ComputeBandMagnitudeDb(b, freq);
            points[i] = new Point(x, DbToY(db, plot));
        }

        var areaGeom = new StreamGeometry();
        using (var ctx = areaGeom.Open())
        {
            ctx.BeginFigure(new Point(plot.Left, zeroY), true, true);
            for (int i = 0; i < samples; i++)
                ctx.LineTo(points[i], true, true);
            ctx.LineTo(new Point(plot.Right, zeroY), true, true);
        }
        areaGeom.Freeze();
        if (!_bypassed)
            dc.DrawGeometry(_curveFillBrush, null, areaGeom);

        var curveGeom = new StreamGeometry();
        using (var ctx = curveGeom.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < samples; i++)
                ctx.LineTo(points[i], true, true);
        }
        curveGeom.Freeze();

        if (_bypassed)
        {
            dc.DrawGeometry(null, _curveBypassPen, curveGeom);
        }
        else
        {
            dc.DrawGeometry(null, _curveGlowOuterPen, curveGeom);
            dc.DrawGeometry(null, _curveGlowPen, curveGeom);
            dc.DrawGeometry(null, _curvePen, curveGeom);
        }
    }

    private void DrawBandNodes(DrawingContext dc, Rect plot, double dpi)
    {
        if (_profile is null) return;

        for (int i = 0; i < _profile.Bands.Count; i++)
        {
            var band = _profile.Bands[i];
            double x = FreqToX(band.Frequency, plot);
            double y = DbToY(band.GainDb, plot);
            bool isSelected = i == _selectedBandIndex;
            bool isHovered = i == _hoveredBandIndex || i == _draggingBandIndex;

            double radius = isHovered ? 9.5 : isSelected ? 7.5 : 6.0;

            Color nodeColor = !band.Enabled || _bypassed
                ? Color.FromRgb(120, 105, 112)
                : band.Frequency is >= 1400 and <= 5600
                    ? Color.FromRgb(255, 110, 68) // Bright ember for footsteps zone
                    : Color.FromRgb(255, 36, 66); // Vivid crimson red

            if (isSelected || isHovered)
            {
                var halo = new SolidColorBrush(Color.FromArgb(75, nodeColor.R, nodeColor.G, nodeColor.B));
                dc.DrawEllipse(halo, null, new Point(x, y), radius + 6.0, radius + 6.0);
            }

            var fill = new SolidColorBrush(Color.FromRgb(18, 10, 14));
            var stroke = new Pen(new SolidColorBrush(nodeColor), isSelected ? 2.6 : 1.9);
            dc.DrawEllipse(fill, stroke, new Point(x, y), radius, radius);

            var dot = new SolidColorBrush(nodeColor);
            dc.DrawEllipse(dot, null, new Point(x, y), 2.3, 2.3);
        }

        int activeIdx = _draggingBandIndex >= 0 ? _draggingBandIndex : _hoveredBandIndex;
        if (activeIdx >= 0 && activeIdx < _profile.Bands.Count)
        {
            var b = _profile.Bands[activeIdx];
            double nx = FreqToX(b.Frequency, plot);
            double ny = DbToY(b.GainDb, plot);

            string freqStr = b.Frequency >= 1000 ? $"{b.Frequency / 1000.0:0.#} kHz" : $"{b.Frequency:0} Hz";
            string gainStr = b.GainDb >= 0 ? $"+{b.GainDb:0.1} dB" : $"{b.GainDb:0.1} dB";
            string hud = $"{freqStr}  •  {gainStr}";

            var ft = MakeText(hud, 11.5, Brushes.White, _monoTypeface, dpi);
            double boxW = ft.Width + 16;
            double boxH = ft.Height + 10;
            double bx = Math.Clamp(nx - boxW / 2, plot.Left + 4, plot.Right - boxW - 4);
            double by = ny - boxH - 16;
            if (by < plot.Top + 4) by = ny + 16;

            dc.DrawRoundedRectangle(_tooltipBgBrush, _tooltipBorderPen, new Rect(bx, by, boxW, boxH), 6, 6);
            dc.DrawText(ft, new Point(bx + 8, by + 5));
        }
    }

    private void DrawZoneLabel(DrawingContext dc, string text, double x0, double x1, double y, Brush brush, double dpi)
    {
        double width = x1 - x0;
        if (width < 58) return;
        var ft = MakeText(text, 9.5, brush, _uiTypeface, dpi);
        dc.DrawText(ft, new Point(x0 + (width - ft.Width) / 2, y));
    }

    private static FormattedText MakeText(string text, double size, Brush brush, Typeface typeface, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);

    private int HitTestBand(Point pt)
    {
        if (_profile is null) return -1;
        var plot = PlotRect;
        int best = -1;
        double bestDist = 20.0;

        for (int i = 0; i < _profile.Bands.Count; i++)
        {
            var b = _profile.Bands[i];
            double dx = pt.X - FreqToX(b.Frequency, plot);
            double dy = pt.Y - DbToY(b.GainDb, plot);
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_profile is null) return;
        Point pos = e.GetPosition(this);
        var plot = PlotRect;

        if (_draggingBandIndex >= 0 && _draggingBandIndex < _profile.Bands.Count)
        {
            var band = _profile.Bands[_draggingBandIndex];
            // Vertical-first intuitive gamer drag (keeps clean 10-band spacing unless Shift is held for horizontal shift)
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                band.Frequency = Math.Round(Math.Clamp(XToFreq(pos.X, plot), 20.0, 18500.0), 0);

            band.GainDb = Math.Round(Math.Clamp(YToDb(pos.Y, plot), MinDb, MaxDb), 1);
            BandChanged?.Invoke(band);
            InvalidateVisual();
            return;
        }

        int hit = HitTestBand(pos);
        if (hit != _hoveredBandIndex)
        {
            _hoveredBandIndex = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_profile is null) return;
        Point pos = e.GetPosition(this);
        int hit = HitTestBand(pos);

        if (hit >= 0)
        {
            _selectedBandIndex = hit;
            SelectedBandChanged?.Invoke(hit);

            if (e.ClickCount == 2)
            {
                var band = _profile.Bands[hit];
                band.GainDb = 0.0;
                BandChanged?.Invoke(band);
                InvalidateVisual();
                return;
            }

            _draggingBandIndex = hit;
            CaptureMouse();
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_draggingBandIndex >= 0)
        {
            _draggingBandIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_draggingBandIndex < 0 && _hoveredBandIndex != -1)
        {
            _hoveredBandIndex = -1;
            InvalidateVisual();
        }
    }
}
