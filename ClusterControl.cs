using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Ets2Cluster;

/// <summary>
/// The instrument cluster, drawn on a fixed 1280 x 720 canvas that is scaled to fit the window:
/// speedometer with needle, gear, rev strip, fuel and AdBlue gauges, coolant and oil temperature gauges, consumption, range, cruise, ETA, clocks, odometer and trip,
/// indicator lamps and warnings.
/// </summary>
public sealed partial class ClusterControl : FrameworkElement
{
    public const double DW = 1280, DH = 720;
    static readonly Point C = new(640, 440);
    const double R = 330;   // radius of the speed dial

    public Truck T = new();
    public Settings Cfg = new();
    public double TripKm, TripLPer100 = -1;
    public string StatusText = "";
    /// <summary>Shown as a small notice at the bottom when a newer release exists.</summary>
    public string UpdateText = "";

    // smoothed values (the needle glides between telemetry updates)
    double dispSpeed, dispRpm, dispFuel, dispAdBlue, dispWater, dispOil;
    double dispLight;   // instrument backlight, 0 = off .. 1 = on (follows the truck's lights)

    static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static readonly Brush BgBrush = Fz(Color.FromRgb(3, 6, 10));
    static readonly Brush White = Fz(Color.FromRgb(236, 241, 246)), Dim = Fz(Color.FromRgb(124, 136, 148)), Grey = Fz(Color.FromRgb(58, 68, 79));
    static readonly Brush Green = Fz(Color.FromRgb(56, 214, 108)), Amber = Fz(Color.FromRgb(255, 176, 46)), Red = Fz(Color.FromRgb(255, 59, 48)), Blue = Fz(Color.FromRgb(58, 160, 255));
    static readonly Typeface Face = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface FaceLight = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    readonly Dictionary<string, FormattedText> texts = new();

    public double DebugSpeed => dispSpeed;
    public ClusterControl() { SnapsToDevicePixels = false; UseLayoutRounding = false; }

    // ------------------------------------------------------------------------------------------ helpers

    FormattedText Text(string t, double size, Brush b, bool light = false)
    {
        string key = t + "|" + size.ToString("0.#", CultureInfo.InvariantCulture) + "|" + (light ? "l" : "b") + "|" + b.GetHashCode();
        if (!texts.TryGetValue(key, out var ft))
        {
            if (texts.Count > 3000) texts.Clear();
            ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, light ? FaceLight : Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            texts[key] = ft;
        }
        return ft;
    }

    /// <summary>align: 0 left, 1 centre, 2 right of x; y is the vertical centre.</summary>
    double Draw(DrawingContext dc, string t, double x, double y, double size, Brush b, int align = 1, bool light = false)
    {
        var ft = Text(t, size, b, light);
        double lx = align == 0 ? x : align == 1 ? x - ft.Width / 2 : x - ft.Width;
        dc.DrawText(ft, new Point(lx, y - ft.Height / 2));
        return ft.Width;
    }

    static Point P(double angleDeg, double r) { double a = angleDeg * Math.PI / 180; return new Point(C.X + r * Math.Cos(a), C.Y - r * Math.Sin(a)); }

    static StreamGeometry Arc(double r, double a0, double a1)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(P(a0, r), false, false);
        c.ArcTo(P(a1, r), new Size(r, r), 0, Math.Abs(a1 - a0) > 180, a1 < a0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        g.Freeze();
        return g;
    }

    static Pen PenOf(Brush b, double w, bool round = false) { var p = new Pen(b, w); if (round) { p.StartLineCap = PenLineCap.Round; p.EndLineCap = PenLineCap.Round; } p.Freeze(); return p; }

    // ------------------------------------------------------------------------------------------ animation

