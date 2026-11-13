import * as maplibregl from './vendor/maplibre-gl.mjs';
let map, bindings, storedPoints = 0;
const protocol = new globalThis.pmtiles.Protocol();
const tileReads = [];
maplibregl.addProtocol('pmtiles', async (request, controller) => {
  const start = performance.now();
  const result = await protocol.tile(request, controller);
  tileReads.push({url:request.url,readAndDecodeMs:performance.now()-start});
  return result;
});
function report(text) {
  const output = document.querySelector('#map-status');
  if (output) output.textContent = text;
}
export function clear() {
  bindings?.abort(); bindings = undefined;
  if (map) map.remove();
  map = undefined; storedPoints = 0; tileReads.length = 0;
  // Release selected File references and caches on screen exit.
  protocol.tiles.clear();
}
export function show() {
  clear();
  bindings = new AbortController();
  map = new maplibregl.Map({container:'spike-map',center:[-122.67,45.52],zoom:11,
    // The spike never accepts a remote style or tile URL.
    transformRequest(url) {
      if (/^https?:/i.test(url)) throw new Error('Remote map requests are disabled');
      return {url};
    },
    style:{version:8,sources:{},layers:[{id:'background',type:'background',paint:{'background-color':'#e6eee9'}}]}});
  map.on('error',event=>report(`Map error: ${event.error.message}`));
  document.querySelector('#archive').addEventListener('change', async event=>{
    const current = map, file = event.target.files?.[0];
    if (!file) return;
    try {
      const source = new globalThis.pmtiles.FileSource(file);
      source.getKey = ()=>'local-archive';
      const archive = new globalThis.pmtiles.PMTiles(source);
      const header = await archive.getHeader();
      const metadata = await archive.getMetadata();
      if (map !== current) return;
      if (!current.isStyleLoaded()) await new Promise(resolve=>current.once('load',resolve));
      if (map !== current) return;
      for (const layer of current.getStyle().layers.filter(x=>x.source==='archive')) current.removeLayer(layer.id);
      if (current.getSource('archive')) current.removeSource('archive');
      protocol.tiles.clear(); protocol.add(archive); tileReads.length=0;
      if (header.tileType === 1) {
        current.addSource('archive',{type:'vector',url:'pmtiles://local-archive'});
        for (const [index,layer] of (metadata.vector_layers??[]).entries()) {
          current.addLayer({id:`archive-${index}`,type:'line',source:'archive','source-layer':layer.id,paint:{'line-color':'#80978d','line-width':1}});
        }
      } else if ([2,3,4].includes(header.tileType)) {
        current.addSource('archive',{type:'raster',url:'pmtiles://local-archive',tileSize:256});
        current.addLayer({id:'archive-raster',type:'raster',source:'archive'});
      } else throw new Error('Unsupported archive tile encoding');
      current.jumpTo({center:[header.centerLon,header.centerLat],zoom:header.centerZoom});
      report(`Local archive ${file.name}: ${file.size} bytes. File slices avoid loading the whole archive. Tile read timing excludes visible-frame latency.`);
    } catch(error) { report(`Archive failed: ${error.message}`); }
  },{signal:bindings.signal});
}
export async function assets(points) {
  if (!map) return;
  const current=map;
  if (!current.isStyleLoaded()) await new Promise(resolve=>current.once('load',resolve));
  if (map !== current) return;
  const data={type:'FeatureCollection',features:points.map((coordinates,id)=>({type:'Feature',properties:{id},geometry:{type:'Point',coordinates}}))};
  if (current.getSource('assets')) current.getSource('assets').setData(data);
  else {
    current.addSource('assets',{type:'geojson',data});
    current.addLayer({id:'assets',type:'circle',source:'assets',paint:{'circle-radius':3,'circle-color':'#006b5a'}});
  }
  storedPoints=points.length;
}
export async function measure() {
  if (!map?.isStyleLoaded()) return 'Wait for the map to load';
  if (storedPoints !== 50000) return 'Load 50,000 assets from the record store first';
  const current=map, start=performance.now(), center=current.getCenter();
  let last=start, renderedEvents=0;
  const rendered=()=>{renderedEvents++;};
  current.on('render',rendered);
  await new Promise(resolve=>{
    function frame(now) {
      if (map !== current) { resolve(); return; }
      last=now;
      current.jumpTo({center:[center.lng+Math.sin((now-start)/1500)*0.03,center.lat]});
      if(now-start<10000) requestAnimationFrame(frame); else resolve();
    }
    requestAnimationFrame(frame);
  });
  current.off('render',rendered);
  return JSON.stringify({probe:'MapLibre render events while panning; corroborate with platform frame profiler',storedPoints,durationMs:last-start,renderedEvents,renderEventsPerSecond:1000*renderedEvents/(last-start),tileReads,userAgent:navigator.userAgent},null,2);
}
