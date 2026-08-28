export function ProgressBar({ value }: { value: number | null | undefined }) {
  const pct = value == null ? 0 : Math.max(0, Math.min(1, value)) * 100;
  return (
    <div className="progress" title={`${pct.toFixed(1)}%`}>
      <div style={{ width: `${pct}%` }} />
    </div>
  );
}
