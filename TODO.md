# Tree-sitter chunking research — working state

Main source of state for the "investigation and development of Tree-sitter chunking" task.
Must remain current at all times: what is planned, done, in progress, blocked; decisions, hypotheses,
experiment results, links/commits of investigated repositories, tests added, code changes, open questions.

## Rules
- Statuses: `[ ] TODO`, `[~] IN PROGRESS`, `[x] DONE`, `[!] BLOCKED`
- After every substantial step update this file.
- No Docker; run via `dotnet run` / `dotnet test`. Temp files only in `/tmp`. No `git push`.
- One git commit per completed language stage.
- Update this file before any compaction (current task + next concrete step + results + decisions).

## Overall
- [x] Create TODO.md (this file)
- [x] Study current C# Tree-sitter chunking implementation (analyzer, chunk factory, source slicer, tests)
- [x] Determine exact nested-semantic-chunk behavior (which nodes are chunks, how children are excluded from parents)
- [x] Add synthetic tests (one child, several children, three levels, siblings, non-chunk nested node)
- [x] Research JavaScript (synthetic + real repo)
- [ ] Research HTML
- [ ] Research CSS
- [x] Research XML / XSLT (parser built + verified; chunk rule decided; implementation pending with XML stage)
- [ ] Research SQL / T-SQL (SQL-ProcBench)
- [ ] Write final research documentation (.md)
- [ ] Final cross-check

## Current C# implementation — findings

### TreeSitterAnalyzer (`src/CodeRag.Analyzers.TreeSitter/TreeSitterAnalyzer.cs`)
- Extension map: .py/.js/.mjs/.cjs/.jsx/.ts/.tsx/.go/.c/.cpp/.h etc. JS and TS both map to `SupportedLanguages.JavaScript`.
- NodeHandlers (node type → symbol+chunk): `class_definition`, `class_declaration`, `class_specifier`,
  `type_declaration`, `function_definition`, `function_declaration`, `method_declaration`,
  `method_definition`, `arrow_function`, `call`, `call_expression`, imports, `namespace_definition`.
- **Chunk source = `node.Text` (full node text) → nested chunk exclusion is NOT implemented**:
  parent chunk contains the full text of child chunks (foo fully contains bar).
- **No `function_expression` handler** → `var foo = function () {}` produces no chunk at all (gap).
- No HTML/CSS handlers and no .html/.css extensions → `CanAnalyze` false → FileIndexer fallback.
- Fallback: if zero chunks produced → sliding window via `DefaultSourceSlicer`.

### Chunking primitives (`src/CodeRag.Analyzers.Abstractions/`)
- `Chunk` record: `Id`, `SymbolId`, `Language`, `StartLine`, `EndLine`, `Source` (single string), `ContentHash` (SHA256).
- `DefaultChunkFactory`: builds chunk from one source string + lines.
- `DefaultSourceSlicer`: `ExtractLines`, `SlidingWindow` (800 chars / 80 tokens, joins tokens with a single
  space — mangles markup), `SplitByCharBudget`. `ChunkingHelper` is `[Obsolete]`.
- `FileIndexer` (CodeRag.Indexer): picks first analyzer with `CanAnalyze(filePath)`, else raw sliding-window fallback.
- `LanguageDetector`: extension→language map; **no HTML/CSS entries**.

