package com.ets2cluster

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.os.SystemClock
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.View
import java.util.Calendar
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sin

/**
 * The instrument cluster, drawn on a fixed 1280 x 720 canvas that is scaled to fit the screen: speedometer with needle, gear, rev strip,
 * diesel and AdBlue gauges, coolant and oil temperature gauges, consumption, range, cruise, ETA, clocks, odometer and trip, lamps and warnings. The layout is the same as the Windows app.
 */
class ClusterView(ctx: Context, private val onLongPress: () -> Unit) : View(ctx) {
    companion object {
        const val DW = 1280f; const val DH = 720f
        const val CORNER = 98f   // top-corner radius (also the temperature gauges' centre height)
        private const val CX = 640f; private const val CY = 440f
        private const val R = 330f   // radius of the speed dial

        private val BG = Color.rgb(3, 6, 10)
        private val WHITE = Color.rgb(236, 241, 246); private val DIM = Color.rgb(124, 136, 148); private val GREY = Color.rgb(58, 68, 79)
        /** Backlight choices: id, menu label, colour. */
        val BACKLIGHTS = listOf(Triple("white", "Cool white", Color.rgb(170, 215, 255)), Triple("red", "Red", Color.rgb(255, 74, 48)),
            Triple("amber", "Amber", Color.rgb(255, 160, 40)), Triple("green", "Green", Color.rgb(90, 230, 120)), Triple("off", "Off", Color.BLACK))
        private val GREEN = Color.rgb(56, 214, 108); private val AMBER = Color.rgb(255, 176, 46); private val RED = Color.rgb(255, 59, 48); private val BLUE = Color.rgb(58, 160, 255)
    }

    var truck = Truck()
    var units = "game"
    var showMake = true
    var style = "classic"
    var backlight = "white"
    private val chrome get() = style == "chrome"
    var tripKm = 0.0
    var tripLPer100 = -1.0
    var statusText = ""
    var updateText = ""

    // smoothed values: the needle glides between telemetry updates
    private var dispSpeed = 0f; private var dispRpm = 0f; private var dispFuel = 0f; private var dispAdBlue = 0f
    private var dispWater = 0f; private var dispOil = 0f
    private var dispLight = 0f   // instrument backlight, 0 = off .. 1 = on (follows the truck's lights)
    private var lastFrame = 0L

    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val tp = Paint(Paint.ANTI_ALIAS_FLAG).apply { textAlign = Paint.Align.LEFT }
    private val bold = Typeface.create("sans-serif-condensed", Typeface.BOLD)
    private val light = Typeface.create("sans-serif-condensed", Typeface.NORMAL)
    private val path = Path()
    private val oval = RectF()
    private val box = RectF()

    private val gestures = GestureDetector(ctx, object : GestureDetector.SimpleOnGestureListener() {
        override fun onLongPress(e: MotionEvent) { onLongPress() }
        override fun onDown(e: MotionEvent) = true
    })

    override fun onTouchEvent(event: MotionEvent): Boolean = gestures.onTouchEvent(event) || super.onTouchEvent(event)

    /** Moves the smoothed values towards the telemetry; true while anything is still moving. */
    private fun step(dt: Float): Boolean {
        val k = (1 - exp(-dt * 12f.toDouble())).toFloat()
        var moving = false
        fun ease(cur: Float, target: Float, eps: Float): Float {
            val d = target - cur
            if (abs(d) < eps) { if (d != 0f) moving = true; return target }
            moving = true
            return cur + d * k
        }
        dispSpeed = ease(dispSpeed, max(0f, truck.speedKmh), 0.03f)
        dispRpm = ease(dispRpm, max(0f, truck.rpm), 2f)
        dispFuel = ease(dispFuel, truck.fuelFraction, 0.001f)
        dispAdBlue = ease(dispAdBlue, truck.adBlueFraction, 0.001f)
        dispWater = ease(dispWater, max(0f, truck.waterC), 0.05f)
        dispOil = ease(dispOil, max(0f, truck.oilC), 0.05f)
        // instrument backlight fades in and out over about half a second
        val lt = if (backlight != "off" && (truck.parking || truck.lowBeam || truck.highBeam)) 1f else 0f
        val ld = lt - dispLight
        if (abs(ld) > 0.004f) { dispLight += ld * (1 - exp(-dt * 6.0)).toFloat(); moving = true } else if (ld != 0f) { dispLight = lt; moving = true }
        return moving
    }

