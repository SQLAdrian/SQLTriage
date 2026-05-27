/* In the name of God, the Merciful, the Compassionate */

// Theme system for SQLTriage — sets data-theme on <html>; all colour/personality
// variables are defined in app.css under [data-theme="..."] blocks.
// DO NOT use root.style.setProperty here — inline styles override attribute selectors.

var VALID_THEMES = ['default', 'rolls-royce', 'amg'];
var DEFAULT_THEME = 'default';
var STORAGE_KEY = 'sqltriage-theme';

function applyTheme(themeName) {
    var theme = VALID_THEMES.indexOf(themeName) >= 0 ? themeName : DEFAULT_THEME;
    document.documentElement.setAttribute('data-theme', theme);
    // Clear any legacy inline property overrides that would win over attribute selectors
    document.documentElement.style.removeProperty('--bg-primary');
    document.documentElement.style.removeProperty('--bg-secondary');
    document.documentElement.style.removeProperty('--bg-panel');
    document.documentElement.style.removeProperty('--bg-hover');
    document.documentElement.style.removeProperty('--text-primary');
    document.documentElement.style.removeProperty('--text-secondary');
    document.documentElement.style.removeProperty('--text-muted');
    document.documentElement.style.removeProperty('--border');
    document.documentElement.style.removeProperty('--accent');
    document.documentElement.style.removeProperty('--green');
    document.documentElement.style.removeProperty('--orange');
    document.documentElement.style.removeProperty('--red');
    document.documentElement.style.removeProperty('--purple');
    document.documentElement.style.removeProperty('--yellow');
    document.body.style.removeProperty('background');
    document.body.style.removeProperty('background-image');
    try { localStorage.setItem(STORAGE_KEY, theme); } catch(e) {}
}

function loadSavedTheme() {
    var saved;
    try { saved = localStorage.getItem(STORAGE_KEY); } catch(e) {}
    applyTheme(saved || DEFAULT_THEME);
}

window.loadSavedTheme = loadSavedTheme;
window.applyTheme = applyTheme;
