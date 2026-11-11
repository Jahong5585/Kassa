import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createServer} from 'node:http';
import {createRequire} from 'node:module';
import {warehouseDemo} from '../web/warehouse-demo.js';
import {defaultSupplier, managementDemo, storeDay} from '../web/management-demo.js';
import {DemoApi, RequestError, demoRegister, lineTotal, money, quantity, scaleQuantity, refundCents} from '../web/model.js';

const require = createRequire(import.meta.url);
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const expectedTypes = {'html': 'text/html', 'css': 'text/css', 'js': 'text/javascript'};
const server = createServer(async (request, response) => {
  const name = request.url === '/' ? 'index.html' : request.url.slice(1);
  if (!/^[a-z0-9-]+\.(html|css|js)$/.test(name)) { response.writeHead(404).end(); return; }
  try {
    const bytes = await readFile(new URL('../web/' + name, import.meta.url));
    response.writeHead(200, {'Content-Type': expectedTypes[name.split('.').at(-1)]}).end(bytes);
  } catch { response.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = 'http://127.0.0.1:' + server.address().port;

assert.equal(money('1234,56'), 123456);
assert.equal(money('1,234'), null);
assert.equal(quantity('0,012'), 12);
assert.equal(scaleQuantity('0,375 kg'),375);
assert.equal(scaleQuantity('375 g'),375);
assert.equal(scaleQuantity('0.375 кг'),375);
assert.equal(scaleQuantity('0.2 g'),null);
assert.equal(scaleQuantity('375 ml'),null);
assert.equal(lineTotal(105, 100), 11);
assert.equal(lineTotal(1640000, 375), 615000);
const discountedLine = {soldAmount: 0.05, soldQuantity: 3, returnedAmount: 0.02, returnedQuantity: 1};
assert.equal(refundCents(discountedLine, 1000), 1);
assert.equal(refundCents(discountedLine, 2000), 3);

const cola = '55000000-0000-4000-8000-000000000020';
const forecastLedger = JSON.stringify({
  goods:[{productId:cola,name:'Кола',unit:'piece',requiresExpiry:false,lots:[{quantity:25}]}],
  sales:[],supplierRegistry:[defaultSupplier],supplierQuotes:[{code:'DEMO',supplierSku:'COLA5',productId:cola,
    unitsPerPackage:5,packagePrice:20000,version:1,validFrom:storeDay()}],
});
const forecastStorage={getItem:()=>forecastLedger,setItem:()=>assert.fail('Forecast must be read-only')};
const forecast=managementDemo(forecastStorage,'admin','recommend','DEMO|COLA5',
  {historyDays:28,minimumHistoryDays:7,coverageDays:5,safetyStock:5,minimumPackages:1,manualDailyDemand:20});
assert.equal(forecast.projection.stockAtArrival,5);
assert.equal(forecast.projection.targetQuantity,105);
assert.equal(forecast.suggestedQuantity,100);

const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_PATH,
  headless: true,
  args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-gpu'],
});
const faults = [];
const watch = page => { page.setDefaultTimeout(6500); page.on("pageerror", error => faults.push(String(error))); };
const button = (page, name) => page.getByRole('button', {name, exact: true});
const text = (page, value) => page.getByText(value, {exact: true});
const normalize = value => value.replace(/[\s\u00a0]/g, '');
const scan = async (page, input) => {
  const field = page.getByRole('searchbox');
  await field.fill(input);
  await field.press('Enter');
};
const selectRole = (page, role) => page.getByLabel('Текущая роль').selectOption(role);
const allowStoreChanges = async page => {
  await button(page, 'Настройки').click();
  const protection = page.getByLabel('Запретить случайное изменение данных');
  assert.equal(await protection.isChecked(), true);
  page.once('dialog', dialog => dialog.accept());
  await protection.uncheck();
  await text(page, '◷ Изменения разрешены').waitFor();
};
const opening = async page => {
  await button(page, 'Открыть смену').click();
  await page.getByLabel('Начальный размен, сум').fill('50000');
  await page.locator('#amount-confirm').click();
  await text(page, '● Смена открыта').waitFor();
};
const pay = async (page, tendered) => {
  await page.keyboard.press('F2');
  await page.getByLabel('Покупатель внёс, сум').fill(tendered);
  await button(page, 'Подтвердить оплату').click();
};

try {
  const demo = await browser.newPage({viewport: {width: 1366, height: 768}});
  watch(demo);
  await demo.goto(url);
  assert.equal(await demo.locator('#pay').isDisabled(), true);
  await opening(demo);
  await scan(demo, '9999999999999');
  await demo.locator('#alert').getByText(/Товар не найден/).waitFor();
  await scan(demo, '4780011001001');
  await demo.getByRole('heading', {name: 'Выберите партию'}).waitFor();
  assert.equal(await demo.getByLabel('Партия', {exact: true}).inputValue(), '');
  await demo.getByLabel('Партия', {exact: true}).selectOption({index: 1});
  await button(demo, 'Добавить в чек').click();
  await demo.locator('#basket td').filter({hasText: 'Молоко 3,2% · 1 л'}).waitFor();
  await scan(demo, 'яблоки');
  await button(demo, 'Выбрать').click();
  await demo.getByLabel('Вес, кг').fill('0,375');
  await button(demo, 'Добавить в чек').click();
  await demo.locator('#basket td').filter({hasText: 'Яблоки красные'}).waitFor();
  assert.equal(await demo.locator('#basket tr').filter({hasText:'Яблоки красные'}).locator('.quantity-step').count(),0);
  await scan(demo, '4780011001002');
  await demo.locator('#basket td').filter({hasText: 'Хлеб пшеничный'}).waitFor();
  await button(demo, 'Уменьшить на 1 · Хлеб пшеничный').click();
  await demo.locator('#basket tr').filter({hasText:'Хлеб пшеничный'}).waitFor({state:'detached'});
  await scan(demo, '4780011001002');
  await button(demo, 'Увеличить на 1 · Хлеб пшеничный').click();
  await demo.waitForFunction(() => document.querySelector('[aria-label="Количество · Хлеб пшеничный"]').value === '2');
  assert.equal(normalize(await demo.locator('#total').textContent()), '31650');

  if (process.env.KASA_QA_DIR) await demo.screenshot({path: process.env.KASA_QA_DIR + '/sale.png'});
  await demo.reload();
  assert.equal(normalize(await demo.locator('#total').textContent()), '31650');
  await pay(demo, '31000');
  await text(demo, 'Внесённая сумма меньше итога чека.').waitFor();
  await button(demo, 'Отмена').click();
  await button(demo, 'Настройки').click();
  await demo.getByLabel('Потерять ответ на следующую продажу').check();
  await button(demo, 'Продажа').click();
  await pay(demo, '40000');
  await demo.getByRole('heading', {name: 'Чек сохранён'}).waitFor();
  assert.match(await demo.locator('#receipt-paper').textContent(), /Тестовый • нефискальный/);
  assert.match(normalize(await demo.locator('#receipt-paper').textContent()), /сдача8350сум/);
  if (process.env.KASA_QA_DIR) {
    await demo.screenshot({path: process.env.KASA_QA_DIR + '/receipt.png'});
    await demo.pdf({path: process.env.KASA_QA_DIR + '/receipt.pdf'});
  }
  await button(demo, 'Закрыть').last().click();
  await button(demo, 'Чеки').click();
  assert.equal(await demo.locator('#history-rows tr').count(), 1);
  await button(demo, 'Открыть').click();
  await demo.getByRole('heading', {name: 'Просмотр чека'}).waitFor();
  await button(demo, 'Закрыть').last().click();
  await button(demo, 'Смена').click();
  assert.equal(await demo.locator('#metric-sales').textContent(), '1');
  assert.equal(normalize(await demo.locator('#metric-cash').textContent()), '81650');
  await button(demo, 'Закрыть смену').click();
  await demo.getByLabel('Фактически пересчитано, сум').fill('81500');
  await demo.locator('#amount-confirm').click();
  await text(demo, '○ Смена закрыта').waitFor();
  assert.match(normalize(await demo.locator('#shift-detail').textContent()), /Расхождение-150сум/);

  for (const viewport of [{width: 1280, height: 720}, {width: 1024, height: 550}, {width: 880, height: 480}]) {
    await demo.setViewportSize(viewport);
    await button(demo, 'Продажа').click();
    assert.equal(await demo.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await button(demo, 'Открыть смену').click();
    assert.equal(await button(demo, 'Отмена').isVisible(), true);
    await button(demo, 'Отмена').click();
    await demo.locator('#pay').scrollIntoViewIfNeeded();
    assert.equal(await demo.locator('#pay').isVisible(), true);
  }

  const stage2 = await browser.newPage({viewport: {width: 1280, height: 720}});
  watch(stage2);
  await stage2.goto(url);
  await opening(stage2);
  await scan(stage2, '4780011001003');
  await stage2.getByLabel('Количество · Яйца · 10 шт.').fill('2');
  await stage2.getByLabel('Количество · Яйца · 10 шт.').press('Enter');
  await stage2.waitForFunction(() => document.querySelector('#total').textContent.replace(/\s/g, '') === '36000');
  await pay(stage2, '40000');
  await stage2.getByRole('heading', {name: 'Чек сохранён'}).waitFor();
  const saleId = (await stage2.locator('#receipt-paper .receipt-id').textContent()).slice(4);
  await button(stage2, 'Закрыть').last().click();
  await button(stage2, 'Чеки').click();
  await stage2.getByRole('searchbox', {name: 'Полный ID чека'}).fill(saleId);
  await button(stage2, 'Найти чек').click();
  const refund = async (units, reason) => {
    await button(stage2, 'Возврат').click();
    await stage2.getByLabel(/К возврату · Яйца/).fill(units);
    await stage2.getByLabel('Причина возврата').fill(reason);
  };
  await refund('3', 'Неверное количество');
  assert.equal(await button(stage2, 'Оформить возврат').isDisabled(), true);
  await stage2.getByLabel(/К возврату · Яйца/).fill('1');
  await stage2.getByLabel('Причина возврата').fill('Первый возврат');
  assert.equal(normalize(await stage2.locator('#refund-total').textContent()), '18000сум');
  if (process.env.KASA_QA_DIR) await stage2.screenshot({path: process.env.KASA_QA_DIR + '/refund-stage2.png'});
  await button(stage2, 'Оформить возврат').click();
  await stage2.getByRole('heading', {name: 'Возврат сохранён'}).waitFor();
  assert.match(await stage2.locator('#receipt-paper').textContent(), /Карантин/);
  await button(stage2, 'Закрыть').last().click();
  await stage2.locator('#history-rows').getByText(/Частичный/).waitFor();
  await refund('1', 'Второй возврат');
  await stage2.getByLabel(/Куда вернуть · Яйца/).selectOption('stock');
  await button(stage2, 'Оформить возврат').click();
  await stage2.locator('#pending').getByText(/Нужно подтверждение/).waitFor();
  await stage2.reload();
  await button(stage2, 'Учебное подтверждение').click();
  await button(stage2, 'Завершить возврат').click();
  await stage2.getByRole('heading', {name: 'Возврат сохранён'}).waitFor();
  assert.match(await stage2.locator('#receipt-paper').textContent(), /На остаток/);
  await button(stage2, 'Закрыть').last().click();
  await button(stage2, 'Чеки').click();
  await stage2.locator('#history-rows').getByText('Полный', {exact: true}).waitFor();
  assert.equal(await button(stage2, 'Возврат').count(), 0);

  await button(stage2, 'Смена').click();
  const movement = async (label, amount, reason) => {
    await button(stage2, label).click();
    await stage2.getByLabel('Сумма, сум').fill(amount);
    await stage2.getByLabel('Причина операции').fill(reason);
    await button(stage2, 'Записать операцию').click();
  };
  await movement('Внести наличные', '5000', 'Размен');
  await stage2.locator('#movement-rows').getByText('Размен', {exact: true}).waitFor();
  await movement('Изъять наличные', '2000', 'Инкассация');
  await stage2.locator('#movement-rows').getByText('Инкассация', {exact: true}).waitFor();
  await movement('Изъять наличные', '60000', 'Сверх остатка');
  await stage2.locator('#alert').getByText(/недостаточно наличных/).waitFor();
  assert.equal(await stage2.locator('#movement-rows tr').count(), 2);
  assert.equal(normalize(await stage2.locator('#metric-cash').textContent()), '53000');
  if (process.env.KASA_QA_DIR) await stage2.screenshot({path: process.env.KASA_QA_DIR + '/cash-stage2.png'});

  await button(stage2, 'Сроки').click();
  await button(stage2, 'Сегодня').click();
  await stage2.locator('#expiry-rows .badge').getByText('Сегодня', {exact: true}).waitFor();
  await button(stage2, 'Нет даты').click();
  await stage2.locator('#expiry-rows .badge').getByText('Нет даты', {exact: true}).waitFor();
  await button(stage2, 'Просрочено').click();
  const stockBefore = await stage2.locator('#expiry-rows td.numeric').textContent();
  await button(stage2, 'Снято с полки').click();
  await stage2.getByLabel('Снятое количество, шт.').fill('2');
  await button(stage2, 'Передать задачу').click();
  await stage2.locator('#expiry-rows').getByText(/Задача создана/).waitFor();
  assert.equal(await stage2.locator('#expiry-rows td.numeric').textContent(), stockBefore);
  if (process.env.KASA_QA_DIR) await stage2.screenshot({path: process.env.KASA_QA_DIR + '/expiry-stage2.png'});
  await button(stage2, 'Снято с полки').click();
  await stage2.getByLabel('Снятое количество, шт.').fill('1');
  await button(stage2, 'Передать задачу').click();
  await stage2.locator('#alert').getByText(/уже есть открытая задача/).waitFor();

  const online = await browser.newPage({viewport: {width: 1280, height: 720}});
  watch(online);
  const ledger = new Map();
  const fixture = new DemoApi({getItem: key => ledger.get(key), setItem: (key, value) => ledger.set(key, value)});
  let stored = null;
  let loss = true;
  let shiftOffline = false;
  let defaultPrinter = null;
  let repositoryValid = true;
  let packageError = 'printer_package_hash_mismatch';
  let devices = [{name:'Xprinter XP-58IIH', hardwareId:'USB\\VID_FFFF&PID_0123', status:28, model:'xprinter-xp-58iih'}];
  let heldScanWrite = null;
  let checks = 0;
  const loseAfterSave = new Set(['createRefund', 'createCash', 'shelf']);
  const requests = [];
  await online.exposeFunction('standCall', async ({action, data}) => {
    requests.push({action, data});
    try {
      if (action === 'connect') return {data: {register:demoRegister, server:'stand.invalid'}};
      if (action === 'load') return {data: stored};
      if (action === 'save') {
        if (heldScanWrite && JSON.parse(data.value).basket.length) {
          const hold = heldScanWrite;
          heldScanWrite = null;
          hold.started();
          await hold.release;
        }
        stored = data.value;
        return {data:true};
      }
      if (action === 'device.printers') return {data:{defaultPrinter,installed:['Учебный принтер']}};
      if (action === 'device.printerDrivers') return {data:{devices, usbAvailable:true, repositoryValid, models:[
        {id:'xprinter-xp-58iih', name:'Xprinter XP-58IIH', host:'www.xprintertech.com', download:null},
        {id:'rongta-rp58e', name:'Rongta RP58E', host:'www.rongtatech.com', download:repositoryValid ? 'server' : null},
        {id:'epson-tm-t20iii', name:'Epson TM-T20III', host:'download-center.epson.com', download:'vendor', terms:true},
      ]}};
      if (action === 'device.printerSupport' || action === 'device.printerTerms') return {data:{status:'browser'}};
      if (action === 'device.installPrinterDriver') {
        if (packageError) throw new RequestError(409, packageError);
        return {data:{status:'started'}};
      }
      if ((action === 'shift' || action === 'quote') && shiftOffline) throw new RequestError(0, 'connection_failed');
      if (action === 'commit' && loss) { loss = false; throw new RequestError(0, 'timeout'); }
      if (action === 'sale' && checks++ === 0) throw new RequestError(503, 'busy');
      const result = await fixture.call(action, data.value, data.body);
      if (loseAfterSave.delete(action)) throw new RequestError(0, 'timeout');
      return {data: result};
    } catch (e) { return {error: {status: e.status, code: e.code}}; }
  });
  await online.addInitScript(() => {
    const listeners = new EventTarget();
    const chrome = window.chrome || (window.chrome = {});
    Object.defineProperty(chrome, 'webview', {value: {
      addEventListener: (...args) => listeners.addEventListener(...args),
      async postMessage(message) {
        const result = await window.standCall(message);
        listeners.dispatchEvent(new MessageEvent('message', {data: {id: message.id, result}}));
      },
    }});
  });
  await online.goto(url);
  await text(online, '◷ Принтер не проверен').waitFor();
  await button(online, 'Настройки').click();
  await button(online, 'Проверить принтеры').click();
  await text(online, '◷ Принтер не назначен').waitFor();
  defaultPrinter = 'Учебный принтер';
  await button(online, 'Проверить принтеры').click();
  await text(online, '● Принтер назначен').waitFor();
  await button(online, 'Найти USB-принтеры и драйверы').click();
  const usbList = online.locator('#device-usb-list');
  const driverPicker = online.locator('#device-driver-model');
  await usbList.getByText(/Xprinter XP-58IIH.*Драйвер не установлен/).waitFor();
  assert.equal(await driverPicker.inputValue(), 'xprinter-xp-58iih');
  await button(online, 'Открыть драйвер производителя').click();
  await text(online, 'В браузере открыта официальная страница драйвера.').waitFor();
  assert.equal(requests.at(-1).data.value, 'xprinter-xp-58iih');

  devices = [{name:'USB Printing Support', hardwareId:'USB\\Class_07', status:0, model:null}];
  await button(online, 'Найти USB-принтеры и драйверы').click();
  await usbList.getByText(/USB Printing Support/).waitFor();
  assert.equal(await driverPicker.inputValue(), '');
  assert.equal(await button(online, 'Открыть драйвер производителя').isDisabled(), true);
  await driverPicker.selectOption('rongta-rp58e');
  await button(online, 'Установить драйвер с сервера').click();
  await text(online, 'Контрольная сумма драйвера не совпала. Установщик заблокирован.').waitFor();
  assert.equal(requests.at(-1).data.value, 'rongta-rp58e');
  packageError = null;
  await button(online, 'Установить драйвер с сервера').click();
  await online.locator('#device-driver-status').getByText(/Установщик запущен/).waitFor();

  repositoryValid = false;
  devices = [];
  await button(online, 'Найти USB-принтеры и драйверы').click();
  await online.locator('#device-driver-status').getByText(/USB-принтеры не найдены.*PrinterDrivers.json/).waitFor();
  await driverPicker.selectOption('rongta-rp58e');
  await button(online, 'Открыть драйвер производителя').waitFor();
  await driverPicker.selectOption('epson-tm-t20iii');
  await button(online, 'Условия лицензии').click();
  assert.equal(requests.at(-1).action, 'device.printerTerms');
  await button(online, 'Скачать и установить драйвер').click();
  await online.locator('#device-driver-status').getByText(/Установщик запущен/).waitFor();
  assert.equal(requests.at(-1).data.value, 'epson-tm-t20iii');
  await button(online, 'Подключить учебный стенд').click();
  await text(online, '◉ Учебный API · stand.invalid').waitFor();
  await button(online, 'Продажа').click();
  await text(online, '● API на связи').waitFor();
  assert.equal(await online.locator('#printer-status').getAttribute('title'), defaultPrinter);
  assert.equal(await online.evaluate(() => {
    return ['api-status','printer-status'].every(id => {
      const rect = document.getElementById(id).getBoundingClientRect();
      return rect.width > 0 && rect.right <= innerWidth && rect.bottom <= innerHeight;
    });
  }), true);
  await opening(online);
  let startScanWrite;
  let releaseScanWrite;
  const writeStarted = new Promise(resolve => { startScanWrite = resolve; });
  const writeRelease = new Promise(resolve => { releaseScanWrite = resolve; });
  heldScanWrite = {started:startScanWrite,release:writeRelease};
  try {
    await scan(online, '4780011001002');
    await Promise.race([writeStarted, new Promise((_, reject) =>
      setTimeout(() => reject(Error('Scan did not reach the journal')), 3000).unref())]);
    await online.evaluate(() => {
      const field = document.querySelector('#search');
      if (field.disabled || document.activeElement !== field) throw Error('Scanner lost focus during journal write');
      for (let i = 0; i < 29; i++) {
        field.value = '4780011001002';
        document.querySelector('#scan-form').requestSubmit();
      }
    });
    assert.equal(await online.locator('#pay').isDisabled(), true);
    assert.equal(await online.locator('#clear-basket').isDisabled(), true);
  } finally { releaseScanWrite(); }
  await online.waitForFunction(() => document.querySelector('#basket input')?.value === '30');
  let firstStepWritten;
  let releaseStep;
  const stepWritten = new Promise(resolve => { firstStepWritten = resolve; });
  const stepRelease = new Promise(resolve => { releaseStep = resolve; });
  heldScanWrite = {started:firstStepWritten,release:stepRelease};
  try {
    await button(online, 'Увеличить на 1 · Хлеб пшеничный').click();
    await Promise.race([stepWritten,new Promise((_,reject)=>
      setTimeout(()=>reject(Error('Quantity button did not reach the journal')),3000).unref())]);
    assert.equal(await online.locator('#pay').isDisabled(),true);
    await button(online, 'Увеличить на 1 · Хлеб пшеничный').click();
    await button(online, 'Увеличить на 1 · Хлеб пшеничный').click();
    await button(online, 'Уменьшить на 1 · Хлеб пшеничный').click();
  } finally { releaseStep(); }
  await online.waitForFunction(() => document.querySelector('#basket input')?.value === '32');
  assert.equal(JSON.parse(stored).basket[0].units,32000);
  await button(online, 'Очистить чек').click();
  await online.locator('#confirm-clear').click();
  await text(online, 'Корзина пуста. Добавьте первый товар.').waitFor();
  await scan(online, '4780011001002');
  await online.locator('#basket td').filter({hasText: 'Хлеб пшеничный'}).waitFor();
  shiftOffline = true;
  await online.keyboard.press('F2');
  await online.locator('#alert').getByText(/Оплата недоступна\. Корзина сохранена/).waitFor();
  await text(online, '◷ API не отвечает').waitFor();
  assert.equal(await online.locator('#amount-dialog').isVisible(), false);
  assert.equal(await online.getByRole('searchbox').evaluate(input => input === document.activeElement), true);
  assert.equal(JSON.parse(stored).pending, null);
  assert.equal(JSON.parse(stored).basket.length, 1);
  assert.equal(requests.some(row => row.action === 'commit'), false);
  shiftOffline = false;
  await online.keyboard.press('F2');
  await text(online, '● API на связи').waitFor();
  await online.getByLabel('Покупатель внёс, сум').fill('10000');
  await button(online, 'Подтвердить оплату').click();
  await text(online, 'Операция ожидает подтверждения. Проверьте статус перед следующей продажей.').waitFor();
  assert.equal(await online.locator('#pay').isDisabled(), true);
  assert.ok(JSON.parse(stored).pending);
  await online.reload();
  await button(online, 'Настройки').click();
  await button(online, 'Подключить учебный стенд').click();
  await button(online, 'Проверить статус').click();
  await button(online, 'Повторить ту же операцию').click();
  await online.getByRole('heading', {name:'Чек сохранён'}).waitFor();
  const posts = requests.filter(r => r.action === 'commit');
  assert.equal(posts.length, 2);
  assert.deepEqual(posts[0].data.body, posts[1].data.body);
  assert.equal(requests.find(r => r.action === 'sale').data.value, posts[0].data.body.operationId);
  assert.equal(JSON.parse(stored).pending, null);
  assert.equal(JSON.parse(stored).basket.length, 0);
  await button(online, 'Закрыть').last().click();
  await button(online, 'Чеки').click();
  await button(online, 'Возврат').click();
  await online.getByLabel(/К возврату · Хлеб/).fill('1');
  await online.getByLabel('Причина возврата').fill('Потеря ответа на возврат');
  await button(online, 'Оформить возврат').click();
  await online.getByRole('heading', {name: 'Возврат сохранён'}).waitFor();
  const refundPost = requests.find(row => row.action === 'createRefund');
  assert.equal(requests.find(row => row.action === 'refund').data.value, refundPost.data.body.operationId);
  assert.equal(requests.filter(row => row.action === 'createRefund').length, 1);
  await button(online, 'Закрыть').last().click();

  await button(online, 'Смена').click();
  await button(online, 'Внести наличные').click();
  await online.getByLabel('Сумма, сум').fill('100');
  await online.getByLabel('Причина операции').fill('Сверка внесения');
  await button(online, 'Записать операцию').click();
  await online.locator('#movement-rows').getByText('Сверка внесения', {exact: true}).waitFor();
  assert.equal(requests.filter(row => row.action === 'createCash').length, 1);
  await button(online, 'Сроки').click();
  await button(online, 'Просрочено').click();
  await button(online, 'Снято с полки').click();
  await online.getByLabel('Снятое количество, шт.').fill('1');
  await button(online, 'Передать задачу').click();
  await button(online, 'Подтвердить той же заявкой').click();
  await online.locator('#expiry-rows').getByText(/Задача создана/).waitFor();
  const shelfPosts = requests.filter(row => row.action === 'shelf');
  assert.equal(shelfPosts.length, 2);
  assert.deepEqual(shelfPosts[0].data.body, shelfPosts[1].data.body);
  const store = await browser.newPage({viewport: {width: 1366, height: 768}});
  watch(store);
  await store.goto(url);
  await button(store, 'Приёмка').click();
  assert.equal(await button(store, 'Новая накладная').isDisabled(), true);
  await allowStoreChanges(store);
  await button(store, 'Приёмка').click();
  await button(store, 'Новая накладная').click();
  await store.getByLabel('Номер накладной').fill('DEMO-012');
  await store.getByRole('combobox', {name: 'Заказ поставщику'}).selectOption({index: 1});
  await store.locator('#w-order-summary').getByText(/доступно по заказу 12/).waitFor();
  for (const [index, units] of [[1, '7'], [2, '5']]) {
    await store.getByRole('searchbox', {name: 'Штрихкод товара'}).fill('4780011001002');
    await button(store, 'Добавить партию').click();
    await store.getByLabel('Срок партии ' + index).fill('2099-11-0' + index);
    await store.getByLabel('Факт партии ' + index).fill(units);
  }
  await button(store, 'Сохранить черновик').click();
  await store.getByRole('heading', {name: 'Накладная DEMO-012'}).waitFor();
  assert.equal(await button(store, 'На проверку').isDisabled(), true);
  assert.equal(await button(store, 'Провести накладную').isVisible(), false);
  assert.equal(await store.locator('#w-cost-panel').isVisible(), false);
  if (process.env.KASA_QA_DIR) await store.screenshot({path: process.env.KASA_QA_DIR + '/receiving-stage3.png'});
  const breadStock = () => store.evaluate(() => JSON.parse(localStorage.getItem('kasa-demo-ledger-v1')).goods[1].lots
    .reduce((sum, lot) => sum + lot.quantity, 0));
  assert.equal(await breadStock(), 58);
  await button(store, 'Приложить учебный образец').click();
  await store.locator('#w-files').getByText(/Проверен/).waitFor();
  await button(store, 'На проверку').click();
  await store.locator('#w-receipt-note').getByText(/На проверке/).waitFor();
  assert.equal(await breadStock(), 58);
  const openReceipt = async role => {
    await button(store, role).click();
    await button(store, 'Открыть накладную').click();
    await store.getByRole('heading', {name:'Накладная DEMO-012'}).waitFor();
  };
  await selectRole(store, 'admin');
  await button(store, 'Открыть накладную').click();
  await store.getByRole('heading', {name:'Накладная DEMO-012'}).waitFor();
  await button(store, 'Вернуть на правку').click();
  await store.getByLabel('Причина', {exact:true}).fill('Сверить упаковку');
  await button(store, 'Подтвердить').click();
  await store.locator('#w-receipt-note').getByText(/Сверить упаковку/).waitFor();
  await selectRole(store, 'receiver');
  await button(store, 'Открыть накладную').click();
  await store.getByRole('heading', {name:'Накладная DEMO-012'}).waitFor();
  await store.locator('#w-receipt-note').getByText(/Сверить упаковку/).waitFor();
  await store.getByLabel('Факт партии 1').fill('6');
  await store.getByLabel('Факт партии 2').fill('6');
  await button(store, 'Сохранить черновик').click();
  await store.locator('#w-message').getByText(/Изменения черновика/).waitFor();
  await button(store, 'На проверку').click();
  await store.locator('#w-receipt-note').getByText(/На проверке/).waitFor();
  await selectRole(store, 'admin');
  await button(store, 'Открыть накладную').click();
  await store.getByRole('heading', {name:'Накладная DEMO-012'}).waitFor();
  assert.equal(await button(store, 'Провести накладную').isDisabled(), true);
  await store.getByLabel('Цена партии 1').fill('4500');
  await store.getByLabel('Цена партии 2').fill('4500');
  await store.getByLabel('Налог партии 2').fill('200');
  await button(store, 'Сохранить стоимость').click();
  await store.locator('#w-cost-summary').getByText(/54[\s\u00a0]200/).waitFor();
  const readyLedger = await store.evaluate(() => localStorage.getItem('kasa-demo-ledger-v1'));
  await button(store, 'Провести накладную').click();
  await button(store, 'Провести').click();
  await store.locator('#w-receipt-note').getByText(/Товар на остатке/).waitFor();
  assert.equal(await breadStock(), 70);
  assert.equal(await button(store, 'Провести накладную').isVisible(), false);
  await button(store, 'К списку').click();
  await button(store, 'Открыть накладную').click();
  assert.equal(await breadStock(), 70);

  await button(store, 'Остатки').click();
  const receivedLot = store.locator('#w-list-body tr').filter({hasText:'Хлеб пшеничный'}).filter({hasText:'01.11.2099'});
  await receivedLot.getByRole('button', {name:'Движение'}).click();
  await store.locator('#w-dialog').getByLabel('Количество', {exact:true}).fill('2');
  await store.getByLabel('Причина', {exact:true}).fill('Повреждение упаковки');
  await button(store, 'Провести движение').click();
  await store.locator('#w-message').getByText(/движение проведено/).waitFor();
  assert.match(await receivedLot.textContent(), /4 шт\.2 шт\./);
  await receivedLot.getByRole('button', {name:'Пересчитать'}).click();
  await store.getByLabel('Причина', {exact:true}).fill('Контроль партии');
  await button(store, 'Подтвердить').click();
  await store.getByLabel('Фактический остаток 1').fill('3');
  await store.getByLabel('Фактический карантин 1').fill('2');
  await button(store, 'Сохранить пересчёт').click();
  await store.locator('#w-inventory-note').getByText(/Пересчитан/).waitFor();
  await button(store, 'Провести пересчёт').click();
  await button(store, 'Провести').click();
  await store.locator('#w-inventory-note').getByText(/Проведён/).waitFor();
  await button(store, 'Остатки').click();
  await receivedLot.waitFor();
  assert.match(await receivedLot.textContent(), /3 шт\.2 шт\./);
  if (process.env.KASA_QA_DIR) await store.screenshot({path: process.env.KASA_QA_DIR + '/stock-stage3.png'});
  const storeMock = await browser.newPage({viewport: {width:1280, height:720}});
  watch(storeMock);
  const warehouseState = new Map([['kasa-demo-ledger-v1', readyLedger]]);
  const warehouseStorage = {getItem:key => warehouseState.get(key), setItem:(key, data) => warehouseState.set(key, data)};
  const storeCalls = [];
  let lastJournal = null;
  let activeRole = null;
  await storeMock.exposeFunction('storeStand', async ({action, data}) => {
    try {
      if (action === 'store.connect') {
        activeRole = data.value;
        return {data:{role:activeRole, server:'warehouse.invalid', storeId:'50000000-0000-4000-8000-000000000001'}};
      }
      if (action === 'store.load') return {data:lastJournal};
      if (action === 'store.save') { lastJournal = data.value; return {data:true}; }
      if (action === 'store.disconnect') { activeRole = null; return {data:true}; }
      const result = warehouseDemo(warehouseStorage, activeRole, action.slice(6), data.value, data.body);
      if (action === 'store.post') {
        storeCalls.push(data);
        if (storeCalls.length === 1) throw new RequestError(0, 'timeout');
      }
      return {data:result};
    } catch (err) { return {error:{status:err.status, code:err.code}}; }
  });
  await storeMock.addInitScript(() => {
    const events = new EventTarget();
    const chrome = window.chrome || (window.chrome = {});
    Object.defineProperty(chrome, 'webview', {value:{
      addEventListener:(...args) => events.addEventListener(...args),
      async postMessage(message) {
        const result = await window.storeStand(message);
        events.dispatchEvent(new MessageEvent('message', {data:{id:message.id, result}}));
      },
    }});
  });
  await storeMock.goto(url);
  await allowStoreChanges(storeMock);
  await selectRole(storeMock, 'admin');
  await storeMock.locator('#w-mode').getByText(/warehouse.invalid/).waitFor();
  await button(storeMock, 'Открыть накладную').click();
  await button(storeMock, 'Провести накладную').click();
  await button(storeMock, 'Провести').click();
  await button(storeMock, 'Сверить операцию').waitFor();
  assert.equal(JSON.parse(lastJournal).action, 'post');
  await storeMock.reload();
  await selectRole(storeMock, 'admin');
  await button(storeMock, 'Сверить операцию').click();
  await storeMock.locator('#w-receipt-note').getByText(/Товар на остатке/).waitFor();
  assert.equal(storeCalls.length, 2);
  assert.deepEqual(storeCalls[0], storeCalls[1]);
  const savedLedger = JSON.parse(warehouseState.get('kasa-demo-ledger-v1'));
  assert.equal(savedLedger.goods[1].lots.reduce((sum, lot) => sum + lot.quantity, 0), 70);
  assert.equal(JSON.parse(lastJournal), null);
  assert.throws(() => warehouseDemo(warehouseStorage, 'receiver', 'post', savedLedger.receipts[0].receiptId,
    {expectedVersion:savedLedger.receipts[0].version - 1}), {status:403});
  assert.throws(() => warehouseDemo(warehouseStorage, 'receiver', 'costs', savedLedger.receipts[0].receiptId),
    {status:403});
  const manage=store.locator('#w-management');
  const dialog=store.locator('#w-dialog');
  const mb=(name)=>manage.getByRole('button',{name,exact:true});
  const db=(name)=>dialog.getByRole('button',{name,exact:true});
  const input=(label)=>dialog.getByLabel(label,{exact:true});
  await selectRole(store,'receiver');
  assert.equal(await button(store,'Управление').isVisible(),false);
  await button(store,'Новая накладная').click();
  await button(store,'Предложить товар').click();
  await input('Название товара').fill('Чай по предложению');
  await input('Штрихкод').fill('4780000000781');
  await db('Передать предложение').click();
  await store.locator('#w-message').getByText(/Предложение зарегистрировано/).waitFor();
  await selectRole(store,'admin');
  await button(store,'Управление').click();
  await mb('Предложения').click();
  await manage.getByText('Чай по предложению',{exact:true}).waitFor();
  await mb('Утвердить').click();
  await db('Утвердить').click();
  await store.locator('#w-message').getByText(/Карточка опубликована/).waitFor();
  await mb('Каталог').click();
  await mb('Новый товар').click();
  await input('Название товара').fill('Кола 0,5 л');
  await input('Артикул').fill('COLA');
  await input('Штрихкоды').fill('4780000000782;4780000000783*5');
  await db('Сохранить товар').click();
  await manage.getByRole('heading',{name:'Кола 0,5 л'}).waitFor();
  const productId=(await manage.locator('#m-detail').textContent()).match(/ID ([0-9a-f-]+)/)[1];
  await mb('Изменить цену').click();
  await input('Розничная цена, сум').fill('9500');
  await input('Причина изменения').fill('Учебный прайс');
  await db('Сохранить цену').click();
  await manage.getByText('Учебный прайс',{exact:true}).waitFor();
  assert.equal(await store.evaluate(()=>JSON.parse(localStorage.getItem('kasa-demo-ledger-v1')).goods.find(p=>p.sku==='COLA').unitPrice),9500);
  await mb('К списку').click();
  await mb('Импорт CSV').click();
  const csvInput=input('Содержимое CSV');
  const importId='55000000-0000-4000-8000-000000000010';
  await csvInput.fill((await csvInput.inputValue())+importId+',0,"Сок, 1 л",piece,false,JUICE,,,,,4780000000784,15000,1');
  await db('Проверить CSV').click();
  await manage.getByRole('heading',{name:'Предпросмотр импорта'}).waitFor();
  assert.equal(await store.evaluate(id=>JSON.parse(localStorage.getItem('kasa-demo-ledger-v1')).goods.some(row=>row.productId===id),importId),false);
  await mb('Применить CSV').click();
  await db('Применить импорт').click();
  await store.locator('#w-message').getByText(/Каталог обновлён из CSV/).waitFor();

  await mb('Поставщики').click();
  await mb('Открыть поставщика').click();
  await mb('Добавить закупочную цену').click();
  await input('Артикул поставщика').fill('COLA5');
  await input('ID товара').fill(productId);
  await input('Единиц в упаковке').fill('5');
  await input('Цена упаковки, сум').fill('40000');
  await db('Сохранить предложение').click();
  await manage.getByRole('heading',{name:'История предложения COLA5'}).waitFor();
  await mb('К поставщику').click();
  await mb('Рассчитать закупку').click();
  await db('Рассчитать').click();
  await manage.getByText(/Недостаточно истории/).waitFor();
  assert.equal(await mb('Создать черновик').count(),0);
  await mb('Поставщики').click();
  await mb('Открыть поставщика').click();
  await mb('Рассчитать закупку').click();
  await input('Ручной спрос в день').fill('5');
  await db('Рассчитать').click();
  await mb('Создать черновик').click();
  await mb('Утвердить заказ').click();
  await db('Утвердить').click();
  await mb('Отметить отправку').waitFor();
  await mb('Пересчитать заказ').click();
  await input('Ручной спрос в день').fill('6');
  await db('Рассчитать').click();
  await mb('Обновить черновик').click();
  await mb('Утвердить заказ').waitFor();
  assert.equal(await mb('Отметить отправку').count(),0);
  await mb('Утвердить заказ').click();
  await db('Утвердить').click();
  await mb('Отметить отправку').click();
  await input('Подтверждение отправки').fill('Учебный заказ № 42');
  await db('Зарегистрировать отправку').click();
  await manage.getByText(/Отправлен.*Учебный заказ № 42/).waitFor();
  const orderId=await store.evaluate(()=>JSON.parse(localStorage.getItem('kasa-demo-ledger-v1')).procurement[0].orderId);
  if (process.env.KASA_QA_DIR) await store.screenshot({path:process.env.KASA_QA_DIR+'/order-stage4.png'});
  await selectRole(store,'receiver');
  await button(store,'Новая накладная').click();
  assert.ok(await store.locator('#w-order option').evaluateAll((items,id)=>items.some(row=>row.value===id),orderId));

  await selectRole(store,'admin');
  await button(store,'Управление').click();
  await mb('Отчёты и аудит').click();
  await manage.getByLabel('Тип отчёта').selectOption('stock-movements');
  await mb('Сформировать отчёт').click();
  await manage.getByText('Хлеб пшеничный',{exact:true}).first().waitFor();
  const downloaded=store.waitForEvent('download');
  await mb('Выгрузить CSV').click();
  const csvDownload=await downloaded;
  assert.equal(csvDownload.suggestedFilename(),'stock-movements.csv');
  assert.match(await readFile(await csvDownload.path(),'utf8'),/Хлеб пшеничный/);
  await manage.getByLabel('Тип отчёта').selectOption('audit');
  await mb('Сформировать отчёт').click();
  await manage.getByText('purchase_order.sent',{exact:true}).waitFor();
  if (process.env.KASA_QA_DIR) await store.screenshot({path:process.env.KASA_QA_DIR+'/audit-stage4.png'});
  assert.throws(()=>warehouseDemo(warehouseStorage,'receiver','management.report','sales',{from:'2026-11-08',to:'2026-11-08'}),{status:403});
  const locale = await browser.newPage({viewport:{width:1366,height:768}});
  watch(locale);
  await locale.goto(url);
  await button(locale,'Настройки').click();
  await locale.locator('#device-scan').fill('4780000000001');
  await button(locale,'Проверить сканер').click();
  await locale.locator('#device-scan-status').getByText('Штрихкод принят: 4780000000001').waitFor();
  await locale.locator('#device-scale').fill('375 g');
  await button(locale,'Проверить вес').click();
  await locale.locator('#device-scale-status').getByText('Вес принят: 0,375 кг').waitFor();
  locale.once('dialog', dialog => dialog.accept());
  await locale.locator('#ui-language').selectOption('uz');
  await locale.locator('html[lang="uz-Latn"]').waitFor({state:'attached'});
  await button(locale,'Smenani ochish').click();
  await locale.getByLabel('Boshlang‘ich naqd pul, so‘m').fill('50000');
  await locale.locator('#amount-confirm').click();
  await locale.getByText('● Smena ochiq').waitFor();
  await scan(locale,'4780011001002');
  await locale.getByText(/Qo‘shildi/).waitFor();
  await locale.keyboard.press('F2');
  await locale.getByLabel('Xaridor bergan summa, so‘m').fill('20000');
  await button(locale,'To‘lovni tasdiqlash').click();
  await locale.getByRole('heading',{name:'Chek saqlandi'}).waitFor();
  await locale.getByText('Sinov cheki • fiskal emas',{exact:true}).last().waitFor();
  await locale.locator('#receipt-dialog').getByRole('button',{name:'Yopish',exact:true}).last().click();
  await button(locale,'Qabul').click();
  await button(locale,'Administrator').click();
  await button(locale,'Boshqaruv').click();
  await locale.getByRole('heading',{name:'Mahsulotlar katalogi'}).waitFor();
  await button(locale,'Hisobotlar va audit').click();
  await button(locale,'Hisobotni tuzish').click();
  const reportType = locale.getByLabel('Hisobot turi');
  assert.equal(await reportType.inputValue(), 'sales');
  assert.equal(await reportType.locator('option:checked').textContent(), 'Sotuvlar va to‘lovlar');
  await locale.getByRole('button',{name:'CSV yuklab olish',exact:true}).waitFor();
  if (process.env.KASA_QA_DIR) await locale.screenshot({path:process.env.KASA_QA_DIR+'/uz-stage5.png'});
  assert.deepEqual(faults, []);
  console.log('OK: Stage 1 regression, partial and full returns, persisted approval, cash ledger, shelf tasks, API retry, receipt approval, stock posting, inventory, catalog, supplier prices, order revisions, reports, Uzbek interface and equipment inputs.');
} finally {
  await browser.close();
  server.close();
}
