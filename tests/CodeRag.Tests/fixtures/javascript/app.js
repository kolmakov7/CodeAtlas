// Placeholder JS — classes, functions, calls
class Repository { get(id) { return { isActive: true }; } }
class ClientService {
  constructor(repo) { this.repo = repo; }
  deactivateClient(id) { const c = this.repo.get(id); c.isActive = false; }
}
