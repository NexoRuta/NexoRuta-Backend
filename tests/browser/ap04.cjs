const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');

const modulePath = process.env.NEXORUTA_PLAYWRIGHT_MODULE || path.join(__dirname, 'node_modules/playwright');
const { chromium } = require(modulePath);
const { AxeBuilder } = require(path.join(path.dirname(modulePath), '@axe-core/playwright'));
const env = process.env;
const commerce = required('AP04_BROWSER_BASE_URL').replace(/\/$/, '');
const api = required('AP04_API_BASE_URL').replace(/\/$/, '');
const email = required('AP04_BROWSER_EMAIL');
const password = required('AP04_BROWSER_PASSWORD');
const reference = env.AP04_FIXTURE_REFERENCE || 'AP04-GCP-DEV-C2-02';
const expectPending = env.AP04_EXPECT_PENDING !== 'false';
const output = required('AP04_EVIDENCE_DIR');
const apiContainer = env.AP04_API_CONTAINER || 'ap04-local-api';
const commerceContainer = env.AP04_COMMERCE_CONTAINER || 'ap04-local-commerce';
const report = { checks: [], views: [], responsiveMetrics: [], apiFaultInjection: [], pageErrors: [], accessibilityViolations: [] };
const allowedOrigins = new Set([new URL(commerce).origin, new URL(api).origin]);
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

function required(name) {
  const value = process.env[name];
  if (!value) throw new Error(`Missing required setting ${name}`);
  return value;
}

async function until(action, label, timeoutMs = 40000) {
  const expires = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < expires) {
    try {
      const result = await action();
      if (result) return result;
    } catch (error) { lastError = error; }
    await pause(300);
  }
  throw new Error(`Timed out waiting for ${label}${lastError ? `: ${lastError.message}` : ''}`);
}

function dockerRequest(method, requestPath) {
  return new Promise((resolve, reject) => {
    const req = http.request({ socketPath: '/var/run/docker.sock', method, path: requestPath }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const body = Buffer.concat(chunks).toString('utf8');
        if (response.statusCode < 200 || response.statusCode >= 300)
          return reject(new Error(`Docker API ${method} ${requestPath} returned HTTP ${response.statusCode}`));
        resolve(body ? JSON.parse(body) : null);
      });
    });
    req.on('error', reject);
    req.end();
  });
}

let dockerApiPrefix;
async function docker(method, requestPath) {
  if (!dockerApiPrefix) {
    const version = await dockerRequest('GET', '/version');
    dockerApiPrefix = `/v${version.ApiVersion}`;
  }
  return dockerRequest(method, `${dockerApiPrefix}${requestPath}`);
}

async function assertLocalContainer(name, service) {
  const info = await docker('GET', `/containers/${encodeURIComponent(name)}/json`);
  assert.equal(info.Config.Labels['com.docker.compose.project'], 'ap04local', `${name} belongs to the isolated AP-04 project`);
  assert.equal(info.Config.Labels['com.docker.compose.service'], service, `${name} is the expected AP-04 service`);
  return info;
}

async function waitReady(url, label) {
  return until(async () => {
    try { return (await fetch(url, { signal: AbortSignal.timeout(2000) })).ok; }
    catch { return false; }
  }, label);
}

