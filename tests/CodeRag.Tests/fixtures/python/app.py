# Placeholder Python fixture — functions, classes, imports, generics via typing
from typing import Generic, TypeVar

T = TypeVar("T")

class Repository(Generic[T]):
    def get(self, id: int) -> T: ...
    def save(self, entity: T) -> None: ...

class Client:
    is_active: bool

class IClientService:
    def deactivate_client(self, id: int) -> None: ...

class ClientService(IClientService):
    def __init__(self, repo: Repository[Client]): self._repo = repo
    def deactivate_client(self, id: int):
        c = self._repo.get(id)
        c.is_active = False  # reference
