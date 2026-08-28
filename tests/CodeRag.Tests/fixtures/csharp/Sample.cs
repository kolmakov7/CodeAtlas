// Placeholder fixture csharp — покрывает: interfaces + implementations (1:M), inheritance, overloads, generics, namespaces, method calls, references, project refs
// См. docs/testing.md §3 Analyzer fixtures

using System.Threading.Tasks;

namespace NsA
{
    public interface IClientService
    {
        Task DeactivateClient(int id);
        Task ActivateClient(int id);
    }

    public class BaseService
    {
        public virtual void Foo(int x) { }
        public virtual void Foo(string s) { } // overload
    }

    public class Repository<T> where T : class
    {
        public Task<T> GetAsync(int id) => Task.FromResult<T>(null!);
    }
}

namespace NsB
{
    using NsA;

    public class ClientServiceA : IClientService, ICloneable
    {
        private readonly Repository<Client> _repo;
        public ClientServiceA(Repository<Client> repo) => _repo = repo;

        public async Task DeactivateClient(int id)
        {
            var c = await _repo.GetAsync(id);
            c.IsActive = false;
        }

        public Task ActivateClient(int id) => Task.CompletedTask;
        public object Clone() => MemberwiseClone();
    }

    public class ClientServiceB : IClientService
    {
        public Task DeactivateClient(int id) => Task.CompletedTask;
        public Task ActivateClient(int id) => Task.CompletedTask;
    }

    public class DerivedService : BaseService
    {
        public override void Foo(int x) => base.Foo(x);
        public void CallLocal() => Foo(42);
        public void CallCross() => new ClientServiceA(null!).DeactivateClient(1);
    }

    public class Client { public bool IsActive { get; set; } }

    public static class ClientExtensions
    {
        public static void DeactivateExtension(this Client c) => c.IsActive = false;
    }
}
