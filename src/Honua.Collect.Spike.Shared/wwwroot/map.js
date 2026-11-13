import * as maplibregl from './vendor/maplibre-gl.mjs';
let map;
export function clear() { if (map) map.remove(); map = undefined; }
export function show() {
  clear();
  map = new maplibregl.Map({container:'spike-map',center:[-122.67,45.52],zoom:11,
    style:{version:8,sources:{},layers:[{id:'background',type:'background',paint:{'background-color':'#e6eee9'}}]}});
  map.on('load',()=>{
    // Deterministic in-memory geometry is explicitly not the local-store pass mark.
    const features = Array.from({length:50000},(_,i)=>({type:'Feature',properties:{id:i},geometry:{type:'Point',coordinates:[-122.85+(i%250)*0.0015,45.40+Math.floor(i/250)*0.0012]}}));
    map.addSource('assets',{type:'geojson',data:{type:'FeatureCollection',features}});
    map.addLayer({id:'assets',type:'circle',source:'assets',paint:{'circle-radius':3,'circle-color':'#006b5a'}});
  });
}
export async function measure() {
  if (!map?.isStyleLoaded()) return 'Wait for the map to load';
  const current = map, intervals = [], start = performance.now();
  let last = start;
  await new Promise(resolve=>{
    function frame(now) {
      if (map !== current) { resolve(); return; }
      intervals.push(now-last); last=now;
      current.jumpTo({center:[-122.67+Math.sin((now-start)/1500)*0.03,45.52]});
      if(now-start<10000) requestAnimationFrame(frame); else resolve();
    }
    requestAnimationFrame(frame);
  });
  return JSON.stringify({probe:'animation callbacks while panning; not rendered-frame certification',durationMs:last-start,callbacks:intervals.length,callbackHz:1000*intervals.length/(last-start),userAgent:navigator.userAgent},null,2);
}
