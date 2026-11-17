package com.ets2cluster

import android.graphics.Bitmap
import android.graphics.BitmapShader
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.DashPathEffect
import android.graphics.LinearGradient
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.sin
import kotlin.random.Random

/**
 * The "Chrome" style, the same as the Windows app: the cluster set in a padded, upholstered surround with a polished chrome
 * bezel, a chrome-ringed dial and temperature gauges, glass-tube diesel and AdBlue gauges, divider lines and a glass sheen.
 * The parts that never change are drawn once into a picture the size of the screen ([layer]); only the readings are drawn each frame.
 */
object ChromeStyle {
    private const val DW = ClusterView.DW; private const val DH = ClusterView.DH
    private const val CX = 640f; private const val CY = 440f; private const val R = 330f
    const val PAD = 26f; const val BEZEL = 26f; const val OUTER_R = 100f
    const val SCREEN_INSET = PAD + BEZEL
    /** The 1280 x 720 cluster is scaled to the screen height and centred inside the bezel. */
    const val CS = (DH - 2 * SCREEN_INSET) / DH
    const val CX0 = (DW - DW * CS) / 2; const val CY0 = SCREEN_INSET

    private val p = Paint(Paint.ANTI_ALIAS_FLAG)

    fun inset(d: Float) = RectF(d, d, DW - d, DH - d)
    fun insetPath(d: Float): Path = Path().apply { val r = max(0f, OUTER_R - d); addRoundRect(inset(d), r, r, Path.Direction.CW) }

    private fun c8(v: Double) = v.coerceIn(0.0, 255.0).toInt()
    /** Chrome brightness while a picture is drawn: 1 by day, almost black at night. */
    private var ck = 1.0
    private fun steel(b: Double, tint: Double) = Color.rgb(c8(b * ck * 255), c8((b * 255 + tint) * ck), c8((b * 255 + 2 * tint) * ck))

    /** How dark it is outside from the game clock (minutes after midnight, -1 unknown): 0 by day, 1 at night, easing over
     *  dusk (19:00-21:00) and dawn (5:00-7:00). The game does not report the light level, so the time of day stands in for it. */
    fun nightFromClock(min: Int): Float {
        if (min < 0) return 0f
        val h = (min % 1440) / 60.0
        fun s(x: Double): Double { val v = x.coerceIn(0.0, 1.0); return v * v * (3 - 2 * v) }
        return (if (h >= 12) s((h - 19) / 2) else 1 - s((h - 5) / 2)).toFloat()
    }

    /** Brightness of polished chrome across a band, t from the outer edge (0) to the inner edge (1). */
    fun profile(t: Double): Double {
        val x = doubleArrayOf(0.0, 0.07, 0.3, 0.55, 0.78, 0.9, 1.0); val y = doubleArrayOf(0.22, 0.92, 0.72, 0.36, 0.86, 0.96, 0.18)
        for (i in 1 until x.size) if (t <= x[i]) return y[i - 1] + (y[i] - y[i - 1]) * (t - x[i - 1]) / (x[i] - x[i - 1])
        return y.last()
    }

    /** Chrome reflects a bright sky at the top, a dark horizon, and the lit dashboard below; y0..y1 is the shape's height. */
    fun chromeShader(b: Double, y0: Float, y1: Float): Shader = LinearGradient(0f, y0, 0f, y1,
        intArrayOf(steel(b, 4.0), steel(b * 0.62, 5.0), steel(b * 0.38, 6.0), steel(b * 0.8, 4.0), steel(b * 0.6, 3.0)),
        floatArrayOf(0f, 0.42f, 0.55f, 0.85f, 1f), Shader.TileMode.CLAMP)

    private fun stroke(width: Float, shader: Shader?, color: Int = Color.WHITE, round: Boolean = false): Paint {
        p.reset(); p.isAntiAlias = true; p.style = Paint.Style.STROKE; p.strokeWidth = width; p.shader = shader; p.color = color
        p.strokeCap = if (round) Paint.Cap.ROUND else Paint.Cap.BUTT
        return p
    }
    private fun fill(shader: Shader?, color: Int = Color.WHITE): Paint {
        p.reset(); p.isAntiAlias = true; p.style = Paint.Style.FILL; p.shader = shader; p.color = color
        return p
    }

    private fun roundRectStroke(c: Canvas, d: Float, width: Float, shader: Shader) {
        val r = max(0f, OUTER_R - d)
        c.drawRoundRect(inset(d), r, r, stroke(width, shader))
    }

    /** Vertical extent of an arc of radius r about (cx, cy) from math angle a0 to a1, for its gradient. */
    private fun arcY(cy: Float, r: Float, a0: Float, a1: Float): Pair<Float, Float> {
        val ys = mutableListOf(cy - r * sin(a0 * PI / 180).toFloat(), cy - r * sin(a1 * PI / 180).toFloat())
        val lo = min(a0, a1); val hi = max(a0, a1)
        for (k in -1..1) { if (90f + 360f * k in lo..hi) ys.add(cy - r); if (270f + 360f * k in lo..hi) ys.add(cy + r) }
        return ys.min() to ys.max()
    }

