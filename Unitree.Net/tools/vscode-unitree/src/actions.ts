import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { UnitreeCli } from './cli';

/**
 * Finds the robot project to act on.
 *
 * Prefers the project owning the active editor, so someone with several projects open acts on the
 * one they are looking at rather than on whichever was found first.
 */
export async function pickProject(): Promise<string | undefined> {
    const active = vscode.window.activeTextEditor?.document.uri.fsPath;

    if (active) {
        const owner = findProjectAbove(active);

        if (owner) {
            return owner;
        }
    }

    const found = await vscode.workspace.findFiles('**/*.csproj', '**/{bin,obj,node_modules}/**', 32);

    if (found.length === 0) {
        const create = await vscode.window.showErrorMessage(
            'No .csproj found in this workspace.', 'New robot project…');

        if (create) {
            await vscode.commands.executeCommand('unitree.newProject');
        }

        return undefined;
    }

    if (found.length === 1) {
        return found[0].fsPath;
    }

    const picked = await vscode.window.showQuickPick(
        found.map((uri) => ({
            label: path.basename(uri.fsPath),
            description: vscode.workspace.asRelativePath(uri),
            uri,
        })),
        { title: 'Which project?' });

    return picked?.uri.fsPath;
}

function findProjectAbove(startPath: string): string | undefined {
    let directory = path.dirname(startPath);

    for (let depth = 0; depth < 8; depth++) {
        const match = fs.existsSync(directory)
            ? fs.readdirSync(directory).find((entry) => entry.endsWith('.csproj'))
            : undefined;

        if (match) {
            return path.join(directory, match);
        }

        const parent = path.dirname(directory);

        if (parent === directory) {
            return undefined;
        }

        directory = parent;
    }

    return undefined;
}

/** Runs `dotnet build` as a VS Code task, so errors land in the Problems panel. */
export async function build(projectPath: string): Promise<void> {
    const task = new vscode.Task(
        { type: 'shell' },
        vscode.TaskScope.Workspace,
        `build ${path.basename(projectPath, '.csproj')}`,
        'unitree',
        new vscode.ShellExecution('dotnet', ['build', projectPath, '--nologo']),
        // The matcher is what turns compiler output into clickable diagnostics rather than text.
        '$unitree-dotnet');

    task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, panel: vscode.TaskPanelKind.Shared };
    await vscode.tasks.executeTask(task);
}

/**
 * Runs the project in a terminal, with the run target passed through the environment.
 *
 * The target is an environment variable rather than a rewrite of `appsettings.json`: editing a file
 * the operator can see, behind their back, is the kind of thing that makes a tool untrustworthy.
 */
export async function run(projectPath: string, cli: UnitreeCli): Promise<void> {
    const settings = vscode.workspace.getConfiguration('unitree');
    const target = settings.get<string>('runTarget') ?? 'Simulator';

    if (target === 'Robot') {
        const go = await vscode.window.showWarningMessage(
            'Run target is a real robot. Commands will reach hardware on the configured network.',
            { modal: true },
            'Run anyway');

        if (go !== 'Run anyway') {
            return;
        }
    }

    const terminal = vscode.window.createTerminal({
        name: `Unitree · ${path.basename(projectPath, '.csproj')}`,
        env: { UNITREE_RUN_TARGET: target },
        iconPath: new vscode.ThemeIcon('play'),
    });

    const args = cli.transportArgs().map((arg) => (arg.includes(' ') ? `"${arg}"` : arg)).join(' ');

    terminal.show();
    terminal.sendText(`dotnet run --project "${projectPath}" -- ${args}`);
}

