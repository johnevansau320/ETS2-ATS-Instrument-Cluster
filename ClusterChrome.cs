using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ets2Cluster;

/// <summary>
/// The "Chrome" style: the same cluster set in a padded, upholstered surround with a polished chrome bezel,
/// a chrome-ringed dial and temperature gauges, glass-tube fuel and AdBlue gauges, divider lines and a glass sheen.
/// Everything that never changes is built once as a frozen drawing; only the live readings are drawn each frame.
/// </summary>
public sealed partial class ClusterControl
{
    bool Chrome => Cfg.Style == "chrome";

    const double Pad = 26, Bezel = 26, OuterR = 100;               // upholstery and chrome band widths, outer corner radius
    const double ScreenInset = Pad + Bezel, ScreenR = OuterR - ScreenInset;
    // the 1280 x 720 cluster is scaled to the screen height and centred inside the bezel
    const double CS = (DH - 2 * ScreenInset) / DH;
    static readonly double CX0 = (DW - DW * CS) / 2, CY0 = ScreenInset;

    static RectangleGeometry Inset(double d)
    {
        var g = new RectangleGeometry(new Rect(d, d, DW - 2 * d, DH - 2 * d), Math.Max(0, OuterR - d), Math.Max(0, OuterR - d));
        g.Freeze();
        return g;
    }

    static Color Steel(double b, double tint = 0) => Color.FromRgb((byte)Math.Clamp(b * 255, 0, 255), (byte)Math.Clamp(b * 255 + tint, 0, 255), (byte)Math.Clamp(b * 255 + 2 * tint, 0, 255));

    /// <summary>Brightness of polished chrome across a band, t from the outer edge (0) to the inner edge (1).</summary>
    static double ChromeProfile(double t)
    {
        double[] x = { 0, 0.07, 0.3, 0.55, 0.78, 0.9, 1 }, y = { 0.22, 0.92, 0.72, 0.36, 0.86, 0.96, 0.18 };
        for (int i = 1; i < x.Length; i++) if (t <= x[i]) return y[i - 1] + (y[i] - y[i - 1]) * (t - x[i - 1]) / (x[i] - x[i - 1]);
        return y[^1];
    }

    /// <summary>Chrome reflects a bright sky at the top, a dark horizon, and the lit dashboard below.</summary>
    static Brush ChromeBrush(double b)
    {
        var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        br.GradientStops.Add(new GradientStop(Steel(b * 1.0, 4), 0));
        br.GradientStops.Add(new GradientStop(Steel(b * 0.62, 5), 0.42));
        br.GradientStops.Add(new GradientStop(Steel(b * 0.38, 6), 0.55));
        br.GradientStops.Add(new GradientStop(Steel(b * 0.8, 4), 0.85));
        br.GradientStops.Add(new GradientStop(Steel(b * 0.6, 3), 1));
        br.Freeze();
        return br;
    }

    static Drawing? frameDrawing, faceDrawing;

