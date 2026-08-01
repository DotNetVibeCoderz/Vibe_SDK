import * as path from 'node:path';
import * as vscode from 'vscode';
import { UnitreeCli } from './cli';
import { CreatedProject, TemplateInfo } from './model';

/** A template, or the group heading above one. */
class TemplateNode extends vscode.TreeItem {
    constructor(
        label: string,
        public readonly template?: TemplateInfo,
        collapsible = vscode.TreeItemCollapsibleState.None) {
        super(label, collapsible);

        if (template) {
            this.description = template.summary;
            this.tooltip = new vscode.MarkdownString(
                `**${template.name}**\n\n${template.summary}\n\n` +
                `Kind: \`${template.kind}\`\n\nTags: ${template.tags.map((t) => `\`${t}\``).join(' ')}`);

            this.iconPath = new vscode.ThemeIcon('file-code');
            this.command = {
                command: 'unitree.newProject',
                title: 'New project from this template',
                arguments: [template],
            };
        } else {
            this.iconPath = new vscode.ThemeIcon('folder-library');
        }
    }
}

/** The template gallery, grouped by project kind. */
export class TemplateProvider implements vscode.TreeDataProvider<TemplateNode> {
    private readonly changed = new vscode.EventEmitter<TemplateNode | undefined>();
    private templates: TemplateInfo[] | undefined;

    public readonly onDidChangeTreeData = this.changed.event;

    constructor(private readonly cli: UnitreeCli) {}

    public getTreeItem(element: TemplateNode): vscode.TreeItem {
        return element;
    }

    public async getChildren(element?: TemplateNode): Promise<TemplateNode[]> {
        this.templates ??= await this.cli.json<TemplateInfo[]>(['templates']) ?? [];

        if (this.templates.length === 0) {
            return [];
        }

        if (!element) {
            const kinds = [...new Set(this.templates.map((template) => template.kind))];

            return kinds.map((kind) => new TemplateNode(
                kind, undefined, vscode.TreeItemCollapsibleState.Expanded));
        }

        return this.templates
            .filter((template) => template.kind === element.label)
            .map((template) => new TemplateNode(template.name, template));
    }

    public refresh(): void {
        this.templates = undefined;
        this.changed.fire(undefined);
    }

    /** Every template, fetched once and cached. */
    public async all(): Promise<TemplateInfo[]> {
        this.templates ??= await this.cli.json<TemplateInfo[]>(['templates']) ?? [];
        return this.templates;
    }
}

/**
 * Walks the operator through creating a project, then opens it.
 *
 * @param preselected A template chosen from the gallery, if any.
 */
export async function newProject(
    cli: UnitreeCli,
    provider: TemplateProvider,
    log: vscode.LogOutputChannel,
    preselected?: TemplateInfo): Promise<void> {

    let template = preselected;

    if (!template) {
        const templates = await provider.all();

        if (templates.length === 0) {
            void vscode.window.showErrorMessage(
                'Could not read the template catalogue. Check that the Unitree.Net repository is reachable.');
            return;
        }

        // A blank project is offered first, because someone who knows what they want should not have
        // to scroll past sixteen templates to decline them.
        const blank = { label: '$(file) Blank project', description: 'A minimal application that connects and prints a snapshot' };

        const picked = await vscode.window.showQuickPick(
            [blank, ...templates.map((item) => ({
                label: item.name,
                description: `${item.kind} · ${item.tags.join(', ')}`,
                detail: item.summary,
                template: item,
            }))],
            { title: 'New robot project', placeHolder: 'Every template runs against the simulator without edits', matchOnDetail: true });

        if (!picked) {
            return;
        }

        template = 'template' in picked ? picked.template as TemplateInfo : undefined;
    }

    let kind = template?.kind ?? 'Console';

    if (!template) {
        const pickedKind = await vscode.window.showQuickPick(
            [
                { label: 'Console', detail: 'Runs on your machine, talking to the robot over the network' },
                { label: 'Desktop', detail: 'A windowed operator console' },
                { label: 'Web', detail: 'ASP.NET Core — a dashboard or an HTTP API' },
                { label: 'Embedded', detail: "Published for the robot's ARM64 module and deployed over SSH" },
            ],
            { title: 'Project kind' });

        if (!pickedKind) {
            return;
        }

        kind = pickedKind.label as TemplateInfo['kind'];
    }

    const name = await vscode.window.showInputBox({
        title: 'Project name',
        value: template ? template.name.replace(/[^A-Za-z0-9]/g, '') : 'RobotApp',
        validateInput: (value) => {
            if (!value.trim()) {
                return 'A name is required.';
            }
            if (/[\\/:*?"<>|]/.test(value)) {
                return 'A project name cannot contain path characters.';
            }
            // A leading digit produces a namespace the compiler rejects, and that error arrives much
            // later and much less clearly than this one.
            if (/^\d/.test(value)) {
                return 'A project name cannot start with a digit.';
            }
            return undefined;
        },
    });

    if (!name) {
        return;
    }

    const defaultParent = vscode.workspace.workspaceFolders?.[0]?.uri;

    const chosen = await vscode.window.showOpenDialog({
        title: 'Where should the project be created?',
        openLabel: 'Create here',
        canSelectFolders: true,
        canSelectFiles: false,
        canSelectMany: false,
        defaultUri: defaultParent,
    });

    if (!chosen || chosen.length === 0) {
        return;
    }

    const output = chosen[0].fsPath;

    const created = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Creating ${name}…` },
        () => cli.json<CreatedProject>([
            'new',
            '--name', name,
            '--output', output,
            ...(template ? ['--template', template.id] : ['--kind', kind]),
        ]));

    if (!created) {
        void vscode.window.showErrorMessage(`Could not create ${name}. See the Unitree log for the reason.`);
        return;
    }

    log.info(`Created ${created.name} (${created.kind}) at ${created.rootPath}`);

    const open = await vscode.window.showInformationMessage(
        `Created ${created.name}. It runs against the simulator without any edits.`,
        'Open folder', 'Add to workspace', 'Open Program.cs');

    const uri = vscode.Uri.file(created.rootPath);

    if (open === 'Open folder') {
        await vscode.commands.executeCommand('vscode.openFolder', uri, { forceNewWindow: false });
    } else if (open === 'Add to workspace') {
        vscode.workspace.updateWorkspaceFolders(vscode.workspace.workspaceFolders?.length ?? 0, 0, { uri });
    } else if (open === 'Open Program.cs') {
        const document = await vscode.workspace.openTextDocument(
            vscode.Uri.file(path.join(created.rootPath, 'Program.cs')));

        await vscode.window.showTextDocument(document);
    }
}
