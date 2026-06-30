/* In the name of God, the Merciful, the Compassionate */

// Flow-field background — an "ocean wave / anemone" field of small rounded-cap
// dashes that comb along a SIMPLEX-NOISE curl flow field and react to expanding
// RING PULSES that emanate from the cursor. A Canvas-2D homage to the Google
// Antigravity landing page (antigravity.google), adapted to SQLTriage's dark
// polished canvas.
//
// HOW THE REAL ANTIGRAVITY EFFECT WORKS (reverse-engineered from main-*.js):
//   It is a three.js GPGPU particle system — positions live in a float texture,
//   advected each frame by a GLSL Simplex-noise flow field, springing back to
//   reference positions (uPosRefs), disturbed by an expanding ring uniform set
//   (uRingPos / uRingRadius / uRingWidth) driven from the pointer, coloured by a
//   three-stop palette (uColor1/2/3), GSAP-driven and always-on.
//
// THIS FILE mirrors that *model* on the CPU (no WebGL dependency): real 2D
// Simplex noise → curl flow for orientation; spring-to-home anchors; expanding
// ring pulses from the cursor; a three-colour palette mixed per mark via a seed.
// It keeps the embedded-app discipline the GPU version doesn't need: the loop is
// ALIVE ON INTERACTION, FREE AT REST — it settles to a static (still combed)
// frame and stops when the pointer is idle. prefers-reduced-motion → static.
//
// Layer: fixed <canvas id="cl-wave"> at z-index -2, pointer-events:none.