    // ------------------------------------------------------------------------------------------ units

    // the Units setting: the game's own units (US units in ATS), metric or imperial
    private val imp get() = units == "imperial" || (units != "metric" && truck.ats)
    private fun spd(kmh: Float) = if (imp) kmh * 0.621371f else kmh
    private fun dist(km: Float) = if (imp) km * 0.621371f else km
    private val spdUnit get() = if (imp) "mph" else "km/h"
    private val distUnit get() = if (imp) "mi" else "km"
    private fun temp(c: Float) = if (c > 0) num(if (imp) c * 9f / 5f + 32f else c) + if (imp) "°F" else "°C" else "--"
    /** Litres per 100 km -> the consumption shown: l/100km, or US miles per gallon in ATS. */
    private fun cons(lPer100: Double) = if (lPer100 <= 0) "--" else "%.1f".format(if (imp) 235.215 / lPer100 else lPer100)
    private val consUnit get() = if (imp) "mpg" else "l/100km"
    private val defName get() = if (truck.ats) "DEF" else "ADBLUE"

    private fun clock(minutes: Int) = if (minutes < 0) "--:--" else "%02d:%02d".format((minutes / 60) % 24, minutes % 60)
    private fun hoursMinutes(min: Float): String { if (min < 0) return "--:--"; val m = min.roundToInt(); return "%d:%02d".format(m / 60, m % 60) }
    private fun num(v: Float) = v.roundToInt().toString()

    // ------------------------------------------------------------------------------------------ helpers

    private fun p(angleDeg: Float, r: Float): FloatArray {
        val a = angleDeg * PI / 180
        return floatArrayOf((CX + r * cos(a)).toFloat(), (CY - r * sin(a)).toFloat())
    }

    /** align: 0 left, 1 centre, 2 right of x; y is the vertical centre. Returns the text width. */
    private fun text(c: Canvas, t: String, x: Float, y: Float, size: Float, color: Int, align: Int = 1, lightFace: Boolean = false): Float {
        tp.typeface = if (lightFace) light else bold
        tp.textSize = size; tp.color = color
        val w = tp.measureText(t)
        val fm = tp.fontMetrics
        val bx = when (align) { 0 -> x; 1 -> x - w / 2; else -> x - w }
        c.drawText(t, bx, y - (fm.ascent + fm.descent) / 2, tp)
        return w
    }

    private fun line(c: Canvas, x1: Float, y1: Float, x2: Float, y2: Float, color: Int, width: Float, round: Boolean = false) {
        stroke.shader = null; stroke.color = color; stroke.strokeWidth = width; stroke.strokeCap = if (round) Paint.Cap.ROUND else Paint.Cap.BUTT
        c.drawLine(x1, y1, x2, y2, stroke)
    }

    private fun line(c: Canvas, a: FloatArray, b: FloatArray, color: Int, width: Float, round: Boolean = false) = line(c, a[0], a[1], b[0], b[1], color, width, round)

    /** Arc of radius r from math angle a0 to a1 (degrees; counter-clockwise positive, as on a maths graph). */
    private fun arc(c: Canvas, r: Float, a0: Float, a1: Float, color: Int, width: Float) {
        oval.set(CX - r, CY - r, CX + r, CY + r)
        stroke.shader = null; stroke.color = color; stroke.strokeWidth = width; stroke.strokeCap = Paint.Cap.ROUND
        c.drawArc(oval, -a0, a0 - a1, false, stroke)
    }

    private fun roundRect(c: Canvas, l: Float, t: Float, r: Float, b: Float, rad: Float, fillColor: Int, strokeColor: Int, strokeW: Float) {
        box.set(l, t, r, b)
        fill.shader = null; fill.color = fillColor
        c.drawRoundRect(box, rad, rad, fill)
        stroke.shader = null; stroke.color = strokeColor; stroke.strokeWidth = strokeW; stroke.strokeCap = Paint.Cap.BUTT
        c.drawRoundRect(box, rad, rad, stroke)
    }

