/* Particle background — a field of theme-tinted dots that ripple away from the
 * pointer and spring back to rest. Layers on the default "deep HUD backdrop"
 * (clippy.css) the same way the Antigravity landing page floats dots over dark.
 *
 * FOOTPRINT — this is built to cost ~0% CPU at idle, not "a little":
 *   • requestAnimationFrame (never setInterval) → Chromium/WebView2 already
 *     pauses RAF when the window is minimised or occluded.
 *   • Settle-to-static: dots are ANCHORED (spring back to a home point). Once
 *     the pointer goes idle AND every dot has come to rest, the RAF loop STOPS.
 *     A still field of dots burns no CPU even while the window is focused and
 *     visible. The loop only restarts on pointer movement / resize.
 *   • devicePixelRatio is capped (DPR_CAP) so the canvas bitmap — the only real
 *     memory cost here — stays modest on 4K panels (the JS heap is a few KB).
 *
 * Wiring mirrors ambientBg.js: IIFE, localStorage flag, prefers-reduced-motion
 * aware, and a window.particleBgInterop toggle for a Settings → Appearance switch.
 *
 * Layer: a fixed <canvas id="cl-particles"> at z-index -2 — above the deep
 * backdrop gradient (html::before, z-index -3) and behind all app content.
 */
(function () {
    "use strict";

    // ── Tunables ───────────────────────────────────────────────────────────
    var DPR_CAP      = 1.5;     // cap canvas resolution; dots don't need 4K crispness.
    var DENSITY      = 9000;    // one dot per ~9k CSS px²; scaled by viewport.
    var MIN_DOTS     = 48;
    var MAX_DOTS     = 300;     // O(n) per frame — even a few hundred is a rounding error.
    var REPEL_RADIUS = 130;     // px reach of the pointer's push.
    var REPEL_FORCE  = 2.2;     // push strength at the cursor centre.
    var SPRING       = 0.014;   // pull back toward home (higher = snappier return).
    var FRICTION     = 0.86;    // velocity damping per frame (lower = settles faster).
    var REST_EPS     = 0.05;    // below this max motion (px/frame), treat field as at rest.
    var IDLE_MS      = 900;     // pointer-idle window before we allow the loop to stop.

    var canvas, ctx, dpr = 1;
    var w = 0, h = 0;
    var dots = [];
    var palette = [];
    var mouse = { x: -1e6, y: -1e6, t: 0 };   // far away = no influence until first move.
    var rafId = 0;
    var running = false;
    var resizeTimer = 0;
    var reduceMotion = false;

    function bgStyle() {
        try { return localStorage.getItem("clippy.bg") || "none"; }
        catch (e) { return "none"; }
    }

    // Pull the app's accent RGB triples from clippy.css so the field auto-adapts
    // to theme + colorblind-mode remaps instead of hard-coding a rainbow.
    function readPalette() {
        var cs = getComputedStyle(document.documentElement);
        function triple(name, fallback) {
            var v = (cs.getPropertyValue(name) || "").trim();
            return /^\d/.test(v) ? v : fallback;
        }
        var hue  = triple("--cl-hue", "45, 212, 191");      // accent (teal)
        var edge = triple("--cl-edge-rgb", "94, 234, 212"); // edge/hairline
        palette = [hue, hue, edge];                          // weight toward the accent
    }

    function spawn() {
        var area = w * h;
        var n = Math.round(area / DENSITY);
        n = Math.max(MIN_DOTS, Math.min(MAX_DOTS, n));
        dots = new Array(n);
        for (var i = 0; i < n; i++) {
            var hx = Math.random() * w;
            var hy = Math.random() * h;
            dots[i] = {
                hx: hx, hy: hy, x: hx, y: hy, vx: 0, vy: 0,
                r: 1.0 + Math.random() * 2.4,
                a: 0.25 + Math.random() * 0.5,
                c: palette[(Math.random() * palette.length) | 0]
            };
        }
    }

    function resize() {
        dpr = Math.min(window.devicePixelRatio || 1, DPR_CAP);
        w = window.innerWidth;
        h = window.innerHeight;
        canvas.width  = Math.round(w * dpr);
        canvas.height = Math.round(h * dpr);
        canvas.style.width  = w + "px";
        canvas.style.height = h + "px";
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0); // work in CSS pixels.
        spawn();
        if (reduceMotion) drawStatic(); else wake();
    }

    // One frame of physics + paint. Returns the largest per-dot motion seen so
    // we know when the field has come to rest.
    function step() {
        ctx.clearRect(0, 0, w, h);
        var maxMotion = 0;
        // Only repel while the pointer moved recently. Once it goes idle the
        // push releases and dots spring home — even if the cursor stays parked
        // in the window — so the field settles instead of holding a "hole".
        var influence = (performance.now() - mouse.t) < IDLE_MS;
        for (var i = 0; i < dots.length; i++) {
            var d = dots[i];
            var ax = (d.hx - d.x) * SPRING;   // spring toward home
            var ay = (d.hy - d.y) * SPRING;

            if (influence) {
                var ddx = d.x - mouse.x;
                var ddy = d.y - mouse.y;
                var dist2 = ddx * ddx + ddy * ddy;
                if (dist2 < REPEL_RADIUS * REPEL_RADIUS && dist2 > 0.01) {
                    var dist = Math.sqrt(dist2);
                    var f = (1 - dist / REPEL_RADIUS) * REPEL_FORCE;
                    ax += (ddx / dist) * f;
                    ay += (ddy / dist) * f;
                }
            }

            d.vx = (d.vx + ax) * FRICTION;
            d.vy = (d.vy + ay) * FRICTION;
            d.x += d.vx;
            d.y += d.vy;

            var m = Math.abs(d.vx) + Math.abs(d.vy);
            if (m > maxMotion) maxMotion = m;

            ctx.beginPath();
            ctx.arc(d.x, d.y, d.r, 0, 6.283185307);
            ctx.fillStyle = "rgba(" + d.c + "," + d.a + ")";
            ctx.fill();
        }
        return maxMotion;
    }

    function frame() {
        var motion = step();
        var idle = (performance.now() - mouse.t) > IDLE_MS;
        // Settle-to-static: stop the loop once the pointer is idle and every dot
        // has all but stopped. Next pointer move (wake) restarts it.
        if (idle && motion < REST_EPS) {
            running = false;
            rafId = 0;
            return;
        }
        rafId = requestAnimationFrame(frame);
    }

    function wake() {
        if (reduceMotion || running) return;
        running = true;
        rafId = requestAnimationFrame(frame);
    }

    // Reduced-motion: render a single still frame, no loop, no pointer reaction.
    function drawStatic() {
        ctx.clearRect(0, 0, w, h);
        for (var i = 0; i < dots.length; i++) {
            var d = dots[i];
            ctx.beginPath();
            ctx.arc(d.hx, d.hy, d.r, 0, 6.283185307);
            ctx.fillStyle = "rgba(" + d.c + "," + d.a + ")";
            ctx.fill();
        }
    }

    function onMove(e) {
        mouse.x = e.clientX;
        mouse.y = e.clientY;
        mouse.t = performance.now();
        if (!reduceMotion) wake();
    }

    function onLeave() {
        // Park the cursor far away so dots spring home, then let the loop settle.
        mouse.x = -1e6;
        mouse.y = -1e6;
    }

    function onResize() {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(resize, 150);
    }

    function onVisibility() {
        // RAF already pauses when hidden; stop explicitly so no stray frame runs.
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
        dots = [];
    }

    function start() {
        if (canvas) return;
        reduceMotion = !!(window.matchMedia &&
            window.matchMedia("(prefers-reduced-motion: reduce)").matches);

        canvas = document.createElement("canvas");
        canvas.id = "cl-particles";
        canvas.setAttribute("aria-hidden", "true");
        var s = canvas.style;
        s.position = "fixed";
        s.top = s.left = "0";
        s.width = "100%";
        s.height = "100%";
        s.zIndex = "-2";            // above deep backdrop (-3), behind app content.
        s.pointerEvents = "none";   // never intercepts clicks.
        document.body.appendChild(canvas);

        ctx = canvas.getContext("2d", { alpha: true, desynchronized: true });
        readPalette();
        resize();

        window.addEventListener("mousemove", onMove, { passive: true });
        // mouseleave on <html> (does NOT bubble) → fires only when the pointer
        // truly leaves the viewport, not when crossing between child elements.
        document.documentElement.addEventListener("mouseleave", onLeave, { passive: true });
        window.addEventListener("resize", onResize);
        document.addEventListener("visibilitychange", onVisibility);
    }

    function init() {
        if (bgStyle() === "particle") start();
    }

    // Settings → Appearance toggle (mirrors window.ambientBgInterop.setPhoto).
    window.particleBgInterop = {
        setEnabled: function (on) {
            try { localStorage.setItem("clippy.particles", on ? "on" : "off"); } catch (e) {}
            if (on) start(); else teardown();
        },
        // Live density tuning (no reload). densityPxPerDot: lower = more dots.
        // maxDots: hard cap. Re-seeds the field and redraws immediately.
        setDensity: function (densityPxPerDot, maxDots) {
            if (densityPxPerDot > 0) DENSITY = densityPxPerDot;
            if (maxDots > 0) MAX_DOTS = maxDots;
            if (!canvas) return;
            spawn();
            if (reduceMotion) { drawStatic(); return; }
            running = false;     // ensure a fresh frame is scheduled even if at rest
            wake();
        }
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
