import * as path from 'node:path';
import * as vscode from 'vscode';
import * as actions from './actions';
import { UnitreeCli } from './cli';
import { RobotStatusProvider } from './status';
import { TemplateProvider, newProject } from './templates';
import { TemplateInfo } from './model';

let simulator: vscode.Terminal | undefined;

export function activate(context: vscode.ExtensionContext): void {
    // A LogOutputChannel rather than a plain one: it gives the operator level filtering and
    // timestamps for free, which is most of what a log panel is for.
    const log = vscode.window.createOutputChannel('Unitree', { log: true });
    const cli = new UnitreeCli(log);
    const status = new RobotStatusProvider(cli, log);
    const templates = new TemplateProvider(cli);

    context.subscriptions.push(log, status);

    context.subscriptions.push(
        vscode.window.createTreeView('unitree.status', { treeDataProvider: status }),
        vscode.window.createTreeView('unitree.templates', { treeDataProvider: templates }));

    const root = cli.findSdkRoot();

    log.info(root
        ? `Unitree.Net repository: ${root}`
        : 'Unitree.Net repository not found. Set "unitree.sdkRoot" to enable project and robot commands.');

    const register = (id: string, handler: (...args: never[]) => unknown) =>
        context.subscriptions.push(vscode.commands.registerCommand(id, handler));

    register('unitree.newProject', (template?: TemplateInfo) =>
        newProject(cli, templates, log, template));

    register('unitree.connect', () => status.connect());
    register('unitree.disconnect', () => status.disconnect());
    register('unitree.refreshStatus', () => { templates.refresh(); status.refresh(); });
    register('unitree.openLogs', () => log.show(true));
    register('unitree.diagnose', () => actions.diagnose(cli));

    register('unitree.setTarget', async () => {
        const picked = await vscode.window.showQuickPick(
            [
                { label: 'Simulator', detail: 'Commands reach the simulator on the local multicast group' },
                { label: 'Robot', detail: 'Commands reach real hardware on the configured network' },
            ],
            { title: 'Where should Run send commands?' });

        if (!picked) {
            return;
        }

        await vscode.workspace.getConfiguration('unitree')
            .update('runTarget', picked.label, vscode.ConfigurationTarget.Workspace);

        log.info(`Run target is now ${picked.label}.`);
        status.refresh();
    });

    register('unitree.startSimulator', async () => {
        if (simulator) {
            simulator.show();
            return;
        }

        const sdkRoot = cli.findSdkRoot();

        if (!sdkRoot) {
            void vscode.window.showErrorMessage('Set "unitree.sdkRoot" to start the simulator.');
            return;
        }

        // The headless sample rather than the WPF simulator: it runs anywhere, and this command is
        // about getting telemetry flowing rather than about the 3D view.
        const project = path.join(sdkRoot, 'samples', 'Unitree.Net.Samples.VirtualRobot');

        simulator = vscode.window.createTerminal({
            name: 'Unitree · simulator',
            iconPath: new vscode.ThemeIcon('play-circle'),
        });

        const settings = vscode.workspace.getConfiguration('unitree');

        simulator.show();
        simulator.sendText(
            `dotnet run --project "${project}" -- ` +
            `--group ${settings.get<string>('multicastAddress') ?? '239.255.0.1'} ` +
            `--port ${settings.get<number>('multicastPort') ?? 7447}`);

        await vscode.commands.executeCommand('setContext', 'unitree.simulatorRunning', true);
        log.info('Simulator starting. Connect once it reports that it is publishing.');

        // Give it a moment to bind the socket before the status stream starts looking for it.
        setTimeout(() => status.connect(), 4000);
    });

    register('unitree.stopSimulator', async () => {
        simulator?.dispose();
        simulator = undefined;
        await vscode.commands.executeCommand('setContext', 'unitree.simulatorRunning', false);
        log.info('Simulator stopped.');
    });

    register('unitree.openWizard', async () => {
        const sdkRoot = cli.findSdkRoot();

        if (!sdkRoot) {
            void vscode.window.showErrorMessage('Set "unitree.sdkRoot" to open the wizard.');
            return;
        }

        const terminal = vscode.window.createTerminal({ name: 'Unitree · wizard', hideFromUser: false });
        terminal.sendText(`dotnet run --project "${path.join(sdkRoot, 'apps', 'Unitree.Net.Wizard')}"`);
        log.info('Opening the Robot Wizard. It is a Windows desktop application.');
    });

    register('unitree.build', async () => {
        const project = await actions.pickProject();

        if (project) {
            await actions.build(project);
        }
    });

    register('unitree.run', async () => {
        const project = await actions.pickProject();

        if (project) {
            await actions.run(project, cli);
        }
    });

    register('unitree.debug', async () => {
        const project = await actions.pickProject();

        if (project) {
            await actions.debug(project);
        }
    });

    register('unitree.deploy', async () => {
        const project = await actions.pickProject();

        if (project) {
            await actions.deploy(project, cli, log);
        }
    });

    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration('unitree')) {
            // Transport settings only take effect on the next stream, so a change has to restart it —
            // otherwise editing the multicast group appears to do nothing.
            status.refresh();
        }
    }));

    context.subscriptions.push(vscode.window.onDidCloseTerminal((closed) => {
        if (closed === simulator) {
            simulator = undefined;
            void vscode.commands.executeCommand('setContext', 'unitree.simulatorRunning', false);
        }
    }));

    void vscode.commands.executeCommand('setContext', 'unitree.connected', false);
    void vscode.commands.executeCommand('setContext', 'unitree.simulatorRunning', false);
}

export function deactivate(): void {
    simulator?.dispose();
    simulator = undefined;
}