    // ------------------------------------------------------------------------------------------ backlight

    /** The temperature gauges sit over the DIESEL / ADBLUE labels (the top end of the fuel and AdBlue arcs). */
    private val tempX get() = p(163.5f, R + 96f)[0]
    private val backColor get() = BACKLIGHTS.firstOrNull { it.first == backlight && it.first != "off" }?.third ?: BACKLIGHTS[0].third
    private fun back(a: Float) = Color.argb((a * dispLight).roundToInt().coerceIn(0, 255), Color.red(backColor), Color.green(backColor), Color.blue(backColor))
    private fun mix(a: Int, b: Int, t: Float) = Color.rgb((Color.red(a) + (Color.red(b) - Color.red(a)) * t).toInt(),
        (Color.green(a) + (Color.green(b) - Color.green(a)) * t).toInt(), (Color.blue(a) + (Color.blue(b) - Color.blue(a)) * t).toInt())
    // unlit ticks and numbers brighten towards these, tinted by the backlight colour
    private val litTick get() = mix(Color.rgb(140, 146, 152), backColor, 0.35f)
    private val litNumber get() = mix(Color.rgb(214, 220, 226), backColor, 0.2f)
    private fun marking(off: Int, lit: Int) = if (dispLight < 0.01f) off else mix(off, lit, dispLight)

    /** Soft light shining through a dial from behind, strongest just inside the scale. */
    private fun backlightFace(c: Canvas, cx: Float, cy: Float, r: Float) {
        if (dispLight < 0.01f) return
        fill.shader = RadialGradient(cx, cy, r, intArrayOf(back(0f), back(14f), back(0f), back(52f), back(0f)), floatArrayOf(0f, 0.45f, 0.62f, 0.9f, 1f), Shader.TileMode.CLAMP)
        c.drawCircle(cx, cy, r, fill)
        fill.shader = null
    }

    /** Halo under a marking so it looks lit from behind. */
    private fun halo(c: Canvas, a: FloatArray, b: FloatArray, w: Float) {
        if (dispLight < 0.01f) return
        line(c, a, b, back(55f), w + 6f, true)
    }

    // ------------------------------------------------------------------------------------------ drawing

    override fun onDraw(canvas: Canvas) {
        val now = SystemClock.uptimeMillis()
        val dt = min(0.1f, max(0.001f, (now - lastFrame) / 1000f)); lastFrame = now
        val moving = step(dt)

        canvas.drawColor(BG)
        val w = width.toFloat(); val h = height.toFloat()
        if (w < 10 || h < 10) return
        val s = min(w / DW, h / DH)
        val save = canvas.save()
        canvas.translate((w - DW * s) / 2, (h - DH * s) / 2)
        canvas.scale(s, s)
        if (chrome) {
            canvas.restoreToCount(save)
            canvas.drawBitmap(ChromeStyle.layer(width, height, tempX, CORNER), 0f, 0f, null)
            canvas.save()
            canvas.translate((w - DW * s) / 2, (h - DH * s) / 2)
            canvas.scale(s, s)
            canvas.clipPath(ChromeStyle.insetPath(ChromeStyle.SCREEN_INSET))
            val inner = canvas.save()
            canvas.translate(ChromeStyle.CX0, ChromeStyle.CY0); canvas.scale(ChromeStyle.CS, ChromeStyle.CS)
            cluster(canvas)
            canvas.restoreToCount(inner)
            ChromeStyle.sheen(canvas)
        } else {
            canvas.clipPath(outline(0f))
            cluster(canvas)
            frame(canvas)
        }
        canvas.restoreToCount(save)

        if (moving) postInvalidateOnAnimation()
    }