### Reference models
- `CSharpChunkBuilder` (C# analyzer): type skeleton chunk + per-method chunks + whole-node for enums;
  nested types get context headers. Root namespace/class files are not themselves chunks.
- `SqlAnalyzer`: ScriptDom `TSql170Parser` (NOT tree-sitter), requires `GO` batch separator;
  each procedure/function/view = one symbol + one chunk.

### TreeSitter.DotNet 1.3.0 (mariusgreuel/tree-sitter-dotnet-bindings, branch `main`)
- Bundles 24 prebuilt languages: agda bash c cpp c-sharp css embedded-template go haskell html java
  javascript jsdoc julia ocaml ocaml-type php python ql razor ruby rust scala tsx typescript verilog.
  **No XML, no SQL.**
- API used: `Node.Range` (StartIndex/EndIndex), `Node.Text`, `StartPosition/EndPosition`, `Tree.GetText`,
  `new Language(id)`, `Parser.Parse(string)`.
- **Language loading mechanics** (src/Language.cs + src/NativeLibrary.cs, verified from source):
  `new Language("xml")` → dlopen `libtree-sitter-xml.so` tried at `AppContext.BaseDirectory`,
  then `AppContext.BaseDirectory/runtimes/linux-x64/native/`, then default dlopen paths;
  entry point symbol = `tree_sitter_xml`. ⇒ Any grammar can be added by shipping the `.so`
  under `runtimes/<rid>/native/` — no API changes needed.

## AST structure findings (verified with /tmp/opencode/tsre harness)
- **JavaScript**: `variable_declaration > variable_declarator > function_expression`;
  nested `function_declaration` inside `statement_block`; `async function` = `function_declaration`;
  `class_declaration > method_definition`; `arrow_function`; IIFE = `call_expression > parenthesized_expression > function_expression`.
- **HTML** (tree-sitter-html): `document > element (every tag) > start_tag/tag_name/attribute` + `text` nodes.
- **CSS** (tree-sitter-css): `stylesheet > rule_set (selectors + block)`, `media_statement`,
  `keyframes_statement > keyframe_block (from/to)`.

## XML / XSLT parser availability
- No standalone NuGet package for tree-sitter-xml exists (searched azuresearch).
- Standard grammar: `tree-sitter-grammars/tree-sitter-xml` (plain C, no external scanner) →
  build `libtree-sitter-xml.so` with gcc, ship under `runtimes/linux-x64/native/`;
  `new Language("Xml")` then works exactly like bundled languages (offline, same API). **Chosen approach.**
- Alternative: XbergIo `TreeSitterLanguagePack` 1.10.9 (NuGet, net10.0, Kreuzberg Team) — 306+ languages incl.
  Xml, Tsql, generic SQL; single core `libts_pack_core_ffi.so`; **downloads grammars at first use** (network),
  separate registry API (`LanguageRegistryGetLanguage`). Heavier dependency + runtime network ⇒ not preferred.
- **No dedicated tree-sitter XSLT grammar exists** — XSLT files are XML, parsed by the XML grammar
  (namespace elements like `xsl:template` are just attributes/elements). Document as limitation/decision.

## Decisions
1. **Nested chunk exclusion**: two-pass walk in TreeSitterAnalyzer — first collect all chunk-candidate
   nodes (symbols), then build each chunk's source by subtracting ranges of its descendant chunks
   and concatenating the remaining segments in order. Child chunks keep full content.
2. **Range model**: parent `[0..100]`, child `[30..70]` → parent source = `src[0..30] + src[70..100]`;
   multiple children → concatenate all remaining segments. Implemented via `Tree.GetText(start, end)`
   slices. Multi-byte UTF-8 verified (see Tests added / JS results).
3. `function_expression` becomes a JS chunk; symbol name from enclosing `variable_declarator` identifier.
4. Root containers (`program`, `document`, `stylesheet`, `namespace_definition`) are NOT chunks
   (consistent with C#/JS/SQL behavior). Zero chunks → existing sliding-window fallback.
5. HTML chunk set (tentative, finalize in HTML stage): sectioning elements
   (`article, aside, section, nav, main, header, footer, form, fieldset, details, dialog, template, figure, table`)
   + `script`/`style` with content. `document/html/head/body` = structural wrappers, not chunks.
6. CSS chunk set (tentative): `rule_set`, `media_statement`, `keyframes_statement`;
   `media_statement`/`keyframes_statement` exclude nested chunk ranges.
7. XML: ship self-built `libtree-sitter-xml.so`; XML chunk set to be finalized after AST inspection
   (root `document` not a chunk; candidates: named `element` nodes, depth/size heuristic TBD).
8. SQL stage stays on existing `SqlAnalyzer` (ScriptDom) — validate behavior + SQL-ProcBench;
   tree-sitter T-SQL noted as alternative only.

## Problems found
- Nested chunks not excluded: parent chunk currently contains full child source (duplication in RAG context).
- `function_expression` unhandled → anonymous/assigned functions missing from index.
- `.html`/`.css` files unanalyzed; fallback sliding window joins tokens with single spaces (markup destroyed).
- `DefaultSourceSlicer.SlidingWindow` degrades any markup language — only acceptable as last-resort fallback.

## External repositories / revisions
- `zxuqian/html-css-examples` — JS/HTML/CSS sample corpus. Cloned to `/tmp/htmlcss` at commit
  `2a9fc59f44aeda092cbac07472aaf0f1ead4e1a6` ("Add form example").
- `microsoft/SQL-ProcBench` — main T-SQL procedure corpus (pin commit at clone; clone to /tmp).
- `tree-sitter-grammars/tree-sitter-xml` — XML grammar source (pin commit at build).
- `mariusgreuel/tree-sitter-dotnet-bindings` — binding source, branch `main` (Language/NativeLibrary mechanics).
- `xberg-io/tree-sitter-language-pack` (a.k.a. Kreuzberg TreeSitterLanguagePack) — alternative 306+ lang pack.

## Tests added
- `tests/CodeRag.Tests/Unit/TreeSitterNestedChunkTests.cs` — 8 tests, all passing:
  one child (parent excludes child range, declarator prefix `var bar = ` stays in parent),
  several children (class excludes all methods, keeps non-chunk members), three levels
  (each level excludes only its own children), siblings, non-chunk nested node (if/for NOT cut),
  `function_expression` named from enclosing declarator, multi-byte UTF-8 (Cyrillic intact),
  cross-language sanity (Python class excludes methods).

## Code changes
- `src/CodeRag.Analyzers.TreeSitter/TreeSitterAnalyzer.cs`:
  - Two-pass chunk building: `Walk` now collects `ChunkCandidate(Node, Symbol, StartLine, EndLine)`
    instead of creating chunks inline; after the walk each candidate becomes a chunk via `TryBuildChunk`.
  - `BuildExcludedSource`: descendants = candidates whose range is contained in the candidate's range
    (StartIndex >= P.StartIndex && EndIndex <= P.EndIndex), sorted by StartIndex; source = concatenation
    of `tree.GetText` segments between descendant ranges. Degenerate guard: if the node consists entirely
    of child chunks → fall back to full `node.Text`.
  - New `function_expression` handler: Kind=Function, name from enclosing `variable_declarator`
    identifier, fallback "anonymous".
  - Symbols/relations/fallback behavior unchanged. Full test suite: 208 passed / 1 skipped / 0 failed.

## JavaScript — real repo validation
- Corpus: `zxuqian/html-css-examples` commit `2a9fc59f44aeda092cbac07472aaf0f1ead4e1a6`
  cloned to `/tmp/htmlcss`; 30 `.js` files (hand-written examples + vendored minified libs incl.
  react-dom, face-api, lottie, html2canvas).
- Ran `TreeSitterAnalyzer` over all 30 files (harness `/tmp/opencode/tsre`, mode `repojs`):
  7467 symbols, 7467 chunks, 24/30 files contain nested chunks, 0 analyzer errors.
- Strict verification (full line+column containment to handle minified same-line nesting):
  **leafExact=5810 leafMismatch=0** (every chunk without nested children equals the exact source
  substring — including multi-byte files with Chinese/Cyrillic comments),
  **parentLeaks=0** (25 readable files, string check: no parent chunk contains any child chunk's
  full source).
- Minified single-line files produce huge nesting counts (thousands of nested pairs) but remain
  correct; they are realistic only as vendor code.

## XML / XSLT — verified results
- Built `libtree-sitter-xml.so` from `tree-sitter-grammars/tree-sitter-xml` commit
  `5000ae8f22d11fbe93939b05c1e37cf21117162d` (v0.7.0, ABI 14):
  `gcc -shared -fPIC -O2 -std=c11 -I xml/src xml/src/parser.c xml/src/scanner.c` (70 KB, exports `tree_sitter_xml`).
- Placed under `<output>/runtimes/linux-x64/native/` → `new Language("Xml")` loads it with zero API changes
  (loader mechanics verified: AppContext.BaseDirectory then runtimes/<rid>/native/). Core runtime is
  tree-sitter 0.26 (bundles ABIs 14+15 grammars) → compatible.
- XML AST (verified, hasError=False): `document > [prolog>XMLDecl] + element*;`
  `element > STag(Name, Attribute*) + content(...) + ETag`; self-closing = `EmptyElemTag(Name, Attribute*)`;
  text = `CharData`; also `Comment`, `PI`, `CData`.
- **XSLT verified**: parses as plain XML, `hasError=False`; all `xsl:*` namespaced tags are regular
  `element`/`EmptyElemTag` nodes with attributes. No XSLT-specific grammar needed.
- **XML chunk rule (final)**: an `element` is a chunk iff it has ≥1 child element
  (`element` or `EmptyElemTag` descendant in `content`) — i.e. "container elements".
  Name from `STag > Name` (include attributes in display, e.g. `book[@id=bk101]` — keep simple: tag name).
  Leaf/text-only elements are NOT chunks (they stay inside the parent container's source).
  `document`, `prolog`, `Comment`, `PI` are NOT chunks. For XSLT this yields
  stylesheet → templates → inner blocks, which is the desired semantic split.

## Open questions
- Multi-byte UTF-8: RESOLVED — `Node.StartIndex/EndIndex` are C# string (char) indices
  (`ByteToIndex` of byte offsets); `Tree.GetText(start,end)` substring is exact for multi-byte sources
  (verified by synthetic test + exact-substring check over files with CJK/Cyrillic text).
- HTML: should `script`/`style` content be chunked as-is or routed to JS/CSS sub-parsing (document as limitation if as-is).
- Where to ship `libtree-sitter-xml.so` in the repo: content under `src/CodeRag.Analyzers.TreeSitter/runtimes/linux-x64/native/`
  copied to output via csproj (mirrors TreeSitter.DotNet package layout). Decide at XML implementation step.

## Current task
JS stage complete (implementation + tests + real-repo validation). Next: commit, then HTML stage.

## Next step
1. `git add` TODO.md + TreeSitterAnalyzer.cs + TreeSitterNestedChunkTests.cs; commit `tree-sitter: validate JavaScript chunking`.
2. HTML stage: add `.html`/`.htm` mapping (TreeSitter language `html`, SupportedLanguages TBD — likely new
   "html" or route via existing map), HTML chunk handlers per decision #5 (sectioning elements + script/style with
   content; `document/html/head/body` not chunks), reuse the two-pass exclusion (sectioning elements nest).
   Check what `SupportedLanguages` values exist (CodeRag.Analyzers.Abstractions) before adding.
3. Synthetic HTML tests + run over /tmp/htmlcss 66 HTML files; update TODO.md; commit.
