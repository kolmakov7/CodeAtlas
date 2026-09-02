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
- [x] Research HTML (synthetic + real repo)
- [x] Research CSS (synthetic + real repo)
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
- **HTML** (tree-sitter-html): `document > element > start_tag(tag_name, attribute*) + children + text`;
  `<script>`/`<style>` are NOT `element` — separate `script_element`/`style_element` nodes whose content
  is a `raw_text` child; void/self-closing tags (`<img/>`, `<meta/>`, `<br/>`) have `self_closing_tag`
  (not `start_tag`) as first child with the same `tag_name`/`attribute` children inside.
- **CSS** (tree-sitter-css): `stylesheet > rule_set (selectors + block > declaration*)`;
  `media_statement (feature_query + block > rule_set*)`;
  `keyframes_statement (keyframes_name + keyframe_block_list > keyframe_block (from/to + block))`;
  `@import`/`@charset` are separate at-rule nodes (not rule_set); comments between rules belong to the
  enclosing block's range, not to the neighboring rule's.

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
5. HTML chunk set (FINAL): an `element` is a chunk iff its tag is sectioning/landmark
   (`article, aside, section, nav, main, header, footer, form, fieldset, details, dialog, template, figure, table`)
   OR it has a non-empty `id` attribute (explicit semantic anchor — covers id-heavy div-based markup),
   plus `script_element`/`style_element` with non-blank inline `raw_text` content (external `<script src>` is not).
   `document/html/head/body` = structural wrappers, never chunks (even with id).
   Symbol kind = new `SymbolKind.Section`; name = `tag` or `tag#id`.
   Rationale for the id rule: corpus files mix sectioning tags with heavy div usage (e.g. 43–55 `<div>`s,
   0–2 sectioning tags); id anchors are how real markup marks semantic blocks.
6. CSS chunk set (FINAL): `rule_set` (name = `selectors` text), `media_statement` and
   `keyframes_statement` (name = at-rule head up to `{`, e.g. `@media (min-width: 600px)` /
   `@keyframes spin`) — all with kind = new `SymbolKind.Rule`. Root `stylesheet` is not a chunk.
   Nested `rule_set` inside `media_statement` is excluded from the media chunk source (common rule).
   `keyframe_block` (from/to/50%) is NOT a chunk — stays inside the keyframes chunk.
   Other at-rules (`@import`, `@charset`, `@supports`) are not chunks; files without any rule
   fall back to sliding window. Comments at a level stay in that level's chunk (they are not
   part of the nested rule's range).
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
 - `tests/CodeRag.Tests/Unit/TreeSitterHtmlChunkTests.cs` — 7 tests, all passing:
   sectioning elements are chunks while `html`/`head`/`body`/`div`/`title` are not (+ symbol Language == "html"),
   nested sections (parent excludes child range, child keeps full content),
   any element with id is a chunk (`div#hero`, `span#badge`) / without id is not,
   inline `script`/`style` are chunks + excluded from parent `div#wrap`, external `<script src>` is not,
   `html`/`head`/`body` with id never chunks (sliding-window fallback kicks in),
   no-semantic-elements file falls back to sliding window, multi-byte UTF-8 (Cyrillic + CJK intact through exclusion).
 - `tests/CodeRag.Tests/Unit/TreeSitterCssChunkTests.cs` — 6 tests, all passing:
   rule_set chunks named by selectors (`.a`, `:root`, `div > p`, symbol Language == "css"),
   media_statement excludes all nested rule_set ranges (keeps its at-rule head, ends with `}`),
   keyframes_statement keeps full content while from/to keyframe_blocks are NOT chunks,
   mixed file (@import + 2x .top + media + keyframes → 4 chunks, media copy excluded from media chunk),
   no-rules file (@import/@charset only) falls back to sliding window,
   multi-byte UTF-8 (comment inside rule stays in rule chunk; comment at media level stays in media chunk).

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
  - Symbols/relations/fallback behavior unchanged.
- JavaScript stage (committed `0c2bca7`): full test suite 208 passed / 1 skipped / 0 failed.
- HTML stage (committed `32caf70`):
  - `SupportedLanguages.Html = "html"` added (+ `All` set); `SymbolKind.Section = "Section"` added.
  - `LanguageDetector`: `.html`/`.htm` → `html`.
  - `TreeSitterAnalyzer`: extension map `.html`/`.htm` → `"HTML"`, context map `"html"`,
    `TreeSitterToSupported["html"] = Html`; chunk condition extended to `SymbolKind.Section`.
  - New handlers: `HandleHtmlElement` (sectioning tags or any element with non-empty `id`;
    `html`/`head`/`body` excluded; reads `start_tag` OR `self_closing_tag` for void elements),
    `HandleRawTextElement` for `script_element`/`style_element` (chunk iff non-blank `raw_text`),
    shared `GetIdFromStartTag` helper (handles quoted and unquoted values).
  - Full test suite: 215 passed / 1 skipped / 0 failed.
