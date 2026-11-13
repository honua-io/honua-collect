import {copyFile,mkdir,readdir} from 'node:fs/promises';
const source = new URL('node_modules/maplibre-gl/dist/',import.meta.url);
const target = new URL('../../src/Honua.Collect.Spike.Shared/wwwroot/vendor/',import.meta.url);
await mkdir(target,{recursive:true});
for (const file of await readdir(source)) if (file.endsWith('.mjs') || file.endsWith('.css')) await copyFile(new URL(file,source),new URL(file,target));
await copyFile(new URL('node_modules/maplibre-gl/LICENSE.txt',import.meta.url),new URL('LICENSE.txt',target));
