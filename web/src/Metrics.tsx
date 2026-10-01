import { useEffect, useState } from 'react';
import { CopyButton, Skeleton } from './ui';

type Point = {
  timestamp: string;
  cpuPercent: number;
  memoryBytes: number;
  receivedBytesPerSecond: number | null;
  sentBytesPerSecond: number | null;
};
type Service = {
  containerId?: string;
  service: string;
  cpuPercent: number;
  memoryBytes: number;
  memoryLimitBytes: number;
  networkReceivedBytes: number;
  networkSentBytes: number;
  blockReadBytes: number;
  blockWrittenBytes: number;
  pids: number;
  uptimeSeconds: number;
};
type MetricsData = {
  range: string;
  start: string;
  end: string;
  bucketSeconds: number;
  fresh: boolean;
  latestAt: string | null;
  services: string[];
  latest: Service[];
  points: Point[];
};
import type { Api } from './types';
const bytes = (value: number) => {
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let n = value,
    index = 0;
  while (n >= 1024 && index < units.length - 1) {
    n /= 1024;
    index++;
  }
  return `${n.toLocaleString(undefined, { maximumFractionDigits: 1 })} ${units[index]}`;
};
const duration = (seconds: number) =>
  seconds >= 86400
    ? `${Math.floor(seconds / 86400)}d ${Math.floor((seconds % 86400) / 3600)}h`
    : seconds >= 3600
      ? `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`
      : `${Math.floor(seconds / 60)}m`;
