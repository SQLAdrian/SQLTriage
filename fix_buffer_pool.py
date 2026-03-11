#!/usr/bin/env python3
"""
Fix the pmemory.buffer_pool query to add AS [Value] alias
"""

import json
from pathlib import Path

def main():
    config_path = Path('Config') / 'dashboard-config.json'
    
    print(f"Loading {config_path}...")
    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)
    
    fixed = False
    
    # Find and fix the pmemory.buffer_pool panel
    for dashboard in config.get('dashboards', []):
        if dashboard.get('id') == 'pmemory':
            for panel in dashboard.get('panels', []):
                if panel.get('id') == 'pmemory.buffer_pool':
                    query = panel.get('query', {}).get('sqlServer', '')
                    
                    if 'CACHESTORE_BUFPOOL' in query and 'AS [Value]' not in query:
                        # Fix the query
                        old_query = query
                        new_query = query.replace(
                            'AS DECIMAL(18,2))',
                            'AS DECIMAL(18,2)) AS [Value]'
                        )
                        
                        panel['query']['sqlServer'] = new_query
                        
                        print(f"\n[FIXED] pmemory.buffer_pool query")
                        print(f"Old: {old_query[:100]}...")
                        print(f"New: {new_query[:100]}...")
                        fixed = True
                        break
            if fixed:
                break
    
    if fixed:
        # Save the fixed config
        print(f"\nSaving fixed config...")
        with open(config_path, 'w', encoding='utf-8') as f:
            json.dump(config, f, indent=2, ensure_ascii=False)
        print("[SUCCESS] Fixed pmemory.buffer_pool query")
    else:
        print("[INFO] pmemory.buffer_pool query already has AS [Value] or not found")
    
    return 0

if __name__ == '__main__':
    exit(main())
