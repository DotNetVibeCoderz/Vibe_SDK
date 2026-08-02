// Extension DepthAI.Net untuk VS Code.
//
// Ditulis dalam JavaScript polos, bukan TypeScript, supaya bisa dipasang langsung dari
// folder ini tanpa langkah kompilasi — extension ini hanya membungkus CLI, jadi
// menambahkan toolchain build hanya akan menambah hambatan tanpa manfaat.

const vscode = require('vscode');
const cp = require('child_process');

/** Terminal bersama, supaya perintah berturut-turut tidak menumpuk terminal baru. */
let terminal = null;

function getConfig() {
  const config = vscode.workspace.getConfiguration('depthai');
  return {
    cli: config.get('cliPath', 'depthai-dotnet-cli'),
    simulate: config.get('simulate', false),
  };
}

function getTerminal() {
  if (!terminal || terminal.exitStatus !== undefined) {
    terminal = vscode.window.createTerminal('DepthAI');
  }
  return terminal;
}

/** Menjalankan perintah CLI di terminal agar keluarannya terlihat dan bisa dihentikan. */
function runInTerminal(args) {
  const { cli, simulate } = getConfig();
  const flags = simulate ? ' --simulate' : '';

  const term = getTerminal();
  term.show(true);
  term.sendText(`${cli} ${args}${flags}`);
}

/** Menjalankan perintah CLI dan mengembalikan keluarannya sebagai string. */
function runCapture(args) {
  const { cli, simulate } = getConfig();
  const flags = simulate ? ' --simulate' : '';

  return new Promise((resolve, reject) => {
    cp.exec(`${cli} ${args}${flags}`, { timeout: 30000 }, (error, stdout, stderr) => {
      if (error && !stdout) {
        reject(new Error(stderr || error.message));
        return;
      }
      resolve(stdout || stderr);
    });
  });
}

function activePipelinePath() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || !editor.document.fileName.endsWith('.pipeline.json')) {
    vscode.window.showWarningMessage('Buka berkas .pipeline.json dulu.');
    return null;
  }
  return editor.document.fileName;
}

function activate(context) {
  const output = vscode.window.createOutputChannel('DepthAI');

  context.subscriptions.push(
    vscode.commands.registerCommand('depthai.listDevices', async () => {
      try {
        const result = await runCapture('devices list');
        output.clear();
        output.appendLine(result);
        output.show(true);
      } catch (error) {
        // Penyebab paling sering adalah CLI belum terpasang, jadi sarankan langkahnya
        // alih-alih hanya menampilkan pesan error mentah.
        vscode.window.showErrorMessage(
          `Tidak bisa menjalankan CLI DepthAI: ${error.message}. ` +
          'Pasang dengan: dotnet tool install -g DepthAI.Net.Cli'
        );
      }
    }),

    vscode.commands.registerCommand('depthai.validatePipeline', async () => {
      const path = activePipelinePath();
      if (!path) return;

      await vscode.window.activeTextEditor.document.save();

      try {
        const result = await runCapture(`pipeline validate "${path}"`);
        output.clear();
        output.appendLine(result);
        output.show(true);
      } catch (error) {
        vscode.window.showErrorMessage(`Validasi gagal: ${error.message}`);
      }
    }),

    vscode.commands.registerCommand('depthai.deployPipeline', async () => {
      const path = activePipelinePath();
      if (!path) return;

      await vscode.window.activeTextEditor.document.save();

      const duration = await vscode.window.showInputBox({
        prompt: 'Berapa detik pipeline dijalankan? (0 = sampai dihentikan)',
        value: '10',
        validateInput: (v) => (/^\d+$/.test(v) ? null : 'Masukkan angka.'),
      });

      if (duration === undefined) return;

      runInTerminal(`pipeline deploy "${path}" --duration ${duration}`);
    }),

    vscode.commands.registerCommand('depthai.newPipeline', async () => {
      const presets = [
        { label: 'rgb-preview', detail: 'Preview kamera warna saja' },
        { label: 'stereo-depth', detail: 'Stereo depth plus preview warna' },
        { label: 'object-detection', detail: 'Deteksi objek 2D' },
        { label: 'spatial-detection', detail: 'Deteksi objek dengan koordinat 3D' },
        { label: 'record-rgbd', detail: 'Rekam RGB terkompresi bersama kedalaman' },
        { label: 'imu-stream', detail: 'Aliran gerak dari IMU' },
      ];

      const choice = await vscode.window.showQuickPick(presets, {
        placeHolder: 'Pilih preset pipeline',
      });

      if (!choice) return;

      const folders = vscode.workspace.workspaceFolders;
      if (!folders || folders.length === 0) {
        vscode.window.showWarningMessage('Buka folder workspace dulu.');
        return;
      }

      const target = vscode.Uri.joinPath(folders[0].uri, `${choice.label}.pipeline.json`).fsPath;
      runInTerminal(`pipeline new ${choice.label} -o "${target}"`);
    })
  );

  context.subscriptions.push(output);
}

function deactivate() {
  if (terminal) {
    terminal.dispose();
    terminal = null;
  }
}

module.exports = { activate, deactivate };