    private fun arc(c: Canvas, cx: Float, cy: Float, r: Float, a0: Float, a1: Float, paint: Paint) =
        c.drawArc(RectF(cx - r, cy - r, cx + r, cy + r), -a0, a0 - a1, false, paint)

    private fun grain(): Shader {
        val n = 96; val rnd = Random(7); val px = IntArray(n * n)
        for (i in px.indices) { val a = rnd.nextInt(0, 34); px[i] = if (rnd.nextBoolean()) Color.argb(a, 255, 255, 255) else Color.argb(a, 0, 0, 0) }
        val bmp = Bitmap.createBitmap(px, n, n, Bitmap.Config.ARGB_8888)
        return BitmapShader(bmp, Shader.TileMode.REPEAT, Shader.TileMode.REPEAT).apply { setLocalMatrix(Matrix().apply { setScale(0.5f, 0.5f) }) }
    }

    /** Upholstered padding, chrome bezel and the screen behind the glass (canvas coordinates). */
    private fun frame(c: Canvas, lk: Double) {
        c.drawRoundRect(inset(0f), OUTER_R, OUTER_R, fill(null, Color.rgb(c8(16 * lk), c8(15 * lk), c8(15 * lk))))
        var d = 1f
        while (d < PAD) {
            val t = d / PAD; val b = (0.05 + 0.13 * sin(PI * t).pow(0.7)) * lk
            roundRectStroke(c, d, 2f, LinearGradient(0f, d, 0f, DH - d, Color.rgb(c8(b * 290), c8(b * 280), c8(b * 272)), Color.rgb(c8(b * 160), c8(b * 154), c8(b * 150)), Shader.TileMode.CLAMP))
            d += 1.5f
        }
        val ring = Path().apply { fillType = Path.FillType.EVEN_ODD; addPath(insetPath(0f)); addPath(insetPath(PAD)) }
        c.drawPath(ring, fill(grain()).apply { alpha = c8(255 * lk) })
        val st = stroke(1.4f, null, Color.argb(c8(150 * lk), 92, 88, 84), true); st.pathEffect = DashPathEffect(floatArrayOf(3.2f, 2.4f), 0f)
        c.drawRoundRect(inset(PAD - 6), OUTER_R - PAD + 6, OUTER_R - PAD + 6, st)
        d = 0f
        while (d <= BEZEL) { roundRectStroke(c, PAD + d, 1.9f, chromeShader(profile((d / BEZEL).toDouble()), PAD + d, DH - PAD - d)); d += 1.25f }
        val s = inset(SCREEN_INSET)
        c.drawRoundRect(s, OUTER_R - SCREEN_INSET, OUTER_R - SCREEN_INSET, fill(RadialGradient(s.centerX(), s.top + s.height() * 0.55f, s.width() * 0.62f, Color.rgb(17, 21, 26), Color.rgb(5, 6, 8), Shader.TileMode.CLAMP)))
    }

