import React from "react";

export class ErrorBoundary extends React.Component<{ children: React.ReactNode }, { hasError: boolean; error?: Error }> {
  constructor(props: { children: React.ReactNode }) {
    super(props);
    this.state = { hasError: false };
  }
  static getDerivedStateFromError(error: Error) {
    return { hasError: true, error };
  }
  componentDidCatch(error: Error, info: React.ErrorInfo) {
    console.error("ErrorBoundary", error, info);
  }
  render() {
    if (this.state.hasError) {
      return (
        <div className="card" style={{ borderColor: "var(--danger)", margin: 16 }}>
          <h3>Something went wrong</h3>
          <pre style={{ fontSize: 12, whiteSpace: "pre-wrap", marginTop: 8 }}>{this.state.error?.message}</pre>
          <button onClick={() => this.setState({ hasError: false })} style={{ marginTop: 8 }}>Retry</button>
        </div>
      );
    }
    return this.props.children;
  }
}
