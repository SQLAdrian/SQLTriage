/* In the name of God, the Merciful, the Compassionate */

// Background selector — standalone owner of the optional animated backdrop.
// Replaces the Clippy config surface (removed). The app is FLAT by default;
// the user can opt into the wave ("confetti") or particle field from
// Settings → Appearance. Choice persists in localStorage under 'clippy.bg'
// (key kept for continuity with existing installs).
//
//   'none'     → flat charcoal backdrop (default)
//   'wave'     → Antigravity-style flow field (waveBg.js)
//   'particle' → spring-physics dot field (particleBg.js)
//
// waveBg.js / particleBg.js self-init from the same key on load; this file
// owns the live toggle used by the Settings selector and re-applies on load
// to keep the two canvases in sync.

(function () {
    if (window.bgConfig) return; // idempotent

    function get() {
        try { return localStorage.getItem('clippy.bg') || 'none'; }
        catch (e) { return 'none'; }
    }

    function apply(style) {
        if (window.waveBgInterop) window.waveBgInterop.setEnabled(style === 'wave');
        if (window.particleBgInterop) window.particleBgInterop.setEnabled(style === 'particle');
    }

    // ── Occlusion measurement ────────────────────────────────────────────────
    // Both canvases sit at z-index:-2, fixed. A negative-z child paints BEFORE its
    // non-positioned ancestors' background boxes, so an opaque background anywhere
    // in the normal-flow chain above them buries the backdrop completely — the app
    // then looks exactly as it does with the backdrop off, and the Settings dropdown
    // still says "wave". That regression has happened once already (a later
    // stylesheet section re-set body's background-color and gave .app-layout an
    // opaque radial gradient, undoing the fix documented at app.css:253).
    // This measures the chain rather than trusting it.
    function alphaOf(colour) {
        if (!colour) return 0;
        var c = colour.trim();
        if (c === 'transparent') return 0;
        var m = c.match(/^rgba?\(([^)]+)\)$/);
        if (!m) return 1;                       // a named/hex colour is opaque
        var parts = m[1].split(',');
        return parts.length > 3 ? parseFloat(parts[3]) : 1;
    }

    function occluders() {
        var found = [];
        try {
            var check = function (el, label) {
                if (!el) return;
                var cs = getComputedStyle(el);
                if (alphaOf(cs.backgroundColor) > 0.01) { found.push(label); return; }
                var img = (cs.backgroundImage || '').trim();
                if (img && img !== 'none') found.push(label);
            };
            check(document.body, 'the page body');
            check(document.querySelector('.app-container'), 'the app container');
            check(document.querySelector('.app-layout'), 'the app layout');
        } catch (e) {}
        return found;
    }

    window.bgConfig = {
        get: get,
        set: function (style) {
            style = style || 'none';
            try { localStorage.setItem('clippy.bg', style); } catch (e) {}
            apply(style);
        },
        occluders: occluders,
        // The measured state of the CHOSEN backdrop. Settings prints its sentence from
        // this, never from the dropdown value alone. Occlusion is only measured when a
        // canvas is actually present, so a missing canvas can never be reported as
        // "hidden behind something".
        status: function () {
            var s = get();
            var st = null;
            if (s === 'wave' && window.waveBgInterop && window.waveBgInterop.getStatus) {
                st = window.waveBgInterop.getStatus();
            } else if (s === 'particle' && window.particleBgInterop && window.particleBgInterop.getStatus) {
                st = window.particleBgInterop.getStatus();
            }
            return {
                style: s,
                canvasPresent: st ? !!st.canvasPresent : false,
                marks: st ? (st.marks || 0) : 0,
                reducedMotion: st ? !!st.reducedMotion : false,
                occluders: (st && st.canvasPresent) ? occluders() : []
            };
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { apply(get()); }, { once: true });
    } else {
        apply(get());
    }
})();
