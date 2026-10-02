import { useCallback } from 'react';
import type { Api } from './types';
import { useUI } from './ui';

/** Centralizes session credentials, CSRF protection, authentication expiry, and mutation feedback. */
export function useManagementApi(
  token: string,
  onAuthentication: (authenticated: boolean) => void,
  csrfToken?: string,
): Api {
  const { notify } = useUI();
  const api = useCallback(
    async <T>(path: string, body?: unknown, method?: string): Promise<T> => {
      const response = await fetch('/api' + path, {
        method: method ?? (body === undefined ? 'GET' : 'POST'),
        credentials: 'same-origin',
        headers: {
          ...(token ? { Authorization: 'Bearer ' + token } : {}),
          ...(csrfToken ? { 'X-CSRF-Token': csrfToken } : {}),
          'Content-Type': 'application/json',
        },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        const problem = await response.json().catch(() => ({}));
        if (response.status === 401) onAuthentication(false);
        throw new Error(
          problem.errors
            ? Object.values(problem.errors).flat().join(' ')
            : (problem.error ??
              problem.detail ??
              problem.title ??
              `Request failed (${response.status})`),
        );
      }
      const verb = method ?? (body === undefined ? 'GET' : 'POST');
      if (verb !== 'GET' && !path.endsWith('/session') && !path.endsWith('/console')) {
        const message = path.includes('/jobs')
          ? path.endsWith('/run')
            ? 'Task execution queued'
            : 'Task settings saved'
          : path.includes('/releases')
            ? verb === 'PUT'
              ? 'Environment group saved'
              : 'Release promotion queued'
            : path.startsWith('/storage')
              ? verb === 'PUT'
                ? 'Retention policy saved'
                : 'Storage cleanup queued'
              : path.includes('/webhook')
                ? 'Auto-deploy settings saved'
                : path.includes('/environment')
                  ? verb === 'DELETE'
                    ? 'Environment variable removed'
                    : 'Environment variables saved'
                  : path.includes('/domains')
                    ? verb === 'DELETE'
                      ? 'Domain removal requested'
                      : path.endsWith('/verify')
                        ? 'DNS verification requested'
                        : 'Domain added'
                    : path.endsWith('/cancel')
                      ? 'Deployment cancelled'
                      : path.includes('/deployments') ||
                          path.endsWith('/rollback') ||
                          path.endsWith('/redeploy') ||
                          path.endsWith('/restart')
                        ? verb === 'DELETE'
                          ? 'Deployment history deleted'
                          : 'Deployment started'
                        : verb === 'PUT'
                          ? 'Settings saved'
                          : path.endsWith('/operations')
                            ? 'Project operation requested'
                            : 'Project created';
        notify(message);
      }
      return response.status === 204
        ? (undefined as T)
        : response.headers.get('content-type')?.includes('application/zip')
          ? ((await response.blob()) as T)
          : response.json();
    },
    [token, csrfToken, onAuthentication, notify],
  );
  return api;
}
