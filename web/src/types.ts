import type { DeploymentConfig } from './DeploymentFields';

/** Management requests use cookie sessions with CSRF protection or a legacy in-memory token; callers receive parsed JSON or an archive Blob. */
export type Api = <T>(path: string, body?: unknown, method?: string) => Promise<T>;
export type Action = (task: () => Promise<void>) => Promise<void>;
export type Project = DeploymentConfig & {
  id: string;
  name: string;
  repositoryUrl: string;
  branch: string;
  containerPort: number;
  healthPath: string;
  activeDeploymentId: string | null;
  healthStatus: string;
};
