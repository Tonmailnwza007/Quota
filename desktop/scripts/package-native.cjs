'use strict';
const fs = require('node:fs');
const path = require('node:path');
const {spawnSync} = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const {name,version} = require('../package.json');
const output = path.join(root, 'artifacts', version, `${name}-win32-x64`);
if (fs.existsSync(output)) throw new Error('This release already exists. Use a new version.');
const npmCli = process.env.npm_execpath || path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js');
function run(command,args) {
  const result = spawnSync(command,args,{cwd:root,stdio:'inherit',windowsHide:true,env:{...process.env,DOTNET_CLI_TELEMETRY_OPTOUT:'1',DOTNET_NOLOGO:'1'}});
  if (result.error || result.status !== 0) throw new Error(`Native build failed: ${path.basename(command)}`);
}
run(process.execPath,[npmCli,'--prefix',path.join(root,'web'),'run','build']);
const publish = path.resolve(root, 'native/publish');
if (publish !== path.join(root,'native','publish') || !publish.startsWith(root + path.sep)) throw new Error('Unexpected native publish target.');
fs.rmSync(publish,{recursive:true,force:true});
run(process.env.DOTNET_PATH || 'dotnet',['publish',path.join(root,'native/Quota.Native.csproj'),'-c','Release','-r','win-x64','--self-contained','true',
  '-p:RuntimeIdentifiers=win-x64','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true',
  // Keep release binaries free of developer source paths and embedded debug symbols.
  '-p:DebugType=None','-p:DebugSymbols=false',`-p:PathMap=${root}=/_/quota`,'-o',publish]);
if (!fs.existsSync(path.join(publish, `${name}.exe`))) throw new Error('Native executable was not produced.');
fs.mkdirSync(output,{recursive:true});
fs.cpSync(publish,output,{recursive:true});
// The native tray reader loads this adjacent file; single-file publish may embed it.
fs.copyFileSync(path.join(root,'native','app.ico'),path.join(output,'app.ico'));
for (const file of ['README.md','CHECKPOINT.md','RELEASE_NOTES.md','LICENSE','THIRD_PARTY_NOTICES.md']) fs.copyFileSync(path.join(root,file),path.join(output,file));
const nugetRoot = process.env.NUGET_PACKAGES || path.join(require('node:os').homedir(), '.nuget', 'packages');
const webview = path.join(nugetRoot,'microsoft.web.webview2','1.0.4258.31');
fs.mkdirSync(path.join(output,'licenses'),{recursive:true});
for (const file of ['LICENSE.txt','NOTICE.txt']) fs.copyFileSync(path.join(webview,file),path.join(output,'licenses',`WebView2-${file}`));
console.log(JSON.stringify({nativePackage:output}));
