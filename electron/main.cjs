// Electron main process: sobe API (.NET) + Worker (.NET) + UI numa janela.
//
// Dois modos:
//   • Dev (npm run dev) — spawn `dotnet run` direto do source, Vite serve a UI.
//   • Packaged (electron-builder) — spawn os .exe self-contained de extraResources;
//     UI vem de um http server local que proxia /api pro API e serve frontend-dist
//     com SPA fallback (BrowserRouter quebra em file://).
//
// Cleanup: tree-kill em todos os filhos no before-quit (necessário no Windows;
// child.kill() puro não derruba a árvore do dotnet).
const { app, BrowserWindow, dialog, shell } = require('electron');
const { spawn } = require('node:child_process');
const path = require('node:path');
const http = require('node:http');
const fs = require('node:fs');
const crypto = require('node:crypto');
const treeKill = require('tree-kill');

// ----------------------------------------------------------------------------
// Logging
// ----------------------------------------------------------------------------
// No Windows packaged (GUI subsystem), process.stdout/stderr não vão pra lugar
// nenhum. Forçamos um arquivo em userData pra que stdout dos filhos (.NET) +
// console.log do main fiquem acessíveis pra debug pós-mortem.
const LOG_PATH = path.join(app.getPath('userData'), 'electron.log');
try { fs.mkdirSync(path.dirname(LOG_PATH), { recursive: true }); } catch { /* ignore */ }
const logStream = fs.createWriteStream(LOG_PATH, { flags: 'a' });
function log(...args) {
  const line = `[${new Date().toISOString()}] ${args.map(String).join(' ')}\n`;
  logStream.write(line);
  process.stdout.write(line);
}
console.log = log;
console.error = log;
log(`=== boot pid=${process.pid} packaged=${app.isPackaged} log=${LOG_PATH}`);

const REPO_ROOT = path.resolve(__dirname, '..');
const IS_PACKAGED = app.isPackaged;
const DEV_MODE = !IS_PACKAGED && process.env.DBSENSE_MODE !== 'prod';

const API_PORT = Number(process.env.DBSENSE_API_PORT || 5000);
const VITE_PORT = Number(process.env.DBSENSE_VITE_PORT || 5173);
const STATIC_PORT = Number(process.env.DBSENSE_STATIC_PORT || 5180);
const HEALTH_TIMEOUT_MS = Number(process.env.DBSENSE_HEALTH_TIMEOUT_MS || 60_000);

// Em packaged, configs gravam em userData (instalação fica read-only quando o
// MSI vai pra Program Files). Em dev, mantemos o runtime-config junto do API.
const USER_DATA = app.getPath('userData');
const PACKAGED_RESOURCES = IS_PACKAGED ? process.resourcesPath : null;

const children = [];
let isQuitting = false;
let staticServer = null;

// ----------------------------------------------------------------------------
// Config / secrets
// ----------------------------------------------------------------------------

// Em packaged: gera secrets fortes na primeira execução e persiste em userData
// (per-user no Windows; ACL default já restringe a leitura ao próprio usuário).
// Em dev: usa os mesmos defaults do docker-compose pra não quebrar workflows.
// Hash curto pra log: identifica unicamente uma chave (mesmo prefixo = mesma chave)
// sem expor o valor. Permite cruzar logs de Electron/API/Worker e ver se "fonte
// X" é a mesma "fonte Y" depois.
function fingerprint(s) {
  if (!s) return 'EMPTY';
  return crypto.createHash('sha256').update(s).digest('hex').slice(0, 8);
}

function pickSecret(envKey, fallback, fallbackSource) {
  const fromEnv = process.env[envKey];
  if (fromEnv) return { value: fromEnv, source: `env:${envKey}`, fp: fingerprint(fromEnv) };
  return { value: fallback, source: fallbackSource, fp: fingerprint(fallback) };
}

