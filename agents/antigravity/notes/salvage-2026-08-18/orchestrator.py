import asyncio
import os
import sys
from google.antigravity import Agent, LocalAgentConfig, types

# A system prompt that tells the agent about the current project context
SYSTEM_INSTRUCTION = """
You are the principal architect and orchestrator for the `server-monitor-manager` project.
This repository consists of:
1. A C# Windows Desktop client (in `src/ServerMonitorManager.Desktop`).
2. A bash-based Linux agent/control server component (in `deploy/` and `src/agent/`).
3. Various CI and test scripts (in `tests/` and `.github/workflows/`).

Your role is to autonomously execute tasks requested by the user on this repository.
You are equipped with the Google Antigravity SDK.
Always evaluate if a task is complex enough to be delegated to subagents. 
If so, use your ability to invoke specialized subagents to tackle individual components (e.g., C# refactoring, bash scripting, CI debugging).
Always prioritize security, robust design, and keeping tests green.
"""

async def run_orchestrator(task: str):
    # Enable subagents so this orchestrator can delegate work
    capabilities = types.CapabilitiesConfig(
        enable_subagents=True,
        enable_bash_tools=True,
        enable_file_tools=True
    )
    
    # Configure the agent
    config = LocalAgentConfig(
        capabilities=capabilities,
        system_instructions=SYSTEM_INSTRUCTION,
    )
    
    print(f"[*] Starting Orchestrator Agent for task: '{task}'")
    
    # Run the agent interaction
    async with Agent(config) as agent:
        response = await agent.chat(task)
        print("\n[Orchestrator Output]:\n")
        async for token in response:
            print(token, end="", flush=True)
        print("\n")

async def main():
    if len(sys.argv) > 1:
        task = " ".join(sys.argv[1:])
        await run_orchestrator(task)
    else:
        # Fallback to interactive mode if no task was passed
        print("No task provided. Starting interactive loop...")
        from google.antigravity.utils.interactive import run_interactive_loop
        
        config = LocalAgentConfig(
            capabilities=types.CapabilitiesConfig(
                enable_subagents=True,
                enable_bash_tools=True,
                enable_file_tools=True
            ),
            system_instructions=SYSTEM_INSTRUCTION,
        )
        await run_interactive_loop(config)

if __name__ == "__main__":
    # Ensure GEMINI_API_KEY is loaded from .env if present
    try:
        from dotenv import load_dotenv
        load_dotenv()
    except ImportError:
        pass
        
    if not os.environ.get("GEMINI_API_KEY"):
        print("[!] Warning: GEMINI_API_KEY environment variable is not set.")
        print("[!] You may need to set it or run `pip install python-dotenv` and add it to a .env file.")
        
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n[*] Exiting orchestrator.")