    /** The cluster outline (rounded top corners, square bottom), inset by d. */
    private fun outline(d: Float): Path {
        val r = max(0f, CORNER - d); val x0 = d; val y0 = d; val x1 = DW - d; val y1 = DH - d
        return Path().apply {
            moveTo(x0, y1); lineTo(x0, y0 + r)
            arcTo(RectF(x0, y0, x0 + 2 * r, y0 + 2 * r), 180f, 90f, false)
            lineTo(x1 - r, y0)
            arcTo(RectF(x1 - 2 * r, y0, x1, y0 + 2 * r), 270f, 90f, false)
            lineTo(x1, y1); close()
        }
    }

    /** Fine bevelled metal frame: a lit outer edge that darkens downwards and a shadowed inner edge, so it looks raised. */
    private fun frame(c: Canvas) {
        val p = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
        fun edge(d: Float, width: Float, top: Int, bottom: Int) {
            p.strokeWidth = width
            p.shader = LinearGradient(0f, 0f, 0f, DH, top, bottom, Shader.TileMode.CLAMP)
            c.drawPath(outline(d), p)
        }
        edge(3f, 6f, Color.rgb(150, 162, 174), Color.rgb(40, 48, 56))          // bevel face
        edge(0.75f, 1.5f, Color.rgb(215, 225, 235), Color.rgb(70, 80, 90))    // outer highlight
        edge(7.125f, 2.25f, Color.rgb(8, 12, 16), Color.rgb(90, 102, 114))    // inner shadow
    }

    private fun cluster(c: Canvas) {
        val t = truck
        val max = if (imp) 80f else 120f
        val v = spd(dispSpeed)
        val limit = if (t.speedLimit > 0) spd(t.speedLimit.toFloat()) else 0f
        val over = limit > 0 && v > limit + (if (imp) 2 else 3)
        fun ang(value: Float) = 200f - 220f * (value / max).coerceIn(0f, 1.03f)

        // background glow
        if (!chrome) {
            fill.shader = RadialGradient(640f, 446f, 780f, Color.rgb(12, 28, 38), Color.rgb(3, 6, 10), Shader.TileMode.CLAMP)
            c.drawRect(0f, 0f, DW, DH, fill)
            fill.shader = null
        }

        // amber glow beside the dial centre: bright while over the speed limit
        val a = if (over) 120 else 22
        for (gx in floatArrayOf(480f, DW - 480f)) {
            fill.shader = RadialGradient(gx, 370f, 120f, Color.argb(a, 255, 150, 30), Color.argb(0, 255, 150, 30), Shader.TileMode.CLAMP)
            c.drawCircle(gx, 370f, 120f, fill)
            fill.shader = null
        }

        // dial ring
        if (!chrome) arc(c, R + 16, 204f, -24f, Color.rgb(20, 34, 44), 8f)

        // fuel (left) and AdBlue (right) gauges hugging the dial
        gauge(c, 163.5f, 200f, dispFuel, if (t.fuelWarn) RED else GREEN, true)
        gauge(c, 16.5f, -20f, dispAdBlue, if (t.adBlueWarn) RED else BLUE, false)

        // backlight behind the dial (not below the bottom row)
        c.save(); c.clipRect(0f, 0f, DW, 594f); backlightFace(c, CX, CY, R + 6); c.restore()

        // ticks and numbers
        val labelFrom = if (imp) 40 else 60
        var i = 0
        while (i <= max.toInt()) {
            val major = i % 10 == 0
            val an = ang(i.toFloat())
            val lit = i <= v
            halo(c, p(an, R - (if (major) 38f else 20f)), p(an, R), if (major) 3.6f else 2f)
            line(c, p(an, R - (if (major) 38f else 20f)), p(an, R), if (lit) WHITE else marking(GREY, litTick), if (major) 3.6f else 2f)
            if (major && i >= labelFrom) { val q = p(an, R - 72f); text(c, i.toString(), q[0], q[1], 30f, if (lit) WHITE else marking(DIM, litNumber)) }
            i += 2
        }

        // cruise-control setting and speed limit markers
        if (t.cruiseOn && t.cruiseKmh > 1f) { val ca = ang(spd(t.cruiseKmh)); line(c, p(ca, R - 46f), p(ca, R + 10f), GREEN, 6f, true) }
        if (limit > 0 && limit <= max) {
            val la = ang(limit)
            val tip = p(la, R + 2f); val l1 = p(la - 1.6f, R + 24f); val l2 = p(la + 1.6f, R + 24f)
            path.reset(); path.moveTo(tip[0], tip[1]); path.lineTo(l1[0], l1[1]); path.lineTo(l2[0], l2[1]); path.close()
            fill.shader = null; fill.color = if (over) RED else AMBER
            c.drawPath(path, fill)
        }

        // needle
        val na = ang(v)
        if (chrome) line(c, p(na, R - 150f), p(na, R + 2f), Color.argb(40, 255, 59, 48), 26f, true)
        line(c, p(na, R - 150f), p(na, R + 2f), Color.argb(70, 255, 59, 48), 16f, true)
        line(c, p(na, R - 150f), p(na, R + 2f), RED, if (chrome) 7f else 6f, true)
        if (chrome) line(c, p(na, R - 146f), p(na, R - 2f), Color.argb(200, 255, 190, 170), 1.6f, true)

        // speed
        val speedText = num(max(0f, v))
        val wNum = text(c, speedText, 640f, 268f, 118f, if (over) AMBER else WHITE, 1)
        text(c, spdUnit, 640f + wNum / 2 + 8f, 308f, 26f, DIM, 0, true)

        // make
        if (showMake && t.make.isNotEmpty()) {
            val name = t.make.uppercase().toCharArray().joinToString(" ")
            text(c, "‹", 640f - 170f, 372f, 30f, DIM, 1, true); text(c, name, 640f, 372f, 26f, Color.rgb(176, 184, 192), 1, true); text(c, "›", 640f + 170f, 372f, 30f, DIM, 1, true)
        }

        // gear
        text(c, gearText(t), 640f, 430f, 46f, if (t.gear < 0) AMBER else WHITE, 1)

        revs(c)
        panels(c)
        lamps(c)

        if (updateText.isNotEmpty()) {
            tp.typeface = bold; tp.textSize = 21f
            val tw = tp.measureText(updateText)
            roundRect(c, 640f - tw / 2 - 18f, 556f, 640f + tw / 2 + 18f, 588f, 8f, Color.rgb(30, 22, 8), AMBER, 1.6f)
            text(c, updateText, 640f, 572f, 21f, AMBER, 1)
        }

        if (statusText.isNotEmpty()) {
            fill.shader = null; fill.color = Color.argb(150, 3, 6, 10)
            c.drawRect(0f, 0f, DW, DH, fill)
            text(c, statusText, 640f, 330f, if (statusText.length > 40) 34f else 44f, DIM, 1, true)
        }
    }

