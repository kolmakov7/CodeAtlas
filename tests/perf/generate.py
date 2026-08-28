#!/usr/bin/env python3
"""
Синтетический генератор репо для perf-тестов (§48 testing.md, §11).
Генерирует 10–50 репо, 100k–1M чанков: CSharpA/B, python, sql, js, cpp, go, vbs.

Usage:
  python tests/perf/generate.py --repos 10 --chunks 100000 -o ./data/perf
  python tests/perf/generate.py --repos 50 --chunks 1000000 --seed 42

Выход: директория с git-репо + stats.json
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import subprocess
import sys
import textwrap
from pathlib import Path

LANGUAGES = ["csharp_a", "csharp_b", "python", "sql", "javascript", "cpp", "go", "vbs"]

CSHARP_A_TEMPLATE = textwrap.dedent("""
    using System;
    using System.Threading.Tasks;
    namespace {ns} {{
        public interface I{service} {{
            Task<{ret}> {method}Async({paramType} request);
            void Deactivate{entity}(string id, string reason);
        }}
        public sealed class {service}Impl : I{service} {{
            private readonly I{dep} _dep;
            public {service}Impl(I{dep} dep) {{ _dep = dep; }}
            public async Task<{ret}> {method}Async({paramType} request) {{
                await _dep.HandleAsync(request);
                return new {ret} {{ Id = Guid.NewGuid().ToString(), Status = "Active" }};
            }}
            public void Deactivate{entity}(string id, string reason) {{
                // client deactivation performed here — FTS phrase "client deactivation"
                Console.WriteLine($"Deactivating {{id}}: {{reason}}");
                _dep.Log(id);
            }}
            public T GenericMethod<T>(T input) where T : class => input;
            public void Foo(int x) {{ }}
            public void Foo(string s) {{ }} // overload
        }}
        public sealed class Repository<T> where T : class {{
            public Task<T> GetAsync(string id) => Task.FromResult<T>(null!);
        }}
        public interface I{dep} {{
            Task HandleAsync({paramType} req);
            void Log(string id);
        }}
        public sealed class {ret} {{ public string Id {{ get; set; }} = ""; public string Status {{ get; set; }} = ""; }}
        public sealed class {paramType} {{ public string ClientId {{ get; set; }} = ""; public string Payload {{ get; set; }} = ""; }}
    }}
""")

CSHARP_B_TEMPLATE = textwrap.dedent("""
    using System;
    using {ref_ns};
    namespace {ns} {{
        public sealed class {cls} {{
            private readonly I{service} _svc;
            public {cls}(I{service} svc) {{ _svc = svc; }}
            public void CallDeactivation(string clientId) {{
                _svc.Deactivate{entity}(clientId, "requested by user");
            }}
            public async Task DoAsync() {{
                await _svc.{method}Async(new {paramType}{{ ClientId="1" }});
            }}
        }}
    }}
""")

PYTHON_TEMPLATE = textwrap.dedent("""
    # app.py — python fixture
    from typing import Generic, TypeVar
    T = TypeVar('T')
    class Repository(Generic[T]):
        async def get_async(self, id: str) -> T: ...
    class ClientService:
        def deactivate_client(self, client_id: str, reason: str):
            # client deactivation — semantic query "where is client deactivation performed?"
            print(f"Deactivating {{client_id}}: {{reason}}")
        def foo(self, x: int): pass
        def foo_str(self, s: str): pass
    def handle_request(req): ClientService().deactivate_client(req['id'], 'test')
""")

SQL_TEMPLATE = textwrap.dedent("""
    -- schema.sql — sql fixture
    CREATE TABLE clients (id TEXT PRIMARY KEY, status TEXT);
    CREATE VIEW active_clients AS SELECT * FROM clients WHERE status='Active';
    CREATE PROCEDURE DeactivateClient(@id TEXT, @reason TEXT)
    AS BEGIN UPDATE clients SET status='Inactive' WHERE id=@id; END;
    -- procedure DeactivateClient handles client deactivation
""")

JS_TEMPLATE = textwrap.dedent("""
    // app.js — javascript fixture
    export class ClientService {
      deactivateClient(id, reason) { console.log(`Deactivating ${id}: ${reason}`); }
      foo(x) {}
    }
    export async function handle(req) { new ClientService().deactivateClient(req.id, 'test'); }
