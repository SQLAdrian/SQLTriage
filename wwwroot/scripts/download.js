/* In the name of God, the Merciful, the Compassionate */

// Download helper functions for Blazor

// Guided tour for guide page
function startGuidedTour() {
    const arrow = document.getElementById('tour-arrow');
    if (!arrow) return;

    arrow.style.display = 'block';

    const sections = [
        '.guide-section:nth-child(3)', // Audits and Tools
        'nav', // Navigation menu
        '.guide-section:nth-child(1)', // Live Dashboards
        '.guide-section:nth-child(4)', // Configure and Deploy
        '.guide-section:nth-child(2)'  // SQLWATCH Monitoring
    ];

    let currentIndex = 0;

    function highlightNext() {
        // Remove previous highlight
        document.querySelectorAll('.tour-highlight').forEach(el => el.classList.remove('tour-highlight'));

        if (currentIndex >= sections.length) {
            arrow.style.display = 'none';
            return;
        }

        const target = document.querySelector(sections[currentIndex]);
        if (target) {
            target.classList.add('tour-highlight');
            target.scrollIntoView({ behavior: 'smooth', block: 'center' });

            // Position arrow
            const rect = target.getBoundingClientRect();
            arrow.style.left = (rect.left + rect.width / 2 - 20) + 'px';
            arrow.style.top = (rect.top - 40) + 'px';
        }

        currentIndex++;
        setTimeout(highlightNext, 3000); // 3 seconds per section
    }

    highlightNext();
}

// Progressive loading with IntersectionObserver
function setupIntersectionObserver(panelId) {
    const element = document.getElementById(panelId);
    if (!element) return;

    const observer = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                // Load the panel
                DotNet.invokeMethodAsync('SqlHealthAssessment', 'LoadOnVisible', panelId)
                    .then(() => observer.disconnect());
            }
        });
    }, { threshold: 0.1 });

    observer.observe(element);
}

// Session charts rendering
function renderSessionCharts(cpuData, memoryData) {
    const cpuCtx = document.getElementById('cpuChart');
    if (cpuCtx) {
        new Chart(cpuCtx, {
            type: 'bar',
            data: {
                labels: cpuData.map(d => d.label),
                datasets: [{
                    label: 'CPU Time (ms)',
                    data: cpuData.map(d => d.value),
                    backgroundColor: 'rgba(255, 99, 132, 0.5)'
                }]
            }
        });
    }

    const memoryCtx = document.getElementById('memoryChart');
    if (memoryCtx) {
        new Chart(memoryCtx, {
            type: 'bar',
            data: {
                labels: memoryData.map(d => d.label),
                datasets: [{
                    label: 'Memory Usage (KB)',
                    data: memoryData.map(d => d.value),
                    backgroundColor: 'rgba(54, 162, 235, 0.5)'
                }]
            }
        });
    }
}

// Correlation heatmap rendering
function renderCorrelationHeatmap(data, labels) {
    const canvas = document.getElementById('correlationChart');
    if (!canvas) return;

    const ctx = canvas.getContext('2d');

    // Destroy existing chart if any
    if (window.correlationChart) {
        window.correlationChart.destroy();
    }

    window.correlationChart = new Chart(ctx, {
        type: 'matrix',
        data: {
            datasets: [{
                data: data,
                backgroundColor: (ctx) => {
                    const value = ctx.raw.v;
                    const alpha = Math.abs(value) / 100;
                    return value > 0 ? `rgba(255, 0, 0, ${alpha})` : `rgba(0, 0, 255, ${alpha})`;
                },
                borderColor: 'white',
                borderWidth: 1,
                width: ({chart}) => (chart.chartArea || {}).width / labels.length - 1,
                height: ({chart}) => (chart.chartArea || {}).height / labels.length - 1
            }]
        },
        options: {
            responsive: true,
            plugins: {
                legend: { display: false },
                tooltip: {
                    callbacks: {
                        title: () => '',
                        label: (ctx) => `${ctx.raw.x} vs ${ctx.raw.y}: ${ctx.raw.v}%`
                    }
                }
            },
            scales: {
                x: {
                    type: 'category',
                    labels: labels,
                    ticks: { display: true },
                    grid: { display: false }
                },
                y: {
                    type: 'category',
                    labels: labels,
                    ticks: { display: true },
                    grid: { display: false }
                }
            }
        }
    });
}
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
