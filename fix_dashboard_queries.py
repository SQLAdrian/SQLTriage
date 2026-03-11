#!/usr/bin/env python3
"""
Fix SQL query issues in dashboard-config.json
- Add AS [Value] alias to pmemory.buffer_pool query
- Add semicolons before CTE queries
- Fix other SQL syntax issues
"""

import json
import re
from pathlib import Path

def fix_buffer_pool_query(query):
    """Add AS [Value] alias to buffer pool query"""
    if 'CACHESTORE_BUFPOOL' in query and 'AS [Value]' not in query:
        # Find the SELECT statement and add AS [Value] before FROM
        query = re.sub(
            r'(SELECT\s+CAST\([^)]+\)\s+AS\s+DECIMAL\([^)]+\))\s+(FROM)',
            r'\1 AS [Value] \2',
            query,
            flags=re.IGNORECASE
        )
    return query

def add_semicolon_before_cte(query):
    """Add semicolon before WITH clause if not present"""
    if re.search(r'\bWITH\b', query, re.IGNORECASE):
        # Check if there's already a semicolon or SET statement
        if not re.search(r'(;|SET\s+TRANSACTION)', query, re.IGNORECASE):
            # Add SET TRANSACTION at the beginning
            query = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; ' + query
    return query

def fix_panel_query(panel):
    """Fix SQL queries in a panel"""
    if 'query' not in panel:
        return panel
    
    query_obj = panel['query']
    
    # Fix SQL Server queries
    if 'sqlServer' in query_obj and query_obj['sqlServer']:
        sql = query_obj['sqlServer']
        
        # Fix buffer pool query
        if panel['id'] == 'pmemory.buffer_pool':
            sql = fix_buffer_pool_query(sql)
        
        # Fix CTE queries
        if panel['id'] in ['waits.details', 'qs.topcpu', 'qs.topduration', 
                           'qs.planvariation', 'qs.regressed']:
            sql = add_semicolon_before_cte(sql)
        
        # Fix queries with TOP (@TopRows) - ensure parameter is used correctly
        if '@TopRows' in sql and 'TOP (@TopRows)' not in sql:
            sql = re.sub(r'TOP\s+\(@TopRows\)', 'TOP (@TopRows)', sql, flags=re.IGNORECASE)
        
        query_obj['sqlServer'] = sql
    
    return panel

def fix_dashboard_config(config_path):
    """Fix all SQL queries in dashboard-config.json"""
    print(f"Loading {config_path}...")
    
    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)
    
    fixed_count = 0
    
    # Process each dashboard
    for dashboard in config.get('dashboards', []):
        dashboard_id = dashboard.get('id', 'unknown')
        print(f"\nProcessing dashboard: {dashboard_id}")
        
        # Process each panel
        for panel in dashboard.get('panels', []):
            panel_id = panel.get('id', 'unknown')
            
            # Store original query for comparison
            original_query = json.dumps(panel.get('query', {}))
            
            # Fix the panel
            panel = fix_panel_query(panel)
            
            # Check if anything changed
            new_query = json.dumps(panel.get('query', {}))
            if original_query != new_query:
                print(f"  [FIXED] panel: {panel_id}")
                fixed_count += 1
    
    # Create backup
    backup_path = config_path.with_suffix('.json.backup')
    print(f"\nCreating backup: {backup_path}")
    with open(backup_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=2, ensure_ascii=False)
    
    # Save fixed config
    print(f"Saving fixed config: {config_path}")
    with open(config_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, indent=2, ensure_ascii=False)
    
    print(f"\n[SUCCESS] Fixed {fixed_count} panels")
    print(f"[SUCCESS] Backup saved to: {backup_path}")
    
    return fixed_count

def main():
    """Main entry point"""
    config_path = Path(__file__).parent / 'Config' / 'dashboard-config.json'
    
    if not config_path.exists():
        print(f"Error: Config file not found: {config_path}")
        return 1
    
    try:
        fixed_count = fix_dashboard_config(config_path)
        print(f"\n[SUCCESS] Successfully fixed {fixed_count} SQL queries")
        return 0
    except Exception as e:
        print(f"\n[ERROR] Error: {e}")
        import traceback
        traceback.print_exc()
        return 1

if __name__ == '__main__':
    exit(main())