    /// <summary>Upholstered padding, chrome bezel and the screen behind the glass (canvas coordinates).</summary>
    static Drawing FrameDrawing()
    {
        if (frameDrawing != null) return frameDrawing;
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // padding: dark leather that bulges between its edges, lit from above, with a stitch line beside the chrome
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(16, 15, 15)), null, Inset(0));
            for (double d = 1; d < Pad; d += 1.5)
            {
                double t = d / Pad, b = 0.05 + 0.13 * Math.Pow(Math.Sin(Math.PI * t), 0.7);
                var br = new LinearGradientBrush(Color.FromRgb((byte)(b * 290), (byte)(b * 280), (byte)(b * 272)), Color.FromRgb((byte)(b * 160), (byte)(b * 154), (byte)(b * 150)), 90);
                br.Freeze();
                dc.DrawGeometry(null, new Pen(br, 2), Inset(d));
            }
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, Inset(0), Inset(Pad)); ring.Freeze();
            dc.DrawGeometry(LeatherGrain(), null, ring);
            var stitch = new Pen(new SolidColorBrush(Color.FromArgb(150, 92, 88, 84)), 1.4) { DashStyle = new DashStyle(new double[] { 3.2, 2.4 }, 0), DashCap = PenLineCap.Round };
            stitch.Freeze();
            dc.DrawGeometry(null, stitch, Inset(Pad - 6));
            // chrome bezel
            for (double d = 0; d <= Bezel; d += 1.25)
                dc.DrawGeometry(null, new Pen(ChromeBrush(ChromeProfile(d / Bezel)), 1.9), Inset(Pad + d));
            // screen
            var scr = new RadialGradientBrush(Color.FromRgb(17, 21, 26), Color.FromRgb(5, 6, 8)) { Center = new Point(0.5, 0.55), GradientOrigin = new Point(0.5, 0.55), RadiusX = 0.7, RadiusY = 0.85 };
            scr.Freeze();
            dc.DrawGeometry(scr, null, Inset(ScreenInset));
        }
        g.Freeze();
        return frameDrawing = g;
    }

    /// <summary>A fine random grain, tiled over the leather.</summary>
    static Brush LeatherGrain()
    {
        const int n = 96;
        var px = new byte[n * n * 4]; var rnd = new Random(7);
        for (int i = 0; i < n * n; i++)
        {
            bool light = rnd.Next(2) == 0; byte a = (byte)rnd.Next(0, 34);
            byte v = light ? (byte)255 : (byte)0;
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = (byte)(v * a / 255); px[i * 4 + 3] = a;   // premultiplied
        }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Pbgra32, null, px, n * 4); bmp.Freeze();
        var br = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, n / 2.0, n / 2.0), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        br.Freeze();
        return br;
    }

    /// <summary>The fixed parts drawn under the readings (cluster coordinates): dial face and chrome ring, the hub behind
    /// the gear, temperature gauge rims, the glass tubes of the fuel and AdBlue gauges, the lamp strip and divider lines.</summary>
    static Drawing FaceDrawing()
    {
        if (faceDrawing != null) return faceDrawing;
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // dial face, cut off by the bottom row
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, DW, 594)));
            var face = new RadialGradientBrush(Color.FromRgb(20, 24, 29), Color.FromRgb(7, 9, 11)) { RadiusX = 0.5, RadiusY = 0.5 };
            face.Freeze();
            dc.DrawEllipse(face, null, C, R + 8, R + 8);
            dc.Pop();
            // chrome ring around the dial
            for (double d = 0; d <= 18; d += 1.25)
                dc.DrawGeometry(null, new Pen(ChromeBrush(ChromeProfile(1 - d / 18) * 0.95), 1.9), Arc(R + 8 + d, 205, -25));
            // hub behind the make and gear: a raised panel that widens towards the bottom
            var hub = new StreamGeometry();
            using (var c = hub.Open())
            {
                c.BeginFigure(new Point(430, 594), true, true);
                c.BezierTo(new Point(470, 520), new Point(480, 350), new Point(570, 340), true, true);
                c.LineTo(new Point(710, 340), true, true);
                c.BezierTo(new Point(800, 350), new Point(810, 520), new Point(850, 594), true, true);
            }
            hub.Freeze();
            var hubFill = new LinearGradientBrush(Color.FromRgb(22, 26, 31), Color.FromRgb(9, 11, 14), 90); hubFill.Freeze();
            var hubEdge = new LinearGradientBrush(Color.FromArgb(150, 120, 132, 146), Color.FromArgb(0, 120, 132, 146), 90); hubEdge.Freeze();
            dc.DrawGeometry(hubFill, new Pen(hubEdge, 2), hub);
            // temperature gauge faces and chrome rims
            double tx = P(163.5, R + 96).X;
            foreach (double x in new[] { tx, DW - tx })
            {
                var gf = new RadialGradientBrush(Color.FromRgb(18, 22, 27), Color.FromRgb(5, 6, 8)); gf.Freeze();
                dc.DrawEllipse(gf, null, new Point(x, Corner), 74, 74);
                for (double d = 0; d <= 7; d += 1)
                    dc.DrawEllipse(null, new Pen(ChromeBrush(ChromeProfile(1 - d / 7)), 1.5), new Point(x, Corner), 74 + d, 74 + d);
            }
            // glass tubes for the fuel and AdBlue gauges
            foreach (var (a0, a1) in new[] { (163.5, 200.0), (16.5, -20.0) })
            {
                var arc = Arc(R + 56, a0, a1);
                for (double w = 26; w >= 18; w -= 1.5)
                    dc.DrawGeometry(null, PenOf(ChromeBrush(ChromeProfile(Math.Abs(w - 22) / 4 * 0.5) * 0.9), w, true), arc);
                dc.DrawGeometry(null, PenOf(Fz(Color.FromRgb(6, 8, 10)), 16, true), arc);
            }
            // lamp strip hanging from the top of the screen
            var strip = new StreamGeometry();
            using (var c = strip.Open())
            {
                c.BeginFigure(new Point(420, -10), true, true);
                c.LineTo(new Point(420, 52), true, true);
                c.ArcTo(new Point(446, 78), new Size(26, 26), 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(834, 78), true, true);
                c.ArcTo(new Point(860, 52), new Size(26, 26), 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(860, -10), true, true);
            }
            strip.Freeze();
            var sf = new LinearGradientBrush(Color.FromRgb(9, 11, 14), Color.FromRgb(19, 23, 28), 90); sf.Freeze();
            var se = new LinearGradientBrush(Color.FromArgb(0, 120, 132, 146), Color.FromArgb(120, 120, 132, 146), 90); se.Freeze();
            dc.DrawGeometry(sf, new Pen(se, 1.5), strip);
            // divider lines: fade out at their ends
            void HLine(double x0, double x1, double y)
            {
                var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 70, 80, 92), 0)); b.GradientStops.Add(new GradientStop(Color.FromArgb(150, 70, 80, 92), 0.2));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(150, 70, 80, 92), 0.8)); b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 70, 80, 92), 1)); b.Freeze();
                dc.DrawRectangle(b, null, new Rect(x0, y - 0.75, x1 - x0, 1.5));
            }
            void VLine(double x, double y0, double y1)
            {
                var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 70, 80, 92), 0)); b.GradientStops.Add(new GradientStop(Color.FromArgb(150, 70, 80, 92), 0.3));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(150, 70, 80, 92), 0.7)); b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 70, 80, 92), 1)); b.Freeze();
                dc.DrawRectangle(b, null, new Rect(x - 0.75, y0, 1.5, y1 - y0));
            }
            foreach (bool right in new[] { false, true })
            {
                double X(double x) => right ? DW - x : x;
                HLine(Math.Min(X(36), X(300)), Math.Max(X(36), X(300)), 262);
                HLine(Math.Min(X(36), X(205)), Math.Max(X(36), X(205)), 372);
                VLine(X(190), 286, 352);
            }
            foreach (double x in new[] { 205.0, 450, 770, 1005 }) VLine(x, 606, 676);
        }
        g.Freeze();
        return faceDrawing = g;
    }

    /// <summary>Faint reflection on the glass, over everything (canvas coordinates).</summary>
    static readonly Brush Sheen = MakeSheen();
    static Brush MakeSheen()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.75, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(26, 255, 255, 255), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(10, 255, 255, 255), 0.28));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.42));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.7));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(9, 255, 255, 255), 1));
        b.Freeze();
        return b;
    }

    void RenderChrome(DrawingContext dc)
    {
        dc.PushClip(Inset(0));
        dc.DrawDrawing(FrameDrawing());
        dc.PushClip(Inset(ScreenInset));
        dc.PushTransform(new TranslateTransform(CX0, CY0));
        dc.PushTransform(new ScaleTransform(CS, CS));
        Cluster(dc);
        dc.Pop(); dc.Pop();
        dc.DrawGeometry(Sheen, null, Inset(ScreenInset));
        dc.Pop(); dc.Pop();
    }
}
