/* Parallax background — anchors the ambient image to the user's monitor,
 * not the app window. Drag the window around and the photo "stays put"
 * relative to the screen, like wallpaper.
 *
 * Pairs with MainWindow.xaml.cs which posts JSON messages of shape:
 *   { type: "parallax", monitorW, monitorH, windowX, windowY }
 * via WebView2.PostWebMessageAsString. All values are CSS pixels (the host
 * has already converted from device pixels using the per-monitor DPI).
 */
(function () {
    "use strict";

    var pending = null;     // latest unprocessed message
    var rafId = 0;          // queued requestAnimationFrame
    var last = { mw: 0, mh: 0, wx: -1, wy: -1 };

    function flush() {
        rafId = 0;
        if (!pending) return;
        var m = pending;
        pending = null;
        // Skip if nothing actually changed — drags often emit identical ticks
        // when the OS de-bounces sub-pixel motion.
        if (m.monitorW === last.mw && m.monitorH === last.mh &&
            m.windowX === last.wx && m.windowY === last.wy) return;
        last.mw = m.monitorW; last.mh = m.monitorH;
        last.wx = m.windowX;  last.wy = m.windowY;
        var root = document.documentElement;
        root.style.setProperty("--bg-monitor-w", m.monitorW + "px");
        root.style.setProperty("--bg-monitor-h", m.monitorH + "px");
        root.style.setProperty("--bg-window-x", m.windowX + "px");
        root.style.setProperty("--bg-window-y", m.windowY + "px");
    }

    function apply(msg) {
        // Coalesce: WPF can fire LocationChanged faster than the browser
        // paints. Only commit the latest message per frame so we don't
        // queue up redundant style writes.
        pending = msg;
        if (!rafId) rafId = requestAnimationFrame(flush);
    }

    function onMessage(ev) {
        var data = ev.data;
        if (typeof data === "string") {
            try { data = JSON.parse(data); } catch { return; }
        }
        if (data && data.type === "parallax") apply(data);
    }

    // window.chrome.webview is only present when running inside WebView2.
    // In Blazor Server fallback (browser) the parallax simply stays
    // disabled and the existing background-cover behaviour remains.
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener("message", onMessage);
        // Tag root so CSS can switch from "cover" to monitor-anchored mode
        // only when the host is actually pushing coordinates.
        document.documentElement.classList.add("has-parallax-bg");
    }
})();