function logConfigSources(picks) {
  for (const [name, p] of Object.entries(picks)) {
    console.log(`[electron] config ${name}: source=${p.source} fp=${p.fp}`);
  }
}

function loadConfig() {
  if (DEV_MODE) {
    const picks = {
      controlDb: pickSecret('ConnectionStrings__ControlDb',
        'Data Source=DESKTOP-I66H2NG\\SQLEXPRESS;Initial Catalog=dbsense_control_2;Integrated Security=True;Persist Security Info=False;Pooling=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Command Timeout=0',
        'dev-default'),
      encryptionKey: pickSecret('Security__EncryptionKey',
        '8n6kqfZL6lW/1uY2kH3x+dYQHc3t7cZ9mVkR8wT5x1c=',
        'dev-default'),
      jwtSecret: pickSecret('Security__JwtSecret',
        'ZGV2LWp3dC1zZWNyZXQtZG8tbm90LXVzZS1pbi1wcm9kdWN0aW9uLXBsZWFzZS0xMjM0NTY3ODkwYWJjZGVmZ2hpams=',
        'dev-default'),
    };
    logConfigSources(picks);
    return {
      controlDb: picks.controlDb.value,
      encryptionKey: picks.encryptionKey.value,
      jwtSecret: picks.jwtSecret.value,
    };
  }

  const cfgPath = path.join(USER_DATA, 'dbsense.config.json');
  if (fs.existsSync(cfgPath)) {
    const raw = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
    // Permite override por env no packaged tb (útil pra CI/QA do app).
    const picks = {
      controlDb: pickSecret('ConnectionStrings__ControlDb', raw.controlDb, `file:${cfgPath}`),
      encryptionKey: pickSecret('Security__EncryptionKey', raw.encryptionKey, `file:${cfgPath}`),
      jwtSecret: pickSecret('Security__JwtSecret', raw.jwtSecret, `file:${cfgPath}`),
    };
    logConfigSources(picks);
    return {
      controlDb: picks.controlDb.value,
      encryptionKey: picks.encryptionKey.value,
      jwtSecret: picks.jwtSecret.value,
    };
  }

  fs.mkdirSync(USER_DATA, { recursive: true });
  const generated = {
    controlDb:
      process.env['ConnectionStrings__ControlDb'] ||
      'Data Source=DESKTOP-I66H2NG\\SQLEXPRESS;Initial Catalog=dbsense_control_2;Integrated Security=True;Persist Security Info=False;Pooling=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Command Timeout=0',
    encryptionKey: process.env['Security__EncryptionKey'] || crypto.randomBytes(32).toString('base64'),
    jwtSecret: process.env['Security__JwtSecret'] || crypto.randomBytes(64).toString('base64'),
  };
  fs.writeFileSync(cfgPath, JSON.stringify(generated, null, 2));
  console.log(`[electron] config gerada em ${cfgPath} — edite pra ajustar control DB.`);
  console.log(`[electron] config controlDb: source=generated fp=${fingerprint(generated.controlDb)}`);
  console.log(`[electron] config encryptionKey: source=generated fp=${fingerprint(generated.encryptionKey)}`);
  console.log(`[electron] config jwtSecret: source=generated fp=${fingerprint(generated.jwtSecret)}`);
  return generated;
}

// ----------------------------------------------------------------------------
// Process lifecycle
// ----------------------------------------------------------------------------

