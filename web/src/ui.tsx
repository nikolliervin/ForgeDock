import {
  createContext,
  useCallback,
  useMemo,
  useContext,
  useEffect,
  useRef,
  useState,
  type ReactNode,
} from 'react';

type Notice = { id: number; message: string; kind: 'success' | 'error' };
type Confirmation = { title: string; message: string; label?: string; danger?: boolean };
type Ui = {
  notify: (message: string, kind?: Notice['kind']) => void;
  confirm: (options: Confirmation) => Promise<boolean>;
};
const Context = createContext<Ui>({ notify: () => {}, confirm: async () => false });
export const useUI = () => useContext(Context);

export function Modal({
  title,
  children,
  onClose,
}: {
  title: string;
  children: ReactNode;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    const dialog = ref.current!;
    dialog.showModal();
    return () => dialog.close();
  }, []);
  return (
    <dialog
      ref={ref}
      className="modal"
      aria-labelledby="modal-title"
      onCancel={(e) => {
        e.preventDefault();
        close.current();
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget) {
          const box = e.currentTarget.getBoundingClientRect();
          if (
            e.clientX < box.left ||
            e.clientX > box.right ||
            e.clientY < box.top ||
            e.clientY > box.bottom
          )
            close.current();
        }
      }}
    >
      <div className="modal-heading">
        <h2 id="modal-title">{title}</h2>
        <button
          type="button"
          className="icon-button secondary"
          aria-label="Close dialog"
          title="Close (Escape)"
          onClick={onClose}
        >
          ×
        </button>
      </div>
      {children}
    </dialog>
  );
}
export function UIProvider({ children }: { children: ReactNode }) {
  const [notices, setNotices] = useState<Notice[]>([]);
  const [pending, setPending] = useState<
    (Confirmation & { resolve: (answer: boolean) => void }) | null
  >(null);
  const id = useRef(0),
    resolver = useRef<((answer: boolean) => void) | null>(null);
  const timers = useRef(new Set<ReturnType<typeof setTimeout>>());
  useEffect(
    () => () => {
      timers.current.forEach(clearTimeout);
      resolver.current?.(false);
    },
    [],
  );
  const notify = useCallback((message: string, kind: Notice['kind'] = 'success') => {
    const key = ++id.current;
    setNotices((list) => [...list.slice(-3), { id: key, message, kind }]);
    const timer = setTimeout(() => {
      setNotices((list) => list.filter((n) => n.id !== key));
      timers.current.delete(timer);
    }, 5500);
    timers.current.add(timer);
  }, []);
  const confirm = useCallback((options: Confirmation) => {
    resolver.current?.(false);
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve;
      setPending({ ...options, resolve });
    });
  }, []);
  function answer(value: boolean) {
    pending?.resolve(value);
    resolver.current = null;
    setPending(null);
  }
  const value = useMemo(() => ({ notify, confirm }), [notify, confirm]);
  return (
    <Context.Provider value={value}>
      {children}
      <div className="toasts" aria-label="Notifications" aria-live="polite">
        {notices.map((n) => (
          <div className={'toast toast-' + n.kind} key={n.id}>
            <span aria-hidden="true">{n.kind === 'error' ? '!' : '✓'}</span>
            <span>{n.message}</span>
            <button
              className="icon-button secondary"
              title="Dismiss notification"
              aria-label="Dismiss notification"
              onClick={() => setNotices((list) => list.filter((item) => item.id !== n.id))}
            >
              ×
            </button>
          </div>
        ))}
      </div>
      {pending && (
        <Modal title={pending.title} onClose={() => answer(false)}>
          <p>{pending.message}</p>
          <div className="modal-actions">
            <button className="secondary" autoFocus onClick={() => answer(false)}>
              Keep it
            </button>
            <button className={pending.danger ? 'danger' : ''} onClick={() => answer(true)}>
              {pending.label ?? 'Confirm'}
            </button>
          </div>
        </Modal>
      )}
    </Context.Provider>
  );
}
export function CopyButton({ value, label = 'value' }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const { notify } = useUI();
  useEffect(() => () => clearTimeout(timer.current), []);
  return (
    <button
      type="button"
      className="copy-button secondary"
      aria-label={'Copy ' + label}
      title={copied ? 'Copied' : 'Copy ' + label}
      onClick={() => {
        void navigator.clipboard
          .writeText(value)
          .then(() => {
            setCopied(true);
            clearTimeout(timer.current);
            timer.current = setTimeout(() => setCopied(false), 1800);
          })
          .catch(() => notify('Clipboard unavailable. Select the text to copy it.', 'error'));
      }}
    >
      {copied ? '✓ Copied' : <Icon name="copy" />}
    </button>
  );
}
export function Icon({ name, size = 16 }: { name: string; size?: number }) {
  const paths: Record<string, string> = {
    storage: 'M4 4h16v16H4z M4 14h16 M7 17h.01 M10 17h.01',
    copy: 'M9 9h11v11H9z M4 15H3V3h12v1',
    external: 'M14 3h7v7 M21 3L10 14 M10 3H3v18h18v-7',
    branch:
      'M6 3v12a6 6 0 0 0 12 0V9 M15 6a3 3 0 1 0 6 0a3 3 0 1 0-6 0 M3 18a3 3 0 1 0 6 0a3 3 0 1 0-6 0',
    project: 'M3 7l9-5 9 5v10l-9 5-9-5z M3 7l9 5 9-5 M12 12v10',
    search: 'M16 16l5 5 M3 10a7 7 0 1 0 14 0a7 7 0 1 0-14 0',
    sun: 'M12 2v2 M12 20v2 M2 12h2 M20 12h2 M5 5l2 2 M17 17l2 2 M5 19l2-2 M17 7l2-2 M7 12a5 5 0 1 0 10 0a5 5 0 1 0-10 0',
    moon: 'M21 13A9 9 0 1 1 11 3a7 7 0 0 0 10 10',
    monitor: 'M3 3h18v14H3z M8 21h8 M12 17v4',
    chevron: 'M6 9l6 6 6-6',
    collapse: 'M13 6l-6 6 6 6 M20 6l-6 6 6 6',
    expand: 'M4 6l6 6-6 6 M11 6l6 6-6 6',
    git: 'M3 12l9-9 9 9-9 9z M9 9l6 6 M9 9v6',
  };
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name] ?? paths.project} />
    </svg>
  );
}
export function Skeleton({ label = 'Loading…', rows = 3 }: { label?: string; rows?: number }) {
  return (
    <div className="skeleton-group" role="status" aria-label={label}>
      <span className="sr-only">{label}</span>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="skeleton" style={{ width: i === rows - 1 ? '70%' : '100%' }} />
      ))}
    </div>
  );
}
export function Menu({ label, children }: { label: string; children: ReactNode }) {
  const ref = useRef<HTMLDetailsElement>(null);
  useEffect(() => {
    const close = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) ref.current?.removeAttribute('open');
    };
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && ref.current?.open) {
        ref.current.removeAttribute('open');
        ref.current.querySelector('summary')?.focus();
      }
    };
    document.addEventListener('click', close);
    document.addEventListener('keydown', key);
    return () => {
      document.removeEventListener('click', close);
      document.removeEventListener('keydown', key);
    };
  }, []);
  return (
    <details ref={ref} className="action-menu">
      <summary aria-label={label} title={label}>
        ⋯
      </summary>
      <div
        className="menu-items"
        onClick={(e) => {
          if ((e.target as Element).closest('button:not(:disabled),a'))
            ref.current?.removeAttribute('open');
        }}
      >
        {children}
      </div>
    </details>
  );
}
export function ThemeToggle() {
  const [theme, setTheme] = useState(() => {
    try {
      return localStorage.getItem('forgedock-theme') ?? 'dark';
    } catch {
      return 'dark';
    }
  });
  useEffect(() => {
    const media = matchMedia('(prefers-color-scheme: dark)');
    const apply = () =>
      (document.documentElement.dataset.theme =
        theme === 'system' ? (media.matches ? 'dark' : 'light') : theme);
    apply();
    media.addEventListener('change', apply);
    try {
      localStorage.setItem('forgedock-theme', theme);
    } catch {
      /* Storage may be unavailable. */
    }
    return () => media.removeEventListener('change', apply);
  }, [theme]);
  return (
    <button
      className="secondary theme-toggle"
      title={`Theme: ${theme}. Click to switch to ${theme === 'dark' ? 'light' : theme === 'light' ? 'system' : 'dark'}.`}
      aria-label={`Theme: ${theme}`}
      onClick={() => setTheme(theme === 'dark' ? 'light' : theme === 'light' ? 'system' : 'dark')}
    >
      <Icon name={theme === 'dark' ? 'moon' : theme === 'light' ? 'sun' : 'monitor'} />
      <span>{theme[0].toUpperCase() + theme.slice(1)}</span>
    </button>
  );
}
export function RelativeTime({ value }: { value: string }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 15000);
    return () => clearInterval(timer);
  }, []);
  const seconds = Math.max(0, Math.floor((now - Date.parse(value)) / 1000));
  const text =
    seconds < 60
      ? 'just now'
      : seconds < 3600
        ? `${Math.floor(seconds / 60)} min ago`
        : seconds < 86400
          ? `${Math.floor(seconds / 3600)} hr ago`
          : `${Math.floor(seconds / 86400)} days ago`;
  return (
    <time dateTime={value} title={new Date(value).toLocaleString('en-GB')}>
      {text}
    </time>
  );
}
