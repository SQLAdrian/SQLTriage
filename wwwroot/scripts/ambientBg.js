/* Ambient background — picks one of the available images on load and applies it.
 * Adds `has-ambient-bg` to <body> and sets --ambient-bg CSS variable.
 *
 * Backgrounds live in /Backgrounds/ and follow the pattern general1..N.jpg.
 * If a file fetch fails (e.g. you delete one), the image silently falls through
 * and the next visit picks a different number.
 */
(function () {
    "use strict";

    // Hard-coded count matches the current files in wwwroot/Backgrounds/.
    // If you add more, bump this. (Lightweight — avoids needing a server-side index.)
    var COUNT = 10;
    var prefix = "/Backgrounds/general";
    var ext = ".jpg";

    function pickAndApply() {
        var n = Math.floor(Math.random() * COUNT) + 1;
        var url = prefix + n + ext;

        // Preload before applying so we don't flash a broken image
        var img = new Image();
        img.onload = function () {
            // Apply on <html> so the bg sits behind the entire scroll universe
            // (and stays fixed even when inner containers scroll). Also tag
            // <body> so glass card overrides keyed on body.has-ambient-bg apply.
            document.documentElement.style.setProperty("--ambient-bg", "url('" + url + "')");
            document.documentElement.classList.add("has-ambient-bg");
            document.body.classList.add("has-ambient-bg");
            console.log("[ambientBg] applied", url);
        };
        img.onerror = function () {
            var retry = ((n % COUNT) + 1);
            var retryImg = new Image();
            retryImg.onload = function () {
                var retryUrl = prefix + retry + ext;
                document.documentElement.style.setProperty("--ambient-bg", "url('" + retryUrl + "')");
                document.documentElement.classList.add("has-ambient-bg");
                document.body.classList.add("has-ambient-bg");
                console.log("[ambientBg] applied (retry)", retryUrl);
            };
            retryImg.src = prefix + retry + ext;
        };
        img.src = url;
    }

    // CLIPPY redesign (2026-05-29): the photo wallpaper is now OPT-IN. The
    // default is the deep HUD backdrop (clippy.css) so the glow/grain/glass
    // read like a real interface instead of fighting a stock photo. Users can
    // turn the photo back on via Settings → Appearance, which sets this flag.
    //   localStorage 'clippy.wallpaper' === 'photo'  → show photo
    //   anything else (default)                      → deep HUD backdrop
    function wallpaperEnabled() {
        try { return localStorage.getItem("clippy.wallpaper") === "photo"; }
        catch (e) { return false; }
    }

    function init() { if (wallpaperEnabled()) pickAndApply(); }

    // Expose so a Settings toggle / DevBridge can flip it without a reload guess.
    window.ambientBgInterop = {
        setPhoto: function (on) {
            try { localStorage.setItem("clippy.wallpaper", on ? "photo" : "hud"); } catch (e) {}
            if (on) { pickAndApply(); }
            else {
                document.documentElement.classList.remove("has-ambient-bg", "has-parallax-bg");
                document.body.classList.remove("has-ambient-bg");
                document.documentElement.style.removeProperty("--ambient-bg");
            }
        }
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
