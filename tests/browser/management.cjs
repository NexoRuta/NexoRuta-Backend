const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const modulePath = process.env.NEXORUTA_PLAYWRIGHT_MODULE || path.join(__dirname,'node_modules/playwright');
const { chromium } = require(modulePath);
const { AxeBuilder } = require(path.join(path.dirname(modulePath),'@axe-core/playwright'));
const output = process.env.NEXORUTA_GESTION_E2E_DIR;
const env = process.env;
const report = {checks:[],views:[],externalBlocked:[]};
(async()=>{
 await fs.mkdir(output,{recursive:true});
 const browser=await chromium.launch({headless:true});
 const contexts=[];
 try{
  async function session(base,email){
   const context=await browser.newContext({viewport:{width:1440,height:1000},locale:'es-UY',reducedMotion:'reduce'});contexts.push(context);
   await context.route('**/*',route=>{const u=new URL(route.request().url());if(/^https?:$/.test(u.protocol)&&!['127.0.0.1','localhost'].includes(u.hostname)){report.externalBlocked.push(u.hostname);return route.abort();}return route.continue();});
   const page=await context.newPage();page.setDefaultTimeout(20000);
   await page.goto(base+'/ingresar');await page.locator('#Email').fill(email);await page.locator('#Password').fill(env.MANAGEMENT_PASSWORD);
   await page.getByRole('button',{name:'Ingresar',exact:true}).click();await page.waitForURL(u=>!u.pathname.includes('ingresar'));
   return page;
  }
  async function visual(page,name){
   for(const width of [1440,768,375,320]){
    await page.setViewportSize({width,height:1000});
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,`${name}: page overflow at ${width}`);
    const axe=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze();
    report.views.push({name,width,violations:axe.violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target)}))});
    assert.deepEqual(axe.violations.map(v=>v.id),[],`${name}: accessibility scan`);
    await page.screenshot({path:path.join(output,`${name}-${width}.png`),fullPage:true});
    // Keyboard reaches a visible skip link and it transfers focus to the main region.
    await page.locator('body').click({position:{x:1,y:1}});
    await page.keyboard.press('Control+Home');
    for(let i=0;i<80;i++){await page.keyboard.press('Shift+Tab');if(await page.locator('.nr-skip-link').evaluate(e=>e===document.activeElement))break;}
    assert.equal(await page.locator('.nr-skip-link').evaluate(e=>e===document.activeElement),true);
    const bounds=await page.locator('.nr-skip-link').boundingBox();assert.ok(bounds&&bounds.x>=0&&bounds.y>=0);
    await page.keyboard.press('Enter');assert.equal(await page.evaluate(()=>document.activeElement.id),'main-content');
    if(width===320){await page.evaluate(()=>document.documentElement.style.fontSize='200%');assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,`${name}: text 200% overflow`);await page.evaluate(()=>document.documentElement.style.fontSize='');}
   }
   await page.setViewportSize({width:1440,height:1000});
  }
  const op=await session(env.MANAGEMENT_BACKOFFICE,'gestion-op-a@example.test');
  await op.goto(env.MANAGEMENT_BACKOFFICE+'/ReporteOperaciones?Desde=2026-11-01&Hasta='+new Date(Date.now()+86400000).toISOString().slice(0,10));
  await op.getByRole('heading',{name:'Cumplimiento y volumen',exact:true}).waitFor();
  assert.match(await op.locator('main').innerText(),/Sin compromiso: 1/);
  await visual(op,'reportes');
  const from=await op.locator('#Desde').inputValue(),to=await op.locator('#Hasta').inputValue();
  for(const field of ['ZonaId','RepartidorId']){
   const option=await op.locator('#'+field+' option').nth(1).getAttribute('value');
   assert.ok(option);await op.locator('#'+field).selectOption(option);
  }
  await op.getByRole('button',{name:'Consultar indicadores',exact:true}).click();await op.waitForLoadState('networkidle');
  assert.ok(await op.locator('#ZonaId').inputValue());assert.ok(await op.locator('#RepartidorId').inputValue());
  await op.getByRole('link',{name:'Limpiar filtros',exact:true}).click();
  await op.locator('#Desde').fill(from);await op.locator('#Hasta').fill(to);
  await op.locator('#ComercioId').selectOption(env.MANAGEMENT_COMMERCE_ID);await op.getByRole('button',{name:'Consultar indicadores',exact:true}).click();await op.waitForLoadState('networkidle');
  assert.equal(await op.locator('#ComercioId').inputValue(),env.MANAGEMENT_COMMERCE_ID);
  await op.reload();assert.equal(await op.locator('#ComercioId').inputValue(),env.MANAGEMENT_COMMERCE_ID);
  const selector=op.locator('[data-nr-selector][data-tipo="Comercio"]');
  await selector.locator('summary').click();
  await selector.locator('input[type=search]').fill('inexistente');await selector.getByRole('button',{name:'Buscar',exact:true}).click();
  await selector.getByRole('status').filter({hasText:'Sin coincidencias'}).waitFor();
  assert.equal(await op.locator('#ComercioId').inputValue(),env.MANAGEMENT_COMMERCE_ID);
  await op.route('**/ReporteOperaciones?handler=Opciones*',route=>route.fulfill({status:503,body:'synthetic outage'}));
  await selector.getByRole('button',{name:'Buscar',exact:true}).click();await selector.getByRole('status').filter({hasText:'No pudimos cargar'}).waitFor();
  await op.unroute('**/ReporteOperaciones?handler=Opciones*');
  await selector.locator('input[type=search]').fill('');await selector.getByRole('button',{name:'Buscar',exact:true}).click();
  await selector.getByRole('status').filter({hasText:'Opciones actualizadas'}).waitFor();
  await selector.locator('summary').click();
  await op.screenshot({path:path.join(output,'reportes-filtrados-1440.png'),fullPage:true});
  await op.locator(`a[href^="/reportes/operaciones/${env.MANAGEMENT_PARTIAL}"]`).click();
  await op.getByRole('heading',{name:'Detalle de indicador',exact:true}).waitFor();await visual(op,'detalle-bultos');
  await op.getByRole('link',{name:'Volver a los filtros del reporte'}).click();assert.equal(await op.locator('#ComercioId').inputValue(),env.MANAGEMENT_COMMERCE_ID);
  const accountLink=op.getByRole('link',{name:'Ver cuenta',exact:true}).first();
  const target=new URL(await accountLink.getAttribute('href'),env.MANAGEMENT_BACKOFFICE);
  assert.equal(target.searchParams.get('Desde'),from);assert.equal(target.searchParams.get('Hasta'),to);
  await accountLink.click();
  await op.getByRole('heading',{name:'Cuenta del comercio',exact:true}).waitFor();await visual(op,'cuenta-operador');
  await op.getByRole('button',{name:'Preparar liquidación del período',exact:true}).click();
  const confirm=op.getByRole('button',{name:'Confirmar liquidación revisada',exact:true});await confirm.waitFor();
  assert.match(await op.locator('main').innerText(),/330[,.]70/);
  const documentId=new URL(op.url()).searchParams.get('DocumentoId');assert.ok(documentId);await visual(op,'borrador');
  await confirm.click();await op.waitForLoadState('networkidle');assert.equal(await confirm.count(),0);assert.match(await op.locator('main').textContent(),/Confirmada por/);await visual(op,'confirmada');
  report.checks.push('Operator filters, parcel authorship, prepares, reviews and confirms a real persisted settlement.');
  const commerce=await session(env.MANAGEMENT_COMMERCE,'gestion-a@example.test');
  await commerce.goto(env.MANAGEMENT_COMMERCE+`/cuenta?OperadorId=${env.MANAGEMENT_OPERATOR}&Desde=${from}&Hasta=${to}&DocumentoId=${documentId}`);
  await commerce.getByRole('status').filter({hasText:'Consultando cuenta'}).waitFor();
  await commerce.getByRole('heading',{name:'Saldo operativo',exact:true}).waitFor();
  assert.match(await commerce.locator('main').innerText(),/661[,.]30/);assert.match(await commerce.locator('main').innerText(),/330[,.]70/);
  assert.equal(await commerce.getByRole('button',{name:/Confirmar liquidación/}).count(),0);
  await visual(commerce,'cuenta-comercio');
  await commerce.locator('#cuenta-operador').selectOption(env.MANAGEMENT_OTHER_OPERATOR);await commerce.getByRole('button',{name:'Consultar cuenta',exact:true}).click();
  await commerce.getByRole('heading',{name:'Saldo operativo',exact:true}).waitFor();assert.match(await commerce.locator('main').innerText(),/220[,.]40/);assert.doesNotMatch(await commerce.locator('main').innerText(),/661[,.]30/);
  await commerce.goto(env.MANAGEMENT_COMMERCE+`/cuenta?OperadorId=${env.MANAGEMENT_OTHER_OPERATOR}&Desde=${from}&Hasta=${to}&DocumentoId=${documentId}`);
  await commerce.getByRole('alert').filter({hasText:'no está disponible para tu cuenta y operador'}).waitFor();
  report.checks.push('Commerce reads only its confirmed document and movements, sees loading, switches independent operator account and cannot enumerate another operator document.');
  const other=await session(env.MANAGEMENT_COMMERCE,'gestion-b@example.test');await other.goto(env.MANAGEMENT_COMMERCE+`/cuenta?OperadorId=${env.MANAGEMENT_OPERATOR}&Desde=${from}&Hasta=${to}&DocumentoId=${documentId}`);await other.getByRole('alert').filter({hasText:'no está disponible para tu cuenta y operador'}).waitFor();
  report.checks.push('Second commerce cannot open the first commerce settlement.');
  for(const route of ['/','/rutas','/deposito','/vehiculos']){
   const response=await op.goto(env.MANAGEMENT_BACKOFFICE+route);assert.ok(response.ok(),route);
   await op.setViewportSize({width:375,height:900});
   assert.equal(await op.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,route+' page overflow');
   await op.screenshot({path:path.join(output,'relacionada-'+(route==='/'?'envios':route.slice(1))+'-375.png'),fullPage:true});
  }
  await op.goto(env.MANAGEMENT_BACKOFFICE+'/tablero');
  await op.getByRole('link',{name:'Solicitudes de nueva visita',exact:true}).click();
  await op.getByRole('heading',{name:'Solicitudes de nueva visita',exact:true}).waitFor();
  await op.goto(env.MANAGEMENT_BACKOFFICE+'/ReporteOperaciones?Desde=2030-01-01&Hasta=2030-01-02');
  await op.getByText('No hay operaciones admitidas en este período.',{exact:true}).waitFor();
  await op.screenshot({path:path.join(output,'reportes-vacio-1440.png'),fullPage:true});
  report.checks.push('Named filters, reload and direct URLs, empty results, recoverable option lookup failure and new-visit navigation.');
  const nativeZoom=require('./native-zoom.cjs');
  await nativeZoom(op,[env.MANAGEMENT_BACKOFFICE+'/ReporteOperaciones?Desde='+from+'&Hasta='+to,env.MANAGEMENT_BACKOFFICE+`/liquidaciones?ComercioId=${env.MANAGEMENT_COMMERCE_ID}&Desde=${from}&Hasta=${to}&DocumentoId=${documentId}`],output,'backoffice');
  await nativeZoom(commerce,[env.MANAGEMENT_COMMERCE+`/cuenta?OperadorId=${env.MANAGEMENT_OPERATOR}&Desde=${from}&Hasta=${to}&DocumentoId=${documentId}`],output,'commerce');
  report.checks.push('Actual Chromium zoom 200%: tabs.getZoom=2, innerWidth=720 and devicePixelRatio=2 at a 1440px viewport; keyboard and no page overflow.');
  assert.deepEqual(report.externalBlocked,[]);
  await fs.writeFile(path.join(output,'acceptance.json'),JSON.stringify(report,null,2));
  console.log('Management full-stack acceptance passed: '+report.views.length+' viewport checks, real API/PostgreSQL, distinct sessions.');
 }catch(error){await fs.writeFile(path.join(output,'failure.json'),JSON.stringify({message:error.message,report},null,2));for(let i=0;i<contexts.length;i++){const pages=contexts[i].pages();if(pages[0])await pages[0].screenshot({path:path.join(output,`failure-${i}.png`),fullPage:true}).catch(()=>{});}throw error;}
 finally{await browser.close();}
})().catch(error=>{console.error(error);process.exit(1);});
