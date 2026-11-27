import { type FormEvent, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { authAction, onSessionExpired, useResource, verifyAnalysis } from "./api";
import "./style.css";

type Verdict = "PASS" | "FAIL" | "INDETERMINATE";
type Analysis = { id: string; status: Verdict; created_at: string };
type Project = { id: string; name: string; role: string };
type Detail = Analysis & {
  summary: { errors: number; warnings: number; info: number; unknown: number };
  provenance: Record<string, string | null>;
  verification: { status: "UNVERIFIED" } | {
    status: "VERIFIED"; provider: string; image_digest: string; verified_at: string;
  };
  diagnostics: { code: string; message: string }[];
};
type Source = { line: number; column: number };
type Finding = {
  rule_id: string; severity: string; message: string; fingerprint: string;
  method?: string; path?: string; base_source?: Source | null; candidate_source?: Source | null;
};

const provenanceLabels: Record<string, string> = {
  guardian_version: "Guardian version", diff_engine: "Diff engine", diff_engine_version: "Engine version",
  baseline_commit_sha: "Baseline commit", candidate_commit_sha: "Candidate commit", pipeline_id: "Pipeline",
  ci_provider: "CI provider", ci_job_id: "CI job",
  baseline_sha256: "Baseline hash", candidate_sha256: "Candidate hash", policy_sha256: "Policy hash",
};

function Problem({ message, retry }: { message: string; retry: () => void }) {
  return <div className="notice"><p role="alert">{message}</p><button onClick={retry}>Retry</button></div>;
}

function SignIn({ onSuccess }: { onSuccess: () => void }) {
  const [error, setError] = useState("");
  const [pending, setPending] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    setPending(true);
    setError("");
    try {
      await authAction("login", { email: String(fields.get("email")), password: String(fields.get("password")) });
      onSuccess();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Sign in failed.");
    } finally {
      setPending(false);
    }
  }
  return <main className="login">
    <form onSubmit={submit} className="card login-card">
      <h1>Sign in</h1>
      <p>Access your projects and review API contract reports.</p>
      <label>Email<input name="email" type="email" autoComplete="username" maxLength={254} required /></label>
      <label>Password<input name="password" type="password" autoComplete="current-password" maxLength={256} required /></label>
      {error && <p role="alert" className="error">{error}</p>}
      <button className="primary" disabled={pending}>{pending ? "Signing in…" : "Sign in"}</button>
    </form>
  </main>;
}

function App() {
  const [signedOut, setSignedOut] = useState(false);
  const [generation, setGeneration] = useState(0);
  const [error, setError] = useState("");
  useEffect(() => onSessionExpired(() => setSignedOut(true)), []);
  const { resource: projects, retry } = useResource<Project[]>("/projects", generation);
  async function signOut() {
    try {
      await authAction("logout");
      setSignedOut(true);
      setError("");
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Sign out failed.");
    }
  }
  return <>
    <header className="masthead">
      <span className="mark" aria-hidden="true">G</span>
      <span>API Contract Guardian</span>
      {!signedOut && projects.kind === "ready" &&
        <button className="sign-out" onClick={() => void signOut()}>Sign out</button>}
    </header>
    {error && !signedOut && <p role="alert">{error}</p>}
    {signedOut ? <SignIn onSuccess={() => { setSignedOut(false); setGeneration(value => value + 1); }} />
      : projects.kind === "loading" ? <main><p role="status">Loading projects…</p></main>
      : projects.kind === "error" ? <main><Problem message={projects.message} retry={retry} /></main>
      : projects.value.length ? <Workspace key={generation} projects={projects.value} />
      : <main><h1>Projects</h1><p>No projects are assigned to this account.</p></main>}
  </>;
}