async function apiLogin() {
  const response = await fetch(`${api}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });
  assert.equal(response.status, 200, 'Synthetic commerce account can authenticate against the API');
  const result = await response.json();
  assert.ok(result.accessToken);
  return result.accessToken;
}

async function apiGet(token, pathname) {
  const response = await fetch(`${api}${pathname}`, { headers: { Authorization: `Bearer ${token}` } });
  assert.equal(response.status, 200, `${pathname} returns HTTP 200`);
  return response.json();
}

async function createPage(browser) {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1000 }, locale: 'es-UY', reducedMotion: 'reduce',
  });
  await context.route('**/*', route => {
    const url = new URL(route.request().url());
    if (/^https?:$/.test(url.protocol) && !allowedOrigins.has(url.origin)) return route.abort();
    return route.continue();
  });
  const page = await context.newPage();
  page.setDefaultTimeout(25000);
  page.on('pageerror', error => report.pageErrors.push(error.message));
  await page.goto(`${commerce}/ingresar`);
  await page.locator('#Email').fill(email);
  await page.locator('#Password').fill(password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.includes('ingresar')),
    page.getByRole('button', { name: 'Ingresar', exact: true }).click(),
  ]);
  return { context, page };
}

async function keyboardCheck(page, name) {
  await page.locator('body').click({ position: { x: 1, y: 1 } });
  await page.keyboard.press('Control+Home');
  for (let i = 0; i < 80; i++) {
    await page.keyboard.press('Shift+Tab');
    if (await page.locator('.nr-skip-link').evaluate(element => element === document.activeElement)) break;
  }
  assert.equal(await page.locator('.nr-skip-link').evaluate(element => element === document.activeElement), true,
    `${name}: keyboard reaches the skip link`);
  const bounds = await page.locator('.nr-skip-link').boundingBox();
  assert.ok(bounds && bounds.x >= 0 && bounds.y >= 0, `${name}: focused skip link is visible`);
  await page.keyboard.press('Enter');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'main-content', `${name}: skip link moves focus to main`);
}

async function visualChecks(page, name) {
  for (const width of [1440, 320]) {
    await page.setViewportSize({ width, height: 1000 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true,
      `${name}: no horizontal overflow at ${width}px`);
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
    const violations = axe.violations.map(item => ({ id: item.id, targets: item.nodes.map(node => node.target) }));
    report.views.push({ name, width, violations });
    report.accessibilityViolations.push(...violations.map(item => ({ view: name, width, ...item })));
    assert.deepEqual(violations, [], `${name}: axe accessibility checks at ${width}px`);
    await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true });
    if (width === 1440) await keyboardCheck(page, name);
    if (width === 320) {
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true,
        `${name}: no horizontal overflow at 320px with base text at 200%`);
      assert.equal(await page.evaluate(() => document.body.scrollWidth <= innerWidth + 1), true,
        `${name}: the page body also remains within the viewport at 200% text`);
      const metrics = await page.evaluate(() => {
        const width = selector => document.querySelector(selector)?.getBoundingClientRect().width ?? null;
        return { viewport: innerWidth, body: width('body'), shell: width('.nr-app-shell'), main: width('.nr-main-content'), page: width('.nr-page'), feature: width('.nr-returns-page') };
      });
      report.responsiveMetrics.push({ name, ...metrics });
      const overlaps = await page.evaluate(() => {
        const brandElement = document.querySelector('.nr-brand');
        const brand = brandElement?.getBoundingClientRect();
        const brandText = document.querySelector('.nr-brand > span:last-child')?.getBoundingClientRect();
        const actions = document.querySelector('.nr-topbar-actions')?.getBoundingClientRect();
        const actionElement = document.querySelector('.nr-topbar-actions');
        const logout = document.querySelector('.nr-topbar-actions form button')?.getBoundingClientRect();
        const heading = document.querySelector('.nr-return-section-heading h2');
        const brandActionOverlap = brandText && actions &&
          Math.min(brandText.right, actions.right) > Math.max(brandText.left, actions.left) &&
          Math.min(brandText.bottom, actions.bottom) > Math.max(brandText.top, actions.top);
        return {
          brandContentOverflow: Boolean(brandElement && brandElement.scrollWidth > brandElement.clientWidth + 1),
          actionContentOverflow: Boolean(actionElement && actionElement.scrollWidth > actionElement.clientWidth + 1),
          brandActionOverlap: Boolean(brandActionOverlap),
          controlsOutsideViewport: Boolean((brandText && (brandText.left < 0 || brandText.right > innerWidth + 1)) ||
            (logout && (logout.left < 0 || logout.right > innerWidth + 1))),
          headingOverflow: Boolean(heading && heading.scrollWidth > heading.clientWidth + 1),
        };
      });
      assert.equal(overlaps.brandContentOverflow, false, `${name}: Commerce brand text fits its box at 200% text`);
      assert.equal(overlaps.actionContentOverflow, false, `${name}: Commerce actions fit their box at 200% text`);
      assert.equal(overlaps.brandActionOverlap, false, `${name}: Commerce brand and actions do not overlap at 200% text`);
      assert.equal(overlaps.controlsOutsideViewport, false, `${name}: Commerce topbar controls stay in the viewport`);
      assert.equal(overlaps.headingOverflow, false, `${name}: detail section heading wraps within the viewport`);
      assert.equal(await page.locator('#returns-title').evaluate(element => element.scrollWidth <= element.clientWidth), true,
        `${name}: page title fits without splitting or horizontal overflow at 200% text`);
      const zoomAxe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
      const zoomViolations = zoomAxe.violations.map(item => ({ id: item.id, targets: item.nodes.map(node => node.target) }));
      report.views.push({ name: `${name}-text-200`, width, violations: zoomViolations });
      report.accessibilityViolations.push(...zoomViolations.map(item => ({ view: `${name}-text-200`, width, ...item })));
      assert.deepEqual(zoomViolations, [], `${name}: axe checks with base text at 200%`);
      await page.screenshot({ path: path.join(output, `${name}-320-text-200.png`), fullPage: true });
      await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
    }
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
}

async function runFaultStates(page) {
  await assertLocalContainer(apiContainer, 'api');
  await assertLocalContainer(commerceContainer, 'commerce');
  await page.locator('.nr-returns-results[aria-busy="false"]').waitFor();

  await docker('POST', `/containers/${encodeURIComponent(apiContainer)}/pause`);
  try {
    await page.getByRole('button', { name: 'Actualizar', exact: true }).click();
    await page.locator('.nr-returns-results[aria-busy="true"]').waitFor();
    const updatingStatus = page.getByRole('status').filter({ hasText: 'Actualizando resultados' });
    await updatingStatus.waitFor();
    assert.equal(await updatingStatus.isVisible(), true, 'A slow refresh has a visible progress status while preserving existing rows');
    await pause(1000);
    await page.screenshot({ path: path.join(output, 'commerce-devoluciones-local-slow.png'), fullPage: true });
    report.apiFaultInjection.push('Paused only the isolated AP-04 API container; Commerce exposed aria-busy while refresh waited.');
  } finally {
    await docker('POST', `/containers/${encodeURIComponent(apiContainer)}/unpause`).catch(() => {});
    await page.locator('.nr-returns-results[aria-busy="false"]').waitFor({ timeout: 40000 });
  }

  await docker('POST', `/containers/${encodeURIComponent(apiContainer)}/stop?t=10`);
  try {
    await page.getByRole('button', { name: 'Actualizar', exact: true }).click();
    await page.getByRole('alert').filter({ hasText: 'No pudimos cargar las devoluciones' }).waitFor();
    await page.screenshot({ path: path.join(output, 'commerce-devoluciones-local-api-error.png'), fullPage: true });
    report.apiFaultInjection.push('Stopped only the isolated AP-04 API container; Commerce exposed the recoverable API error state.');
  } finally {
    const state = await assertLocalContainer(apiContainer, 'api').catch(() => null);
    if (!state?.State.Running) await docker('POST', `/containers/${encodeURIComponent(apiContainer)}/start`).catch(() => {});
  }
  await waitReady(`${api}/ready`, 'API readiness after restart');
  await page.getByRole('button', { name: 'Reintentar', exact: true }).click();
  await page.getByRole('alert').filter({ hasText: 'No pudimos cargar las devoluciones' }).waitFor({ state: 'hidden' });
  await page.locator('.nr-returns-results[aria-busy="false"]').waitFor();
}

(async () => {
  await fs.mkdir(output, { recursive: true });
  const token = await apiLogin();
  const since = new Date(Date.now() - 29 * 86400000).toISOString();
  const untilDate = new Date(Date.now() + 86400000).toISOString();
  const query = new URLSearchParams({ desdeUtc: since, hastaUtc: untilDate, referencia: reference, limite: '50' });
  const initialPage = await apiGet(token, `/api/devoluciones?${query}`);
  assert.equal(initialPage.items.length, 1, 'The AP-04 synthetic reference is unique');
  assert.equal(initialPage.items[0].referencia, reference);
  assert.equal(initialPage.items[0].pendientesAcuse, expectPending ? 1 : 0,
    expectPending ? 'The synthetic fixture starts pending a Commerce acknowledgement' : 'The synthetic fixture is already acknowledged');
  const envioId = initialPage.items[0].envioId;
  const operadorId = initialPage.items[0].operadorId;
  report.checks.push(expectPending
    ? 'Authenticated API lists one unique pending AP-04 fixture for Commerce.'
    : 'Authenticated API lists one unique acknowledged AP-04 fixture for Commerce.');
  if (env.AP04_VERIFY_PRIOR_FIXTURE !== 'false') {
    const priorQuery = new URLSearchParams({ desdeUtc: since, hastaUtc: untilDate, referencia: 'AP04-GCP-DEV-C2-01', limite: '50' });
    const priorFixture = await apiGet(token, `/api/devoluciones?${priorQuery}`);
    assert.equal(priorFixture.items.length, 1, 'The prior AP-04 fixture remains unique after repeated seeding');
    assert.equal(priorFixture.items[0].pendientesAcuse, 0, 'Repeated seeding preserves its previously saved acknowledgement');
    report.checks.push('Repeated seed preserved the earlier fixture acknowledgement.');
  }

  const browser = await chromium.launch({ headless: true });
  let context;
  let page;
  let recoveryRequired = false;
  try {
    ({ context, page } = await createPage(browser));
    await page.goto(`${commerce}/devoluciones`);
    await page.getByRole('heading', { name: 'Devoluciones', exact: true }).waitFor();
    await page.locator('#returns-reference').fill(reference);
    await page.getByRole('button', { name: 'Aplicar filtros', exact: true }).click();
    const row = page.locator('.nr-return-row').filter({ hasText: reference });
    await row.waitFor();
    assert.match(await row.innerText(), expectPending ? /1 pendiente de acuse/ : /0 pendientes de acuse/);
    const viewPrefix = expectPending ? 'commerce-devoluciones-local-pending' : 'commerce-devoluciones-local-received';
    await visualChecks(page, `${viewPrefix}-list`);

    await row.getByRole('link', { name: 'Ver devolución', exact: true }).click();
    await page.getByRole('heading', { name: 'Detalle de devolución', exact: true }).waitFor();
    const detailUrl = page.url();
    const detailParts = new URL(detailUrl);
    const parcel = page.locator('.nr-return-parcel');
    await parcel.waitFor();
    assert.equal(await page.locator('.nr-return-parcel').count(), 1);
    assert.match(await page.locator('.nr-returns-page').innerText(), new RegExp(`Pendientes de acuse:\\s*${expectPending ? 1 : 0}`));
    if (expectPending) assert.match(await parcel.innerText(), /Pendiente de acuse/);
    await visualChecks(page, `${viewPrefix}-detail`);

    let observation = null;
    if (expectPending) {
      observation = 'Sello con daño visible; empaque recibido.';
      await page.getByLabel('Con observaciones', { exact: true }).check();
      await page.locator('.nr-return-note textarea').waitFor();
      assert.equal(await page.locator('#blazor-error-ui').isVisible(), false, 'Changing acknowledgement result does not crash Blazor');
      await page.locator('.nr-return-note textarea').fill(observation);
      await page.getByRole('button', { name: /Registrar acuse de 1 bulto/ }).click();
      const acknowledgementStatus = page.locator('#return-ack-status');
      await acknowledgementStatus.filter({ hasText: 'Acuse registrado para 1 bulto(s).' }).waitFor();
      assert.equal(await page.evaluate(() => document.activeElement.id), 'return-ack-status',
        'Successful acknowledgement returns focus to its confirmation status');
      const focusStyle = await acknowledgementStatus.evaluate(element => {
        const style = getComputedStyle(element);
        return { width: style.outlineWidth, style: style.outlineStyle };
      });
      assert.equal(focusStyle.width, '3px', 'Acknowledgement confirmation has a visible focus outline');
      assert.notEqual(focusStyle.style, 'none',
        `Acknowledgement confirmation focus is not visually suppressed: ${JSON.stringify(focusStyle)}`);
      await page.screenshot({ path: path.join(output, 'commerce-devoluciones-local-ack-focus.png'), fullPage: true });
      assert.match(await page.locator('.nr-returns-page').innerText(), /Pendientes de acuse:\s*0/);
      assert.ok((await page.locator('.nr-returns-page').innerText()).includes(observation), 'Saved observation is visible in parcel detail');
      assert.match(await page.locator('.nr-return-history').innerText(), /Acuse/);
      report.checks.push('Commerce records an acknowledgement with observations; detail and canonical history update; keyboard focus returns to the visible confirmation.');
    } else {
      assert.equal(await page.locator('.nr-return-ack').count(), 0, 'Read-only accepted fixture exposes no further acknowledgement action');
      report.checks.push('Already acknowledged fixture remains read-only; no second acknowledgement is offered.');
    }

    const detail = await apiGet(token, `/api/devoluciones/envios/${envioId}?operadorId=${operadorId}`);
    assert.equal(detail.pendientesAcuse, 0);
    if (observation) assert.equal(detail.bultos[0].observacion, observation);
    else observation = detail.bultos[0].observacion;
    const history = await apiGet(token, `/api/devoluciones/envios/${envioId}/historial?operadorId=${operadorId}`);
    assert.ok(history.some(item => item.tipo.includes('Acuse')));
    report.checks.push('HTTP API confirms the saved acknowledgement and observation.');

    await page.getByRole('link', { name: 'Volver a devoluciones', exact: true }).click();
    await page.getByRole('heading', { name: 'Devoluciones', exact: true }).waitFor();
    if (env.AP04_FAULT_INJECTION === 'true') {
      recoveryRequired = true;
      await runFaultStates(page);
      recoveryRequired = false;
    } else report.apiFaultInjection.push('Skipped: AP04_FAULT_INJECTION is not enabled.');

    if (env.AP04_FAULT_INJECTION === 'true') {
      await context.close();
      context = null;
      await docker('POST', `/containers/${encodeURIComponent(apiContainer)}/restart?t=20`);
      await waitReady(`${api}/ready`, 'API readiness after persistence restart');
      await docker('POST', `/containers/${encodeURIComponent(commerceContainer)}/restart?t=20`);
      await waitReady(`${commerce}/ready`, 'Commerce readiness after persistence restart');
      report.checks.push('Restarted only the AP-04 API and Commerce containers with project volumes preserved.');
      ({ context, page } = await createPage(browser));
    }

    await page.goto(`${commerce}${detailParts.pathname}${detailParts.search}`);
    await page.getByRole('heading', { name: 'Detalle de devolución', exact: true }).waitFor();
    assert.match(await page.locator('.nr-returns-page').innerText(), /Pendientes de acuse:\s*0/);
    if (observation) assert.ok((await page.locator('.nr-returns-page').innerText()).includes(observation));
    assert.match(await page.locator('.nr-return-history').innerText(), /Acuse/);
    await visualChecks(page, 'commerce-devoluciones-local-reopened-detail');
    await page.screenshot({ path: path.join(output, 'commerce-devoluciones-local-reopened-after-restart.png'), fullPage: true });
    report.checks.push(env.AP04_FAULT_INJECTION === 'true'
      ? 'A fresh Commerce session after API/Commerce restarts sees the persisted acknowledgement and history.'
      : 'The reopened Commerce detail sees the persisted acknowledgement and history.');
    assert.deepEqual(report.accessibilityViolations, []);
    assert.deepEqual(report.pageErrors, []);
    await fs.writeFile(path.join(output, 'ap04-browser-report.json'), JSON.stringify(report, null, 2));
    console.log(`AP-04 browser acceptance passed: ${report.views.length} viewport/accessibility views; ${report.checks.length} checks.`);
  } catch (error) {
    if (recoveryRequired && env.AP04_FAULT_INJECTION === 'true') {
      await docker('POST', `/containers/${encodeURIComponent(apiContainer)}/unpause`).catch(() => {});
      const state = await assertLocalContainer(apiContainer, 'api').catch(() => null);
      if (!state?.State.Running) await docker('POST', `/containers/${encodeURIComponent(apiContainer)}/start`).catch(() => {});
      await waitReady(`${api}/ready`, 'API recovery after browser failure').catch(() => {});
    }
    if (page) await page.screenshot({ path: path.join(output, 'ap04-browser-failure.png'), fullPage: true }).catch(() => {});
    await fs.writeFile(path.join(output, 'ap04-browser-failure.json'), JSON.stringify({ message: error.message, report }, null, 2));
    throw error;
  } finally {
    if (context) await context.close().catch(() => {});
    await browser.close();
  }
})().catch(error => { console.error(error.message); process.exit(1); });
