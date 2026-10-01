import type { DeploymentConfig } from './DeploymentFields';

/** Management requests use an in-memory bearer token; callers receive parsed JSON or an archive Blob. */
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
