#!/usr/bin/env python3
"""
MCP Server for SQLTriage — Exposes monitoring tools to LLMs.

Run: python mcp_server.py --port 3000
Then connect LMStudio/other LLMs to http://localhost:3000
"""

from mcp.server import Server
from mcp.types import TextContent, PromptMessage
import json
import asyncio


# Mock SQLTriage integration (replace with real API calls)
class SQLTriageClient:
    def get_live_sessions(self, server: str) -> str:
        # Simulate calling SQLTriage API
        return json.dumps(
            [
                {"SPID": 1, "Status": "running", "Command": "SELECT", "CPU": 10},
                {"SPID": 2, "Status": "suspended", "Command": "UPDATE", "CPU": 5},
            ]
        )

    def run_health_check(self, server: str) -> str:
        # Simulate Quick Check
        return "Health Check: All systems operational. No critical issues."

    def get_wait_stats(self, server: str) -> str:
        # Simulate wait stats
        return json.dumps(
            {"CXPACKET": 45, "PAGEIOLATCH_SH": 30, "SOS_SCHEDULER_YIELD": 20}
        )


client = SQLTriageClient()


async def get_live_sessions(server_name: str) -> list[TextContent]:
    """Fetch live sessions for a SQL Server instance."""
    result = client.get_live_sessions(server_name)
    return [TextContent(type="text", text=result)]


async def run_health_check(server_name: str) -> list[TextContent]:
    """Run a quick health check on a SQL Server instance."""
    result = client.run_health_check(server_name)
    return [TextContent(type="text", text=result)]


async def get_wait_stats(server_name: str) -> list[TextContent]:
    """Get top wait statistics for a SQL Server instance."""
    result = client.get_wait_stats(server_name)
    return [TextContent(type="text", text=result)]


async def main():
    # Create server
    server = Server("sqltriage")

    @server.call_tool()
    async def call_tool(name: str, arguments: dict) -> list[TextContent]:
        if name == "get_live_sessions":
            return await get_live_sessions(arguments["server_name"])
        elif name == "run_health_check":
            return await run_health_check(arguments["server_name"])
        elif name == "get_wait_stats":
            return await get_wait_stats(arguments["server_name"])
        else:
            return [TextContent(type="text", text=f"Unknown tool: {name}")]

    # Run server
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=3000)
    args = parser.parse_args()

    from mcp.server.stdio import stdio_server

    async with stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream, write_stream, server.create_initialization_options()
        )


if __name__ == "__main__":
    asyncio.run(main())
