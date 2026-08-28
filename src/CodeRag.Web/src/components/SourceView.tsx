import { useMemo } from "react";

// Minimal syntax highlight placeholder: wraps in <pre> with line numbers.
// Replace with Shiki/Prism when desired: `npm i shiki` and call codeToHtml.
export function SourceView({
  content,
  language,
  startLine = 1,
  highlightLines,
  onLineClick,
}: {
  content: string;
  language?: string | null;
  startLine?: number;
  highlightLines?: [number, number] | null;
  onLineClick?: (line: number) => void;
}) {
  const lines = useMemo(() => content.split("\n"), [content]);
  return (
    <div className="card" style={{ padding: 0, overflow: "auto", maxHeight: 560 }}>
      <div style={{ padding: "8px 12px", borderBottom: "1px solid var(--border)", fontSize: 12, color: "var(--text-muted)" }}>
        {language ?? "text"} — {lines.length} lines (offset {startLine})
      </div>
      <div className="code-view" style={{ padding: "8px 0" }}>
        {lines.map((line, idx) => {
          const ln = startLine + idx;
          const isHl = highlightLines && ln >= highlightLines[0] && ln <= highlightLines[1];
          return (
            <div key={ln} className={`code-line ${isHl ? "highlight" : ""}`} onClick={() => onLineClick?.(ln)}>
              <span className="ln">{ln}</span>
              <span style={{ whiteSpace: "pre-wrap", wordBreak: "break-all" }}>{line || " "}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export function PreviewWithHighlight({ preview, query }: { preview: string; query: string }) {
  if (!query.trim()) return <span>{preview.slice(0, 400)}</span>;
  // naive highlight: split by query words
  const words = query.split(/\s+/).filter(Boolean).slice(0, 4);
  let html = preview.slice(0, 600);
  // escape
  const esc = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  let escaped = esc(html);
  for (const w of words) {
    const re = new RegExp(`(${w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")})`, "gi");
    escaped = escaped.replace(re, '<mark class="hl">$1</mark>');
  }
  return <span dangerouslySetInnerHTML={{ __html: escaped }} />;
}