const time = (stamp: string) =>
  new Date(stamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

function Chart({
  title,
  data,
  keys,
  format,
  colors,
}: {
  title: string;
  data: MetricsData;
  keys: (keyof Point)[];
  format: (value: number) => string;
  colors: string[];
}) {
  const start = Date.parse(data.start),
    end = Date.parse(data.end),
    max = Math.max(1, ...data.points.flatMap((p) => keys.map((key) => Number(p[key] ?? 0))));
  const x = (p: Point) => 48 + ((Date.parse(p.timestamp) - start) / Math.max(1, end - start)) * 624;
  const y = (value: number) => 150 - (value / max) * 124;
  const [hover, setHover] = useState<Point | null>(null);
  return (
    <section className="panel metric-chart">
      <h3>{title}</h3>
      {data.points.length ? (
        <>
          <svg
            viewBox="0 0 720 184"
            role="img"
            aria-label={`${title} history`}
            onMouseLeave={() => setHover(null)}
            onMouseMove={(event) => {
              const fraction =
                (event.clientX - event.currentTarget.getBoundingClientRect().left) /
                event.currentTarget.getBoundingClientRect().width;
              const target = start + ((fraction * 720 - 48) / 624) * (end - start);
              setHover(
                data.points.reduce((best, p) =>
                  Math.abs(Date.parse(p.timestamp) - target) <
                  Math.abs(Date.parse(best.timestamp) - target)
                    ? p
                    : best,
                ),
              );
            }}
          >
            {[0, 0.5, 1].map((f) => (
              <g key={f}>
                <line
                  x1="48"
                  x2="672"
                  y1={y(max * f)}
                  y2={y(max * f)}
                  className="metric-gridline"
                />
                <text x="42" y={y(max * f) + 4} textAnchor="end">
                  {format(max * f)}
                </text>
              </g>
            ))}
            {keys.map((key, index) => {
              let previous: Point | null = null;
              const path = data.points
                .map((p) => {
                  const value = p[key];
                  if (value === null) {
                    previous = null;
                    return '';
                  }
                  const command =
                    previous &&
                    Date.parse(p.timestamp) - Date.parse(previous.timestamp) <=
                      Math.max(90, data.bucketSeconds * 1.5) * 1000
                      ? 'L'
                      : 'M';
                  previous = p;
                  return `${command}${x(p).toFixed(2)},${y(Number(value)).toFixed(2)}`;
                })
                .join(' ');
              return (
                <g key={key}>
                  <path d={path} fill="none" stroke={colors[index]} strokeWidth="2.5" />
                  {data.points
                    .filter((p) => p[key] !== null)
                    .map((p) => (
                      <circle
                        key={p.timestamp}
                        cx={x(p)}
                        cy={y(Number(p[key]))}
                        r={data.points.length < 3 ? 3 : 1.4}
                        fill={colors[index]}
                      >
                        <title>
                          {time(p.timestamp)} · {format(Number(p[key]))}
                        </title>
                      </circle>
                    ))}
                </g>
              );
            })}
            <text x="48" y="178">
              {time(data.start)}
            </text>
            <text x="672" y="178" textAnchor="end">
              {time(data.end)}
            </text>
            {hover && (
              <line x1={x(hover)} x2={x(hover)} y1="22" y2="150" className="metric-cursor" />
            )}
          </svg>
          <div className="metric-chart-caption">
            {hover
              ? `${time(hover.timestamp)} · ${keys.map((key) => (hover[key] === null ? 'Awaiting rate' : format(Number(hover[key])))).join(' / ')}`
              : title === 'Network transfer'
                ? 'Received / sent per second'
                : 'Hover to inspect a sample'}
          </div>
        </>
      ) : (
        <div className="metric-chart-empty">History will appear as samples arrive.</div>
      )}
    </section>
  );
}

export function Metrics({ projectId, api }: { projectId: string; api: Api }) {
  const [range, setRange] = useState('1h'),
    [service, setService] = useState('');
  const [data, setData] = useState<MetricsData | null>(null),
    [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let disposed = false,
      timer: ReturnType<typeof setTimeout>;
    setError('');
    async function load() {
      try {
        const result = await api<MetricsData>(
          `/projects/${projectId}/metrics?range=${range}${service ? `&service=${encodeURIComponent(service)}` : ''}`,
        );
        if (!disposed) {
          setData(result);
          setError('');
        }
      } catch (e) {
        if (!disposed) setError((e as Error).message);
      } finally {
        if (!disposed) timer = setTimeout(() => void load(), 10000);
      }
    }
    void load();
    return () => {
      disposed = true;
      clearTimeout(timer);
    };
  }, [projectId, api, range, service, retry]);
  const latest = data?.latest ?? [],
    lastPoint = data?.points.at(-1);
  const cpu = latest.reduce((sum, s) => sum + s.cpuPercent, 0),
    memory = latest.reduce((sum, s) => sum + s.memoryBytes, 0);
  return (
    <section className="metrics">
      <div className="title-row">
        <div>
          <h2>Application metrics</h2>
          <p>Live resource usage and the last 24 hours of history.</p>
        </div>
        <div className="metrics-controls">
          <label>
            Service
            <select
              aria-label="Service"
              value={service}
              onChange={(e) => setService(e.target.value)}
            >
              <option value="">All services</option>
              {data?.services.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </label>
          <label>
            Time range
            <select value={range} onChange={(e) => setRange(e.target.value)}>
              <option value="1h">Last hour</option>
              <option value="6h">Last 6 hours</option>
              <option value="24h">Last 24 hours</option>
            </select>
          </label>
        </div>
      </div>
      {error && (
        <div className="metrics-notice error" role="alert">
          <span>{error}</span>
          <button className="secondary" onClick={() => setRetry((n) => n + 1)}>
            Retry
          </button>
        </div>
      )}
      {!data && !error && <Skeleton label="Loading metrics…" rows={4} />}
      {data && (
        <>
          <div className={'metrics-notice ' + (data.fresh && !error ? 'fresh' : '')}>
            <span className="metric-live-dot" />
            <span>
              {error
                ? 'Collection status unavailable'
                : data.fresh
                  ? 'Live · sampled every 30 seconds'
                  : data.latestAt
                    ? 'Metrics are stale · no recent sample from running services'
                    : 'Waiting for metrics · deploy an application to get started'}
            </span>
            {data.latestAt && <small>Last sample {time(data.latestAt)}</small>}
          </div>
          <div className="metric-cards">
            {[
              ['CPU usage', latest.length ? `${cpu.toFixed(2)}%` : '—', '100% equals one CPU core'],
              [
                'Memory usage',
                latest.length ? bytes(memory) : '—',
                `${latest.length} sampled service${latest.length === 1 ? '' : 's'}`,
              ],
              [
                'Network received',
                lastPoint?.receivedBytesPerSecond != null
                  ? `${bytes(lastPoint.receivedBytesPerSecond)}/s`
                  : '—',
                'Average for the latest chart interval',
              ],
              [
                'Network sent',
                lastPoint?.sentBytesPerSecond != null
                  ? `${bytes(lastPoint.sentBytesPerSecond)}/s`
                  : '—',
                'Average for the latest chart interval',
              ],
            ].map(([label, value, hint]) => (
              <div className="panel metric-card" key={label}>
                <span>{label}</span>
                <strong>{value}</strong>
                <small>{hint}</small>
              </div>
            ))}
          </div>
          <div className="metric-charts">
            <Chart
              title="CPU usage"
              data={data}
              keys={['cpuPercent']}
              format={(n) => `${n.toFixed(1)}%`}
              colors={['#a78bfa']}
            />
            <Chart
              title="Memory usage"
              data={data}
              keys={['memoryBytes']}
              format={bytes}
              colors={['#38bdf8']}
            />
            <Chart
              title="Network transfer"
              data={data}
              keys={['receivedBytesPerSecond', 'sentBytesPerSecond']}
              format={(n) => `${bytes(n)}/s`}
              colors={['#34d399', '#fbbf24']}
            />
          </div>
          {!!latest.length && (
            <section className="panel">
              <h3>Service usage</h3>
              <div className="metrics-table-wrap">
                <table className="services metrics-table">
                  <thead>
                    <tr>
                      <th>Service</th>
                      <th>CPU</th>
                      <th>Memory / limit</th>
                      <th>Uptime</th>
                      <th>Processes</th>
                      <th>Network received / sent</th>
                      <th>Disk read / written</th>
                    </tr>
                  </thead>
                  <tbody>
                    {latest.map((s) => (
                      <tr key={s.containerId ?? s.service}>
                        <td>
                          {s.service}
                          {s.containerId && (
                            <div className="container-reference">
                              <code title={s.containerId}>{s.containerId.slice(0, 12)}</code>
                              <CopyButton value={s.containerId} label="container ID" />
                            </div>
                          )}
                        </td>
                        <td>{s.cpuPercent.toFixed(2)}%</td>
                        <td>
                          {bytes(s.memoryBytes)} / {bytes(s.memoryLimitBytes)}
                        </td>
                        <td>{duration(s.uptimeSeconds)}</td>
                        <td>{s.pids}</td>
                        <td>
                          {bytes(s.networkReceivedBytes)} / {bytes(s.networkSentBytes)}
                        </td>
                        <td>
                          {bytes(s.blockReadBytes)} / {bytes(s.blockWrittenBytes)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className="metrics-footnote">
                Uptime and transfer totals are measured since each container started. Processes
                include kernel threads. Charts average samples within each interval; missing data is
                shown as a gap.
              </p>
            </section>
          )}
        </>
      )}
    </section>
  );
}
