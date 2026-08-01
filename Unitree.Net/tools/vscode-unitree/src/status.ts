import * as vscode from 'vscode';
import { StreamHandle, UnitreeCli } from './cli';
import { RobotSnapshot, RobotTelemetry } from './model';

/** A row in the robot status panel. */
class StatusItem extends vscode.TreeItem {
    constructor(label: string, value: string, icon?: string, tone?: vscode.ThemeColor) {
        super(label, vscode.TreeItemCollapsibleState.None);
        this.description = value;

        if (icon) {
            this.iconPath = new vscode.ThemeIcon(icon, tone);
        }
    }
}

/**
 * Live robot status, backed by a `unitree stream` child process.
 *
 * The stream is started on connect and stopped on disconnect, so nothing holds a multicast socket
 * open while the panel is idle.
 */
export class RobotStatusProvider implements vscode.TreeDataProvider<StatusItem>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<StatusItem | undefined>();
    private readonly statusBar: vscode.StatusBarItem;

    private stream: StreamHandle | undefined;
    private snapshot: RobotSnapshot | undefined;
    private connecting = false;

    public readonly onDidChangeTreeData = this.changed.event;

    constructor(
        private readonly cli: UnitreeCli,
        private readonly log: vscode.LogOutputChannel) {
        this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
        this.statusBar.command = 'unitree.connect';
        this.refreshStatusBar();
        this.statusBar.show();
    }

    public get isConnected(): boolean {
        return this.snapshot?.connected === true;
    }

    public getTreeItem(element: StatusItem): vscode.TreeItem {
        return element;
    }

    public getChildren(): StatusItem[] {
        if (this.connecting) {
            return [new StatusItem('Connecting', '…', 'loading~spin')];
        }

        const snapshot = this.snapshot;

        if (!snapshot) {
            // The welcome view covers this case, so returning nothing is what makes it show.
            return [];
        }

        const rows: StatusItem[] = [
            new StatusItem(
                'Connection',
                snapshot.connected ? (snapshot.state ?? 'Connected') : (snapshot.error ?? 'Disconnected'),
                snapshot.connected ? 'pass-filled' : 'error',
                new vscode.ThemeColor(snapshot.connected ? 'testing.iconPassed' : 'testing.iconFailed')),
            new StatusItem('Model', snapshot.model, 'circuit-board'),
        ];

        if (snapshot.transport) {
            rows.push(new StatusItem('Transport', `${snapshot.transport} · ${snapshot.endpoint ?? ''}`, 'radio-tower'));
        }

        const telemetry = snapshot.telemetry;

        if (!telemetry) {
            rows.push(new StatusItem('Telemetry', 'waiting for the first message', 'watch'));
            return rows;
        }

        rows.push(...this.telemetryRows(telemetry));

        if (snapshot.lowStateCount !== undefined) {
            rows.push(new StatusItem(
                'Messages',
                `${snapshot.lowStateCount.toLocaleString()} low · ${(snapshot.sportStateCount ?? 0).toLocaleString()} sport`,
                'pulse'));
        }

        return rows;
    }

    private telemetryRows(telemetry: RobotTelemetry): StatusItem[] {
        // Thresholds match the SDK's own guidance: 20% is where motion should be reconsidered, 10%
        // where it should stop.
        const batteryTone = telemetry.batteryPercent < 10
            ? 'charts.red'
            : telemetry.batteryPercent < 20 ? 'charts.yellow' : 'charts.green';

        const temperatureTone = telemetry.maxMotorTemperatureCelsius >= 80
            ? 'charts.red'
            : telemetry.maxMotorTemperatureCelsius >= 65 ? 'charts.yellow' : 'charts.green';

        const rows = [
            new StatusItem(
                'Battery',
                `${telemetry.batteryPercent}%` +
                (telemetry.estimatedMinutesRemaining
                    ? ` · ~${Math.round(telemetry.estimatedMinutesRemaining)} min`
                    : ''),
                'zap',
                new vscode.ThemeColor(batteryTone)),
            new StatusItem('Pack', `${telemetry.packVoltage.toFixed(1)} V · ${telemetry.currentAmps.toFixed(1)} A`, 'plug'),
            new StatusItem(
                'Hottest motor',
                `${telemetry.maxMotorTemperatureCelsius} °C`,
                'flame',
                new vscode.ThemeColor(temperatureTone)),
            new StatusItem(
                'Ground contact',
                telemetry.isAirborne ? 'airborne' : `${telemetry.feetLoaded} loaded`,
                telemetry.isAirborne ? 'warning' : 'check',
                telemetry.isAirborne ? new vscode.ThemeColor('charts.yellow') : undefined),
            new StatusItem('Speed', `${telemetry.speed.toFixed(2)} m/s`, 'dashboard'),
            new StatusItem('Body height', `${telemetry.bodyHeight.toFixed(3)} m`, 'arrow-both'),
            new StatusItem(
                'Pose',
                `roll ${telemetry.rollDegrees.toFixed(1)}° · pitch ${telemetry.pitchDegrees.toFixed(1)}° · yaw ${telemetry.yawDegrees.toFixed(1)}°`,
                'compass'),
            new StatusItem(
                'Odometry',
                `${telemetry.odometryX.toFixed(2)}, ${telemetry.odometryY.toFixed(2)} m`,
                'location'),
        ];

        if (telemetry.cellImbalanceMillivolts > 50) {
            rows.push(new StatusItem(
                'Cell imbalance',
                `${telemetry.cellImbalanceMillivolts} mV — worth a look`,
                'warning',
                new vscode.ThemeColor('charts.yellow')));
        }

        return rows;
    }

    /** Starts the telemetry stream. */
    public connect(): void {
        if (this.stream) {
            return;
        }

        const interval = vscode.workspace.getConfiguration('unitree').get<number>('pollIntervalMs') ?? 500;

        this.connecting = true;
        this.snapshot = undefined;
        this.changed.fire(undefined);
        this.refreshStatusBar();

        this.log.info('Connecting…');

        this.stream = this.cli.stream<RobotSnapshot>(
            ['stream', '--interval', String(interval), ...this.cli.transportArgs()],
            (message) => {
                const wasConnected = this.snapshot?.connected;
                this.connecting = false;
                this.snapshot = message;

                if (wasConnected !== message.connected) {
                    this.log.info(message.connected
                        ? `Connected to ${message.model} on ${message.endpoint}.`
                        : `Not connected: ${message.error ?? 'no telemetry'}`);
                }

                void vscode.commands.executeCommand('setContext', 'unitree.connected', message.connected);
                this.changed.fire(undefined);
                this.refreshStatusBar();
            });

        if (!this.stream) {
            this.connecting = false;
            this.changed.fire(undefined);
            this.refreshStatusBar();
        }
    }

    /** Stops the telemetry stream and clears the panel. */
    public disconnect(): void {
        this.stream?.dispose();
        this.stream = undefined;
        this.connecting = false;
        this.snapshot = undefined;

        void vscode.commands.executeCommand('setContext', 'unitree.connected', false);
        this.log.info('Disconnected.');
        this.changed.fire(undefined);
        this.refreshStatusBar();
    }

    /** Reconnects, picking up any changed transport settings. */
    public refresh(): void {
        if (this.stream) {
            this.disconnect();
            this.connect();
        } else {
            this.changed.fire(undefined);
        }
    }

    private refreshStatusBar(): void {
        const target = vscode.workspace.getConfiguration('unitree').get<string>('runTarget') ?? 'Simulator';
        const model = this.snapshot?.model ?? vscode.workspace.getConfiguration('unitree').get<string>('model');

        if (this.connecting) {
            this.statusBar.text = '$(loading~spin) Unitree: connecting';
            this.statusBar.backgroundColor = undefined;
        } else if (this.isConnected) {
            const battery = this.snapshot?.telemetry?.batteryPercent;
            this.statusBar.text = `$(pass-filled) ${model}` + (battery !== undefined ? ` · ${battery}%` : '');
            this.statusBar.backgroundColor = undefined;
        } else {
            this.statusBar.text = `$(debug-disconnect) Unitree: ${model}`;
            this.statusBar.backgroundColor = undefined;
        }

        // Targeting a real robot is worth a permanent warning colour. It is the difference between a
        // mistake costing nothing and a mistake moving fifteen kilograms of machine.
        if (target === 'Robot') {
            this.statusBar.text += ' $(alert) REAL ROBOT';
            this.statusBar.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
        }

        this.statusBar.tooltip = new vscode.MarkdownString(
            `**Unitree.Net**\n\n` +
            `Run target: \`${target}\`\n\n` +
            (this.snapshot?.endpoint ? `Endpoint: \`${this.snapshot.endpoint}\`\n\n` : '') +
            `Click to connect, or run **Unitree: Set Run Target**.`);
    }

    public dispose(): void {
        this.stream?.dispose();
        this.statusBar.dispose();
        this.changed.dispose();
    }
}