    private fun gearText(t: Truck): String {
        val prefix = if (t.shifter == "automatic" || t.shifter == "arcade") "A " else ""
        return when {
            t.gear > 0 -> prefix + t.gear
            t.gear < 0 -> "R" + (if (t.gear < -1) abs(t.gear).toString() else "")
            else -> prefix + "N"
        }
    }

    private fun gauge(c: Canvas, a0: Float, a1: Float, fraction: Float, color: Int, left: Boolean) {
        val r = R + 56f
        if (!chrome) arc(c, r, a0, a1, Color.rgb(24, 36, 46), 12f)
        val fa = a1 + (a0 - a1) * fraction.coerceIn(0f, 1f)   // fills from the bottom end (a1) upwards
        if (fraction > 0.004f) {
            if (chrome) {
                // glowing liquid in a glass tube, with a highlight along the glass
                arc(c, r, a1, fa, Color.argb(60, Color.red(color), Color.green(color), Color.blue(color)), 22f)
                arc(c, r, a1, fa, color, 12f)
                arc(c, r - 2.5f, a1, fa, Color.argb(110, 255, 255, 255), 2.4f)
            } else arc(c, r, a1, fa, color, 12f)
        }
        val top = p(a0, r + 40f); val mid = p((a0 + a1) / 2, r + 36f); val bot = p(a1, r + 34f)
        text(c, if (left) "DIESEL" else defName, top[0], top[1] - 40f, 16f, DIM, 1, true)
        text(c, num(fraction * 100f) + "%", top[0], top[1] - 14f, 28f, color, 1)
        text(c, "1/2", mid[0], mid[1], 18f, DIM, 1, true)
        text(c, "E", bot[0], bot[1], 18f, DIM, 1, true)
    }