function spawnTracked(name, cmd, args, opts) {
  const child = spawn(cmd, args, {
    ...opts,
    // shell:true no Windows resolve PATHEXT (.cmd/.exe) — sem isso o spawn
    // de `dotnet`/`npm` falha com ENOENT em algumas instalações.
    shell: process.platform === 'win32',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  // Stdout/stderr dos filhos vai pro arquivo de log direto — bytes podem
  // conter quebras de linha parciais, gravamos como texto cru sem reparcelar.
  child.stdout.on('data', (b) => logStream.write(`[${name}] ${b}`));
  child.stderr.on('data', (b) => logStream.write(`[${name}!] ${b}`));
  child.on('exit', (code, sig) => {
    console.log(`[electron] ${name} exited code=${code} sig=${sig}`);
    if (!isQuitting) app.quit();
  });
  children.push({ name, child });
  return child;
}

function killAll() {
  isQuitting = true;
  if (staticServer) {
    try { staticServer.close(); } catch { /* ignore */ }
  }
  for (const { name, child } of children) {
    if (child.exitCode !== null) continue;
    try {
      treeKill(child.pid, 'SIGTERM', (err) => {
        if (err) console.error(`[electron] tree-kill ${name} falhou:`, err.message);
      });
    } catch (err) {
      console.error(`[electron] kill ${name} falhou:`, err.message);
    }
  }
}

function waitForHttp(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve, reject) => {
    const tick = () => {
      const req = http.get(url, (res) => {
        res.resume();
        if (res.statusCode && res.statusCode < 500) return resolve();
        retry();
      });
      req.on('error', retry);
      req.setTimeout(2000, () => { req.destroy(); retry(); });
    };
    const retry = () => {
      if (Date.now() > deadline) return reject(new Error(`timeout esperando ${url}`));
      setTimeout(tick, 500);
    };
    tick();
  });
}

// Probe único: resolve(true) se URL responde com status<500 dentro do timeout,
// resolve(false) caso contrário. Usado pra detectar backend pré-existente
// antes de spawnar API/Worker — evita EADDRINUSE quando o usuário já tem
// `dotnet run` rodando ou um processo órfão segurando a porta.
function probeOnce(url, timeoutMs) {
  return new Promise((resolve) => {
    let settled = false;
    const done = (v) => { if (!settled) { settled = true; resolve(v); } };
    const req = http.get(url, (res) => {
      res.resume();
      done(res.statusCode != null && res.statusCode < 500);
    });
    req.on('error', () => done(false));
    req.setTimeout(timeoutMs, () => { req.destroy(); done(false); });
  });
}

// ----------------------------------------------------------------------------
// Backend boot
// ----------------------------------------------------------------------------

// Anexa Application Name=<name> na connection string passada via env, para
// identificar API vs Worker em sys.dm_exec_sessions, SQL Profiler, audit logs.
// Se a connection string já tiver Application Name (override do usuário no
// dbsense.config.json ou env do sistema), respeita — não sobrescrevemos.
function tagAppName(cs, name) {
  if (!cs) return cs;
  if (/(^|;)\s*Application\s*Name\s*=/i.test(cs)) return cs;
  const sep = cs.endsWith(';') ? '' : ';';
  return `${cs}${sep}Application Name=${name}`;
}

function backendPaths() {
  if (IS_PACKAGED) {
    return {
      apiCmd: path.join(PACKAGED_RESOURCES, 'api', 'DbSense.Api.exe'),
      apiArgs: [],
      apiCwd: path.join(PACKAGED_RESOURCES, 'api'),
      workerCmd: path.join(PACKAGED_RESOURCES, 'worker', 'DbSense.Worker.exe'),
      workerArgs: [],
      workerCwd: path.join(PACKAGED_RESOURCES, 'worker'),
      runtimeConfigPath: path.join(USER_DATA, 'runtime-config.json'),
    };
  }
  const apiProj = path.join(REPO_ROOT, 'src', 'DbSense.Api');
  const workerProj = path.join(REPO_ROOT, 'src', 'DbSense.Worker');
  return {
    apiCmd: 'dotnet',
    apiArgs: ['run', '--project', apiProj, '--no-launch-profile'],
    apiCwd: apiProj,
    workerCmd: 'dotnet',
    workerArgs: ['run', '--project', workerProj, '--no-launch-profile'],
    workerCwd: workerProj,
    runtimeConfigPath: path.join(apiProj, 'runtime-config.json'),
  };
}

