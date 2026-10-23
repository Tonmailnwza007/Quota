'use strict';
const path = require('node:path');
const fs = require('node:fs');
const { build } = require('./build.cjs');

(async () => {
  build();
  const { packager } = await import('@electron/packager');
  const desktop = path.resolve(__dirname, '..');
  const root = path.dirname(desktop);
  const { version, name, productName } = require('../package.json');
  const stage = path.resolve(root, 'artifacts/stage/bridge');
  if (stage !== path.join(root, 'artifacts', 'stage', 'bridge') || !stage.startsWith(root + path.sep)) throw new Error('Unexpected package staging directory.');
  fs.rmSync(stage, { recursive: true, force: true });
  fs.mkdirSync(stage, { recursive: true });
  fs.cpSync(path.join(root, 'bridge/publish'), stage, { recursive: true });
  const output = path.join(root, 'artifacts', version);
  const paths = await packager({ dir: desktop, out: output, name, executableName: name,
    platform: 'win32', arch: 'x64', electronVersion: require('electron/package.json').version,
    appVersion: version, icon: path.join(desktop, 'assets/app.ico'), asar: true,
    extraResource: [stage], overwrite: false,
    ignore: [/^\/node_modules($|\/)/, /^\/scripts($|\/)/, /^\/test($|\/)/, /^\/package-lock\.json$/, /^\/assets\/costats\.ico$/],
    win32metadata: { CompanyName: 'fmdz', ProductName: productName, FileDescription: 'AI usage tray monitor' },
  });
  for (const folder of paths) {
    for (const filename of ['README.md', 'LICENSE', 'CHECKPOINT.md', 'THIRD_PARTY_NOTICES.md']) {
      const source = path.join(root, filename);
      if (fs.existsSync(source)) fs.copyFileSync(source, path.join(folder, filename));
    }
  }
  console.log(JSON.stringify({ packages: paths }));
})().catch(error => { console.error(error.message); process.exitCode = 1; });
