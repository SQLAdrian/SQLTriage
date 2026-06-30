/* In the name of God, the Merciful, the Compassionate */

// Bubble drag and revert functionality for SessionBubbleView
// Uses event delegation for better handling of dynamic content
window.bubbleDragInterop = {
    // Track dragging state per container
    _state: {},

    // Initialize drag handlers for a bubble container
    initDrag: function (containerId, revertDurationMs) {
        var container = document.getElementById(containerId);
        if (!container) {
            console.log('Bubble drag: container not found:', containerId);
            return;
        }

        // Skip if already initialized
        if (this._state[containerId] && this._state[containerId].initialized) {
            console.log('Bubble drag: already initialized for:', containerId);
            return;
        }

        var self = this;
        this._state[containerId] = {
            isDragging: false,
            currentBubble: null,
            currentSpid: null,
            startX: 0,
            startY: 0,
            originalX: 0,
            originalY: 0,
            revertTimer: null,
            revertDurationMs: revertDurationMs || 10000, // Default 10 seconds
            initialized: true
        };

        console.log('Bubble drag: initializing for container:', containerId);

        // Use event delegation - attach listener to container, catch events from bubbles
        container.addEventListener('mousedown', function(e) {
            self._onMouseDown(containerId, e);
        });

        // Document-level listeners for drag and release
        if (!this._state[containerId].docListeners) {
            document.addEventListener('mousemove', function(e) {
                self._onMouseMove(containerId, e);
            });
            document.addEventListener('mouseup', function(e) {
                self._onMouseUp(containerId, e);
            });
            this._state[containerId].docListeners = true;
        }
    },

    _onMouseDown: function (containerId, event) {
        var state = this._state[containerId];
        if (!state) return;

        // Find the closest session-bubble element
        var bubble = event.target.closest('.session-bubble');
        if (!bubble) return;

        var circle = bubble.querySelector('circle');
        if (!circle) return;

        // Get the SPID from the data attribute
        var spid = bubble.getAttribute('data-spid');

        event.preventDefault();
        
        // Get current position from the circle's cx/cy attributes
        var cx = parseFloat(circle.getAttribute('cx'));
        var cy = parseFloat(circle.getAttribute('cy'));

        state.isDragging = true;
        state.currentBubble = bubble;
        state.currentSpid = spid;
        state.startX = event.clientX;
        state.startY = event.clientY;
        state.originalX = cx;
        state.originalY = cy;

        // Add dragging class for visual feedback
        bubble.classList.add('dragging');
        
        // Clear any existing revert timer
        if (state.revertTimer) {
            clearTimeout(state.revertTimer);
            state.revertTimer = null;
        }
        
        console.log('Bubble drag: started dragging SPID:', spid, 'original pos:', state.originalX, state.originalY);
    },

    _onMouseMove: function (containerId, event) {
        var state = this._state[containerId];
        if (!state || !state.isDragging || !state.currentBubble) return;

        var container = document.getElementById(containerId);
        if (!container) return;

        // Get SVG coordinate system transformation
        var svg = container.querySelector('svg');
        if (!svg) return;

        var pt = svg.createSVGPoint();
        pt.x = event.clientX;
        pt.y = event.clientY;
        var svgP = pt.matrixTransform(svg.getScreenCTM().inverse());

        // Update circle position
        var circle = state.currentBubble.querySelector('circle');
        var text = state.currentBubble.querySelector('text');
        var highlight = state.currentBubble.querySelector('.bubble-highlight');

        if (circle) circle.setAttribute('cx', svgP.x);
        if (circle) circle.setAttribute('cy', svgP.y);
        if (text) {
            text.setAttribute('x', svgP.x);
            text.setAttribute('y', svgP.y + 1);
        }
        if (highlight) {
            var r = parseFloat(circle.getAttribute('r'));
            highlight.setAttribute('cx', svgP.x - r * 0.25);
            highlight.setAttribute('cy', svgP.y - r * 0.25);
        }

        // Update blocking lines connected to this bubble using data attributes
        this._updateBlockingLines(container, state.currentSpid, svgP.x, svgP.y);
    },

    _updateBlockingLines: function (container, spid, newX, newY) {
        if (!spid) return;
        
        // Find all blocking lines connected to this SPID (as blocker or as blocked)
        var lines = container.querySelectorAll('.blocking-line[data-blocker-spid="' + spid + '"], .blocking-line[data-blocked-spid="' + spid + '"]');
        
        lines.forEach(function(line) {
            var blockerSpid = line.getAttribute('data-blocker-spid');
            var blockedSpid = line.getAttribute('data-blocked-spid');
            
            if (blockerSpid === spid) {
                // This SPID is the blocker - update the start of the line
                line.setAttribute('x1', newX);
                line.setAttribute('y1', newY);
            }
            
            if (blockedSpid === spid) {
                // This SPID is blocked - update the end of the line
                line.setAttribute('x2', newX);
                line.setAttribute('y2', newY);
            }
        });
    },

    _onMouseUp: function (containerId, event) {
        var state = this._state[containerId];
        if (!state || !state.isDragging) return;

        // Get final position
        var circle = state.currentBubble ? state.currentBubble.querySelector('circle') : null;
        if (circle) {
            var finalX = parseFloat(circle.getAttribute('cx'));
            var finalY = parseFloat(circle.getAttribute('cy'));

            // Remove dragging class
            if (state.currentBubble) {
                state.currentBubble.classList.remove('dragging');
            }

            console.log('Bubble drag: released SPID:', state.currentSpid, 'at:', finalX, finalY, 'will revert in:', state.revertDurationMs);

            // Start revert timer (10 seconds)
            var self = this;
            state.revertTimer = setTimeout(function () {
                self._animateRevert(containerId, state.currentBubble, state.currentSpid, finalX, finalY, state.originalX, state.originalY);
            }, state.revertDurationMs);
        }

        state.isDragging = false;
        state.currentBubble = null;
        state.currentSpid = null;
    },

    _animateRevert: function (containerId, bubble, spid, fromX, fromY, toX, toY) {
        if (!bubble) return;

        var duration = 2000; // 2 second animation
        var startTime = performance.now();
        var self = this;

        console.log('Bubble drag: animating revert for SPID:', spid, 'from', fromX, fromY, 'to', toX, toY);

        function animate(currentTime) {
            var elapsed = currentTime - startTime;
            var progress = Math.min(elapsed / duration, 1);
            
            // Ease out cubic
            var easeProgress = 1 - Math.pow(1 - progress, 3);

            var currentX = fromX + (toX - fromX) * easeProgress;
            var currentY = fromY + (toY - fromY) * easeProgress;

            var circle = bubble.querySelector('circle');
            var text = bubble.querySelector('text');
            var highlight = bubble.querySelector('.bubble-highlight');

            if (circle) circle.setAttribute('cx', currentX);
            if (circle) circle.setAttribute('cy', currentY);
            if (text) {
                text.setAttribute('x', currentX);
                text.setAttribute('y', currentY + 1);
            }
            if (highlight) {
                var r = parseFloat(circle.getAttribute('r'));
                highlight.setAttribute('cx', currentX - r * 0.25);
                highlight.setAttribute('cy', currentY - r * 0.25);
            }

            // Update blocking lines during animation
            self._updateBlockingLines(document.getElementById(containerId), spid, currentX, currentY);

            if (progress < 1) {
                requestAnimationFrame(animate);
            } else {
                console.log('Bubble drag: revert complete');
            }
        }

        requestAnimationFrame(animate);
    },

    // Re-syncs all blocking line endpoints to match current bubble positions.
    // Called after Blazor re-renders to prevent lines being stranded at stale positions.
    redrawBlockingLines: function (containerId) {
        var container = document.getElementById(containerId);
        if (!container) return;

        var lines = container.querySelectorAll('.blocking-line');
        lines.forEach(function (line) {
            var blockerSpid = line.getAttribute('data-blocker-spid');
            var blockedSpid = line.getAttribute('data-blocked-spid');

            var blockerCircle = container.querySelector('[data-spid="' + blockerSpid + '"] .bubble-circle');
            var blockedCircle = container.querySelector('[data-spid="' + blockedSpid + '"] .bubble-circle');

            if (blockerCircle) {
                line.setAttribute('x1', blockerCircle.getAttribute('cx'));
                line.setAttribute('y1', blockerCircle.getAttribute('cy'));
            }
            if (blockedCircle) {
                line.setAttribute('x2', blockedCircle.getAttribute('cx'));
                line.setAttribute('y2', blockedCircle.getAttribute('cy'));
            }
        });
    },

    // Cleanup
    dispose: function (containerId) {
        if (this._state[containerId]) {
            if (this._state[containerId].revertTimer) {
                clearTimeout(this._state[containerId].revertTimer);
            }
            delete this._state[containerId];
        }
    }
};
