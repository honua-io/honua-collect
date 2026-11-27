const assert = require("node:assert/strict");
const { join } = require("node:path");
const { chromium } = require("playwright");

const origin = "https://today.test";
const assets = {
  "/": ["index.html", "text/html"],
  "/today.css": ["today.css", "text/css"],
  "/today.js": ["today.js", "text/javascript"],
};
const viewports = [
  { width: 390, height: 844 },
  { width: 1024, height: 768 },
];

async function checkRequest(browser, viewport, contract) {
  const page = await browser.newPage({ viewport });
  const errors = [];
  page.on("pageerror", error => errors.push(error));
  try {
    // Serve the prototype files at a stable origin so reloads exercise real browser storage.
    await page.route(origin + "/**", async route => {
      const asset = assets[new URL(route.request().url()).pathname];
      if (!asset) return route.fulfill({ status: 404 });
      const [file, contentType] = asset;
      await route.fulfill({ path: join(__dirname, file), contentType });
    });

    const byId = id => page.locator("#" + id);
    const controls = page.getByText("Prototype controls", { exact: true });
    const assertEvidence = async () => {
      assert.equal(await byId("request-title").inputValue(), "Repair hydrant cap");
      assert.equal(await byId("notes").inputValue(), "Synthetic note");
      assert.match(await byId("attachment").textContent(), /HY-104-request-sample.jpg/);
      assert.match(await byId("asset").textContent(), /HY-104 · 18 Oak Street/);
      assert.equal(await byId("evidence-status").isVisible(), contract === "title-only");
    };
    const readReceipts = () => page.evaluate(() =>
      JSON.parse(localStorage.getItem("honua-field-today-practice-v1")).receipts);

    await page.goto(origin);
    if (viewport.width > 400) await byId("map-view").click();
    await page.locator('button[data-stop="HY-104"]:visible').click();
    await byId("request").click();
    await controls.click();
    await byId("request-contract").selectOption(contract);
    await byId("notes").fill("Synthetic note");
    await byId("photo").click();
    assert.equal(await byId("queue").isDisabled(), true);
    await byId("request-title").fill("Repair hydrant cap");

    await page.reload();
    await assertEvidence();
    assert.deepEqual(await readReceipts(), {});
    await byId("queue").click();
    await controls.click();
    await byId("response").selectOption("lost");
    await byId("retry").click();
    assert.match(await byId("record-status").textContent(), /Awaiting receipt/);
    const receiptsBeforeReload = await readReceipts();
    const receiptValues = Object.values(receiptsBeforeReload);
    assert.equal(receiptValues.length, 1);
    const [receipt] = receiptValues;
    assert.match(receipt, /^R-[0-9A-F]{8}$/);

    await page.reload();
    assert.match(await byId("record-status").textContent(), /Awaiting receipt/);
    await assertEvidence();
    await controls.click();
    await byId("request-contract").selectOption(contract === "title-only" ? "full" : "title-only");
    await byId("retry").click();
    const expectedStatus = contract === "title-only"
      ? "Request accepted · asset location used · receipt " + receipt
      : "Synced · receipt " + receipt;
    assert.equal(await byId("record-status").textContent(), expectedStatus);
    await assertEvidence();

    await page.reload();
    assert.equal(await byId("record-status").textContent(), expectedStatus);
    await assertEvidence();
    await controls.click();
    await byId("retry").click();
    assert.equal(await byId("record-status").textContent(), expectedStatus);
    assert.deepEqual(await readReceipts(), receiptsBeforeReload);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    assert.deepEqual(errors, []);
    console.log("Passed", contract, "request at", viewport.width + "×" + viewport.height);
  } finally {
    await page.close();
  }
}

async function main() {
  const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE });
  try {
    for (const viewport of viewports) {
      for (const contract of ["title-only", "full"]) {
        await checkRequest(browser, viewport, contract);
      }
    }
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
