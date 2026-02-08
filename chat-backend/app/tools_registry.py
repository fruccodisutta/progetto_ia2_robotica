"""
Tool Registry per gestione tool context-aware.

Registry centrale che aggrega tutti i tool dal package tools/.
Fornisce:
- Matching per pattern
- Costruzione prompt per LLM
- Accesso ai tool per ID
"""

from dataclasses import dataclass, field
from typing import Any
from app.schemas import SessionState, ToolContext, ToolResult, UIOption


# =============================================================================
# BASE TOOL (usata come interfaccia)
# =============================================================================

@dataclass
class Tool:
    """
    Classe base per tutti i tool.
    
    Ogni tool deve implementare execute() per gestire la propria logica.
    """
    id: str
    name: str
    description: str
    patterns: list[str] = field(default_factory=list)
    examples: list[str] = field(default_factory=list)
    category: str = "general"
    
    def is_available(self, state: SessionState) -> bool:
        """Override per condizioni di disponibilità custom."""
        return True
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        """
        Esegue il tool con il contesto dato.
        
        Args:
            ctx: Contesto con session_id, user_id, messaggio, parametri
            
        Returns:
            ToolResult con message, ui_options, commands
        """
        return ToolResult(
            message=f"Tool {self.id} non implementato.",
            ui_options=[],
            commands=[]
        )


# =============================================================================
# REGISTRY
# =============================================================================

class ToolRegistry:
    """Registry di tutti i tool disponibili."""
    
    def __init__(self):
        self._tools: dict[str, Tool] = {}
        self._register_all_tools()
    
    def _register_all_tools(self):
        """Registra tutti i tool dal package tools/."""
        from app.tools import ALL_TOOLS
        
        for tool in ALL_TOOLS:
            self._tools[tool.id] = tool
    
    def get_tool(self, tool_id: str) -> Tool | None:
        """Ottiene un tool per ID."""
        return self._tools.get(tool_id)
    
    def get_all_tools(self) -> list[Tool]:
        """Ottiene tutti i tool."""
        return list(self._tools.values())
    
    def get_available_tools(self, state: SessionState) -> list[Tool]:
        """
        Ottiene i tool disponibili per lo stato corrente.
        
        In modalità PRE_RIDE, solo i tool POI sono disponibili.
        In modalità NORMAL (in-ride), tutti i tool sono disponibili.
        """
        from app.schemas import SessionMode
        
        available = [t for t in self._tools.values() if t.is_available(state)]
        
        # In PRE_RIDE mode, only show POI tools
        if state.mode == SessionMode.PRE_RIDE:
            available = [t for t in available if t.category == "poi"]
        
        return available
    
    def match_pattern(self, message: str, state: SessionState) -> Tool | None:
        """
        Cerca un match con i pattern dei tool disponibili.
        
        Returns:
            Tool matchato o None
        """
        text = message.lower().strip()
        available = self.get_available_tools(state)
        
        for tool in available:
            for pattern in tool.patterns:
                if pattern in text:
                    return tool
        return None
    
    def build_tools_prompt(self, state: SessionState) -> str:
        """
        Costruisce la sezione del prompt con i tool disponibili.
        
        Returns:
            Stringa formattata per il prompt LLM
        """
        available = self.get_available_tools(state)
        
        lines = ["Tool disponibili:"]
        for tool in available:
            examples = ", ".join(f'"{e}"' for e in tool.examples[:2])
            lines.append(f"- {tool.id}: {tool.description} Es: {examples}")
        
        return "\n".join(lines)
    
    async def execute_tool(self, tool_id: str, ctx: ToolContext) -> ToolResult | None:
        """
        Esegue un tool per ID.
        
        Args:
            tool_id: ID del tool
            ctx: Contesto di esecuzione
            
        Returns:
            ToolResult o None se tool non trovato
        """
        tool = self.get_tool(tool_id)
        if tool:
            return await tool.execute(ctx)
        return None


# Istanza singleton
tool_registry = ToolRegistry()