(function () {
    "use strict";

    // ── Tunables ────────────────────────────────────────────────────────────
    var DPR_CAP     = 1.5;
    var FPS         = 60;
    var MIN_DIST    = 40;         // Poisson spacing between mark homes (px).
    var MAX_MARKS   = 460;
    var BG_COLOR    = "#0b0f12";

    // Simplex-curl flow field
    var NOISE_SCALE = 0.0016;     // spatial frequency of the potential.
    var NOISE_EPS   = 2.0;        // finite-difference step for the curl (px).
    var FLOW_SPEED  = 0.30;       // field morph rate (× energy).
    var FLOW_PUSH   = 0.30;       // advection accel along the flow (× energy).
    var SWIRL_AMT   = 0.55;       // weight of the centre vortex in the orientation.
    var SWIRL_REACH = 540;
    var SPRING      = 0.012;
    var FRICTION    = 0.88;

    // Expanding ring pulses (the "ocean wave" — uRingPos / uRingRadius analog)
    var RING_SPEED      = 560;    // px/s the ring radius expands.
    var RING_LIFE       = 1.15;   // s a ring lives before it fades out.
    var RING_WIDTH      = 52;     // px half-width of the wave band.
    var RING_PUSH       = 11;     // px crest displacement (× falloff) — a wave, not a hole.
    var RING_BRIGHT     = 1.9;    // alpha boost at the crest.
    var RING_RETRIGGER  = 60;     // px the pointer must travel to spawn a new ring.
    var MAX_RINGS       = 5;

    // Direct pointer hover (uIsHovering analog)
    var HOVER_R     = 150;
    var HOVER_BRIGHT= 1.5;

    // Energy → motion → rest
    var ENERGY_DECAY = 0.95;
    var REST_EPS     = 0.04;
    var IDLE_MS      = 260;

    // Three-colour palette (uColor1/2/3). Cool-dominant: blue → magenta → coral.
    var COLOR1 = [49, 134, 255];  // #3186FF  blue (brand)
    var COLOR2 = [168, 85, 247];  // purple/magenta
    var COLOR3 = [244, 114, 94];  // warm coral
    var COLOR_GAMMA = 1.9;        // skews the per-mark mix toward COLOR1 (cool).
    var SAT_LIGHT  = 0.0;         // (reserved)

    var canvas, ctx, dpr = 1;
    var w = 0, h = 0, cx = 0, cy = 0;
    var marks = [];
    var rings = [];
    var mouse = { x: null, y: null, t: -1e9, lastRingX: -1e9, lastRingY: -1e9 };
    var energy = 0;
    var rafId = 0, running = false;
    var resizeTimer = 0, reduceMotion = false;
    var lastFrame = 0, frameInterval = 1000 / FPS, flowT = 0;

    function bgStyle() {
        try { return localStorage.getItem("clippy.bg") || "none"; }
        catch (e) { return "none"; }
    }

    // ── 2D Simplex noise (Gustavson/Ashima port), deterministic seed ─────────
    var snoise = (function () {
        var grad = [[1, 1], [-1, 1], [1, -1], [-1, -1], [1, 0], [-1, 0], [0, 1], [0, -1]];
        var p = new Uint8Array(256);
        for (var i = 0; i < 256; i++) p[i] = i;
        var s = 1337;
        for (var i = 255; i > 0; i--) {            // seeded Fisher–Yates shuffle
            s = (s * 16807) % 2147483647;
            var j = s % (i + 1);
            var t = p[i]; p[i] = p[j]; p[j] = t;
        }
        var perm = new Uint8Array(512);
        for (var i = 0; i < 512; i++) perm[i] = p[i & 255];
        var F2 = 0.5 * (Math.sqrt(3) - 1), G2 = (3 - Math.sqrt(3)) / 6;
        return function (xin, yin) {
            var sk = (xin + yin) * F2;
            var i = Math.floor(xin + sk), j = Math.floor(yin + sk);
            var t = (i + j) * G2;
            var x0 = xin - (i - t), y0 = yin - (j - t);
            var i1 = x0 > y0 ? 1 : 0, j1 = x0 > y0 ? 0 : 1;
            var x1 = x0 - i1 + G2, y1 = y0 - j1 + G2;
            var x2 = x0 - 1 + 2 * G2, y2 = y0 - 1 + 2 * G2;
            var ii = i & 255, jj = j & 255;
            var n0 = 0, n1 = 0, n2 = 0, tt;
            var g0 = grad[perm[ii + perm[jj]] & 7];
            var g1 = grad[perm[ii + i1 + perm[jj + j1]] & 7];
            var g2 = grad[perm[ii + 1 + perm[jj + 1]] & 7];
            tt = 0.5 - x0 * x0 - y0 * y0; if (tt > 0) { tt *= tt; n0 = tt * tt * (g0[0] * x0 + g0[1] * y0); }
            tt = 0.5 - x1 * x1 - y1 * y1; if (tt > 0) { tt *= tt; n1 = tt * tt * (g1[0] * x1 + g1[1] * y1); }
            tt = 0.5 - x2 * x2 - y2 * y2; if (tt > 0) { tt *= tt; n2 = tt * tt * (g2[0] * x2 + g2[1] * y2); }
            return 70 * (n0 + n1 + n2);            // ≈ [-1, 1]
        };
    })();

    // fBm scalar potential; curl of it (below) gives the divergence-free flow.
    function potential(x, y, t) {
        var s = NOISE_SCALE;
        return snoise(x * s + t * 0.6, y * s) +
               0.5 * snoise(x * s * 2 - t * 0.4 + 19.0, y * s * 2 + 7.0);
    }
    function flowVec(x, y, t) {
        var e = NOISE_EPS;
        var dPdy = potential(x, y + e, t) - potential(x, y - e, t);
        var dPdx = potential(x + e, y, t) - potential(x - e, y, t);
        var vx = dPdy, vy = -dPdx;
        var m = Math.sqrt(vx * vx + vy * vy) || 1e-6;
        vx /= m; vy /= m;
        var dx = x - cx, dy = y - cy;
        var dist = Math.sqrt(dx * dx + dy * dy) || 1e-6;
        var sw = SWIRL_AMT * Math.exp(-dist / SWIRL_REACH);
        vx = vx * (1 - sw) + (-dy / dist) * sw;
        vy = vy * (1 - sw) + ( dx / dist) * sw;
        return { x: vx, y: vy, a: Math.atan2(vy, vx) };
    }

    // Per-mark colour from the 3-stop palette via a cool-biased seed in [0,1].
    function paletteColor(u) {
        var a, b, f;
        if (u < 0.5) { a = COLOR1; b = COLOR2; f = u * 2; }
        else         { a = COLOR2; b = COLOR3; f = (u - 0.5) * 2; }
        return [
            Math.round(a[0] + (b[0] - a[0]) * f),
            Math.round(a[1] + (b[1] - a[1]) * f),
            Math.round(a[2] + (b[2] - a[2]) * f)
        ];
    }

    function seed() {
        marks.length = 0;
        var area = w * h;
        var target = Math.min(Math.floor(area / (MIN_DIST * MIN_DIST)), MAX_MARKS);
        var tries = target * 12, minD2 = MIN_DIST * MIN_DIST;
        while (marks.length < target && tries-- > 0) {
            var px = Math.random() * w, py = Math.random() * h, ok = true;
            for (var k = 0; k < marks.length; k++) {
                var ddx = px - marks[k].hx, ddy = py - marks[k].hy;
                if (ddx * ddx + ddy * ddy < minD2) { ok = false; break; }
            }
            if (!ok) continue;
            var r = Math.random();
            var len = 1 + r * r * 17;
            // Cool-biased palette seed + a soft regional shift (simplex) so colour
            // groups into zones, like the real per-particle seed + colour scheme.
            var u = Math.pow(Math.random(), COLOR_GAMMA) +
                    0.18 * snoise(px * 0.0012 + 30, py * 0.0012 + 60);
            u = u < 0 ? 0 : u > 1 ? 1 : u;
            var rgb = paletteColor(u);
            marks.push({
                hx: px, hy: py, px: px, py: py, vx: 0, vy: 0,
                len: len,
                width: 2.0 + (len / 18) * 2.2,
                baseAlpha: 0.34 + Math.random() * 0.42,
                phase: Math.random() * Math.PI * 2,
                r: rgb[0], g: rgb[1], b: rgb[2]
            });
        }
    }

    function resize() {
        dpr = Math.min(window.devicePixelRatio || 1, DPR_CAP);
        w = window.innerWidth;
        h = window.innerHeight;
        cx = w * 0.5;
        cy = h * 0.42;
        canvas.width  = Math.round(w * dpr);
        canvas.height = Math.round(h * dpr);
        canvas.style.width  = w + "px";
        canvas.style.height = h + "px";
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.lineCap = "round";
        seed();
        drawStatic();          // rest until a pointer move wakes the loop
    }

    function drawMark(x, y, ang, len, width, r, g, b, alpha) {
        var hx = Math.cos(ang) * len * 0.5;
        var hy = Math.sin(ang) * len * 0.5;
        ctx.strokeStyle = "rgba(" + r + "," + g + "," + b + "," + alpha.toFixed(3) + ")";
        ctx.lineWidth = width;
        ctx.beginPath();
        ctx.moveTo(x - hx, y - hy);
        ctx.lineTo(x + hx, y + hy);
        ctx.stroke();
    }

    function render(live) {
        ctx.globalCompositeOperation = "source-over";
        ctx.fillStyle = BG_COLOR;
        ctx.fillRect(0, 0, w, h);
        ctx.globalCompositeOperation = "lighter";

        var now = performance.now();
        var maxMotion = 0;

        // Advance & cull ring pulses (radius grows with real time).
        for (var ri = rings.length - 1; ri >= 0; ri--) {
            var age = (now - rings[ri].born) / 1000;
            if (age > RING_LIFE) { rings.splice(ri, 1); continue; }
            rings[ri].radius = age * RING_SPEED;
            rings[ri].amp = 1 - age / RING_LIFE;          // linear fade
        }

        var hovering = live && mouse.x !== null && (now - mouse.t) < 1600;

        for (var i = 0; i < marks.length; i++) {
            var m = marks[i];
            var fv = flowVec(m.hx, m.hy, flowT);
            var x, y, alpha = m.baseAlpha, len = m.len;

            if (live) {
                var ax = (m.hx - m.px) * SPRING + fv.x * FLOW_PUSH * energy;
                var ay = (m.hy - m.py) * SPRING + fv.y * FLOW_PUSH * energy;

                // Expanding ring pulses: a crest band that displaces + brightens.
                var bright = 1;
                for (var rj = 0; rj < rings.length; rj++) {
                    var R = rings[rj];
                    var rdx = m.px - R.x, rdy = m.py - R.y;
                    var rd = Math.sqrt(rdx * rdx + rdy * rdy) || 1e-6;
                    var band = (rd - R.radius) / RING_WIDTH;
                    var g = Math.exp(-band * band) * R.amp;   // gaussian crest
                    if (g > 0.001) {
                        var pf = g * RING_PUSH;
                        ax += (rdx / rd) * pf * 0.05;
                        ay += (rdy / rd) * pf * 0.05;
                        bright += (RING_BRIGHT - 1) * g;
                        len += len * 0.5 * g;
                    }
                }

                // Direct hover brighten (uIsHovering analog).
                if (hovering) {
                    var hdx = m.px - mouse.x, hdy = m.py - mouse.y;
                    var hd = Math.sqrt(hdx * hdx + hdy * hdy);
                    if (hd < HOVER_R) bright += (HOVER_BRIGHT - 1) * (1 - hd / HOVER_R);
                }

                m.vx = (m.vx + ax) * FRICTION;
                m.vy = (m.vy + ay) * FRICTION;
                m.px += m.vx; m.py += m.vy;
                var mo = Math.abs(m.vx) + Math.abs(m.vy);
                if (mo > maxMotion) maxMotion = mo;

                x = m.px; y = m.py;
                alpha = Math.min(m.baseAlpha * bright, 0.97);
            } else {
                x = m.hx; y = m.hy;
            }
            drawMark(x, y, fv.a, len, m.width, m.r, m.g, m.b, alpha);
        }
        return maxMotion;
    }

    function drawStatic() { render(false); }

    function frame(ts) {
        rafId = requestAnimationFrame(frame);
        if (ts - lastFrame < frameInterval) return;
        var dt = lastFrame ? Math.min((ts - lastFrame) / 1000, 0.05) : 0.016;
        lastFrame = ts;

        flowT += dt * FLOW_SPEED * energy;
        var motion = render(true);
        energy *= ENERGY_DECAY;

        var idle = (performance.now() - mouse.t) > IDLE_MS;
        if (idle && energy < 0.01 && rings.length === 0 && motion < REST_EPS) {
            running = false; rafId = 0;
            drawStatic();
            return;
        }
    }

    function wake() {
        if (reduceMotion || running) return;
        running = true;
        lastFrame = 0;
        rafId = requestAnimationFrame(frame);
    }

    function spawnRing(x, y) {
        if (rings.length >= MAX_RINGS) rings.shift();
        rings.push({ x: x, y: y, born: performance.now(), radius: 0, amp: 1 });
    }

    function onMove(e) {
        mouse.x = e.clientX;
        mouse.y = e.clientY;
        mouse.t = performance.now();
        energy = 1;
        var dx = mouse.x - mouse.lastRingX, dy = mouse.y - mouse.lastRingY;
        if (dx * dx + dy * dy > RING_RETRIGGER * RING_RETRIGGER) {
            spawnRing(mouse.x, mouse.y);
            mouse.lastRingX = mouse.x; mouse.lastRingY = mouse.y;
        }
        if (!reduceMotion) wake();
    }
    function onLeave() { mouse.x = null; mouse.y = null; }
    function onResize() { clearTimeout(resizeTimer); resizeTimer = setTimeout(resize, 150); }
    function onVisibility() {
        if (document.hidden && rafId) { cancelAnimationFrame(rafId); rafId = 0; running = false; }
    }

    function teardown() {
        if (rafId) cancelAnimationFrame(rafId);
        rafId = 0; running = false;
        window.removeEventListener("mousemove", onMove);
        document.documentElement.removeEventListener("mouseleave", onLeave);
        window.removeEventListener("resize", onResize);
        document.removeEventListener("visibilitychange", onVisibility);
        if (canvas && canvas.parentNode) canvas.parentNode.removeChild(canvas);
        canvas = ctx = null;
        marks.length = 0; rings.length = 0;
        flowT = 0; energy = 0;
    }

    function start() {
        if (canvas) return;
        reduceMotion = !!(window.matchMedia &&
            window.matchMedia("(prefers-reduced-motion: reduce)").matches);

        canvas = document.createElement("canvas");
        canvas.id = "cl-wave";
        canvas.setAttribute("aria-hidden", "true");
        var s = canvas.style;
        s.position = "fixed";
        s.top = s.left = "0";
        s.width = "100%";
        s.height = "100%";
        s.zIndex = "-2";
        s.pointerEvents = "none";
        document.body.appendChild(canvas);

        ctx = canvas.getContext("2d", { alpha: false });
        resize();

        window.addEventListener("mousemove", onMove, { passive: true });
        document.documentElement.addEventListener("mouseleave", onLeave, { passive: true });
        window.addEventListener("resize", onResize);
        document.addEventListener("visibilitychange", onVisibility);
    }

    function init() { if (bgStyle() === "wave") start(); }

    window.waveBgInterop = {
        setEnabled: function (on) { if (on) start(); else teardown(); }
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
