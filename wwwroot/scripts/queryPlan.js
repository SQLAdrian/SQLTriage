/* In the name of God, the Merciful, the Compassionate */

// Query plan viewer helpers for Blazor JS interop - Fixed XML tree parsing with proper scoping
window.queryPlanInterop = (function() {
    var svgNS = 'http://www.w3.org/2000/svg';
    
    // ── private state ───────────────────────────────────────────────────────
    var nodeMap = {};  // Global node map accessible to all render functions
    
    // ── public API ──────────────────────────────────────────────────────────
    return {
        showPlan: function(containerId, xml) {
            var container = document.getElementById(containerId);
            if (!container) return;
            container.innerHTML = '';
            
            try {
                this._renderQueryPlan(container, xml);
            } catch (e) {
                container.innerHTML = '<p style="color:#f44336;padding:16px;">Failed to render query plan: ' + e.message + '</p>';
            }
        },
        
        clearPlan: function(containerId) {
            var container = document.getElementById(containerId);
            if (container) container.innerHTML = '';
        },
        
        // ── private helpers ───────────────────────────────────────────────────
        _ns: 'http://schemas.microsoft.com/sqlserver/2004/07/showplan',
        
        // Parse XML and build proper tree structure - FIX: return nodeMap for closure access
        _parseXmlTree: function(xml) {
            var parsedXml = new DOMParser().parseFromString(xml, "text/xml");
            if (!parsedXml || !parsedXml.documentElement) return null;
            
            var qps = parsedXml.getElementsByTagNameNS(this._ns, 'QueryPlan');
            if (qps.length === 0) {
                // Try without namespace
                qps = parsedXml.getElementsByTagName('QueryPlan');
            }
            if (qps.length === 0) return null;
            var qp = qps[0];
            
            // Collect all statement elements (StmtSimple, StmtCond, StmtCursor, etc.)
            var stmts = [];
            var stmtElements = parsedXml.getElementsByTagNameNS(this._ns, 'StmtSimple');
            if (stmtElements.length === 0) {
                stmtElements = parsedXml.getElementsByTagName('StmtSimple');
            }
            
            for (var i = 0; i < stmtElements.length; i++) {
                stmts.push(stmtElements[i]);
            }
            
            // Also collect StmtCond, StmtCursor, etc.
            var condElements = parsedXml.getElementsByTagNameNS(this._ns, 'StmtCond');
            if (condElements.length === 0) {
                condElements = parsedXml.getElementsByTagName('StmtCond');
            }
            for (var i = 0; i < condElements.length; i++) {
                stmts.push(condElements[i]);
            }
            
            var cursorElements = parsedXml.getElementsByTagNameNS(this._ns, 'StmtCursor');
            if (cursorElements.length === 0) {
                cursorElements = parsedXml.getElementsByTagName('StmtCursor');
            }
            for (var i = 0; i < cursorElements.length; i++) {
                stmts.push(cursorElements[i]);
            }
            
            // Build node map: nodeId -> node info - FIX: assign to global scope
            nodeMap = {};
            var statementNodes = []; // Top-level statements
            
            // Process each statement element
            for (var s = 0; s < stmts.length; s++) {
                var stmtEl = stmts[s];
                var stmtNodeId = null;
                
                // Find the root RelOp or operator element in this statement
                var relOps = stmtEl.getElementsByTagNameNS(this._ns, 'RelOp');
                if (relOps.length === 0) {
                    relOps = stmtEl.getElementsByTagName('RelOp');
                }
                
                for (var r = 0; r < relOps.length; r++) {
                    var relOp = relOps[r];
                    var nodeId = relOp.getAttribute('NodeId');
                    if (!nodeId) continue;
                    
                    // Create node entry - FIX: store in global nodeMap
                    var nodeInfo = {
                        element: relOp,
                        statementElement: stmtEl,
                        parentRelOp: null,
                        children: [],
                        isRootOfStatement: false
                    };
                    
                    nodeMap[nodeId] = nodeInfo;
                    
                    // Find parent RelOp (nearest ancestor that is a RelOp)
                    var current = relOp.parentElement;
                    while (current && current !== qp) {
                        var parentRelOps = current.getElementsByTagNameNS(this._ns, 'RelOp');
                        if (parentRelOps.length > 0) {
                            for (var p = 0; p < parentRelOps.length; p++) {
                                var parentRelOp = parentRelOps[p];
                                var parentNodeId = parentRelOp.getAttribute('NodeId');
                                if (parentNodeId && parentNodeId === nodeId) {
                                    // This is the same node, skip
                                    continue;
                                }
                                // Check if this parent contains this RelOp in its operator element
                                var parentOperator = this._getOperatorElement(parentRelOp);
                                if (parentOperator) {
                                    var childOps = parentOperator.getElementsByTagNameNS(this._ns, 'RelOp');
                                    for (var c = 0; c < childOps.length; c++) {
                                        var childOp = childOps[c];
                                        if (childOp.getAttribute('NodeId') === nodeId) {
                                            nodeInfo.parentRelOp = parentRelOp;
                                            break;
                                        }
                                    }
                                } else {
                                    // Check for other operator element types
                                    var seekPreds = parentOperator.getElementsByTagNameNS(this._ns, 'SeekPredicateNew');
                                    if (seekPreds.length > 0) {
                                        for (var sp = 0; sp < seekPreds.length; sp++) {
                                            var spChildOps = seekPreds[sp].getElementsByTagNameNS(this._ns, 'RelOp');
                                            for (var c2 = 0; c2 < spChildOps.length; c2++) {
                                                if (spChildOps[c2].getAttribute('NodeId') === nodeId) {
                                                    nodeInfo.parentRelOp = parentRelOp;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                                if (nodeInfo.parentRelOp) break;
                            }
                        }
                        if (nodeInfo.parentRelOp) break;
                        current = current.parentElement;
                    }
                    
                    // If no parent found, this is a top-level node
                    if (!nodeInfo.parentRelOp) {
                        nodeInfo.isRootOfStatement = true;
                        statementNodes.push(nodeInfo);
                    }
                }
            }
            
            return {
                root: qp,
                statementNodes: statementNodes,
                nodeMap: nodeMap  // Return for reference
            };
        },
        
        // Get the main operator element (not OutputList, RunTimeInformation, etc.)
        _getOperatorElement: function(relOpEl) {
            for (var i = 0; i < relOpEl.children.length; i++) {
                var child = relOpEl.children[i];
                if (child.nodeType !== 1) continue;
                var name = child.localName || child.tagName;
                if (name === 'OutputList' || name === 'RunTimeInformation' || 
                    name === 'Warnings' || name === 'MemoryFractions' || 
                    name === 'RunTimePartitionSummary' || name === 'MemoryGrant' || 
                    name === 'InternalInfo') {
                    continue;
                }
                return child;
            }
            return null;
        },
        
        // ── Render the query plan tree ─────────────────────────────────────────
        _renderQueryPlan: function(container, xml) {
            var tree = this._parseXmlTree(xml);
            if (!tree || tree.statementNodes.length === 0) return;
            
            // Create root container
            var rootDiv = document.createElement('div');
            rootDiv.className = 'qp-root';
            
            // Render each top-level statement
            for (var t = 0; t < tree.statementNodes.length; t++) {
                this._renderNode(rootDiv, tree.statementNodes[t], null);
            }
            
            container.appendChild(rootDiv);
            
            // Draw SVG connectors after nodes are rendered
            setTimeout(function() {
                this._drawConnectors(rootDiv);
            }.bind(this), 0);
        },
        
        _renderNode: function(container, node, parentContainer) {
            var el = node.element;
            var stmtEl = node.statementElement;
            
            // Get operator name
            var nodeName = el.getAttribute('LogicalOp') || el.getAttribute('PhysicalOp') || '?';
            
            // Create outer container
            var outer = document.createElement('div');
            outer.className = 'qp-node-outer';
            
            // Create node
            var nodeDiv = document.createElement('div');
            nodeDiv.className = 'qp-node';
            nodeDiv.setAttribute('data-node-id', el.getAttribute('NodeId'));
            nodeDiv.setAttribute('data-statement-id', el.getAttribute('StatementId') || '1');
            
            // Icon div
            var iconDiv = document.createElement('div');
            iconDiv.className = 'qp-icon-' + this._getNodeIcon(nodeName);
            
            // Check for warnings
            var warnings = stmtEl.getElementsByTagNameNS(this._ns, 'Warnings');
            if (warnings.length > 0) {
                var warnIcon = document.createElement('div');
                warnIcon.className = 'qp-iconwarn';
                iconDiv.appendChild(warnIcon);
            }
            
            // Check for parallelism
            if (el.getAttribute('Parallel') === '1' || el.getAttribute('Parallel') === 'true') {
                var parIcon = document.createElement('div');
                parIcon.className = 'qp-iconpar';
                iconDiv.appendChild(parIcon);
            }
            
            // Node label
            var labelDiv = document.createElement('div');
            labelDiv.textContent = nodeName;
            nodeDiv.appendChild(iconDiv);
            nodeDiv.appendChild(labelDiv);
            
            // Tooltip
            var tooltip = this._createTooltip(node, stmtEl);
            nodeDiv.appendChild(tooltip);
            
            outer.appendChild(nodeDiv);
            container.appendChild(outer);
            
            // Render children (RelOp elements) - FIX: use global nodeMap
            var children = el.getElementsByTagNameNS(this._ns, 'RelOp');
            if (children.length === 0) {
                children = el.getElementsByTagName('RelOp');
            }
            
            for (var i = 0; i < children.length; i++) {
                var childEl = children[i];
                var childNodeId = childEl.getAttribute('NodeId');
                if (!childNodeId) continue;
                
                // FIX: Use global nodeMap instead of this.nodeMap
                var childNode = nodeMap[childNodeId];
                if (childNode) {
                    this._renderNode(container, childNode, outer);
                }
            }
            
            // Store reference for connector drawing
            nodeDiv._outer = outer;
        },
        
        _createTooltip: function(node, stmtEl) {
            var el = node.element;
            var tooltip = document.createElement('div');
            tooltip.className = 'qp-tt';
            tooltip.style.visibility = 'collapse';
            
            var table = document.createElement('table');
            var tbody = document.createElement('tbody');
            
            // Operator name
            var row1 = this._createTooltipRow('Operator', el.getAttribute('LogicalOp') || el.getAttribute('PhysicalOp') || '?');
            tbody.appendChild(row1);
            
            // Estimated rows
            var estRows = parseFloat(el.getAttribute('EstimateRows')) || 0;
            var actualRows = null;
            var rtInfo = el.getElementsByTagNameNS(this._ns, 'RunTimeInformation');
            if (rtInfo.length > 0) {
                var counters = rtInfo[0].getElementsByTagNameNS(this._ns, 'RunTimeCountersPerThread');
                if (counters.length > 0) {
                    actualRows = parseFloat(counters[0].getAttribute('ActualRows')) || null;
                }
            }
            var row2 = this._createTooltipRow('Estimated Rows', estRows.toLocaleString());
            if (actualRows !== null) {
                row2.appendChild(document.createElement('br'));
                row2.appendChild(this._createTooltipRow(null, 'Actual Rows: ' + actualRows.toLocaleString()));
            }
            tbody.appendChild(row2);
            
            // Estimated row size
            var avgRowSize = parseFloat(el.getAttribute('AvgRowSize')) || 0;
            var dataSize = Math.round(avgRowSize * estRows);
            var row3 = this._createTooltipRow('Estimated Row Size', avgRowSize + ' B');
            tbody.appendChild(row3);
            
            // Estimated data size
            var row4 = this._createTooltipRow('Estimated Data Size', (dataSize / 1024).toFixed(1) + ' KB');
            tbody.appendChild(row4);
            
            // CPU cost
            var cpuCost = parseFloat(el.getAttribute('EstimateCPU')) || 0;
            var row5 = this._createTooltipRow('Estimated CPU Cost', cpuCost.toFixed(3));
            tbody.appendChild(row5);
            
            // I/O cost
            var ioCost = parseFloat(el.getAttribute('EstimateIO')) || 0;
            var row6 = this._createTooltipRow('Estimated I/O Cost', ioCost.toFixed(3));
            tbody.appendChild(row6);
            
            // Subtree cost
            var subtreeCost = parseFloat(el.getAttribute('StatementSubTreeCost') || el.getAttribute('EstimatedTotalSubtreeCost')) || 0;
            var row7 = this._createTooltipRow('Estimated Subtree Cost', subtreeCost.toFixed(3));
            tbody.appendChild(row7);
            
            // Parallelism
            if (el.getAttribute('Parallel') === '1' || el.getAttribute('Parallel') === 'true') {
                var row8 = this._createTooltipRow('Execution Mode', 'Parallel');
                tbody.appendChild(row8);
            }
            
            table.appendChild(tbody);
            tooltip.appendChild(table);
            return tooltip;
        },
        
        _createTooltipRow: function(label, value) {
            var tr = document.createElement('tr');
            if (label) {
                var th = document.createElement('th');
                th.textContent = label;
                tr.appendChild(th);
            }
            var td = document.createElement('td');
            td.textContent = value || '';
            tr.appendChild(td);
            return tr;
        },
        
        _getNodeIcon: function(nodeName) {
            // Map common operator names to icon classes
            var iconMap = {
                'Table Scan': 'TableScan',
                'Index Scan': 'IndexScan',
                'Index Seek': 'IndexSeek',
                'Hash Match': 'HashMatch',
                'Nested Loops': 'NestedLoops',
                'Sort': 'Sort',
                'Stream Aggregate': 'StreamAggregate',
                'Top': 'Top',
                'Result': 'Result',
                'Compute Scalar': 'ComputeScalar',
                'Filter': 'Filter',
                'Concatenate': 'Concatenation',
                'Merge Join': 'MergeJoin',
                'Statement': 'Statement'
            };
            return iconMap[nodeName] || 'Catchall';
        },
        
        // ── Draw SVG connectors between parent and child nodes ──────────────────
        _drawConnectors: function(root) {
            var svg = document.createElementNS(svgNS, 'svg');
            svg.setAttribute('width', '100%');
            svg.setAttribute('height', '100%');
            svg.setAttribute('pointer-events', 'none');
            root.appendChild(svg);
            
            // Get all node containers
            var nodeContainers = root.querySelectorAll('.qp-node-outer');
            var nodes = [];
            
            for (var i = 0; i < nodeContainers.length; i++) {
                var outer = nodeContainers[i];
                var nodeDiv = outer.querySelector('.qp-node');
                if (!nodeDiv) continue;
                
                var nodeId = nodeDiv.getAttribute('data-node-id');
                var stmtId = nodeDiv.getAttribute('data-statement-id');
                
                nodes.push({
                    container: outer,
                    nodeDiv: nodeDiv,
                    nodeId: nodeId,
                    statementId: stmtId,
                    rect: null
                });
            }
            
            // Calculate positions relative to SVG
            var svgRect = svg.getBoundingClientRect();
            
            // Build adjacency list: parent -> [children] using global nodeMap
            var adjList = {};
            for (var i = 0; i < nodes.length; i++) {
                var node = nodes[i];
                var nodeId = node.nodeId;
                if (!adjList[nodeId]) adjList[nodeId] = [];
                
                // Find children by walking DOM tree from this node's container
                var childContainers = node.nodeDiv.querySelectorAll('.qp-node-outer');
                for (var j = 0; j < childContainers.length; j++) {
                    var childNodeDiv = childContainers[j].querySelector('.qp-node');
                    if (!childNodeDiv) continue;
                    
                    var childNodeId = childNodeDiv.getAttribute('data-node-id');
                    // FIX: Use global nodeMap to verify child exists
                    if (childNodeId && nodeMap[childNodeId]) {
                        adjList[nodeId].push(childNodeId);
                    }
                }
            }
            
            // Draw lines for each parent-child relationship
            for (var parentId in adjList) {
                var parent = null;
                for (var i = 0; i < nodes.length; i++) {
                    if (nodes[i].nodeId === parentId) {
                        parent = nodes[i];
                        break;
                    }
                }
                if (!parent) continue;
                
                // Get children
                var childIds = adjList[parentId];
                
                for (var c = 0; c < childIds.length; c++) {
                    var childId = childIds[c];
                    var child = null;
                    for (var i = 0; i < nodes.length; i++) {
                        if (nodes[i].nodeId === childId) {
                            child = nodes[i];
                            break;
                        }
                    }
                    if (!child) continue;
                    
                    // Calculate centers
                    var pRect = parent.rect || this._getContainerRect(parent.container, svgRect);
                    var cRect = child.rect || this._getContainerRect(child.container, svgRect);
                    
                    var centerX1 = pRect.left + pRect.width / 2;
                    var centerY1 = pRect.top + pRect.height / 2;
                    var centerX2 = cRect.left + cRect.width / 2;
                    var centerY2 = cRect.top + cRect.height / 2;
                    
                    // Create polyline
                    var polyline = document.createElementNS(svgNS, 'polyline');
                    polyline.setAttribute('points', this._getPolylinePoints(centerX1, centerY1, centerX2, centerY2));
                    polyline.setAttribute('fill', '#E3E3E3');
                    polyline.setAttribute('stroke', '#505050');
                    polyline.setAttribute('stroke-width', '0.5');
                    polyline.setAttribute('data-statement-id', parent.statementId);
                    if (parent.nodeId) {
                        polyline.setAttribute('data-node-id', parent.nodeId);
                    }
                    
                    svg.appendChild(polyline);
                }
            }
        },
        
        _getContainerRect: function(container, svgRect) {
            var rect = container.getBoundingClientRect();
            return {
                left: rect.left - svgRect.left,
                top: rect.top - svgRect.top,
                width: rect.width,
                height: rect.height
            };
        },
        
        _getPolylinePoints: function(x1, y1, x2, y2) {
            // Create arrow path
            var w = 3; // Line thickness
            var headLen = 8; // Arrow head length
            
            var dx = x2 - x1;
            var dy = y2 - y1;
            
            var toX = x2 + w / 2 + 2;
            var toY = y2;
            var fromX = x2 - w / 2 - 2;
            var fromY = y2;
            
            // Bend point (midpoint with offset)
            var midOffset = Math.abs(dx) / 2;
            var bendX = x1 + midOffset;
            var bendY = y1 + (dy === 0 ? w : (dy > 0 ? -w : w));
            
            var points = [
                toX + w/2 + 2, toY - (w/2 + 2),
                toX + w/2 + 2, toY - w/2,
                bendX + (dy <= 0 ? w/2 : -w/2), toY - w/2,
                bendX + (dy <= 0 ? w/2 : -w/2), fromY - w/2,
                fromX, fromY - w/2,
                fromX, fromY + w/2,
                bendX + (dy >= 0 ? -w/2 : w/2), fromY + w/2,
                bendX + (dy >= 0 ? -w/2 : w/2), toY + w/2,
                toX + w/2 + 2, toY + w/2,
                toX + w/2 + 2, toY + w/2 + 2,
                toX, toY
            ];
            
            return points.join(' ');
        }
    };
})();