function Workspace({ projects }: { projects: Project[] }) {
  const [projectId, setProject] = useState(projects[0].id);
  const [analysisId, setAnalysis] = useState<string>();
  const project = projects.find(item => item.id === projectId) ?? projects[0];
  return <main>
    <div className="toolbar">
      <label>Project<select value={project.id} onChange={event => {
        setProject(event.target.value);
        setAnalysis(undefined);
      }}>{projects.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
      <span className="project-role">{project.role}</span>
    </div>
    {analysisId ? <AnalysisView key={analysisId} id={analysisId} canVerify={project.role === "Maintainer"}
      back={() => setAnalysis(undefined)} />
      : <History key={project.id} project={project} open={setAnalysis} />}
  </main>;
}

function History({ project, open }: { project: Project; open: (id: string) => void }) {
  const [cursor, setCursor] = useState<string>();
  const params = cursor ? "?cursor=" + encodeURIComponent(cursor) : "";
  const { resource, retry } = useResource<{ items: Analysis[]; next_cursor: string | null }>(
    "/projects/" + project.id + "/analyses" + params);
  return <section>
    <h1>Analysis history</h1>
    {resource.kind === "loading" && <p role="status">Loading analyses…</p>}
    {resource.kind === "error" && <Problem message={resource.message} retry={retry} />}
    {resource.kind === "ready" && <>
      {resource.value.items.length === 0 ? <p className="card empty">No analyses have been published.</p> :
        <div className="card table-wrap"><table>
          <thead><tr><th scope="col">Verdict</th><th scope="col">Received</th><th scope="col">Report</th></tr></thead>
          <tbody>{resource.value.items.map(item => <tr key={item.id}>
            <td><VerdictLabel status={item.status} /></td>
            <td><time dateTime={item.created_at}>{new Date(item.created_at).toLocaleString()}</time></td>
            <td><button className="link mono" onClick={() => open(item.id)} aria-label={`Open ${item.status} report ${item.id}`}>
              {item.id.slice(0, 8)}
            </button></td>
          </tr>)}</tbody>
        </table></div>}
      <div className="actions">
        {cursor && <button onClick={() => setCursor(undefined)}>Back to latest</button>}
        {resource.value.next_cursor && <button onClick={() => setCursor(resource.value.next_cursor ?? undefined)}>Next page</button>}
      </div>
    </>}
  </section>;
}

function VerdictLabel({ status }: { status: Verdict }) {
  return <span className={"verdict " + status.toLowerCase()}>{status}</span>;
}

function AnalysisView({ id, back, canVerify }: { id: string; back: () => void; canVerify: boolean }) {
  const { resource, retry } = useResource<Detail>("/analyses/" + id);
  const [verificationError, setVerificationError] = useState("");
  const [verifying, setVerifying] = useState(false);
  async function verify() {
    setVerifying(true);
    setVerificationError("");
    try {
      await verifyAnalysis(id);
      retry();
    } catch (error) {
      setVerificationError(error instanceof Error ? error.message : "Verification failed.");
    } finally {
      setVerifying(false);
    }
  }
  return <section>
    <button className="link" onClick={back}>← Analysis history</button>
    <div className="detail-title"><h1>Contract analysis</h1>
      {resource.kind === "ready" && <VerdictLabel status={resource.value.status} />}</div>
    {resource.kind === "loading" && <p role="status">Loading report…</p>}
    {resource.kind === "error" && <Problem message={resource.message} retry={retry} />}
    {resource.kind === "ready" && <>
      <dl className="summary" aria-label="Finding totals">
        {Object.entries(resource.value.summary).map(([label, value]) =>
          <div className="card" key={label}><dt>{label}</dt><dd>{value}</dd></div>)}
      </dl>
      <div className="detail-grid">
        <FindingList analysis={id} />
        <section className="card provenance">
          <h2>Provenance</h2>
          <p role="status">{resource.value.verification.status === "VERIFIED" ? "CI evidence verified" : "CI evidence unverified"}</p>
          {resource.value.verification.status === "VERIFIED"
            ? <p className="mono">{resource.value.verification.image_digest}</p>
            : canVerify && resource.value.provenance.ci_provider &&
              <button disabled={verifying} onClick={() => void verify()}>{verifying ? "Verifying…" : "Verify CI evidence"}</button>}
          {verificationError && <p role="alert">{verificationError}</p>}
          <dl>{Object.entries(provenanceLabels).map(([field, label]) =>
            <div key={field}><dt>{label}</dt><dd className="mono">{resource.value.provenance[field] ?? "Unavailable"}</dd></div>)}</dl>
        </section>
      </div>
      {resource.value.diagnostics.length > 0 && <section className="card diagnostics">
        <h2>Diagnostics</h2>
        <ul>{resource.value.diagnostics.map(({ code, message }, index) =>
          <li key={index}><strong>{code}</strong> {message}</li>)}</ul>
      </section>}
    </>}
  </section>;
}

function FindingList({ analysis }: { analysis: string }) {
  const [severity, setSeverity] = useState("");
  const [rule, setRule] = useState("");
  const [filter, setFilter] = useState<Record<string, string>>({});
  const [after, setAfter] = useState<number>();
  const params = new URLSearchParams(filter);
  if (after !== undefined) params.set("after", String(after));
  const { resource, retry } = useResource<{ items: Finding[]; next_after: number | null }>(
    "/analyses/" + analysis + "/findings?" + params.toString());
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const next: Record<string, string> = {};
    if (severity) next.severity = severity;
    if (rule.trim()) next.rule = rule.trim();
    setAfter(undefined);
    setFilter(next);
  }
  return <section className="card findings">
    <h2>Findings</h2>
    <form className="filters" onSubmit={submit}>
      <label>Severity<select value={severity} onChange={event => setSeverity(event.target.value)}>
        <option value="">All</option>{["ERR", "WARN", "INFO", "UNKNOWN"].map(value => <option key={value}>{value}</option>)}
      </select></label>
      <label>Rule<input placeholder="Rule ID" value={rule} onChange={event => setRule(event.target.value)} /></label>
      <button type="submit">Apply filters</button>
    </form>
    {resource.kind === "loading" && <p role="status">Loading findings…</p>}
    {resource.kind === "error" && <Problem message={resource.message} retry={retry} />}
    {resource.kind === "ready" && <>
      {resource.value.items.length === 0 && <p>No findings match.</p>}
      <ul className="finding-list">{resource.value.items.map((finding, index) =>
        <li key={finding.fingerprint + "-" + index}>
          <strong className="rule">{finding.severity} · {finding.rule_id}</strong>
          {finding.path && <code>{finding.method} {finding.path}</code>}
          <p>{finding.message}</p>
          {(finding.base_source || finding.candidate_source) && <small>
            Baseline {formatSource(finding.base_source)} · Candidate {formatSource(finding.candidate_source)}
          </small>}
        </li>)}</ul>
      <div className="actions">
        {after !== undefined && <button onClick={() => setAfter(undefined)}>First findings</button>}
        {resource.value.next_after !== null && <button onClick={() => setAfter(resource.value.next_after ?? undefined)}>Next findings</button>}
      </div>
    </>}
  </section>;
}

function formatSource(source?: Source | null) {
  return source ? `line ${source.line}, column ${source.column}` : "unavailable";
}

createRoot(document.getElementById("root")!).render(<App />);
