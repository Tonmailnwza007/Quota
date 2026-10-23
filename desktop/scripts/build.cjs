'use strict';
const path = require('node:path');
const fs = require('node:fs');
const { spawnSync } = require('node:child_process');
const desktop = path.resolve(__dirname, '..');
const root = path.dirname(desktop);

function run(command, args) {
  const result = spawnSync(command, args, { cwd: root, stdio: 'inherit', windowsHide: true,
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  if (result.error || result.status !== 0) throw new Error(`Build command failed: ${path.basename(command)}`);
}

function build() {
  const npmCli = process.env.npm_execpath || path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js');
  if (!fs.existsSync(npmCli)) throw new Error('Run npm run build from the desktop folder.');
  run(process.execPath, [npmCli, '--prefix', path.join(root, 'web'), 'run', 'build']);
  const publish = path.resolve(root, 'bridge/publish');
  if (publish !== path.join(root, 'bridge', 'publish') || !publish.startsWith(root + path.sep)) throw new Error('Unexpected publish directory.');
  fs.rmSync(publish, { recursive: true, force: true });
  run(process.env.DOTNET_PATH || 'dotnet', ['publish', path.join(root, 'bridge/Costats.Bridge.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '-p:RuntimeIdentifiers=win-x64', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-o', publish]);
  const renderer = path.resolve(desktop, 'renderer');
  if (renderer !== path.join(desktop, 'renderer') || !renderer.startsWith(root + path.sep)) throw new Error('Unexpected renderer directory.');
  fs.rmSync(renderer, { recursive: true, force: true });
  fs.cpSync(path.join(root, 'web/dist'), renderer, { recursive: true });
  if (!fs.existsSync(path.join(desktop, 'assets/app.ico'))) throw new Error('Application icon is missing.');
}

module.exports = { build };
if (require.main === module) { try { build(); } catch (error) { console.error(error.message); process.exitCode = 1; } }
