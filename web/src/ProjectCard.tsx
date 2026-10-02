import type { Project } from './types';
import { Icon } from './ui';
import './ProjectCard.css';

function repository(url: string) {
  try {
    const parsed = new URL(url);
    const host = parsed.hostname.toLowerCase();
    const provider =
      host === 'github.com'
        ? 'GitHub'
        : host === 'gitlab.com'
          ? 'GitLab'
          : host === 'bitbucket.org'
            ? 'Bitbucket'
            : host === 'dev.azure.com' || host.endsWith('.visualstudio.com')
              ? 'Azure DevOps'
              : host;
    return {
      provider,
      name:
        decodeURIComponent(parsed.pathname)
          .replace(/^\/+|\/+$/g, '')
          .replace(/\.git$/, '') || host,
    };
  } catch {
    return { provider: 'Git repository', name: url };
  }
}

function ProviderIcon({ provider }: { provider: string }) {
  if (provider === 'GitHub')
    return (
      <svg aria-hidden="true" viewBox="0 0 24 24" width="17" height="17" fill="currentColor">
        <path d="M12 .8a11.2 11.2 0 0 0-3.54 21.82c.56.1.77-.24.77-.54v-2.1c-3.13.68-3.79-1.33-3.79-1.33-.51-1.3-1.25-1.65-1.25-1.65-1.02-.7.08-.69.08-.69 1.13.08 1.72 1.16 1.72 1.16 1 1.71 2.63 1.22 3.27.93.1-.72.39-1.22.71-1.5-2.5-.28-5.13-1.25-5.13-5.57 0-1.23.44-2.24 1.16-3.03-.12-.28-.5-1.43.11-2.98 0 0 .94-.3 3.08 1.15a10.73 10.73 0 0 1 5.6 0c2.14-1.45 3.08-1.15 3.08-1.15.61 1.55.23 2.7.11 2.98.72.79 1.16 1.8 1.16 3.03 0 4.33-2.64 5.28-5.15 5.56.4.35.76 1.03.76 2.08v3.11c0 .3.2.65.78.54A11.2 11.2 0 0 0 12 .8Z" />
      </svg>
    );
  if (provider === 'GitLab')
    return (
      <svg
        aria-hidden="true"
        viewBox="0 0 24 24"
        width="17"
        height="17"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinejoin="round"
      >
        <path d="m12 22 10-8-3-11-3 8H8L5 3 2 14l10 8Zm0 0L8 11m4 11 4-11M2 14h20" />
      </svg>
    );
  return <Icon name="git" size={17} />;
}

export function ProjectCard({ project, onOpen }: { project: Project; onOpen: () => void }) {
  const repo = repository(project.repositoryUrl);
  const status = project.healthStatus === 'NotDeployed' ? 'Not deployed' : project.healthStatus;
  const health = project.healthStatus.toLowerCase();
  return (
    <button className="panel project-card" onClick={onOpen} aria-label={`Open ${project.name}`}>
      <div className="project-card-header">
        <span className="project-card-symbol">
          <Icon name="project" size={20} />
        </span>
        <span className={`project-card-status ${health}`}>
          <i />
          {status}
        </span>
      </div>
      <h2>{project.name}</h2>
      <div className="project-card-repository" title={project.repositoryUrl}>
        <ProviderIcon provider={repo.provider} />
        <span>{repo.name}</span>
      </div>
      <div className="project-card-provider">{repo.provider}</div>
      <div className="project-card-footer">
        <span className="project-card-branch" title={`Branch: ${project.branch}`}>
          <Icon name="branch" />
          <span>{project.branch}</span>
        </span>
        <span className="project-card-open">
          View project <span aria-hidden="true">↗</span>
        </span>
      </div>
    </button>
  );
}
