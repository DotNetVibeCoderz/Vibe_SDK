import { ChildProcessWithoutNullStreams, spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as readline from 'node:readline';
import * as vscode from 'vscode';

/**
 * How the extension reaches the SDK.
 *
 * Everything goes through the `unitree` CLI rather than being reimplemented here. The template
 * catalogue, the telemetry decoding and the deploy sequence all live in C#, and a TypeScript copy of
 * any of them would drift the first time either side changed.
 */
export class UnitreeCli {
    constructor(private readonly log: vscode.LogOutputChannel) {}

    /**
     * Locates the repository root.
     *
     * Generated projects reference the SDK by relative path, so this has to be right or every
     * scaffolded project fails to restore with an error that says nothing about the cause.
     */
    public findSdkRoot(): string | undefined {
        const configured = vscode.workspace.getConfiguration('unitree').get<string>('sdkRoot');

        if (configured && this.isSdkRoot(configured)) {
            return configured;
        }

        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            let directory = folder.uri.fsPath;

            // Walk up: the open folder is very often a project inside the repository rather than the
            // repository itself.
            for (let depth = 0; depth < 8; depth++) {
                if (this.isSdkRoot(directory)) {
                    return directory;
                }

                const parent = path.dirname(directory);
                if (parent === directory) {
                    break;
                }
                directory = parent;
            }
        }

        return undefined;
    }

    private isSdkRoot(directory: string): boolean {
        // The solution file plus a src folder is a much stronger signal than either alone.
        return fs.existsSync(path.join(directory, 'Unitree.Net.slnx'))
            && fs.existsSync(path.join(directory, 'src'));
    }

    /**
     * Builds the command line that invokes the CLI.
     *
     * Three routes, in order of preference: a configured executable, an already-built assembly, and
     * finally `dotnet run` from source.
     *
     * The middle one matters more than it looks. `dotnet run` performs a restore and build on every
     * invocation, and two concurrent invocations — the telemetry stream and a template listing, say —
     * block on the same MSBuild lock. The result is a view that stays empty for a minute and then
     * times out, which reads as a broken extension rather than a slow one.
     */
    public resolve(args: string[]): { command: string; args: string[]; cwd: string } | undefined {
        const configured = vscode.workspace.getConfiguration('unitree').get<string>('cliPath');

        if (configured && fs.existsSync(configured)) {
            return { command: configured, args, cwd: path.dirname(configured) };
        }

        const root = this.findSdkRoot();

        if (!root) {
            return undefined;
        }

        const cliProject = path.join(root, 'apps', 'Unitree.Net.Cli');

        for (const configuration of ['Release', 'Debug']) {
            const assembly = path.join(cliProject, 'bin', configuration, 'net10.0', 'unitree.dll');

            if (fs.existsSync(assembly)) {
                return { command: 'dotnet', args: [assembly, ...args], cwd: root };
            }
        }

        this.log.debug('No built CLI found; falling back to `dotnet run`, which is much slower.');

        return {
            command: 'dotnet',
            args: ['run', '--project', cliProject, '--'].concat(args),
            cwd: root,
        };
    }

    /** Settings that every robot-facing command needs, in the CLI's own argument form. */
    public transportArgs(): string[] {
        const settings = vscode.workspace.getConfiguration('unitree');

        const args = [
            '--Unitree:Model', settings.get<string>('model') ?? 'Go2',
            '--Unitree:Transport', settings.get<string>('transport') ?? 'ManagedMulticast',
            '--Unitree:MulticastAddress', settings.get<string>('multicastAddress') ?? '239.255.0.1',
            '--Unitree:MulticastPort', String(settings.get<number>('multicastPort') ?? 7447),
        ];

        const nic = settings.get<string>('networkInterface');

        if (nic) {
            args.push('--Unitree:NetworkInterface', nic);
        }

        return args;
    }

    /**
     * Runs a command that prints one JSON document and exits.
     *
     * @returns The parsed document, or undefined if the CLI could not be run or did not produce JSON.
     */
    public async json<T>(args: string[], timeoutMs = 60_000): Promise<T | undefined> {
        const resolved = this.resolve(args);

        if (!resolved) {
            this.reportMissingRoot();
            return undefined;
        }

        return new Promise<T | undefined>((resolve) => {
            const child = spawn(resolved.command, resolved.args, { cwd: resolved.cwd, shell: false });

            let stdout = '';
            let stderr = '';

            const timer = setTimeout(() => {
                child.kill();
                this.log.error(`unitree ${args[0]} timed out after ${timeoutMs} ms`);
                resolve(undefined);
            }, timeoutMs);

            child.stdout.on('data', (chunk) => (stdout += chunk.toString()));

            child.stderr.on('data', (chunk) => {
                stderr += chunk.toString();
                // stderr is the CLI's progress channel, so it belongs in the log rather than being
                // treated as failure.
                for (const line of chunk.toString().split('\n')) {
                    if (line.trim()) {
                        this.log.info(line.trimEnd());
                    }
                }
            });

            child.on('error', (error) => {
                clearTimeout(timer);
                this.log.error(`Could not start ${resolved.command}: ${error.message}`);
                resolve(undefined);
            });

            child.on('close', (code) => {
                clearTimeout(timer);

                // The last line that actually looks like JSON. MSBuild writes restore chatter to
                // stdout ahead of the payload when the CLI runs from source, so "the last non-empty
                // line" is not good enough — it picks up whatever was printed last instead.
                const line = stdout
                    .split('\n')
                    .map((l) => l.trim())
                    .filter((l) => l.startsWith('{') || l.startsWith('['))
                    .pop();

                if (!line) {
                    this.log.error(`unitree ${args[0]} exited ${code} with no output. ${stderr.trim()}`);
                    resolve(undefined);
                    return;
                }

                try {
                    resolve(JSON.parse(line) as T);
                } catch {
                    this.log.error(`unitree ${args[0]} exited ${code}; output was not JSON: ${line.slice(0, 300)}`);
                    resolve(undefined);
                }
            });
        });
    }

    /**
     * Starts a long-running command that emits newline-delimited JSON.
     *
     * @returns A handle whose `dispose` stops the child, or undefined if it could not be started.
     */
    public stream<T>(args: string[], onMessage: (message: T) => void): StreamHandle | undefined {
        const resolved = this.resolve(args);

        if (!resolved) {
            this.reportMissingRoot();
            return undefined;
        }

        const child = spawn(resolved.command, resolved.args, { cwd: resolved.cwd, shell: false });
        const reader = readline.createInterface({ input: child.stdout });

        reader.on('line', (line) => {
            const text = line.trim();

            // Restore and build chatter shares stdout when the CLI runs from source, so anything that
            // is not a JSON object is simply not for us.
            if (!text.startsWith('{')) {
                return;
            }

            try {
                onMessage(JSON.parse(text) as T);
            } catch {
                this.log.debug(`Ignored unparsable stream line: ${text.slice(0, 200)}`);
            }
        });

        child.stderr.on('data', (chunk) => {
            for (const line of chunk.toString().split('\n')) {
                if (line.trim()) {
                    this.log.info(line.trimEnd());
                }
            }
        });

        child.on('error', (error) => this.log.error(`Stream failed to start: ${error.message}`));

        return new StreamHandle(child, () => reader.close());
    }

    private reportMissingRoot(): void {
        this.log.error('Could not find the Unitree.Net repository. Set unitree.sdkRoot in settings.');

        void vscode.window
            .showErrorMessage(
                'Unitree.Net repository not found. Open it as a folder, or set "unitree.sdkRoot".',
                'Open settings')
            .then((choice) => {
                if (choice === 'Open settings') {
                    void vscode.commands.executeCommand('workbench.action.openSettings', 'unitree.sdkRoot');
                }
            });
    }
}

/** A running streamed command. */
export class StreamHandle {
    constructor(
        private readonly child: ChildProcessWithoutNullStreams,
        private readonly closeReader: () => void) {}

    public dispose(): void {
        this.closeReader();

        if (!this.child.killed) {
            // The whole tree: `dotnet run` launches the application as a grandchild, and killing only
            // the direct child leaves it holding a multicast socket open.
            this.child.kill('SIGTERM');
        }
    }
}
