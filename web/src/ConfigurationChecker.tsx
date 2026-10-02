import { useEffect, useRef, useState } from 'react';
import type { Api } from './types';
import './ConfigurationChecker.css';
export type ConfigurationCheck = {
  suggestedMode?: string | null;
  composeFiles: string[];
  selectedComposeFile: string | null;
  services: { name: string; ports: number[] }[];
  issues: { severity: 'error' | 'warning' | 'info'; message: string }[];
};
export function ConfigurationChecker({
  api,
  onResult,
  onApplyFile,
  onApplyMode,
}: {
  api: Api;
  onResult: (result?: ConfigurationCheck) => void;
  onApplyFile: (file: string) => void;
  onApplyMode: () => void;
}) {
  const root = useRef<HTMLDivElement>(null);
  const [result, setResult] = useState<ConfigurationCheck>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [stale, setStale] = useState(false);
  const [service, setService] = useState('');
  const generation = useRef(0);
  const automaticCheck = useRef<() => void>(() => {});
  const lastAutomatic = useRef('');
  const pendingAutomatic = useRef(false);
  const resultCallback = useRef(onResult);
  resultCallback.current = onResult;
  useEffect(() => {
    const form = root.current?.closest('form');
    const changed = (event: Event) => {
      const name = (event.target as HTMLInputElement).name;
      if (
        ![
          'repository',
          'branch',
          'deploymentMode',
          'rootDirectory',
          'dockerfile',
          'composeFile',
          'composeService',
          'port',
        ].includes(name)
      )
        return;
      generation.current++;
      setStale(true);
      setService(String(new FormData(form!).get('composeService') ?? ''));
      if (
        ['repository', 'branch', 'deploymentMode', 'rootDirectory', 'composeFile'].includes(name)
      ) {
        setResult(undefined);
        resultCallback.current(undefined);
      }
    };
    const blurred = (event: FocusEvent) => {
      const input = event.target as HTMLInputElement;
      if (input.name !== 'repository' || !input.value || !input.checkValidity()) return;
      if ((event.relatedTarget as HTMLElement | null)?.closest('[data-configuration-check]'))
        return;
      if (lastAutomatic.current === input.value) return;
      lastAutomatic.current = input.value;
      automaticCheck.current();
    };
    form?.addEventListener('focusout', blurred);
    form?.addEventListener('input', changed);
    form?.addEventListener('change', changed);
    return () => {
      generation.current++;
      pendingAutomatic.current = false;
      form?.removeEventListener('focusout', blurred);
      form?.removeEventListener('input', changed);
      form?.removeEventListener('change', changed);
    };
  }, []);
  async function check() {
    const form = root.current!.closest('form')!;
    const data = new FormData(form);
    if (!data.get('repository')) {
      setError('Enter a repository URL first.');
      return;
    }
    const requestGeneration = ++generation.current;
    setBusy(true);
    setError('');
    setResult(undefined);
    try {
      const value = await api<ConfigurationCheck>('/configuration/check', {
        repositoryUrl: data.get('repository'),
        branch: data.get('branch') || 'main',
        deploymentMode: data.get('deploymentMode'),
        rootDirectory: data.get('rootDirectory') || '.',
        dockerfile: data.get('dockerfile') || 'Dockerfile',
        composeFile: data.get('composeFile') || 'docker-compose.yml',
        composeService: data.get('composeService') || '',
        containerPort: Number(data.get('port') || 8080),
      });
      if (requestGeneration !== generation.current) return;
      if (
        !Array.isArray(value.issues) ||
        !Array.isArray(value.services) ||
        !Array.isArray(value.composeFiles)
      )
        throw new Error(
          'Configuration checking is unavailable. You can enter settings manually or try again.',
        );
      setResult(value);
      onResult(value);
      setStale(false);
      setService(String(data.get('composeService') ?? ''));
    } catch (e) {
      if (requestGeneration === generation.current) setError((e as Error).message);
    } finally {
      setBusy(false);
      if (pendingAutomatic.current) {
        pendingAutomatic.current = false;
        void check();
      }
    }
  }
  automaticCheck.current = () => {
    if (busy) pendingAutomatic.current = true;
    else void check();
  };
  const ports = result?.services.find((s) => s.name === service)?.ports ?? [];
  function applyPort(port: number) {
    const input = root.current!.closest('form')!.elements.namedItem('port') as HTMLInputElement;
    input.value = String(port);
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }
  const hasErrors = result?.issues.some((issue) => issue.severity === 'error');
  return (
    <div ref={root} className={`configuration-checker ${busy ? 'is-checking' : ''}`}>
      <div className="configuration-header">
        <div className="configuration-title">
          <span className="configuration-mark" aria-hidden="true">
            <svg
              viewBox="0 0 24 24"
              width="18"
              height="18"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.6"
            >
              <path d="m12 3 8 4.5v9L12 21l-8-4.5v-9L12 3Z" />
              <path d="m4 7.5 8 4.5 8-4.5M12 12v9" />
            </svg>
          </span>
          <div>
            <strong>Engine recommendations</strong>
            <p>Suggested settings for this repository</p>
          </div>
        </div>
        {result && (
          <button
            type="button"
            className="configuration-dismiss"
            aria-label="Dismiss suggestions"
            title="Dismiss suggestions"
            onClick={() => {
              generation.current++;
              pendingAutomatic.current = false;
              setResult(undefined);
              onResult(undefined);
              setStale(false);
            }}
          >
            ×
          </button>
        )}
      </div>
      {busy ? (
        <div className="configuration-loading" role="status">
          <span className="configuration-spinner" aria-hidden="true" />
          Inspecting repository files…
        </div>
      ) : (
        !result &&
        !error && (
          <p className="configuration-empty">
            Leave the repository URL field to discover settings, or run a check below.
          </p>
        )
      )}
      {error && (
        <div className="configuration-notice configuration-error" role="alert">
          {error}
        </div>
      )}
      {stale && result && (
        <div className="configuration-status" role="status">
          <i className="stale" />
          Settings changed. Check again to validate them.
        </div>
      )}
      {result && !busy && (
        <div aria-live="polite">
          {!stale && (
            <div className={`configuration-status ${hasErrors ? 'needs-attention' : 'complete'}`}>
              <i />
              {hasErrors ? 'Settings need attention' : 'Repository checks complete'}
            </div>
          )}
          <div className="configuration-values">
            {result.suggestedMode === 'Compose' && (
              <div className="configuration-value">
                <div>
                  <span>Deployment type</span>
                  <strong>Docker Compose</strong>
                </div>
                <button
                  type="button"
                  aria-label="Use Docker Compose"
                  onClick={() => {
                    onApplyMode();
                    setStale(true);
                  }}
                >
                  Apply
                </button>
              </div>
            )}
            {result.selectedComposeFile && (
              <div className="configuration-value">
                <div>
                  <span>Compose file</span>
                  <code>{result.selectedComposeFile}</code>
                </div>
                <button
                  type="button"
                  aria-label={`Use ${result.selectedComposeFile}`}
                  onClick={() => {
                    onApplyFile(result.selectedComposeFile!);
                    setStale(true);
                  }}
                >
                  Apply
                </button>
              </div>
            )}
            {ports.length > 0 && (
              <div className="configuration-value">
                <div>
                  <span>Internal port · {service}</span>
                  <strong>{ports.join(' / ')}</strong>
                </div>
                <div className="configuration-port-actions">
                  {ports.map((port) => (
                    <button
                      type="button"
                      aria-label={`Use port ${port}`}
                      key={port}
                      onClick={() => applyPort(port)}
                    >
                      {ports.length > 1 ? `Use ${port}` : 'Apply'}
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>
          {result.issues.length > 0 && (
            <ul className="configuration-issues">
              {result.issues.map((issue, i) => (
                <li key={i} className={`configuration-${issue.severity}`}>
                  <span className="configuration-issue-icon" aria-hidden="true">
                    {issue.severity === 'error' ? '!' : issue.severity === 'warning' ? '!' : 'i'}
                  </span>
                  <span>{issue.message}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      <div className="configuration-footer">
        <span>
          {result
            ? 'Optional suggestions. You can use your own settings.'
            : 'No containers will be started.'}
        </span>
        <button
          data-configuration-check
          type="button"
          disabled={busy}
          aria-label={busy ? 'Checking repository…' : 'Check configuration'}
          onClick={() => void check()}
        >
          <svg
            viewBox="0 0 16 16"
            width="13"
            height="13"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            aria-hidden="true"
          >
            <path d="M13 6a5 5 0 1 0 .1 4M13 2v4H9" />
          </svg>
          {busy ? 'Checking' : result ? 'Recheck' : 'Check'}
        </button>
      </div>
    </div>
  );
}
