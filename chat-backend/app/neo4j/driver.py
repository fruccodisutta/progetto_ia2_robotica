"""
Driver Neo4j asincrono per gestione connessione al database.
"""

from contextlib import asynccontextmanager
from typing import Any, AsyncGenerator

from neo4j import AsyncGraphDatabase, AsyncDriver, AsyncSession
from neo4j.exceptions import ServiceUnavailable, AuthError

from app.config import get_settings
from app.utils.logging import get_logger

logger = get_logger(__name__)


class Neo4jDriver:
    """Gestisce la connessione asincrona a Neo4j."""
    
    _instance: "Neo4jDriver | None" = None
    _driver: AsyncDriver | None = None
    
    def __new__(cls) -> "Neo4jDriver":
        """Singleton pattern."""
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance
    
    async def connect(self) -> None:
        """Stabilisce la connessione al database."""
        if self._driver is not None:
            return
            
        settings = get_settings()
        logger.info(f"Connecting to Neo4j at {settings.neo4j_uri}")
        
        try:
            self._driver = AsyncGraphDatabase.driver(
                settings.neo4j_uri,
                auth=(settings.neo4j_user, settings.neo4j_password),
            )
            # Verifica connessione
            await self._driver.verify_connectivity()
            logger.info("Neo4j connection established")
        except AuthError as e:
            logger.error(f"Neo4j authentication failed: {e}")
            raise
        except ServiceUnavailable as e:
            logger.error(f"Neo4j service unavailable: {e}")
            raise
    
    async def disconnect(self) -> None:
        """Chiude la connessione al database."""
        if self._driver is not None:
            await self._driver.close()
            self._driver = None
            logger.info("Neo4j connection closed")
    
    @asynccontextmanager
    async def session(self) -> AsyncGenerator[AsyncSession, None]:
        """
        Context manager per ottenere una sessione Neo4j.
        
        Yields:
            AsyncSession per eseguire query
            
        Raises:
            RuntimeError: Se il driver non è connesso
        """
        if self._driver is None:
            raise RuntimeError("Neo4j driver not connected. Call connect() first.")
        
        session = self._driver.session()
        try:
            yield session
        finally:
            await session.close()
    
    async def execute_query(
        self,
        query: str,
        parameters: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        """
        Esegue una query Cypher e ritorna i risultati.
        
        Args:
            query: Query Cypher
            parameters: Parametri della query
            
        Returns:
            Lista di record come dizionari
        """
        async with self.session() as session:
            result = await session.run(query, parameters or {})
            records = await result.data()
            return records
    
    async def execute_write(
        self,
        query: str,
        parameters: dict[str, Any] | None = None,
    ) -> list[dict[str, Any]]:
        """
        Esegue una query di scrittura Cypher.
        
        Args:
            query: Query Cypher
            parameters: Parametri della query
            
        Returns:
            Lista di record come dizionari
        """
        async with self.session() as session:
            result = await session.run(query, parameters or {})
            records = await result.data()
            await result.consume()
            return records
    
    async def health_check(self) -> bool:
        """
        Verifica lo stato della connessione.
        
        Returns:
            True se la connessione è attiva
        """
        if self._driver is None:
            return False
        try:
            await self._driver.verify_connectivity()
            return True
        except Exception:
            return False


# Istanza singleton
neo4j_driver = Neo4jDriver()
