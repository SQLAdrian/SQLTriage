#!/usr/bin/env python3
"""
Script to fix dashboard configuration to use dashboard-level defaultDatabase
instead of panel-level defaultDatabase settings.
"""

import json
import sys

def fix_dashboard_config(config_path):
    """Remove panel-level defaultDatabase settings to inherit from dashboard level."""
    
    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)
    
    panels_updated = 0
    
    for dashboard in config.get('dashboards', []):
        dashboard_default_db = dashboard.get('defaultDatabase', 'master')
        print(f"Dashboard '{dashboard.get('id')}' default database: {dashboard_default_db}")
        
        for panel in dashboard.get('panels', []):
            panel_default_db = panel.get('defaultDatabase')
            if panel_default_db is not None:
                print(f"  Panel '{panel.get('id')}' removing defaultDatabase: {panel_default_db}")
                del panel['defaultDatabase']
                panels_updated += 1
    
    print(f"\nUpdated {panels_updated} panels to inherit dashboard-level defaultDatabase")
    
    # Write back to file
    with open(config_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=2, ensure_ascii=False)
    
    print(f"Configuration updated: {config_path}")

if __name__ == '__main__':
    config_path = r'c:\GitHub\LiveMonitor\Config\dashboard-config.json'
    fix_dashboard_config(config_path)