    private fun revs(c: Canvas) {
        val rmax = if (truck.rpmMax > 0) max(2500f, ceil(truck.rpmMax / 500f) * 500f) else 2500f
        val x0 = 440f; val x1 = 840f; val y = 514f
        fun xr(rpm: Float) = x0 + (x1 - x0) * (rpm / rmax).coerceIn(0f, 1f)
        line(c, x0, y, x1, y, GREY, 2f)
        var rpm = 0
        while (rpm <= rmax) {
            val major = rpm % 500 == 0 && rpm > 0
            line(c, xr(rpm.toFloat()), y, xr(rpm.toFloat()), y - (if (major) 16f else 8f), if (major) WHITE else GREY, if (major) 2.6f else 1.6f)
            if (major) text(c, (rpm / 100).toString(), xr(rpm.toFloat()), y - 34f, 24f, DIM)
            rpm += 100
        }
        val high = truck.rpmMax > 0 && dispRpm > truck.rpmMax * 0.92f
        val mx = xr(dispRpm)
        fill.shader = null; fill.color = Color.argb(70, 255, 59, 48)
        c.drawRect(mx - 6f, y - 24f, mx + 6f, y + 14f, fill)
        fill.color = if (high) AMBER else RED
        c.drawRect(mx - 2.5f, y - 22f, mx + 2.5f, y + 12f, fill)
        text(c, "rpm x100", 640f, y + 26f, 16f, DIM, 1, true)
    }

    private fun panels(c: Canvas) {
        val t = truck
        // top corners: coolant and oil temperature gauges
        // centred over the DIESEL / ADBLUE labels (the top end of the fuel and AdBlue arcs)
        val tx = tempX
        tempGauge(c, tx, CORNER, "COOLANT", dispWater, t.waterC, 105f, t.waterWarn)
        tempGauge(c, DW - tx, CORNER, "OIL", dispOil, t.oilC, 120f, t.oilWarn)

        // left: consumption, trip consumption, range
        row(c, 50f, 190f, "AVERAGE CONSUMPTION", cons(t.fuelAvgLPerKm * 100.0), consUnit, WHITE, false)
        row(c, 50f, 300f, "THIS TRIP", cons(tripLPer100), consUnit, WHITE, false)
        row(c, 50f, 410f, "RANGE", if (t.rangeKm > 0) num(dist(t.rangeKm)) else "--", distUnit, if (t.fuelWarn) AMBER else WHITE, false)

        // right: cruise, ETA, speed limit
        val cc = t.cruiseOn && t.cruiseKmh > 1f
        row(c, DW - 50f, 190f, "CRUISE CONTROL", if (cc) num(spd(t.cruiseKmh)) else "--", spdUnit, if (cc) GREEN else DIM, true)
        val eta = if (t.etaMin >= 0 && t.gameClockMin >= 0) clock((t.gameClockMin + t.etaMin.roundToInt()) % 1440) else "--:--"
        row(c, DW - 50f, 300f, "ARRIVAL (ETA)", eta, "", WHITE, true)
        row(c, DW - 50f, 410f, "SPEED LIMIT", if (t.speedLimit > 0) num(spd(t.speedLimit.toFloat())) else "--", spdUnit,
            if (t.speedLimit > 0 && spd(dispSpeed) > spd(t.speedLimit.toFloat()) + 3) AMBER else WHITE, true)

        // bottom row: game clock and rest, local time, trip and odometer
        line(c, 40f, 596f, DW - 40f, 596f, Color.rgb(24, 36, 46), 2f)
        text(c, "GAME TIME", 50f, 622f, 16f, DIM, 0, true); text(c, clock(t.gameClockMin), 50f, 656f, 34f, WHITE, 0)
        text(c, "REST IN", 250f, 622f, 16f, DIM, 0, true); text(c, hoursMinutes(t.restMin), 250f, 656f, 34f, if (t.restMin in 0f..30f) AMBER else WHITE, 0)
        // the device's local time, large, in the middle of the bottom row
        val cal = Calendar.getInstance()
        text(c, "LOCAL TIME", 640f, 612f, 16f, DIM, 1, true)
        text(c, "%02d:%02d".format(cal.get(Calendar.HOUR_OF_DAY), cal.get(Calendar.MINUTE)), 640f, 660f, 62f, WHITE, 1)
        text(c, "TRIP", DW - 330f, 622f, 16f, DIM, 2, true); text(c, num(dist(tripKm.toFloat())) + " " + distUnit, DW - 330f, 656f, 34f, WHITE, 2)
        text(c, "ODOMETER", DW - 50f, 622f, 16f, DIM, 2, true); text(c, num(dist(t.odoKm)) + " " + distUnit, DW - 50f, 656f, 34f, WHITE, 2)
    }