- CSS stage (this commit):
  - `SupportedLanguages.Css = "css"` (+ `All` set); `SymbolKind.Rule = "Rule"`.
  - `LanguageDetector`: `.css` → `css`.
  - `TreeSitterAnalyzer`: extension map `.css` → `"CSS"`, context map `"css"`,
    `TreeSitterToSupported["css"] = Css`; chunk condition extended to `SymbolKind.Rule`;
    new `HandleCssRule` handler for `rule_set` (name = `selectors` text) and
    `media_statement`/`keyframes_statement` (name = first line up to `{`), names truncated to 200 chars.
  - Full test suite: 221 passed / 1 skipped / 0 failed.

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

## HTML — real repo validation
- Corpus: same `/tmp/htmlcss` (commit `2a9fc59f44aeda092cbac07472aaf0f1ead4e1a6`); 66 `.html` files
  (hand-written examples, some div-centric, one full Chinese-language website).
- Ran `TreeSitterAnalyzer` over all 66 files (harness `/tmp/opencode/tsre`, mode `repojs <dir> .html html`):
  174 symbols, 174 chunks, 28/66 files contain nested chunks, 1 parse error.
- Strict verification: **leafExact=142 leafMismatch=0** (every chunk without nested children equals the
  exact source substring), **parentLeaks=0** (62 readable files).
- 1 parse error: `23-fullwebsite-design-agency/index.html` — `hasError at 0:0`, grammar strictness on a
  real-world page; tree-sitter error recovery still extracted all 13 sections correctly (recorded as
  `AnalyzerError`, result still valid).
- 4/66 files (05-button-hover, 06-libra-ui, 31-webcomponent, 52-day-night-toggle) have no semantic
  elements at all (no sectioning tags, no ids, no inline scripts) → zero symbols → sliding-window
  fallback by design (decision #4).
- id-anchor rule confirmed on real markup: id-heavy div pages produce clean chunks
  (`div#home`, `section#about-us`, `img#image`, `img#mask`, ...).

## CSS — real repo validation
- Corpus: same `/tmp/htmlcss`; 35 `.css` files (demo stylesheets, grid/flex layouts, glassmorphism etc.).
- Ran `TreeSitterAnalyzer` over all 35 files (harness mode `repojs <dir> .css css`):
  702 symbols, 702 chunks, 10/35 files contain nested chunks (media queries with rules),
  2 parse errors, 0 fallback files.
- Strict verification: **leafExact=685 leafMismatch=0** (every chunk without nested children equals the
  exact source substring), **parentLeaks=0** (all 35 files).
- 2 parse errors: `09-japanese-pronunciation/style.css`, `15-responsive-navbar/style.css`
  (`hasError at 0:0` — grammar strictness, e.g. modern CSS features); tree-sitter error recovery
  still extracted all 18 rules from each file (recorded as `AnalyzerError`, result still valid).

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
JS (`0c2bca7`) and HTML (`32caf70`) stages committed. CSS stage implemented + validated; commit this state.

## Next step
1. Commit CSS stage: `git add` TODO.md + TreeSitterAnalyzer.cs + SupportedLanguages.cs + Symbol.cs +
   LanguageDetector.cs + TreeSitterCssChunkTests.cs; commit `tree-sitter: validate CSS chunking`.
2. XML stage: copy built `/tmp/opencode/tsre/binxml/libtree-sitter-xml.so` into
   `src/CodeRag.Analyzers.TreeSitter/runtimes/linux-x64/native/` + csproj Content/CopyToOutputDirectory
   (mirrors TreeSitter.DotNet package layout); add `.xml`/`.xsl`/`.xslt` → `"Xml"`, `SupportedLanguages.Xml`
   (new constant) + `SymbolKind` for XML elements (TBD — likely `SymbolKind.Field`-like or new `Element`),
   XML chunk rule per decision #7 (container elements only; `document`/leaf elements not chunks),
   synthetic tests (incl. XSLT sample) + validate on a real XML/XSLT repo (find small one to clone into /tmp);
   update TODO.md; commit.
