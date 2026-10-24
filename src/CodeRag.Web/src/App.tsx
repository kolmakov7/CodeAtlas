import { Routes, Route } from "react-router-dom";
import Layout from "@/components/Layout";
import Dashboard from "@/pages/Dashboard";
import RepositoryList from "@/pages/RepositoryList";
import RepositoryDashboard from "@/pages/RepositoryDashboard";
import SearchPage from "@/pages/Search";
import SymbolExplorer from "@/pages/SymbolExplorer";
import DocumentationPage from "@/pages/Documentation";
import JobsPage from "@/pages/Jobs";
import TasksPage from "@/pages/Tasks";
import KnowledgePage from "@/pages/Knowledge";
import ReviewPage from "@/pages/Review";
import SettingsPage from "@/pages/Settings";
import DiagnosticsPage from "@/pages/Diagnostics";

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<Dashboard />} />
        <Route path="repositories" element={<RepositoryList />} />
        <Route path="repositories/:id" element={<RepositoryDashboard />} />
        <Route path="search" element={<SearchPage />} />
        <Route path="documentation" element={<DocumentationPage />} />
        <Route path="symbols/:symbolId" element={<SymbolExplorer />} />
        <Route path="jobs" element={<JobsPage />} />
        <Route path="tasks" element={<TasksPage />} />
        <Route path="knowledge" element={<KnowledgePage />} />
        <Route path="review" element={<ReviewPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="diagnostics" element={<DiagnosticsPage />} />
        <Route path="*" element={<div className="card">404 — Page not found</div>} />
      </Route>
    </Routes>
  );
}
