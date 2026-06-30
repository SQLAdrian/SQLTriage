/* In the name of God, the Merciful, the Compassionate */

// Welcome-tour keyboard navigation.
//
// The Blazor overlay registers a DotNetObjectReference here when it mounts
// (tour active) and clears it when it hides. While the ref is set, we
// translate Esc / ArrowLeft / ArrowRight into Stop / Previous / Next
// callbacks on the .NET side.
//
// We attach a single document-level listener once, then dispatch based on
// the current ref. That way we never leak listeners across overlay
// re-mounts.

(function () {
    if (window.welcomeTourInterop) return; // idempotent

    let dotNetRef = null;

    function onKeyDown(ev) {
        if (!dotNetRef) return;
        // Don't hijack keys while the user is typing into form fields.
        const tag = (ev.target && ev.target.tagName) || '';
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' ||
            (ev.target && ev.target.isContentEditable)) return;

        switch (ev.key) {
            case 'Escape':
                dotNetRef.invokeMethodAsync('OnKeyStop');
                ev.preventDefault();
                break;
            case 'ArrowLeft':
                dotNetRef.invokeMethodAsync('OnKeyPrevious');
                ev.preventDefault();
                break;
            case 'ArrowRight':
                dotNetRef.invokeMethodAsync('OnKeyNext');
                ev.preventDefault();
                break;
        }
    }

    document.addEventListener('keydown', onKeyDown, true);

    window.welcomeTourInterop = {
        setDotNetRef: function (ref) { dotNetRef = ref; },
        clearDotNetRef: function () { dotNetRef = null; }
    };
})();