async function bootBackend(cfg) {
  // Reaproveita backend já rodando (cenário comum: dev com `dotnet run` aberto,
  // ou filho órfão de uma run anterior). Se /api/health responde, não spawnamos
  // nada — só usamos o que está lá. O Worker idem: sem health endpoint próprio,
  // usamos a presença da API como proxy (no fluxo normal os dois sobem juntos).
  const apiHealth = `http://localhost:${API_PORT}/api/health`;
  if (await probeOnce(apiHealth, 1500)) {
    console.log(
      `[electron] backend já em :${API_PORT} — reaproveitando, ` +
      `não vou subir API/Worker. Para forçar nova instância, encerre o backend antes.`
    );
    return;
  }

  const p = backendPaths();
  const sharedEnv = {
    ...process.env,
    Security__EncryptionKey: cfg.encryptionKey,
    Security__JwtSecret: cfg.jwtSecret,
    ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
    // CORS: em packaged a UI vem do static server (porta STATIC_PORT); em dev
    // continua sendo o Vite (porta VITE_PORT). Liberamos as duas pra simplificar.
    CORS_ORIGINS: `http://localhost:${VITE_PORT},http://localhost:${STATIC_PORT}`,
    RuntimeConfig__Path: p.runtimeConfigPath,
  };

  spawnTracked('api', p.apiCmd, p.apiArgs, {
    cwd: p.apiCwd,
    env: {
      ...sharedEnv,
      ConnectionStrings__ControlDb: tagAppName(cfg.controlDb, 'DbSense.Api'),
      ASPNETCORE_ENVIRONMENT: IS_PACKAGED ? 'Production' : 'Development',
    },
  });

  spawnTracked('worker', p.workerCmd, p.workerArgs, {
    cwd: p.workerCwd,
    env: {
      ...sharedEnv,
      ConnectionStrings__ControlDb: tagAppName(cfg.controlDb, 'DbSense.Worker'),
      DOTNET_ENVIRONMENT: IS_PACKAGED ? 'Production' : 'Development',
    },
  });

  console.log(`[electron] aguardando API em http://localhost:${API_PORT}/api/health…`);
  await waitForHttp(apiHealth, HEALTH_TIMEOUT_MS);
  console.log('[electron] API ok.');
}

// ----------------------------------------------------------------------------
// Frontend serving
// ----------------------------------------------------------------------------

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js':   'application/javascript; charset=utf-8',
  '.mjs':  'application/javascript; charset=utf-8',
  '.css':  'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg':  'image/svg+xml',
  '.png':  'image/png',
  '.jpg':  'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif':  'image/gif',
  '.ico':  'image/x-icon',
  '.woff': 'font/woff',
  '.woff2':'font/woff2',
  '.ttf':  'font/ttf',
};

// Proxy /api/* pro Kestrel — assim a UI consome o axios baseURL "/api" sem
// CORS e sem precisar saber em qual porta o API tá ouvindo.
function proxyApiRequest(req, res) {
  const opts = {
    hostname: '127.0.0.1',
    port: API_PORT,
    path: req.url,
    method: req.method,
    headers: { ...req.headers, host: `localhost:${API_PORT}` },
  };
  const proxyReq = http.request(opts, (proxyRes) => {
    res.writeHead(proxyRes.statusCode || 502, proxyRes.headers);
    proxyRes.pipe(res);
  });
  proxyReq.on('error', (err) => {
    if (!res.headersSent) {
      res.writeHead(502, { 'Content-Type': 'text/plain; charset=utf-8' });
    }
    res.end(`API indisponível: ${err.message}`);
  });
  req.pipe(proxyReq);
}

