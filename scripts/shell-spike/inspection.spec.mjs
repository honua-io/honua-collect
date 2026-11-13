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
