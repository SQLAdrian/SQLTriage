#!/usr/bin/env python3
"""
Fix dashboard-config.json to use dashboard-level defaultDatabase instead of hardcoded 'master' in panels.
Removes panel-level defaultDatabase when it equals 'master' so panels inherit from dashboard.
"""

import json
import sys
from pathlib import Path

def fix_dashboard_config(config_path):
    """Load config, remove hardcoded 'master' from panels, and save."""
    
    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)
    
    panels_fixed = 0
    
    for dashboard in config.get('dashboards', []):
        dashboard_db = dashboard.get('defaultDatabase', 'master')
        print(f"Dashboard '{dashboard['id']}' uses defaultDatabase: {dashboard_db}")
        
        for panel in dashboard.get('panels', []):
            panel_db = panel.get('defaultDatabase', 'master')
            
            # If panel has hardcoded 'master', remove it so it inherits from dashboard
            if panel_db == 'master' and dashboard_db != 'master':
                del panel['defaultDatabase']
                panels_fixed += 1
                print(f"  Panel '{panel['id']}': removed hardcoded 'master', will inherit '{dashboard_db}'")
            elif panel_db == 'master':
                print(f"  Panel '{panel['id']}': keeping 'master' (dashboard also uses 'master')")
            else:
                print(f"  Panel '{panel['id']}': keeping '{panel_db}' (explicit override)")
    
    # Write back with proper formatting
    with open(config_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=2, ensure_ascii=False)
    
    print(f"\nFixed {panels_fixed} panels. Config saved to {config_path}")
    return panels_fixed

if __name__ == '__main__':
    config_file = Path(__file__).parent / 'Config' / 'dashboard-config.json'
    
    if not config_file.exists():
        print(f"Error: Config file not found at {config_file}")
        sys.exit(1)
    
    try:
        fixed = fix_dashboard_config(config_file)
        print(f"Success! Fixed {fixed} panels.")
    except Exception as e:
        print(f"Error: {e}")
        sys.exit(1)