function startStaticServer(rootDir, port) {
  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      try {
        if (req.url.startsWith('/api/') || req.url === '/api') {
          return proxyApiRequest(req, res);
        }
        let urlPath = decodeURIComponent(req.url.split('?')[0]);
        if (urlPath === '/') urlPath = '/index.html';
        const resolved = path.join(rootDir, urlPath);
        // Path traversal guard: caminho normalizado tem que ficar dentro do root.
        if (!resolved.startsWith(rootDir)) {
          res.writeHead(403); return res.end();
        }
        fs.stat(resolved, (err, stat) => {
          if (!err && stat.isFile()) return serveFile(resolved, res);
          // SPA fallback: rota desconhecida → index.html (BrowserRouter resolve client-side).
          serveFile(path.join(rootDir, 'index.html'), res);
        });
      } catch (err) {
        res.writeHead(500); res.end(String(err.message || err));
      }
    });
    server.on('error', reject);
    server.listen(port, '127.0.0.1', () => resolve(server));
  });
}

function serveFile(filePath, res) {
  const ext = path.extname(filePath).toLowerCase();
  const ct = MIME[ext] || 'application/octet-stream';
  // index.html não pode cachear (evita ficar preso numa versão antiga após update);
  // assets versionados pelo Vite (com hash no nome) podem cachear muito.
  const cache = filePath.endsWith('index.html')
    ? 'no-cache'
    : 'public, max-age=31536000, immutable';
  fs.stat(filePath, (err, stat) => {
    if (err || !stat.isFile()) { res.writeHead(404); return res.end(); }
    res.writeHead(200, {
      'Content-Type': ct,
      'Content-Length': stat.size,
      'Cache-Control': cache,
    });
    fs.createReadStream(filePath).pipe(res);
  });
}

async function bootFrontend() {
  if (DEV_MODE) {
    spawnTracked('vite', 'npm', ['run', 'dev', '--', '--port', String(VITE_PORT), '--strictPort'], {
      cwd: path.join(REPO_ROOT, 'frontend'),
      env: process.env,
    });
    await waitForHttp(`http://localhost:${VITE_PORT}/`, HEALTH_TIMEOUT_MS);
    return `http://localhost:${VITE_PORT}/`;
  }

  // Packaged: serve dist via http local + proxy /api → Kestrel.
  const distDir = IS_PACKAGED
    ? path.join(PACKAGED_RESOURCES, 'frontend-dist')
    : path.join(REPO_ROOT, 'frontend', 'dist');
  if (!fs.existsSync(path.join(distDir, 'index.html'))) {
    throw new Error(`frontend build não encontrado em ${distDir}. Rode "npm run build:frontend".`);
  }
  staticServer = await startStaticServer(distDir, STATIC_PORT);
  console.log(`[electron] static server em http://localhost:${STATIC_PORT}`);
  return `http://localhost:${STATIC_PORT}/`;
}

// ----------------------------------------------------------------------------
// Window
// ----------------------------------------------------------------------------

function createWindow(targetUrl) {
  const win = new BrowserWindow({
    width: 1400,
    height: 900,
    title: 'DbSense',
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  win.loadURL(targetUrl);
  win.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: 'deny' };
  });
  return win;
}

app.on('before-quit', killAll);
app.on('window-all-closed', () => app.quit());
process.on('SIGINT', () => app.quit());
process.on('SIGTERM', () => app.quit());

app.whenReady().then(async () => {
  try {
    const cfg = loadConfig();
    await bootBackend(cfg);
    const url = await bootFrontend();
    createWindow(url);
  } catch (err) {
    console.error('[electron] boot falhou:', err);
    dialog.showErrorBox(
      'DbSense — falha ao iniciar',
      `${err.message}\n\nVerifique:\n` +
        `• SQL Server (control DB) acessível\n` +
        `• Portas ${API_PORT} (API)${DEV_MODE ? ` e ${VITE_PORT} (UI)` : ` e ${STATIC_PORT} (UI)`} livres\n` +
        (IS_PACKAGED
          ? `• Configuração em "${path.join(USER_DATA, 'dbsense.config.json')}"\n`
          : `• .NET SDK no PATH\n`),
    );
    killAll();
    app.exit(1);
  }
});