    /// <summary>Moves the smoothed values towards the telemetry; returns true while anything is still moving.</summary>
    public bool Step(double dt)
    {
        double k = 1 - Math.Exp(-dt * 12);
        bool moving = false;
        void Ease(ref double cur, double target, double eps)
        {
            double d = target - cur;
            if (Math.Abs(d) < eps) { if (d != 0) { cur = target; moving = true; } return; }
            cur += d * k; moving = true;
        }
        Ease(ref dispSpeed, Math.Max(0, T.SpeedKmh), 0.03);
        Ease(ref dispRpm, Math.Max(0, T.RpmValue), 2);
        Ease(ref dispFuel, T.FuelFraction, 0.001);
        Ease(ref dispAdBlue, T.AdBlueFraction, 0.001);
        Ease(ref dispWater, Math.Max(0, T.WaterC), 0.05);
        Ease(ref dispOil, Math.Max(0, T.OilC), 0.05);
        // instrument backlight fades in and out over about half a second
        double lt = Cfg.Backlight != "off" && (T.Parking || T.LowBeam || T.HighBeam) ? 1 : 0, ld = lt - dispLight;
        if (Math.Abs(ld) > 0.004) { dispLight += ld * (1 - Math.Exp(-dt * 6)); moving = true; } else if (ld != 0) { dispLight = lt; moving = true; }
        return moving;
    }

    // ------------------------------------------------------------------------------------------ units

    // the Units setting: the game's own units (US units in ATS), metric or imperial
    bool Imp => Cfg.UnitChoice == "imperial" || (Cfg.UnitChoice != "metric" && T.Ats);
    double Spd(double kmh) => Imp ? kmh * 0.621371 : kmh;
    double Dist(double km) => Imp ? km * 0.621371 : km;
    string SpdUnit => Imp ? "mph" : "km/h";
    string DistUnit => Imp ? "mi" : "km";
    string Temp(float c) => c > 0 ? Math.Round(Imp ? c * 9 / 5 + 32 : c).ToString(CultureInfo.InvariantCulture) + (Imp ? "°F" : "°C") : "--";
    /// <summary>Litres per 100 km -> the consumption shown: l/100km, or US miles per gallon in ATS.</summary>
    string Cons(double lPer100) => lPer100 <= 0 ? "--" : (Imp ? 235.215 / lPer100 : lPer100).ToString("0.0", CultureInfo.InvariantCulture);
    string ConsUnit => Imp ? "mpg" : "l/100km";
    string DefName => T.Ats ? "DEF" : "ADBLUE";

    static string Clock(DateTime t) => t == DateTime.MinValue ? "--:--" : t.ToString("HH:mm", CultureInfo.InvariantCulture);
    static string HoursMinutes(float min) { if (min < 0) return "--:--"; int m = (int)Math.Round(min); return (m / 60).ToString(CultureInfo.InvariantCulture) + ":" + (m % 60).ToString("00", CultureInfo.InvariantCulture); }