/** Launches the .NET debugger against the project. */
export async function debug(projectPath: string): Promise<void> {
    const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(projectPath))
        ?? vscode.workspace.workspaceFolders?.[0];

    const settings = vscode.workspace.getConfiguration('unitree');
    const target = settings.get<string>('runTarget') ?? 'Simulator';

    if (!vscode.extensions.getExtension('ms-dotnettools.csharp')) {
        const install = await vscode.window.showErrorMessage(
            'Debugging .NET needs the C# extension.', 'Install C#');

        if (install) {
            await vscode.commands.executeCommand(
                'workbench.extensions.search', 'ms-dotnettools.csharp');
        }

        return;
    }

    const started = await vscode.debug.startDebugging(folder, {
        name: `Unitree: ${path.basename(projectPath, '.csproj')}`,
        type: 'coreclr',
        request: 'launch',
        // Building through the debugger's own pre-launch means a compile error stops the session
        // rather than launching a stale binary.
        preLaunchTask: undefined,
        program: 'dotnet',
        args: ['run', '--project', projectPath],
        cwd: path.dirname(projectPath),
        console: 'integratedTerminal',
        stopAtEntry: false,
        env: { UNITREE_RUN_TARGET: target, DOTNET_ENVIRONMENT: 'Development' },
    });

    if (!started) {
        void vscode.window.showErrorMessage('Could not start the debugger. See the Debug Console.');
    }
}

/** Publishes and copies the project to the robot over SSH. */
export async function deploy(
    projectPath: string,
    cli: UnitreeCli,
    log: vscode.LogOutputChannel): Promise<void> {

    const settings = vscode.workspace.getConfiguration('unitree');
    const host = settings.get<string>('deploy.host') ?? '';
    const user = settings.get<string>('deploy.user') ?? 'unitree';
    const key = settings.get<string>('deploy.privateKeyPath') ?? '';

    if (!host) {
        void vscode.window.showErrorMessage('Set "unitree.deploy.host" first.');
        return;
    }

    let password = '';

    if (!key) {
        // Asked for rather than stored: a robot password in a settings file gets copied between
        // machines and committed more often than anyone intends.
        password = await vscode.window.showInputBox({
            title: `Password for ${user}@${host}`,
            prompt: 'Not stored. Configure "unitree.deploy.privateKeyPath" to use a key instead.',
            password: true,
        }) ?? '';

        if (!password) {
            return;
        }
    }

    const confirmed = await vscode.window.showWarningMessage(
        `Deploy ${path.basename(projectPath, '.csproj')} to ${user}@${host}?`,
        { modal: true, detail: 'This publishes the project and copies it onto the robot. It has never been run against real hardware.' },
        'Deploy');

    if (confirmed !== 'Deploy') {
        return;
    }

    log.show(true);
    log.info(`Deploying to ${user}@${host}…`);

    const args = [
        'deploy',
        '--project', projectPath,
        '--host', host,
        '--port', String(settings.get<number>('deploy.port') ?? 22),
        '--user', user,
        '--remote', settings.get<string>('deploy.remoteDirectory') ?? '/home/unitree/apps',
    ];

    if (key) {
        args.push('--key', key);
    } else {
        args.push('--password', password);
    }

    if (settings.get<boolean>('deploy.installService')) {
        args.push('--service');
    }

    const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Deploying to ${host}…`, cancellable: false },
        () => cli.json<{ deployed: boolean; project: string; remote: string }>(args, 15 * 60_000));

    if (result?.deployed) {
        void vscode.window.showInformationMessage(`Deployed ${result.project} to ${host}:${result.remote}`);
    } else {
        void vscode.window.showErrorMessage('Deployment failed. See the Unitree log.');
    }
}

/** Runs the diagnostics command, which needs no robot. */
export function diagnose(cli: UnitreeCli): void {
    const resolved = cli.resolve(['diagnose', ...cli.transportArgs()]);

    if (!resolved) {
        return;
    }

    const terminal = vscode.window.createTerminal({
        name: 'Unitree · diagnose',
        iconPath: new vscode.ThemeIcon('pulse'),
    });

    terminal.show();
    terminal.sendText(`${resolved.command} ${resolved.args.map((a) => (a.includes(' ') ? `"${a}"` : a)).join(' ')}`);
}
