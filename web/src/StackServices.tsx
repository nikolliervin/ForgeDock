import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { Deployment } from './Deployments';
import type { Api } from './types';
import './StackServices.css';

type Node = { name: string; dependencies: string[]; networks: string[]; volumes: string[] };
type Topology = { entryService: string; services: Node[] };
export function StackServices({ deployment, api }: { deployment: Deployment; api: Api }) {
  const [view, setView] = useState('map');
  const [topology, setTopology] = useState<Topology>();
  const [error, setError] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [selected, setSelected] = useState<string>();
  const [zoom, setZoom] = useState(1);
  const [networks, setNetworks] = useState(false);
  const viewport = useRef<HTMLDivElement>(null);
  useEffect(() => {
    let active = true;
    setError(false);
    api<Topology>(`/deployments/${deployment.id}/topology`)
      .then((value) => {
        if (active) setTopology(value);
      })
      .catch(() => {
        if (active) setError(true);
      });
    return () => {
      active = false;
    };
  }, [api, deployment.id, attempt, deployment.state]);
  const services = deployment.services;
  const metadata = new Map(topology?.services?.map((node) => [node.name, node]) ?? []);
  // Traverse dependencies once; cycles and disconnected services remain visible.
  const levels = new Map<string, number>();
  const visit = (name: string, level: number) => {
    if (levels.has(name) || !services.some((s) => s.name === name)) return;
    levels.set(name, level);
    metadata.get(name)?.dependencies.forEach((dep) => visit(dep, level + 1));
  };
  visit(topology?.entryService ?? services[0]?.name, 0);
  services.forEach((s) => {
    if (!levels.has(s.name)) levels.set(s.name, 0);
  });
  const rows = new Map<number, number>();
  const positions = new Map(
    services.map((s) => {
      const level = levels.get(s.name)!;
      const row = rows.get(level) ?? 0;
      rows.set(level, row + 1);
      return [s.name, { x: 30 + level * 245, y: 32 + row * 120 }];
    }),
  );
  const width = Math.max(540, 60 + (Math.max(0, ...levels.values()) + 1) * 245);
  const height = Math.max(260, 60 + Math.max(1, ...rows.values()) * 120);
  const fit = () =>
    setZoom(Math.min(1, Math.max(0.35, (viewport.current?.clientWidth ?? width) / width)));
  useLayoutEffect(() => {
    if (!viewport.current) return;
    let previousWidth = viewport.current.clientWidth;
    const applyFit = (available: number) => setZoom(Math.min(1, Math.max(0.35, available / width)));
    applyFit(previousWidth);
    const observer = new ResizeObserver(() => {
      const available = viewport.current?.clientWidth ?? width;
      if (available === previousWidth) return;
      previousWidth = available;
      applyFit(available);
    });
    observer.observe(viewport.current);
    return () => observer.disconnect();
  }, [width, view]);
  const focus = services.find((s) => s.name === selected);
  const details = selected ? metadata.get(selected) : undefined;
  const connections = services.flatMap((s) =>
    (metadata.get(s.name)?.dependencies ?? [])
      .filter((dep) => positions.has(dep))
      .map((dep) => ({ from: s.name, to: dep })),
  );
  const groups = [...new Set(topology?.services?.flatMap((s) => s.networks) ?? [])];
  const unhealthy = services.filter(
    (s) => s.health === 'unhealthy' || /exited|dead|failed/i.test(s.state),
  ).length;
  return (
    <section className="stack-map" aria-label="Stack services">
      <div className="stack-heading">
        <div>
          <h3>Stack services</h3>
          <span>
            {services.length} services ·{' '}
            {unhealthy ? `${unhealthy} need attention` : 'Status overview'}
          </span>
        </div>
        <div className="stack-switch" aria-label="Service view">
          <button aria-pressed={view === 'map'} onClick={() => setView('map')}>
            Map
          </button>
          <button aria-pressed={view === 'list'} onClick={() => setView('list')}>
            List
          </button>
        </div>
      </div>
      {view === 'list' ? (
        <div className="table-scroll">
          <table className="services">
            <thead>
              <tr>
                <th>Service</th>
                <th>Status</th>
                <th>Health</th>
              </tr>
            </thead>
            <tbody>
              {services.map((s) => (
                <tr key={s.name}>
                  <td>{s.name}</td>
                  <td>{s.state}</td>
                  <td>{s.health || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <>
          <div className="stack-toolbar">
            <span>Declared dependencies →</span>
            <label>
              <input
                type="checkbox"
                checked={networks}
                onChange={(e) => setNetworks(e.target.checked)}
              />{' '}
              Networks
            </label>
            <div>
              <button onClick={fit}>Fit</button>
              <button
                aria-label="Zoom out"
                disabled={zoom <= 0.35}
                onClick={() => setZoom((z) => Math.max(0.35, z - 0.2))}
              >
                −
              </button>
              <button onClick={() => setZoom(1)} aria-label="Reset zoom">
                {Math.round(zoom * 100)}%
              </button>
              <button
                aria-label="Zoom in"
                disabled={zoom >= 1.6}
                onClick={() => setZoom((z) => Math.min(1.6, z + 0.2))}
              >
                +
              </button>
            </div>
          </div>
          {error && (
            <p role="alert">
              Connections could not load.{' '}
              <button onClick={() => setAttempt((a) => a + 1)}>Retry</button>
            </p>
          )}
          <div className="stack-viewport" ref={viewport}>
            <div style={{ width: width * zoom, height: height * zoom }}>
              <div className="stack-canvas" style={{ width, height, transform: `scale(${zoom})` }}>
                <svg width={width} height={height} aria-label="Declared service dependencies">
                  <defs>
                    <marker
                      id={`arrow-${deployment.id}`}
                      viewBox="0 0 10 10"
                      refX="9"
                      refY="5"
                      markerWidth="6"
                      markerHeight="6"
                      orient="auto-start-reverse"
                    >
                      <path d="M 0 0 L 10 5 L 0 10 z" />
                    </marker>
                  </defs>
                  {connections.map(({ from, to }) => {
                    const a = positions.get(from)!;
                    const b = positions.get(to)!;
                    return (
                      <path
                        key={`${from}-${to}`}
                        className={
                          selected && (from === selected || to === selected) ? 'focused' : ''
                        }
                        d={
                          b.x > a.x
                            ? `M ${a.x + 205} ${a.y + 44} C ${a.x + 235} ${a.y + 44}, ${b.x - 30} ${b.y + 44}, ${b.x} ${b.y + 44}`
                            : `M ${a.x + 103} ${a.y + 90} C ${a.x + 103} ${Math.max(a.y, b.y) + 115}, ${b.x + 103} ${Math.max(a.y, b.y) + 115}, ${b.x + 103} ${b.y + 90}`
                        }
                        markerEnd={`url(#arrow-${deployment.id})`}
                      >
                        <title>
                          {from} depends on {to}
                        </title>
                      </path>
                    );
                  })}
                </svg>
                {services.map((s) => {
                  const p = positions.get(s.name)!;
                  const bad = s.health === 'unhealthy' || /exited|dead|failed/i.test(s.state);
                  return (
                    <button
                      key={s.name}
                      className={`stack-node ${bad ? 'bad' : /running/i.test(s.state) ? 'running' : ''}`}
                      style={{ left: p.x, top: p.y }}
                      aria-pressed={selected === s.name}
                      onClick={() => setSelected(selected === s.name ? undefined : s.name)}
                    >
                      <span className="stack-node-title">
                        <i />
                        {s.name}
                      </span>
                      <span>
                        {s.state} · {s.health || 'No health check'}
                      </span>
                      <small>
                        {s.name === topology?.entryService
                          ? '↗ Public entry'
                          : metadata.has(s.name)
                            ? `${metadata.get(s.name)!.dependencies.length} dependencies`
                            : 'Connections unavailable'}
                      </small>
                    </button>
                  );
                })}
              </div>
            </div>
          </div>
          {networks && (
            <div className="stack-networks">
              {groups.length ? (
                groups.map((name) => (
                  <div key={name}>
                    <strong>{name}</strong>
                    <span>
                      {topology?.services
                        .filter((s) => s.networks.includes(name))
                        .map((s) => s.name)
                        .join(' · ')}
                    </span>
                  </div>
                ))
              ) : (
                <span>No network information available.</span>
              )}
            </div>
          )}
          {focus ? (
            <div className="stack-details">
              <div>
                <strong>{focus.name}</strong>
                <button aria-label="Close service details" onClick={() => setSelected(undefined)}>
                  ×
                </button>
              </div>
              <dl>
                <dt>Image</dt>
                <dd>{focus.image || '—'}</dd>
                <dt>Depends on</dt>
                <dd>
                  {details ? details.dependencies.join(', ') || 'None declared' : 'Unavailable'}
                </dd>
                <dt>Networks</dt>
                <dd>{details?.networks.join(', ') || '—'}</dd>
                <dt>Named storage</dt>
                <dd>{details ? details.volumes.join(', ') || 'None' : 'Unavailable'}</dd>
              </dl>
            </div>
          ) : (
            <p className="stack-caption">
              {!topology && !error
                ? 'Loading connections…'
                : !topology?.services?.length
                  ? 'Connections unavailable for this deployment. Select a service to inspect its status.'
                  : 'Select a service to explore. Connections describe the saved stack, not live traffic.'}
            </p>
          )}
        </>
      )}
    </section>
  );
}