    // ------------------------------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        if (W < 10 || H < 10) { dc.DrawRectangle(BgBrush, null, new Rect(0, 0, W, H)); return; }
        double s = Math.Min(W / DW, H / DH);
        // dark bars where the window is not 16:9; the cluster area itself is left unpainted here,
        // so the window (which is transparent) shows through outside the rounded top corners
        var area = new Rect((W - DW * s) / 2, (H - DH * s) / 2, DW * s, DH * s);
        dc.DrawGeometry(BgBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, W, H)), new RectangleGeometry(area)));
        dc.PushTransform(new TranslateTransform((W - DW * s) / 2, (H - DH * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        if (Chrome) RenderChrome(dc);
        else
        {
            dc.PushClip(Outline(0));
            Cluster(dc);
            Frame(dc);
            dc.Pop();
        }
        dc.Pop(); dc.Pop();
    }

    const double Corner = 98;   // radius of the two rounded top corners (also the temperature gauges' centre height)

    /// <summary>The cluster outline (rounded top corners, square bottom), inset by d.</summary>
    static StreamGeometry Outline(double d)
    {
        double r = Math.Max(0, Corner - d), x0 = d, y0 = d, x1 = DW - d, y1 = DH - d;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0, y1), true, true);
            c.LineTo(new Point(x0, y0 + r), true, true);
            c.ArcTo(new Point(x0 + r, y0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(x1 - r, y0), true, true);
            c.ArcTo(new Point(x1, y0 + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(x1, y1), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Fine bevelled metal frame: a lit outer edge that darkens downwards and a shadowed inner edge, so it looks raised.</summary>
    static void Frame(DrawingContext dc)
    {
        static Brush V(Color top, Color bottom) { var b = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1)); b.Freeze(); return b; }
        dc.DrawGeometry(null, new Pen(V(Color.FromRgb(150, 162, 174), Color.FromRgb(40, 48, 56)), 6), Outline(3));          // bevel face
        dc.DrawGeometry(null, new Pen(V(Color.FromRgb(215, 225, 235), Color.FromRgb(70, 80, 90)), 1.5), Outline(0.75));    // outer highlight
        dc.DrawGeometry(null, new Pen(V(Color.FromRgb(8, 12, 16), Color.FromRgb(90, 102, 114)), 2.25), Outline(7.125));   // inner shadow
    }

    // ------------------------------------------------------------------------------------------ backlight

    public static readonly (string Id, string Label, Color Color)[] Backlights =
    {
        ("white", "Cool white", Color.FromRgb(170, 215, 255)), ("red", "Red", Color.FromRgb(255, 74, 48)),
        ("amber", "Amber", Color.FromRgb(255, 160, 40)), ("green", "Green", Color.FromRgb(90, 230, 120)), ("off", "Off", Colors.Black),
    };
    Color BackColor { get { foreach (var b in Backlights) if (b.Id == Cfg.Backlight && b.Id != "off") return b.Color; return Backlights[0].Color; } }
    // unlit ticks and numbers brighten towards these, tinted by the backlight colour
    Color LitTick => Mix(Color.FromRgb(140, 146, 152), BackColor, 0.35);
    Color LitNumber => Mix(Color.FromRgb(214, 220, 226), BackColor, 0.2);
    Color Back(double a) => Color.FromArgb((byte)Math.Clamp(a * dispLight, 0, 255), BackColor.R, BackColor.G, BackColor.B);
    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    /// <summary>Unlit markings brighten when the backlight is on.</summary>
    Brush Marking(Brush off, Color lit) => dispLight < 0.01 || off is not SolidColorBrush sc ? off : Fz(Mix(sc.Color, lit, dispLight));

    /// <summary>Soft light shining through the dial from behind, strongest just inside the scale.</summary>
    void BacklightFace(DrawingContext dc, Point c, double r)
    {
        if (dispLight < 0.01) return;
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Back(0), 0));
        b.GradientStops.Add(new GradientStop(Back(14), 0.45));
        b.GradientStops.Add(new GradientStop(Back(0), 0.62));
        b.GradientStops.Add(new GradientStop(Back(52), 0.9));
        b.GradientStops.Add(new GradientStop(Back(0), 1));
        b.Freeze();
        dc.DrawEllipse(b, null, c, r, r);
    }

    /// <summary>Halo drawn under a marking so it looks lit from behind.</summary>
    void Halo(DrawingContext dc, Point a, Point b, double w)
    {
        if (dispLight < 0.01) return;
        dc.DrawLine(PenOf(Fz(Back(55)), w + 6, true), a, b);
    }

    void Cluster(DrawingContext dc)
    {
        var t = T;
        double max = Imp ? 80 : 120;
        double v = Spd(dispSpeed);
        double limit = t.SpeedLimit > 0 ? Spd(t.SpeedLimit) : 0;
        bool over = limit > 0 && v > limit + (Imp ? 2 : 3);
        double Ang(double val) => 200 - 220 * Math.Clamp(val / max, 0, 1.03);

        // background glow
        var glow = new RadialGradientBrush { Center = new Point(0.5, 0.62), GradientOrigin = new Point(0.5, 0.62), RadiusX = 0.62, RadiusY = 0.9 };
        glow.GradientStops.Add(new GradientStop(Color.FromRgb(12, 28, 38), 0)); glow.GradientStops.Add(new GradientStop(Color.FromRgb(3, 6, 10), 1));
        if (Chrome) dc.DrawDrawing(FaceDrawing());
        else dc.DrawRectangle(glow, null, new Rect(0, 0, DW, DH));

        // amber glow beside the dial centre: bright while over the speed limit
        byte a = (byte)(over ? 235 : 38);
        foreach (int side in new[] { 1, -1 })
        {
            double X(double x) => side == 1 ? x : DW - x;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(452), 292), true, true);
                c.LineTo(new Point(X(528), 330), true, false); c.LineTo(new Point(X(506), 410), true, false); c.LineTo(new Point(X(448), 362), true, false);
            }
            var br = new LinearGradientBrush(Color.FromArgb(a, 255, 150, 30), Color.FromArgb(0, 255, 150, 30), new Point(side == 1 ? 0 : 1, 0.4), new Point(side == 1 ? 1 : 0, 0.6));
            dc.DrawGeometry(br, null, g);
        }

        // dial ring
        if (!Chrome) dc.DrawGeometry(null, PenOf(Fz(Color.FromRgb(20, 34, 44)), 8), Arc(R + 16, 204, -24));

        // fuel (left) and AdBlue (right) gauges hugging the dial
        Gauge(dc, 163.5, 200, dispFuel, t.FuelWarn ? Red : Green, true);
        Gauge(dc, 16.5, -20, dispAdBlue, t.AdBlueWarn ? Red : Blue, false);

        // backlight behind the dial (not below the bottom row)
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, DW, 594)));
        BacklightFace(dc, C, R + 6);
        dc.Pop();

        // ticks and numbers
        int labelFrom = Imp ? 40 : 60;
        for (int i = 0; i <= (int)max; i += 2)
        {
            bool major = i % 10 == 0;
            double ang = Ang(i);
            bool lit = i <= v;
            var b = lit ? White : Marking(Grey, LitTick);
            Halo(dc, P(ang, R - (major ? 38 : 20)), P(ang, R), major ? 3.6 : 2);
            dc.DrawLine(PenOf(b, major ? 3.6 : 2), P(ang, R - (major ? 38 : 20)), P(ang, R));
            if (major && i >= labelFrom) { var p = P(ang, R - 72); Draw(dc, i.ToString(CultureInfo.InvariantCulture), p.X, p.Y, 30, lit ? White : Marking(Dim, LitNumber)); }
        }

        // cruise-control setting and speed limit markers
        if (t.CruiseOn && t.CruiseKmh > 1)
            dc.DrawLine(PenOf(Green, 6, true), P(Ang(Spd(t.CruiseKmh)), R - 46), P(Ang(Spd(t.CruiseKmh)), R + 10));
        if (limit > 0 && limit <= max)
        {
            double la = Ang(limit);
            var tip = P(la, R + 2); var l1 = P(la - 1.6, R + 24); var l2 = P(la + 1.6, R + 24);
            var tg = new StreamGeometry();
            using (var c = tg.Open()) { c.BeginFigure(tip, true, true); c.LineTo(l1, true, false); c.LineTo(l2, true, false); }
            dc.DrawGeometry(over ? Red : Amber, null, tg);
        }

        // needle
        double na = Ang(v);
        if (Chrome) dc.DrawLine(PenOf(Fz(Color.FromArgb(40, 255, 59, 48)), 26, true), P(na, R - 150), P(na, R + 2));
        dc.DrawLine(PenOf(Fz(Color.FromArgb(70, 255, 59, 48)), 16, true), P(na, R - 150), P(na, R + 2));
        dc.DrawLine(PenOf(Red, Chrome ? 7 : 6, true), P(na, R - 150), P(na, R + 2));
        if (Chrome) dc.DrawLine(PenOf(Fz(Color.FromArgb(200, 255, 190, 170)), 1.6, true), P(na, R - 146), P(na, R - 2));

        // speed
        double sp = Math.Max(0, v);
        string speedText = Math.Round(sp).ToString(CultureInfo.InvariantCulture);
        double wNum = Draw(dc, speedText, 640, 268, 118, over ? Amber : White, 1);
        Draw(dc, SpdUnit, 640 + wNum / 2 + 8, 308, 26, Dim, 0, true);

        // make
        if (Cfg.ShowMake && t.Make.Length > 0)
        {
            string name = string.Join(" ", t.Make.ToUpperInvariant().ToCharArray());
            Draw(dc, "‹", 640 - 170, 372, 30, Dim, 1, true); Draw(dc, name, 640, 372, 26, Fz(Color.FromRgb(176, 184, 192)), 1, true); Draw(dc, "›", 640 + 170, 372, 30, Dim, 1, true);
        }

        // gear
        Draw(dc, GearText(t), 640, 430, 46, t.Gear < 0 ? Amber : White, 1);

        Revs(dc);
        Panels(dc);
        Lamps(dc);

        if (UpdateText.Length > 0)
        {
            var ft = Text(UpdateText, 21, Amber);
            var rect = new Rect(640 - ft.Width / 2 - 18, 556, ft.Width + 36, 32);
            dc.DrawRoundedRectangle(Fz(Color.FromRgb(30, 22, 8)), PenOf(Amber, 1.6), rect, 8, 8);
            dc.DrawText(ft, new Point(rect.X + 18, rect.Y + 16 - ft.Height / 2));
        }

        if (StatusText.Length > 0)
        {
            dc.DrawRectangle(Fz(Color.FromArgb(150, 3, 6, 10)), null, new Rect(0, 0, DW, DH));
            Draw(dc, StatusText, 640, 330, 44, Dim, 1, true);
        }
    }

    static string GearText(Truck t)
    {
        string prefix = t.Shifter == "automatic" || t.Shifter == "arcade" ? "A " : "";
        if (t.Gear > 0) return prefix + t.Gear.ToString(CultureInfo.InvariantCulture);
        if (t.Gear < 0) return "R" + (t.Gear < -1 ? Math.Abs(t.Gear).ToString(CultureInfo.InvariantCulture) : "");
        return prefix + "N";
    }

    void Gauge(DrawingContext dc, double a0, double a1, double fraction, Brush fill, bool left)
    {
        double r = R + 56;
        if (!Chrome) dc.DrawGeometry(null, PenOf(Fz(Color.FromRgb(24, 36, 46)), 12, true), Arc(r, a0, a1));
        double fa = a1 + (a0 - a1) * Math.Clamp(fraction, 0, 1);   // fills from the bottom end (a1) upwards
        if (fraction > 0.004)
        {
            if (Chrome && fill is SolidColorBrush sc)
            {
                // glowing liquid in a glass tube, with a highlight along the glass
                dc.DrawGeometry(null, PenOf(Fz(Color.FromArgb(60, sc.Color.R, sc.Color.G, sc.Color.B)), 22, true), Arc(r, a1, fa));
                dc.DrawGeometry(null, PenOf(fill, 12, true), Arc(r, a1, fa));
                dc.DrawGeometry(null, PenOf(Fz(Color.FromArgb(110, 255, 255, 255)), 2.4, true), Arc(r - 2.5, a1, fa));
            }
            else dc.DrawGeometry(null, PenOf(fill, 12, true), Arc(r, a1, fa));
        }
        // labels: the level and the name above the top end, 1/2 in the middle, E at the bottom end
        var top = P(a0, r + 40); var mid = P((a0 + a1) / 2, r + 36); var bot = P(a1, r + 34);
        Draw(dc, left ? "DIESEL" : DefName, top.X, top.Y - 40, 16, Dim, 1, true);
        Draw(dc, Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture) + "%", top.X, top.Y - 14, 28, fill, 1);
        Draw(dc, "1/2", mid.X, mid.Y, 18, Dim, 1, true);
        Draw(dc, "E", bot.X, bot.Y, 18, Dim, 1, true);
    }

    void Revs(DrawingContext dc)
    {
        double rmax = T.RpmMax > 0 ? Math.Max(2500, Math.Ceiling(T.RpmMax / 500) * 500) : 2500;
        double x0 = 440, x1 = 840, y = 514;
        double X(double rpm) => x0 + (x1 - x0) * Math.Clamp(rpm / rmax, 0, 1);
        dc.DrawLine(PenOf(Grey, 2), new Point(x0, y), new Point(x1, y));
        for (int rpm = 0; rpm <= rmax; rpm += 100)
        {
            bool major = rpm % 500 == 0 && rpm > 0;
            dc.DrawLine(PenOf(major ? White : Grey, major ? 2.6 : 1.6), new Point(X(rpm), y), new Point(X(rpm), y - (major ? 16 : 8)));
            if (major) Draw(dc, (rpm / 100).ToString(CultureInfo.InvariantCulture), X(rpm), y - 34, 24, Dim);
        }
        bool high = T.RpmMax > 0 && dispRpm > T.RpmMax * 0.92;
        double mx = X(dispRpm);
        dc.DrawRectangle(Fz(Color.FromArgb(70, 255, 59, 48)), null, new Rect(mx - 6, y - 24, 12, 38));
        dc.DrawRectangle(high ? Amber : Red, null, new Rect(mx - 2.5, y - 22, 5, 34));
        Draw(dc, "rpm x100", 640, y + 26, 16, Dim, 1, true);
    }

    void Panels(DrawingContext dc)
    {
        var t = T;
        // top corners: coolant and oil temperature gauges
        // centred over the DIESEL / ADBLUE labels (the top end of the fuel and AdBlue arcs)
        double tx = P(163.5, R + 96).X;
        TempGauge(dc, tx, Corner, "COOLANT", dispWater, t.WaterC, 105, t.WaterWarn);
        TempGauge(dc, DW - tx, Corner, "OIL", dispOil, t.OilC, 120, t.OilWarn);

        // left: consumption, trip consumption, range
        Row(dc, 50, 190, "AVERAGE CONSUMPTION", Cons(t.FuelAvgLPerKm * 100), ConsUnit, White, false);
        Row(dc, 50, 300, "THIS TRIP", Cons(TripLPer100), ConsUnit, White, false);
        Row(dc, 50, 410, "RANGE", t.RangeKm > 0 ? Math.Round(Dist(t.RangeKm)).ToString(CultureInfo.InvariantCulture) : "--", DistUnit, t.FuelWarn ? Amber : White, false);

        // right: cruise, ETA, speed limit
        bool cc = t.CruiseOn && t.CruiseKmh > 1;
        Row(dc, DW - 50, 190, "CRUISE CONTROL", cc ? Math.Round(Spd(t.CruiseKmh)).ToString(CultureInfo.InvariantCulture) : "--", SpdUnit, cc ? Green : Dim, true);
        string eta = "--:--";
        if (t.EtaMin >= 0 && t.GameTime != DateTime.MinValue) eta = Clock(t.GameTime.AddMinutes(t.EtaMin));
        Row(dc, DW - 50, 300, "ARRIVAL (ETA)", eta, "", White, true);
        Row(dc, DW - 50, 410, "SPEED LIMIT", t.SpeedLimit > 0 ? Math.Round(Spd(t.SpeedLimit)).ToString(CultureInfo.InvariantCulture) : "--", SpdUnit, t.SpeedLimit > 0 && Spd(dispSpeed) > Spd(t.SpeedLimit) + 3 ? Amber : White, true);

        // bottom row: game clock and rest, trip and odometer
        dc.DrawLine(PenOf(Fz(Color.FromRgb(24, 36, 46)), 2), new Point(40, 596), new Point(DW - 40, 596));
        Draw(dc, "GAME TIME", 50, 622, 16, Dim, 0, true); Draw(dc, Clock(t.GameTime), 50, 656, 34, White, 0);
        Draw(dc, "REST IN", 250, 622, 16, Dim, 0, true); Draw(dc, HoursMinutes(t.RestMin), 250, 656, 34, t.RestMin >= 0 && t.RestMin < 30 ? Amber : White, 0);
        Draw(dc, "TRIP", DW - 330, 622, 16, Dim, 2, true); Draw(dc, Math.Round(Dist(TripKm)).ToString(CultureInfo.InvariantCulture) + " " + DistUnit, DW - 330, 656, 34, White, 2);
        // the PC's local time, large, in the middle of the bottom row
        Draw(dc, "LOCAL TIME", 640, 612, 16, Dim, 1, true);
        Draw(dc, DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), 640, 660, 62, White, 1);
        Draw(dc, "ODOMETER", DW - 50, 622, 16, Dim, 2, true); Draw(dc, Math.Round(Dist(t.OdoKm)).ToString(CultureInfo.InvariantCulture) + " " + DistUnit, DW - 50, 656, 34, White, 2);
    }

    /// <summary>
    /// A small round temperature gauge: 270-degree scale open at the bottom, red zone from redC, needle, reading in the middle
    /// and the name in the gap. The scale is 40-130 °C, or 100-260 °F with imperial units.
    /// </summary>
    void TempGauge(DrawingContext dc, double cx, double cy, string name, double shownC, float rawC, double redC, bool warn)
    {
        const double r = 64;
        double lo = Imp ? 100 : 40, hi = Imp ? 260 : 130, minor = Imp ? 20 : 10, major = Imp ? 40 : 30;
        double U(double c) => Imp ? c * 9 / 5 + 32 : c;
        double A(double u) => 225 - 270 * Math.Clamp((u - lo) / (hi - lo), 0, 1);
        Point Q(double angleDeg, double rad) { double a = angleDeg * Math.PI / 180; return new Point(cx + rad * Math.Cos(a), cy - rad * Math.Sin(a)); }
        StreamGeometry Ring(double a0, double a1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(Q(a0, r), false, false);
                c.ArcTo(Q(a1, r), new Size(r, r), 0, Math.Abs(a1 - a0) > 180, a1 < a0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        bool hot = warn || (rawC > 0 && rawC >= redC);
        BacklightFace(dc, new Point(cx, cy), r + 6);
        dc.DrawGeometry(null, PenOf(Fz(Color.FromRgb(24, 36, 46)), 6, true), Ring(225, -45));
        dc.DrawGeometry(null, PenOf(Fz(Color.FromArgb(200, 255, 59, 48)), 6, true), Ring(A(U(redC)), -45));
        for (double u = lo; u <= hi + 0.01; u += minor)
        {
            bool big = Math.Abs((u - lo) % major) < 0.01;
            double ta = A(u);
            Halo(dc, Q(ta, r - (big ? 16 : 10)), Q(ta, r - 4), big ? 2.6 : 1.6);
            dc.DrawLine(PenOf(big ? White : Marking(Grey, LitTick), big ? 2.6 : 1.6), Q(ta, r - (big ? 16 : 10)), Q(ta, r - 4));
        }
        // scale ends: just below the track, or outside the chrome rim in the Chrome style
        double lr = Chrome ? r + 28 : r + 4, dy = Chrome ? 4 : 12;
        var l0 = Q(225, lr); var l1 = Q(-45, lr);
        Draw(dc, lo.ToString(CultureInfo.InvariantCulture), l0.X - 2, l0.Y + dy, 14, Dim, 1, true);
        Draw(dc, hi.ToString(CultureInfo.InvariantCulture), l1.X + 2, l1.Y + dy, 14, Dim, 1, true);

        // needle: drawn from just outside the reading to the scale, like the speed dial
        if (rawC > 0)
        {
            double na = A(U(shownC));
            dc.DrawLine(PenOf(Fz(Color.FromArgb(70, 255, 59, 48)), 9, true), Q(na, 30), Q(na, r - 2));
            dc.DrawLine(PenOf(Red, 3.5, true), Q(na, 30), Q(na, r - 2));
        }
        Draw(dc, Temp(rawC), cx, cy + 2, 26, hot ? Red : White, 1);
        Draw(dc, name, cx, cy + 50, 15, Dim, 1, true);
    }

    void Row(DrawingContext dc, double x, double y, string caption, string value, string unit, Brush b, bool right)
    {
        Draw(dc, caption, x, y, 16, Dim, right ? 2 : 0, true);
        double size = 46;
        var vf = Text(value, size, b); var uf = unit.Length > 0 ? Text(unit, 20, Dim, true) : null;
        double total = vf.Width + (uf != null ? uf.Width + 8 : 0);
        double sx = right ? x - total : x;
        dc.DrawText(vf, new Point(sx, y + 30 - vf.Height / 2));
        if (uf != null) dc.DrawText(uf, new Point(sx + vf.Width + 8, y + 40 - uf.Height / 2));
    }

    // ------------------------------------------------------------------------------------------ lamps

    void Lamps(DrawingContext dc)
    {
        var t = T;
        double y = 50, x = 640;
        // turn signals either side of the middle
        Arrow(dc, x - 96, y, true, t.BlinkerL ? Green : Fz(Color.FromRgb(26, 40, 32)));
        Arrow(dc, x + 96, y, false, t.BlinkerR ? Green : Fz(Color.FromRgb(26, 40, 32)));
        // lights
        Beam(dc, x - 48, y, false, t.LowBeam ? Green : Fz(Color.FromRgb(26, 40, 32)));
        Beam(dc, x + 48, y, true, t.HighBeam ? Blue : Fz(Color.FromRgb(24, 34, 46)));
        // status tags below
        var tags = new List<(string, Brush)>();
        if (t.ParkBrake) tags.Add(("P BRAKE", Red));
        if (t.MotorBrake) tags.Add(("ENGINE BRAKE", Amber));
        if (t.Retarder > 0) tags.Add(("RETARDER " + t.Retarder.ToString(CultureInfo.InvariantCulture), Amber));
        if (t.Wipers) tags.Add(("WIPERS", Dim));
        if (t.Beacon) tags.Add(("BEACON", Amber));
        if (t.AirWarn) tags.Add(("AIR", Red));
        if (t.OilWarn) tags.Add(("OIL", Red));
        if (t.WaterWarn) tags.Add(("COOLANT", Red));
        if (t.BatteryWarn) tags.Add(("BATTERY", Red));
        if (t.FuelWarn) tags.Add(("FUEL", Amber));
        if (t.AdBlueWarn) tags.Add((DefName, Amber));
        if (tags.Count == 0) return;
        double total = 0; var widths = new List<double>();
        foreach (var (s, _) in tags) { double w = Text(s, 18, Dim).Width + 26; widths.Add(w); total += w + 8; }
        double tx = 640 - (total - 8) / 2;
        for (int i = 0; i < tags.Count; i++)
        {
            var (s, b) = tags[i];
            var rect = new Rect(tx, 88, widths[i], 30);
            dc.DrawRoundedRectangle(Fz(Color.FromRgb(14, 22, 30)), PenOf(b, 1.6), rect, 6, 6);
            Draw(dc, s, rect.X + rect.Width / 2, rect.Y + 15, 18, b, 1);
            tx += widths[i] + 8;
        }
    }

    void Arrow(DrawingContext dc, double x, double y, bool left, Brush b)
    {
        double d = left ? -1 : 1;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x + d * 18, y), true, true);
            c.LineTo(new Point(x - d * 2, y - 16), true, false); c.LineTo(new Point(x - d * 2, y - 7), true, false);
            c.LineTo(new Point(x - d * 18, y - 7), true, false); c.LineTo(new Point(x - d * 18, y + 7), true, false);
            c.LineTo(new Point(x - d * 2, y + 7), true, false); c.LineTo(new Point(x - d * 2, y + 16), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(b, null, g);
    }

    void Beam(DrawingContext dc, double x, double y, bool high, Brush b)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x - 8, y - 14), false, false);
            c.ArcTo(new Point(x - 8, y + 14), new Size(14, 14), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, PenOf(b, 3.4, true), g);
        for (int i = -2; i <= 2; i++)
        {
            double dy = high ? i * 6 : i * 5;
            var p1 = new Point(x + 2, y + dy); var p2 = new Point(x + 22, y + dy + (high ? 0 : 3 + Math.Abs(i) * 0));
            dc.DrawLine(PenOf(b, 3, true), p1, p2);
        }
    }
}
