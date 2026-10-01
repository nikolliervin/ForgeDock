import { useEffect, useState } from 'react';
import { useUI } from './ui';
import type { Api } from './types';
export const databaseKinds = ['PostgreSql', 'Redis', 'MySql', 'SqlServer', 'MongoDb'] as const;
export type DatabaseKind = (typeof databaseKinds)[number];
export const databaseNames: Record<DatabaseKind, string> = {
  PostgreSql: 'PostgreSQL',
  Redis: 'Redis',
  MySql: 'MySQL',
  SqlServer: 'SQL Server Express',
  MongoDb: 'MongoDB',
};
export const databaseVariables: Record<DatabaseKind, string> = {
  PostgreSql: 'DATABASE_URL',
  Redis: 'REDIS_URL',
  MySql: 'MYSQL_URL',
  SqlServer: 'SQLSERVER_CONNECTION_STRING',
  MongoDb: 'MONGODB_URL',
};
export type Database = {
  id: string;
  kind: DatabaseKind;
  state: string;
  error: string | null;
  backupIntervalHours?: number;
  nextBackupAt?: string | null;
};
export function Databases({ projectId, api }: { projectId: string; api: Api }) {
  const { confirm } = useUI();
  const [services, setServices] = useState<Database[]>([]),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false);
  async function refresh() {
    try {
      setServices(await api<Database[]>(`/projects/${projectId}/databases`));
    } catch (e) {
      setError((e as Error).message);
    }
  }
  useEffect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), 4000);
    return () => clearInterval(timer);
  }, [projectId, api]);
  async function change(kind: DatabaseKind, id?: string) {
    if (
      !id &&
      kind === 'SqlServer' &&
      !(await confirm({
        title: 'Create SQL Server Express?',
        message:
          'This runs free SQL Server 2022 Express with a 2 GiB memory limit. Creating it accepts the Microsoft SQL Server license terms linked below.',
        label: 'Accept and create',
      }))
    )
      return;
    setBusy(true);
    setError('');
    try {
      await api(
        `/projects/${projectId}/databases${id ? `/${id}/retry` : ''}`,
        id ? {} : { kind, acceptSqlServerLicense: kind === 'SqlServer' },
      );
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="panel form">
      <h2>Database services</h2>
      <p>
        Add PostgreSQL, Redis, MySQL, SQL Server Express, or MongoDB on a private project network
        with persistent storage. Connection variables are added automatically. Deploy your app after
        the service is running.
      </p>
      {error && <p role="alert">{error}</p>}
      <div className="actions">
        {databaseKinds.map((kind) => (
          <button
            key={kind}
            disabled={busy || services.some((service) => service.kind === kind)}
            onClick={() => void change(kind)}
          >
            Add {databaseNames[kind]}
          </button>
        ))}
      </div>
      {services.map((service) => (
        <article key={service.id}>
          <h3>
            {databaseNames[service.kind]} · {service.state}
          </h3>
          <p>
            Connection variable: <code>{databaseVariables[service.kind]}</code>
          </p>
          <p className="field-hint">
            Private container: <code>forgedock-db-{service.id.replaceAll('-', '')}</code>. No
            database port is exposed on the host.
          </p>
          {service.error && <p role="alert">{service.error}</p>}
          {['Failed', 'Stopped'].includes(service.state) && (
            <button disabled={busy} onClick={() => void change(service.kind, service.id)}>
              Start / retry
            </button>
          )}
        </article>
      ))}
      <p className="field-hint">
        SQL Server Express requires an x86-64 host and 2 GiB memory.{' '}
        <a href="https://go.microsoft.com/fwlink/?LinkId=746388" target="_blank" rel="noreferrer">
          Microsoft SQL Server license terms
        </a>
        .
      </p>
      <p className="field-hint">
        Stopping a project also stops its databases. Deleting a project removes its database
        containers and retains their Docker volumes.
      </p>
    </section>
  );
}