    /**
     * A small round temperature gauge: 270-degree scale open at the bottom, red zone from redC, needle, reading in the middle
     * and the name in the gap. The scale is 40-130 °C, or 100-260 °F with imperial units. Same as the Windows app.
     */
    private fun tempGauge(c: Canvas, cx: Float, cy: Float, name: String, shownC: Float, rawC: Float, redC: Float, warn: Boolean) {
        val r = 64f
        val lo = if (imp) 100f else 40f; val hi = if (imp) 260f else 130f
        val minor = if (imp) 20 else 10; val major = if (imp) 40 else 30
        fun u(cel: Float) = if (imp) cel * 9f / 5f + 32f else cel
        fun a(v: Float) = 225f - 270f * ((v - lo) / (hi - lo)).coerceIn(0f, 1f)
        fun q(angleDeg: Float, rad: Float): FloatArray {
            val an = angleDeg * PI / 180
            return floatArrayOf((cx + rad * cos(an)).toFloat(), (cy - rad * sin(an)).toFloat())
        }
        fun ring(a0: Float, a1: Float, color: Int) {
            oval.set(cx - r, cy - r, cx + r, cy + r)
            stroke.shader = null; stroke.color = color; stroke.strokeWidth = 6f; stroke.strokeCap = Paint.Cap.ROUND
            c.drawArc(oval, -a0, a0 - a1, false, stroke)
        }

        val hot = warn || (rawC > 0 && rawC >= redC)
        backlightFace(c, cx, cy, r + 6)
        ring(225f, -45f, Color.rgb(24, 36, 46))
        ring(a(u(redC)), -45f, Color.argb(200, 255, 59, 48))
        var v = lo.toInt()
        while (v <= hi.toInt()) {
            val big = (v - lo.toInt()) % major == 0
            val ta = a(v.toFloat())
            halo(c, q(ta, r - (if (big) 16f else 10f)), q(ta, r - 4f), if (big) 2.6f else 1.6f)
            line(c, q(ta, r - (if (big) 16f else 10f)), q(ta, r - 4f), if (big) WHITE else marking(GREY, litTick), if (big) 2.6f else 1.6f)
            v += minor
        }
        // scale ends: just below the track, or outside the chrome rim in the Chrome style
        val lr = if (chrome) r + 28f else r + 4f; val dy = if (chrome) 4f else 12f
        val l0 = q(225f, lr); val l1 = q(-45f, lr)
        text(c, lo.toInt().toString(), l0[0] - 2f, l0[1] + dy, 14f, DIM, 1, true)
        text(c, hi.toInt().toString(), l1[0] + 2f, l1[1] + dy, 14f, DIM, 1, true)

        // needle: from just outside the reading to the scale, like the speed dial
        if (rawC > 0) {
            val na = a(u(shownC))
            line(c, q(na, 30f), q(na, r - 2f), Color.argb(70, 255, 59, 48), 9f, true)
            line(c, q(na, 30f), q(na, r - 2f), RED, 3.5f, true)
        }
        text(c, temp(rawC), cx, cy + 2f, 26f, if (hot) RED else WHITE, 1)
        text(c, name, cx, cy + 50f, 15f, DIM, 1, true)
    }

