// Chromium's tabs.setZoom changes the actual browser zoom, not CSS font-size.
const fs=require('node:fs/promises'),path=require('node:path');
const assert=require('node:assert/strict');
const {chromium}=require(process.env.NEXORUTA_PLAYWRIGHT_MODULE||path.join(__dirname,'node_modules/playwright'));
module.exports=async function nativeZoom(source,urls,output,name){
 const scratch=path.join(output,'native-zoom-'+name),extension=path.join(scratch,'extension');
 await fs.mkdir(extension,{recursive:true});
 await fs.writeFile(path.join(extension,'manifest.json'),JSON.stringify({manifest_version:3,name:'Synthetic browser zoom verification',version:'1.0',permissions:['tabs'],background:{service_worker:'background.js'}}));
 await fs.writeFile(path.join(extension,'background.js'),'chrome.runtime.onInstalled.addListener(()=>{});');
 const context=await chromium.launchPersistentContext(path.join(scratch,'profile'),{headless:true,channel:'chromium',viewport:{width:1440,height:1000},args:['--disable-extensions-except='+extension,'--load-extension='+extension]});
 try{
  await context.addCookies(await source.context().cookies());
  await context.route('**/*',route=>/^https?:/.test(route.request().url())&&!['localhost','127.0.0.1'].includes(new URL(route.request().url()).hostname)?route.abort():route.continue());
  const worker=context.serviceWorkers()[0]||await context.waitForEvent('serviceworker');
  const page=context.pages()[0];
  for(let i=0;i<urls.length;i++){
   await page.goto(urls[i]);await page.waitForLoadState('networkidle');
   const zoom=await worker.evaluate(async()=>{const [tab]=await chrome.tabs.query({active:true});await chrome.tabs.setZoom(tab.id,2);return chrome.tabs.getZoom(tab.id);});
   assert.equal(zoom,2);await page.waitForFunction(()=>innerWidth===720&&devicePixelRatio===2);
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,'Native 200% zoom page overflow');
   await page.keyboard.press('Tab');assert.ok(await page.evaluate(()=>document.activeElement!==document.body));
   // A viewport capture through CDP avoids Playwright fullPage resetting the native zoom clip.
   const cdp=await context.newCDPSession(page);
   const capture=await cdp.send('Page.captureScreenshot',{format:'png',fromSurface:true,captureBeyondViewport:false});
   await fs.writeFile(path.join(output,`zoom-nativo-${name}-${i}-200.png`),Buffer.from(capture.data,'base64'));
   await cdp.detach();
  }
 }finally{await context.close();await fs.rm(scratch,{recursive:true,force:true});}
};
