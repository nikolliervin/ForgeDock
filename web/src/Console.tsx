import { useEffect, useRef } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import './console.css';

type Result = { output: string; exitCode: number; truncated: boolean };

export function Console({
  projectId,
  available,
  api,
}: {
  projectId: string;
  available: boolean;
  api: <T>(path: string, body?: unknown, method?: string) => Promise<T>;
}) {
  const host = useRef<HTMLDivElement>(null);
  const current = useRef({ available, api });
  current.current = { available, api };
  const terminal = useRef<Terminal | null>(null);
  const availability = useRef<((value: boolean) => void) | null>(null);

  useEffect(() => {
    if (!host.current) return;
    let disposed = false,
      running = false,
      line = '',
      cursor = 0,
      historyIndex = 0,
      draft = '';
    const history: string[] = [];
    const term = new Terminal({
      cursorBlink: true,
      cursorStyle: 'block',
      fontSize: 14,
      lineHeight: 1.35,
      fontFamily: '"SFMono-Regular", Consolas, "Liberation Mono", monospace',
      scrollback: 3000,
      convertEol: true,
      screenReaderMode: true,
      theme: {
        background: '#0b0e14',
        foreground: '#d5d9e2',
        cursor: '#c6d0e0',
        selectionBackground: '#374458',
        black: '#131820',
        red: '#ef8585',
        green: '#9cd69c',
        yellow: '#e7c98b',
        blue: '#91b4ed',
        magenta: '#c7a1df',
        cyan: '#8dcbd1',
        white: '#d5d9e2',
      },
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(host.current);
    terminal.current = term;
    term.textarea?.setAttribute('aria-label', 'Shell command');
    const prompt = '\x1b[32m$\x1b[0m ';
    function redraw() {
      // Keep the editable prompt on one row while allowing long commands.
      const width = Math.max(8, term.cols - 3);
      const start = Math.max(0, cursor - width + 1);
      const visible = line.slice(start, start + width);
      term.write('\r\x1b[2K' + prompt + visible + `\x1b[${3 + cursor - start}G`);
      term.scrollToBottom();
    }
    function ready() {
      if (current.current.available) {
        term.options.disableStdin = false;
        term.write(prompt);
      } else {
        term.options.disableStdin = true;
        term.writeln(
          '\x1b[90mContainer unavailable. Start the application and wait for deployments to finish.\x1b[0m',
        );
      }
    }
    async function execute() {
      if (!line.trim()) {
        term.write('\r\n');
        line = '';
        cursor = 0;
        ready();
        return;
      }
      const command = line;
      term.write('\r\x1b[2K' + prompt + command + '\r\n');
      line = '';
      cursor = 0;
      draft = '';
      if (command.trim() === 'clear') {
        term.clear();
        ready();
        return;
      }
      history.push(command);
      if (history.length > 100) history.shift();
      historyIndex = history.length;
      running = true;
      term.write('\x1b[?25l');
      try {
        const result = await current.current.api<Result>(`/projects/${projectId}/console`, {
          command,
        });
        if (disposed) return;
        if (result.output) {
          term.write(result.output);
          if (!result.output.endsWith('\n')) term.write('\r\n');
        }
        if (result.exitCode !== 0) term.writeln(`\x1b[31m[exit ${result.exitCode}]\x1b[0m`);
        if (result.truncated) term.writeln('\x1b[33m[output truncated at 64 KiB]\x1b[0m');
      } catch (error) {
        if (!disposed)
          term.writeln(
            '\x1b[31m' + (error instanceof Error ? error.message : 'Command failed.') + '\x1b[0m',
          );
      } finally {
        if (!disposed) {
          running = false;
          term.write('\x1b[0m\x1b[?25h');
          ready();
          term.focus();
        }
      }
    }
    const input = term.onData((data) => {
      if (running || !current.current.available) return;
      if (data === '\r') {
        void execute();
        return;
      }
      if (data === '\x03') {
        term.write('^C\r\n');
        line = '';
        cursor = 0;
        ready();
        return;
      }
      if (data === '\x0c') {
        term.clear();
        redraw();
        return;
      }
      if (data === '\x1b[A' || data === '\x1b[B') {
        if (historyIndex === history.length) draft = line;
        historyIndex = Math.max(
          0,
          Math.min(history.length, historyIndex + (data === '\x1b[A' ? -1 : 1)),
        );
        line = history[historyIndex] ?? draft;
        cursor = line.length;
        redraw();
        return;
      }
      if (data === '\x1b[D') cursor = Math.max(0, cursor - 1);
      else if (data === '\x1b[C') cursor = Math.min(line.length, cursor + 1);
      else if (data === '\x01' || data === '\x1b[H' || data === '\x1bOH') cursor = 0;
      else if (data === '\x05' || data === '\x1b[F' || data === '\x1bOF') cursor = line.length;
      else if (data === '\x7f') {
        if (cursor > 0) {
          line = line.slice(0, cursor - 1) + line.slice(cursor);
          cursor--;
        }
      } else if (data === '\x1b[3~') line = line.slice(0, cursor) + line.slice(cursor + 1);
      else if (data === '\x15') {
        line = line.slice(cursor);
        cursor = 0;
      } else if (!data.includes('\x1b')) {
        // Pasted newlines become spaces so pasting cannot submit commands.
        const text = data
          .replace(/[\r\n]+/g, ' ')
          .replace(/[\x00-\x1f\x7f]/g, '')
          .slice(0, 4096 - line.length);
        line = line.slice(0, cursor) + text + line.slice(cursor);
        cursor += text.length;
      }
      redraw();
    });
    availability.current = (value) => {
      term.options.disableStdin = !value;
      if (!running) {
        term.write('\r\n');
        ready();
      }
    };
    const resize = new ResizeObserver(() => {
      fit.fit();
      if (!running && current.current.available) redraw();
    });
    resize.observe(host.current);
    fit.fit();
    ready();
    term.focus();
    return () => {
      disposed = true;
      resize.disconnect();
      input.dispose();
      term.dispose();
      terminal.current = null;
      availability.current = null;
    };
  }, [projectId]);

  const previousAvailable = useRef(available);
  useEffect(() => {
    if (previousAvailable.current !== available) availability.current?.(available);
    previousAvailable.current = available;
  }, [available]);

  return (
    <section className="project-terminal" aria-label="Container console">
      <div className="terminal-bar">
        <span className="terminal-title">
          <span aria-hidden="true">›_</span> Container console
        </span>
        <span className="terminal-connection">
          <span className={available ? 'terminal-dot online' : 'terminal-dot'} />
          {available ? '/bin/sh' : 'Offline'}
        </span>
      </div>
      <div
        className="terminal-screen"
        ref={host}
        aria-label="Console output"
        onClick={() => terminal.current?.focus()}
      />
      <div className="terminal-footer">
        <span>
          Enter to run <span aria-hidden="true">·</span> ↑ ↓ history{' '}
          <span aria-hidden="true">·</span> Ctrl+L clear
        </span>
        <span>Fresh shell per command</span>
      </div>
    </section>
  );
}
