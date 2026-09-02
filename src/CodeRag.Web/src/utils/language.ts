// Shared language colors for charts/legends (used by dashboards).
export const LANGUAGE_COLORS: Record<string, string> = {
  "C#": "#178600",
  "C++": "#f34b7d",
  "Python": "#3572A5",
  "JavaScript": "#f1e05a",
  "TypeScript": "#3178c6",
  "Go": "#00ADD8",
  "Rust": "#dea584",
  "SQL": "#e38c00",
  "VBS": "#818181",
  "Java": "#b07219",
  "PHP": "#8892bf",
  "Ruby": "#cc342d",
  "Shell": "#89e051",
  "HTML": "#e34c26",
  "CSS": "#563d7c",
  "JSON": "#292929",
  "YAML": "#cb171e",
  "Markdown": "#083fa1",
  "unknown": "#6b7280",
};

export function languageColor(lang: string): string {
  const key = Object.keys(LANGUAGE_COLORS).find(
    (k) => k.toLowerCase() === lang?.toLowerCase(),
  );
  return LANGUAGE_COLORS[key ?? "unknown"] ?? "#6366f1";
}

export function languageText(lang: string): string {
  return lang === "JavaScript" || lang === "TypeScript" || lang === "JSON" || lang === "YAML" || lang === "Shell" ? "#000" : "#fff";
}
