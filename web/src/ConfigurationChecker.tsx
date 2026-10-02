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
  return (
    <div ref={root} className="configuration-checker">
      <button
        data-configuration-check
        type="button"
        className="secondary"
        disabled={busy}
        onClick={() => void check()}
      >
        {busy ? 'Checking repository…' : 'Check configuration'}
      </button>
      <p className="field-hint">
        Suggestions load when you leave the repository URL field. You can edit any setting.
      </p>
      {error && <p role="alert">{error}</p>}
      {stale && <p role="status">Settings changed. Check again to validate them.</p>}
      {result && (
        <div aria-live="polite">
          <div className="configuration-recommendations">
            <strong>Engine recommendations</strong>
            <button
              type="button"
              className="secondary"
              onClick={() => {
                generation.current++;
                setResult(undefined);
                onResult(undefined);
                setStale(false);
              }}
            >
              Dismiss suggestions
            </button>
          </div>
          <p>
            These are the settings the engine recommends. Apply the suggestions you want, or enter
            your own values.
          </p>
          {result.suggestedMode === 'Compose' && (
            <button
              type="button"
              className="secondary"
              onClick={() => {
                onApplyMode();
                setStale(true);
              }}
            >
              Use Docker Compose
            </button>
          )}

          {!stale && (
            <strong>
              {result.issues.some((i) => i.severity === 'error')
                ? 'Settings need attention'
                : 'Repository checks complete'}
            </strong>
          )}
          <ul>
            {result.issues.map((issue, i) => (
              <li key={i} className={`configuration-${issue.severity}`}>
                <span>
                  {issue.severity === 'error'
                    ? 'Error'
                    : issue.severity === 'warning'
                      ? 'Check'
                      : 'Note'}
                  :
                </span>{' '}
                {issue.message}
              </li>
            ))}
          </ul>
          {result.selectedComposeFile && (
            <button
              type="button"
              className="secondary"
              onClick={() => {
                onApplyFile(result.selectedComposeFile!);
                setStale(true);
              }}
            >
              Use {result.selectedComposeFile}
            </button>
          )}
          {ports.length > 0 && (
            <div className="configuration-ports">
              <p>
                Ports inside <strong>{service}</strong>: {ports.join(', ')}. ForgeDock uses the
                container port, not the host port.
              </p>
              {ports.map((port) => (
                <button
                  type="button"
                  className="secondary"
                  key={port}
                  onClick={() => applyPort(port)}
                >
                  Use port {port}
                </button>
              ))}
            </div>
          )}
          <p className="field-hint">
            These checks do not guarantee a successful build or runtime health. Check again after
            applying suggestions.
          </p>
        </div>
      )}
    </div>
  );
}