    private fun row(c: Canvas, x: Float, y: Float, caption: String, value: String, unit: String, color: Int, right: Boolean) {
        text(c, caption, x, y, 16f, DIM, if (right) 2 else 0, true)
        tp.typeface = bold; tp.textSize = 46f
        val vw = tp.measureText(value)
        tp.typeface = light; tp.textSize = 20f
        val uw = if (unit.isNotEmpty()) tp.measureText(unit) else 0f
        val total = vw + (if (unit.isNotEmpty()) uw + 8f else 0f)
        val sx = if (right) x - total else x
        text(c, value, sx, y + 30f, 46f, color, 0)
        if (unit.isNotEmpty()) text(c, unit, sx + vw + 8f, y + 40f, 20f, DIM, 0, true)
    }

    // ------------------------------------------------------------------------------------------ lamps

    private fun lamps(c: Canvas) {
        val t = truck
        val y = 50f; val x = 640f
        arrow(c, x - 96f, y, true, if (t.blinkerL) GREEN else Color.rgb(26, 40, 32))
        arrow(c, x + 96f, y, false, if (t.blinkerR) GREEN else Color.rgb(26, 40, 32))
        beam(c, x - 48f, y, false, if (t.lowBeam) GREEN else Color.rgb(26, 40, 32))
        beam(c, x + 48f, y, true, if (t.highBeam) BLUE else Color.rgb(24, 34, 46))
        val tags = ArrayList<Pair<String, Int>>()
        if (t.parkBrake) tags.add("P BRAKE" to RED)
        if (t.motorBrake) tags.add("ENGINE BRAKE" to AMBER)
        if (t.retarder > 0) tags.add("RETARDER ${t.retarder}" to AMBER)
        if (t.wipers) tags.add("WIPERS" to DIM)
        if (t.beacon) tags.add("BEACON" to AMBER)
        if (t.airWarn) tags.add("AIR" to RED)
        if (t.oilWarn) tags.add("OIL" to RED)
        if (t.waterWarn) tags.add("COOLANT" to RED)
        if (t.batteryWarn) tags.add("BATTERY" to RED)
        if (t.fuelWarn) tags.add("FUEL" to AMBER)
        if (t.adBlueWarn) tags.add(defName to AMBER)
        if (tags.isEmpty()) return
        tp.typeface = bold; tp.textSize = 18f
        val widths = tags.map { tp.measureText(it.first) + 26f }
        val total = widths.sum() + 8f * (tags.size - 1)
        var tx = 640f - total / 2
        for ((idx, tag) in tags.withIndex()) {
            roundRect(c, tx, 88f, tx + widths[idx], 118f, 6f, Color.rgb(14, 22, 30), tag.second, 1.6f)
            text(c, tag.first, tx + widths[idx] / 2, 103f, 18f, tag.second, 1)
            tx += widths[idx] + 8f
        }
    }

    private fun arrow(c: Canvas, x: Float, y: Float, left: Boolean, color: Int) {
        val d = if (left) -1f else 1f
        path.reset()
        path.moveTo(x + d * 18, y)
        path.lineTo(x - d * 2, y - 16); path.lineTo(x - d * 2, y - 7)
        path.lineTo(x - d * 18, y - 7); path.lineTo(x - d * 18, y + 7)
        path.lineTo(x - d * 2, y + 7); path.lineTo(x - d * 2, y + 16)
        path.close()
        fill.shader = null; fill.color = color
        c.drawPath(path, fill)
    }

    private fun beam(c: Canvas, x: Float, y: Float, high: Boolean, color: Int) {
        // a "(" shape, then the light rays
        oval.set(x - 8 - 14, y - 14, x - 8 + 14, y + 14)
        stroke.shader = null; stroke.color = color; stroke.strokeWidth = 3.4f; stroke.strokeCap = Paint.Cap.ROUND
        c.drawArc(oval, -90f, -180f, false, stroke)
        for (i in -2..2) {
            val dy = if (high) i * 6f else i * 5f
            line(c, x + 2, y + dy, x + 22, y + dy + (if (high) 0f else 3f), color, 3f, true)
        }
    }
}
