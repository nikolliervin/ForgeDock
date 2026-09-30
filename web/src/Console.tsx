import { useEffect, useRef, useState } from 'react';
import './console.css';

type Result = { output: string; exitCode: number; truncated: boolean };
type Entry = { command: string; output: string; status: string };

export function Console({ projectId, available, api }: { projectId: string; available: boolean; api: <T>(path: string, body?: unknown, method?: string) => Promise<T> }) {
  const [command, setCommand] = useState('');
  const [entries, setEntries] = useState<Entry[]>([]);
  const [running, setRunning] = useState(false);
  const [historyIndex, setHistoryIndex] = useState<number | null>(null);
  const output = useRef<HTMLDivElement>(null);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => { output.current?.scrollTo(0, output.current.scrollHeight); }, [entries, running]);
  async function run() {
    if (!command.trim() || running || !available) return;
    const submitted = command;
    setRunning(true); setCommand(''); setHistoryIndex(null);
    setEntries(old => [...old.slice(-49), { command: submitted, output: '', status: 'Running…' }]);
    try {
      const result = await api<Result>(`/projects/${projectId}/console`, { command: submitted });
      if (mounted.current) setEntries(old => [...old.slice(0, -1), { command: submitted, output: result.output, status: `Exit ${result.exitCode}${result.truncated ? ' · Output truncated at 64 KiB' : ''}` }]);
    } catch (error) {
      if (mounted.current) setEntries(old => [...old.slice(0, -1), { command: submitted, output: error instanceof Error ? error.message : 'Command failed.', status: 'Failed' }]);
    } finally { if (mounted.current) setRunning(false); }
  }
  return <section className="panel project-console"><div className="title-row"><h2>Container console</h2><button type="button" className="secondary" disabled={running || !entries.length} onClick={() => setEntries([])}>Clear output</button></div>
    <p>Run shell commands in this project's active application container. Compose targets the public service. Each command starts a fresh shell; combine directory changes with commands, for example <code>cd /app &amp;&amp; ls</code>.</p>
    {!available && <p role="status">Start the application and wait for deployments to finish to use the console.</p>}
    <div className="console-output" ref={output} tabIndex={0} aria-label="Console output" aria-busy={running}>{!entries.length ? <span className="console-placeholder">Command output will appear here.</span> : entries.map((entry, index) => <div className="console-entry" key={index}><pre className="console-command">$ {entry.command}</pre><pre>{entry.output}</pre><span className="console-status">{entry.status}</span></div>)}</div>
    <form onSubmit={event => { event.preventDefault(); void run(); }}><label htmlFor="console-command">Shell command</label><div className="console-input-row"><input id="console-command" value={command} maxLength={4096} autoComplete="off" spellCheck={false} placeholder="pwd && ls -la" disabled={!available || running} onChange={event => { setCommand(event.target.value); setHistoryIndex(null); }} onKeyDown={event => {
      if ((event.key === 'ArrowUp' || event.key === 'ArrowDown') && entries.length) {
        event.preventDefault(); const next = event.key === 'ArrowUp' ? Math.max(0, (historyIndex ?? entries.length) - 1) : Math.min(entries.length, (historyIndex ?? entries.length) + 1);
        setHistoryIndex(next); setCommand(entries[next]?.command ?? '');
      }
    }} /><button disabled={!available || running || !command.trim()}>{running ? 'Running…' : 'Run command'}</button></div></form>
    <p className="console-note">Requires /bin/sh in the image. Commands use the container's configured user and can change application files or data. Changes outside persistent volumes disappear on redeploy. Requests wait up to 30 seconds; timed-out commands may continue running. Interactive programs are not supported. Containers with host access or bind mounts cannot use the console.</p>
  </section>;
}
