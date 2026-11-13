import {test,expect} from '@playwright/test';
test('required form validation and queued record survive reload',async({page})=>{
  await page.goto(process.env.COLLECT_SPIKE_URL ?? 'http://127.0.0.1:5280');
  await expect(page.getByRole('status')).toHaveText('Ready');
  const before = Number((await page.getByTestId('pending').innerText()).split(' ')[0]);
  await page.getByRole('button',{name:'Inspection',exact:true}).click();
  await page.getByRole('button',{name:'Queue inspection'}).click();
  await expect(page.getByRole('status')).toHaveText('Asset name is required');
  await page.getByLabel('Asset name (required)').fill('Browser journey asset');
  await page.getByLabel('Notes',{exact:true}).fill('Local persistence journey');
  await page.getByRole('button',{name:'Queue inspection'}).click();
  await expect(page.getByTestId('pending')).toHaveText(`${before+1} pending inspections`);
  await page.reload();
  await expect(page.getByTestId('pending')).toHaveText(`${before+1} pending inspections`);
});

test('map receives all stored coordinates and survives navigation',async({page})=>{
  test.setTimeout(960_000);
  const errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  await page.goto(process.env.COLLECT_SPIKE_URL ?? 'http://127.0.0.1:5280');
  await expect(page.getByRole('status')).toHaveText('Ready');
  await page.getByRole('button',{name:'Map',exact:true}).click();
  await expect(page.locator('#spike-map canvas')).toBeVisible();
  await page.getByRole('button',{name:'Prepare and load 50,000 local assets'}).click();
  await expect(page.locator('p[role="status"]')).toHaveText('Loaded 50000 points from the local record store',{timeout:900_000});
  await page.getByRole('button',{name:'Measure ten seconds of panning'}).click();
  await expect(page.getByLabel('Map measurement')).toContainText('"storedPoints": 50000',{timeout:20_000});
  await expect(page.locator('#map-status')).not.toContainText('error');
  await page.getByRole('button',{name:'Today',exact:true}).click();
  await page.getByRole('button',{name:'Map',exact:true}).click();
  await expect(page.locator('#spike-map canvas')).toBeVisible();
  expect(errors).toEqual([]);
});