""")

CPP_TEMPLATE = textwrap.dedent("""
    // main.cpp — cpp fixture
    #include <string>
    class IClientService { public: virtual void DeactivateClient(std::string id, std::string reason)=0; };
    class ClientServiceImpl : public IClientService {
     public: void DeactivateClient(std::string id, std::string reason) override {
       // client deactivation
     }
     void Foo(int x) {} void Foo(std::string s) {}
     template<typename T> T Get(T v){return v;}
    };
""")

GO_TEMPLATE = textwrap.dedent("""
    // main.go — go fixture
    package main
    import "fmt"
    type ClientService struct{}
    func (c *ClientService) DeactivateClient(id, reason string) { fmt.Printf("Deactivating %s: %s\\n", id, reason) }
    func (c *ClientService) Foo(x int) {}
    func Handle(req map[string]string) { (&ClientService{}).DeactivateClient(req["id"], "test") }
""")

VBS_TEMPLATE = textwrap.dedent("""
    ' script.vbs — vbs fixture
    Class ClientService
        Sub DeactivateClient(id, reason)
            ' client deactivation
            WScript.Echo "Deactivating " & id & ": " & reason
        End Sub
        Function Foo(x) Foo = x End Function
    End Class
    Dim svc : Set svc = New ClientService
    svc.DeactivateClient "123", "test"
