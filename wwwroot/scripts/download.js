/* In the name of God, the Merciful, the Compassionate */

// Download helper functions for Blazor
// Version that accepts content directly (not base64)
function downloadFile(fileName, content, contentType) {
    try {
        var blob;
        
        // If content looks like base64, decode it
        if (content && !contentType && content.includes('\n')) {
            // Assume it's plain text content passed directly
            blob = new Blob([content], { type: contentType || 'text/plain;charset=utf-8' });
        } else if (content && !content.includes('\n') && content.length > 100) {
            // Might be base64
            try {
                var byteCharacters = atob(content);
                var byteNumbers = new Array(byteCharacters.length);
                for (var i = 0; i < byteCharacters.length; i++) {
                    byteNumbers[i] = byteCharacters.charCodeAt(i);
                }
                var byteArray = new Uint8Array(byteNumbers);
                blob = new Blob([byteArray], { type: contentType || 'text/csv;charset=utf-8' });
            } catch (e) {
                // Not base64, treat as plain text
                blob = new Blob([content], { type: contentType || 'text/plain;charset=utf-8' });
            }
        } else {
            // Plain text content
            blob = new Blob([content], { type: contentType || 'text/plain;charset=utf-8' });
        }
        
        var link = document.createElement('a');
        if (link.download !== undefined) {
            var url = URL.createObjectURL(blob);
            link.setAttribute('href', url);
            link.setAttribute('download', fileName);
            link.style.visibility = 'hidden';
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);
            URL.revokeObjectURL(url);
        }
    } catch (e) {
        console.error('Error downloading file:', e);
        alert('Error downloading file: ' + e.message);
    }
}
