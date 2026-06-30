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

    window.bgConfig = {
        get: get,
        set: function (style) {
            style = style || 'none';
            try { localStorage.setItem('clippy.bg', style); } catch (e) {}
            apply(style);
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { apply(get()); }, { once: true });
    } else {
        apply(get());
    }
})();