""")


def sha256(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def gen_csharp_a(ns: str, service: str, entity: str, dep: str) -> str:
    return CSHARP_A_TEMPLATE.format(ns=ns, service=service, entity=entity, dep=dep,
                                    ret=f"{entity}Result", method=f"Handle{entity}",
                                    paramType=f"{entity}Request")


def gen_csharp_b(ns: str, cls: str, service: str, entity: str, ref_ns: str) -> str:
    return CSHARP_B_TEMPLATE.format(ns=ns, cls=cls, service=service, entity=entity,
                                    ref_ns=ref_ns, method=f"Handle{entity}", paramType=f"{entity}Request")


def write_file(path: Path, content: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def init_git_repo(repo_path: Path):
    subprocess.run(["git", "init", "-b", "main"], cwd=repo_path, check=True, capture_output=True)
    subprocess.run(["git", "config", "user.email", "perf@test.local"], cwd=repo_path, check=True)
    subprocess.run(["git", "config", "user.name", "perf"], cwd=repo_path, check=True)
    subprocess.run(["git", "add", "."], cwd=repo_path, check=True, capture_output=True)
    subprocess.run(["git", "commit", "-m", "initial"], cwd=repo_path, check=True, capture_output=True)


def generate(output: Path, repos: int, total_chunks: int, seed: int = 42):
    random.seed(seed)
    output.mkdir(parents=True, exist_ok=True)

    # Estimate files per repo: ~50 lines ~ 5 chunks/file average => chunks ≈ files*5
    # For calibration, we vary file sizes.
    chunks_per_file_avg = 5
    total_files = max(repos * 2, total_chunks // chunks_per_file_avg)
    files_per_repo = total_files // repos

    # Distribute chunk types proportionally
    stats = {
        "repos": repos,
        "total_chunks_target": total_chunks,
        "files_per_repo_avg": files_per_repo,
        "languages": {},
        "repos_detail": [],
        "seed": seed,
    }

    total_generated_files = 0
    total_estimated_chunks = 0

    for r in range(repos):
        slug = f"repo-{r:03d}"
        repo_path = output / slug
        repo_path.mkdir(parents=True, exist_ok=True)

        # Variability: some repos larger
        rf = files_per_repo + random.randint(-2, 4)
        rf = max(2, rf)

        # Languages distribution: ensure all present across repos
        # First half repos: emphasis CSharp, second half: python/sql/js/cpp/go/vbs
        entity = f"Client{r}"
        service = f"ClientService{r}"
        ns_a = f"NsA.Repo{r}"
        ns_b = f"NsB.Repo{r}"
        dep = f"DepService{r}"

        # CSharpA
        csharp_a_files = max(1, rf // 4)
        for i in range(csharp_a_files):
            ns = f"{ns_a}.Sub{i}"
            content = gen_csharp_a(ns, f"{service}{i}", entity, dep)
            # inflate to get more chunks: add extra classes
            extra = "\n".join([f"// chunk filler {k} — " + "lorem ipsum " * 20 for k in range(random.randint(2, 6))])
            write_file(repo_path / "CSharpA" / f"Service{i}.cs", content + "\n" + extra)

        # CSharpB (refs A)
        csharp_b_files = max(1, rf // 5)
        for i in range(csharp_b_files):
            content = gen_csharp_b(f"{ns_b}.Sub{i}", f"Caller{i}", f"{service}{i % csharp_a_files}", entity, f"{ns_a}.Sub{i % csharp_a_files}")
            write_file(repo_path / "CSharpB" / f"Caller{i}.cs", content)

        remaining = rf - csharp_a_files - csharp_b_files
        # Split remaining among other languages
        langs = [
            ("python", "python/app.py", PYTHON_TEMPLATE),
            ("sql", "sql/schema.sql", SQL_TEMPLATE),
            ("javascript", "javascript/app.js", JS_TEMPLATE),
            ("cpp", "cpp/main.cpp", CPP_TEMPLATE),
            ("go", "go/main.go", GO_TEMPLATE),
            ("vbs", "vbs/script.vbs", VBS_TEMPLATE),
        ]
        for idx in range(remaining):
            lang, rel, tmpl = random.choice(langs)
            # make each file unique to avoid dedup by ContentHash
            uniq = f"\n// repo {r} file {idx} seed {seed} hash {sha256(f'{r}-{idx}-{random.random()}')[:8]}\n"
            # for python/sql etc add lang-specific suffix
            if lang == "python":
                uniq = f"\n# repo {r} file {idx} {sha256(str(random.random()))[:8]}\n"
            elif lang == "sql":
                uniq = f"\n-- repo {r} file {idx} {sha256(str(random.random()))[:8]}\n"
            elif lang == "vbs":
                uniq = f"\n' repo {r} file {idx} {sha256(str(random.random()))[:8]}\n"
            target = repo_path / rel.replace("app.", f"app_{idx}.").replace("main.", f"main_{idx}.").replace("script.", f"script_{idx}.").replace("schema.", f"schema_{idx}.")
            write_file(target, tmpl + uniq)
            stats["languages"][lang] = stats["languages"].get(lang, 0) + 1

        stats["languages"]["csharp"] = stats["languages"].get("csharp", 0) + csharp_a_files + csharp_b_files

        total_generated_files += rf
        est_chunks = rf * chunks_per_file_avg
        total_estimated_chunks += est_chunks

        # Create .csproj placeholders for CSharpA/B to mimic project references
        csproj_a = textwrap.dedent(f"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
        """)
        write_file(repo_path / "CSharpA" / "CSharpA.csproj", csproj_a)
        write_file(repo_path / "CSharpB" / "CSharpB.csproj", csproj_a.replace("CSharpA", "CSharpB") + f'<ItemGroup><ProjectReference Include="..\\CSharpA\\CSharpA.csproj" /></ItemGroup>')

        # Git init
        try:
            init_git_repo(repo_path)
        except Exception as e:
            print(f"warn: git init failed for {slug}: {e}", file=sys.stderr)

        stats["repos_detail"].append({"slug": slug, "files": rf, "est_chunks": est_chunks})

    stats["total_files"] = total_generated_files
    stats["total_chunks_estimated"] = total_estimated_chunks
    stats["output"] = str(output.resolve())

    (output / "stats.json").write_text(json.dumps(stats, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"Generated {repos} repos, {total_generated_files} files, ~{total_estimated_chunks} chunks (target {total_chunks}) at {output}")
    print(json.dumps(stats, indent=2, ensure_ascii=False))


def main():
    p = argparse.ArgumentParser(description="Perf synthetic repo generator (testing.md §11)")
    p.add_argument("--repos", type=int, default=10, help="number of repos (10–50)")
    p.add_argument("--chunks", type=int, default=100_000, help="target total chunks (100k–1M)")
    p.add_argument("--seed", type=int, default=42, help="random seed")
    p.add_argument("-o", "--output", type=str, default="./data/perf/sample", help="output dir")
    args = p.parse_args()

    if not (1 <= args.repos <= 100):
        p.error("--repos must be 1..100")
    if not (1000 <= args.chunks <= 5_000_000):
        p.error("--chunks must be 1k..5M")

    out = Path(args.output)
    # safety: if output exists and looks like previous perf, clean?
    generate(out, args.repos, args.chunks, args.seed)


if __name__ == "__main__":
    main()