    /** The fixed parts under the readings (cluster coordinates). */
    private fun face(c: Canvas, tempX: Float, tempY: Float) {
        // dial face, cut off by the bottom row
        c.save(); c.clipRect(0f, 0f, DW, 594f)
        c.drawCircle(CX, CY, R + 8, fill(RadialGradient(CX, CY, R + 8, Color.rgb(20, 24, 29), Color.rgb(7, 9, 11), Shader.TileMode.CLAMP)))
        c.restore()
        // chrome ring around the dial
        var d = 0f
        while (d <= 18f) {
            val r = R + 8 + d; val (y0, y1) = arcY(CY, r, 205f, -25f)
            arc(c, CX, CY, r, 205f, -25f, stroke(1.9f, chromeShader(profile(1 - d / 18.0) * 0.95, y0, y1)))
            d += 1.25f
        }
        // hub behind the make and gear
        val hub = Path().apply {
            moveTo(430f, 594f); cubicTo(470f, 520f, 480f, 350f, 570f, 340f); lineTo(710f, 340f); cubicTo(800f, 350f, 810f, 520f, 850f, 594f); close()
        }
        c.drawPath(hub, fill(LinearGradient(0f, 340f, 0f, 594f, Color.rgb(22, 26, 31), Color.rgb(9, 11, 14), Shader.TileMode.CLAMP)))
        c.drawPath(hub, stroke(2f, LinearGradient(0f, 340f, 0f, 594f, Color.argb(150, 120, 132, 146), Color.argb(0, 120, 132, 146), Shader.TileMode.CLAMP)))
        // temperature gauge faces and chrome rims
        for (x in floatArrayOf(tempX, DW - tempX)) {
            c.drawCircle(x, tempY, 74f, fill(RadialGradient(x, tempY, 74f, Color.rgb(18, 22, 27), Color.rgb(5, 6, 8), Shader.TileMode.CLAMP)))
            for (k in 0..7) { val r = 74f + k; c.drawCircle(x, tempY, r, stroke(1.5f, chromeShader(profile(1 - k / 7.0), tempY - r, tempY + r))) }
        }
        // glass tubes for the diesel and AdBlue gauges
        for ((a0, a1) in listOf(163.5f to 200f, 16.5f to -20f)) {
            val r = R + 56; val (y0, y1) = arcY(CY, r, a0, a1)
            var w = 26f
            while (w >= 18f) { arc(c, CX, CY, r, a0, a1, stroke(w, chromeShader(profile(abs(w - 22.0) / 4 * 0.5) * 0.9, y0, y1), round = true)); w -= 1.5f }
            arc(c, CX, CY, r, a0, a1, stroke(16f, null, Color.rgb(6, 8, 10), true))
        }
        // lamp strip hanging from the top of the screen
        val strip = Path().apply {
            moveTo(420f, -10f); lineTo(420f, 52f); arcTo(RectF(420f, 26f, 472f, 78f), 180f, -90f, false)
            lineTo(834f, 78f); arcTo(RectF(808f, 26f, 860f, 78f), 90f, -90f, false); lineTo(860f, -10f); close()
        }
        c.drawPath(strip, fill(LinearGradient(0f, -10f, 0f, 78f, Color.rgb(9, 11, 14), Color.rgb(19, 23, 28), Shader.TileMode.CLAMP)))
        c.drawPath(strip, stroke(1.5f, LinearGradient(0f, -10f, 0f, 78f, Color.argb(0, 120, 132, 146), Color.argb(120, 120, 132, 146), Shader.TileMode.CLAMP)))
        // divider lines that fade out at their ends
        val lineCols = intArrayOf(Color.argb(0, 70, 80, 92), Color.argb(150, 70, 80, 92), Color.argb(150, 70, 80, 92), Color.argb(0, 70, 80, 92))
        fun hLine(x0: Float, x1: Float, y: Float) =
            c.drawRect(x0, y - 0.75f, x1, y + 0.75f, fill(LinearGradient(x0, 0f, x1, 0f, lineCols, floatArrayOf(0f, 0.2f, 0.8f, 1f), Shader.TileMode.CLAMP)))
        fun vLine(x: Float, y0: Float, y1: Float) =
            c.drawRect(x - 0.75f, y0, x + 0.75f, y1, fill(LinearGradient(0f, y0, 0f, y1, lineCols, floatArrayOf(0f, 0.3f, 0.7f, 1f), Shader.TileMode.CLAMP)))
        for (right in listOf(false, true)) {
            fun x(v: Float) = if (right) DW - v else v
            hLine(min(x(36f), x(300f)), max(x(36f), x(300f)), 262f)
            hLine(min(x(36f), x(205f)), max(x(36f), x(205f)), 372f)
            vLine(x(190f), 286f, 352f)
        }
        for (x in floatArrayOf(205f, 450f, 770f, 1005f)) vLine(x, 606f, 676f)
    }

    private val layers = arrayOfNulls<Bitmap>(2)

    /** The fixed picture for a screen of w x h pixels: frame, screen and the fixed parts of the instruments.
     *  [night] 0 is the daylight look, 1 the same with the chrome and padding almost black, as in a dark cab. */
    fun layer(w: Int, h: Int, tempX: Float, tempY: Float, night: Int = 0): Bitmap {
        layers[night]?.let { if (it.width == w && it.height == h) return it; it.recycle() }
        ck = if (night == 1) 0.16 else 1.0
        val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        val c = Canvas(bmp)
        val s = min(w / DW, h / DH)
        c.translate((w - DW * s) / 2, (h - DH * s) / 2); c.scale(s, s)
        c.clipPath(insetPath(0f))
        frame(c, if (night == 1) 0.3 else 1.0)
        c.clipPath(insetPath(SCREEN_INSET))
        c.translate(CX0, CY0); c.scale(CS, CS)
        face(c, tempX, tempY)
        ck = 1.0
        layers[night] = bmp
        return bmp
    }

    /** Faint reflection on the glass (canvas coordinates). */
    fun sheen(c: Canvas, night: Float = 0f) {
        val s = inset(SCREEN_INSET)
        val g = LinearGradient(s.left, s.top, s.left + s.width() * 0.75f, s.bottom,
            intArrayOf(Color.argb(26, 255, 255, 255), Color.argb(10, 255, 255, 255), Color.argb(0, 255, 255, 255), Color.argb(0, 255, 255, 255), Color.argb(9, 255, 255, 255)),
            floatArrayOf(0f, 0.28f, 0.42f, 0.7f, 1f), Shader.TileMode.CLAMP)
        c.drawRoundRect(s, OUTER_R - SCREEN_INSET, OUTER_R - SCREEN_INSET, fill(g).apply { alpha = c8(255 * (1 - 0.75 * night)) })
    }
}
