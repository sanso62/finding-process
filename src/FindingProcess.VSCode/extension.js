'use strict';

const vscode = require('vscode');
const fs = require('node:fs');
const path = require('node:path');

// This extension only creates a normal VS Code terminal. It never reads, copies,
// or forwards the target's input/output and does not own a pseudoterminal relay.
async function activate() {
  const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
  if (!folder || folder.scheme !== 'file' || path.basename(folder.fsPath) !== 'vscode-workspace') return;
  const directory = path.dirname(folder.fsPath);
  try {
    const request = JSON.parse(fs.readFileSync(path.join(folder.fsPath, 'handoff.json'), 'utf8'));
    const script = path.join(directory, 'vscode-shell.ps1');
    if (request.Version !== 1 || !request.Title?.startsWith('FP-lab-') ||
        typeof request.Script !== 'string' || path.resolve(request.Script).toLowerCase() !== script.toLowerCase() ||
        typeof request.Shell !== 'string' || path.basename(request.Shell).toLowerCase() !== 'powershell.exe') {
      throw new Error('Invalid Finding Process terminal request.');
    }
    const terminal = vscode.window.createTerminal({
      name: request.Title,
      shellPath: request.Shell,
      shellArgs: ['-NoLogo', '-NoProfile', '-NoExit', '-File', script],
      cwd: directory,
      location: vscode.TerminalLocation.Editor
    });
    terminal.show();
    fs.writeFileSync(path.join(directory, 'vscode-terminal.json'), JSON.stringify({
      ShellPid: await terminal.processId, ExtensionHostPid: process.pid, VSCodeVersion: vscode.version,
      UsesRelay: false
    }, null, 2));
  } catch (error) {
    fs.writeFileSync(path.join(directory, 'vscode-error.json'), JSON.stringify({ Error: String(error) }));
    vscode.window.showErrorMessage(`Finding Process: ${error.message}`);
  }
}

module.exports = { activate };
