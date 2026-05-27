// Global error handler for JavaScript errors
window.addEventListener('error', function(e) {
    console.error('JavaScript Error:', e.error);
    console.error('Stack:', e.error?.stack);
    console.error('Message:', e.message);
    console.error('Filename:', e.filename);
    console.error('Line:', e.lineno);
    console.error('Column:', e.colno);
});

// Handle unhandled promise rejections
window.addEventListener('unhandledrejection', function(e) {
    console.error('Unhandled Promise Rejection:', e.reason);
});

// Blazor error interop - call from C# to log errors to console
window.blazorErrorLogger = {
    logError: function(message, stack) {
        console.error('Blazor Error:', message);
        if (stack) console.error('Stack:', stack);
    }
};

console.log('JavaScript error handlers initialized');